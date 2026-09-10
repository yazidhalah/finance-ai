using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FinanceAi.Infrastructure.Database;

/// <summary>
/// A transaction with the PostgreSQL tenant context set on it (doc 04 §1.1). This is layer 2's
/// entry point: <c>set_config(..., is_local =&gt; true)</c> binds the setting to <b>this
/// transaction</b>, so it cannot survive the connection's return to the pool and leak into the
/// next borrower — the classic RLS-plus-pooling bug (SEC-23, proved absent by AC-31).
/// <para>
/// The pipeline order is fixed by SEC-11: authenticate, resolve the tenant from the token, enter
/// this scope, check the permission, run the handler. A handler never receives an unscoped context.
/// </para>
/// </summary>
public sealed class DatabaseScope : IAsyncDisposable
{
    private readonly IDbContextTransaction transaction;
    private bool completed;

    private DatabaseScope(IDbContextTransaction transaction) => this.transaction = transaction;

    /// <summary>Opens a scope bound to one tenant. The normal path for every request and job.</summary>
    public static Task<DatabaseScope> EnterTenantAsync(
        TenantDbContext db, Guid tenantId, Guid? userId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        return OpenAsync(db, tenantId, userId, platformScope: false, ct);
    }

    /// <summary>
    /// Opens a scope for the identity flows that necessarily run before a tenant is known —
    /// login, refresh, registration, "which organizations do I belong to".
    /// <para>
    /// <b>This is the one documented escape from tenant scope (DM-06).</b> It is callable only from
    /// <see cref="PlatformIdentityStore"/>, which AC-35's sibling test enforces by static check, and
    /// it still grants nothing on <c>tenant_settings</c> or <c>audit_events</c> — those policies
    /// have no platform clause, so business data always requires a real tenant.
    /// </para>
    /// </summary>
    internal static Task<DatabaseScope> EnterPlatformAsync(
        TenantDbContext db, Guid? tenantId, Guid? userId, CancellationToken ct = default) =>
        OpenAsync(db, tenantId, userId, platformScope: true, ct);

    private static async Task<DatabaseScope> OpenAsync(
        TenantDbContext db, Guid? tenantId, Guid? userId, bool platformScope, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            // Empty string rather than NULL: app_current_tenant() maps '' back to NULL, so an
            // unset tenant makes every policy evaluate to NULL and return no rows (DM-05).
            var tenantValue = tenantId?.ToString() ?? string.Empty;
            var userValue = userId?.ToString() ?? string.Empty;
            var platformValue = platformScope ? "on" : "off";

            await db.Database.ExecuteSqlAsync(
                $"""
                 SELECT set_config('app.tenant_id', {tenantValue}, true),
                        set_config('app.user_id', {userValue}, true),
                        set_config('app.platform_scope', {platformValue}, true)
                 """, ct);

            return new DatabaseScope(transaction);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Re-binds <c>app.tenant_id</c> inside an already-open identity-flow transaction, for the
    /// moment a login or a refresh has just established which organization the session belongs to.
    /// Still transaction-local, so it cannot outlive the transaction (SEC-23).
    /// <para>Internal, and callable only from <see cref="PlatformIdentityStore"/>.</para>
    /// </summary>
    internal static Task SetTenantAsync(
        TenantDbContext db, Guid? tenantId, Guid? userId, CancellationToken ct = default)
    {
        var tenantValue = tenantId?.ToString() ?? string.Empty;
        var userValue = userId?.ToString() ?? string.Empty;

        return db.Database.ExecuteSqlAsync(
            $"""
             SELECT set_config('app.tenant_id', {tenantValue}, true),
                    set_config('app.user_id', {userValue}, true)
             """, ct);
    }

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        await this.transaction.CommitAsync(ct);
        this.completed = true;
    }

    /// <summary>Rolls back unless <see cref="CompleteAsync"/> ran. Failing closed is the default.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!this.completed)
        {
            try
            {
                await this.transaction.RollbackAsync();
            }
            catch (InvalidOperationException)
            {
                // The transaction was already ended by the server (for example after an error).
            }
        }

        await this.transaction.DisposeAsync();
    }
}
