namespace FinanceAi.Infrastructure.Database;

/// <summary>
/// The tenant and user of the current request. Populated <b>only</b> from validated access-token
/// claims (SEC-20). There is deliberately no constructor, setter or helper anywhere that accepts a
/// tenant id originating from a header, query string, body, cookie or referer; AC-35 enforces that
/// by static check over the source tree.
/// </summary>
public interface ITenantContext
{
    /// <summary>The tenant of the current request. Throws when the request is not tenant-scoped.</summary>
    Guid TenantId { get; }

    Guid? UserId { get; }

    bool IsSet { get; }

    /// <summary>
    /// <see cref="Guid.Empty"/> when unset, for use in the EF global query filter — an unset tenant
    /// must match nothing rather than match everything.
    /// </summary>
    Guid TenantIdOrEmpty { get; }
}

public sealed class TenantContext : ITenantContext
{
    private Guid? tenantId;

    public Guid TenantId => this.tenantId
        ?? throw new InvalidOperationException(
            "No tenant is in scope. A tenant-scoped operation ran outside the request pipeline's " +
            "tenant scope, or a background job did not declare its tenant (SEC-22).");

    public Guid? UserId { get; private set; }

    public bool IsSet => this.tenantId is not null;

    public Guid TenantIdOrEmpty => this.tenantId ?? Guid.Empty;

    /// <summary>
    /// Called by the tenant-scope middleware with values taken from the validated token, and by
    /// background jobs that explicitly declare the tenant they are iterating (SEC-22).
    /// </summary>
    public void Set(Guid tenantId, Guid? userId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        this.tenantId = tenantId;
        this.UserId = userId;
    }

    public void SetUser(Guid? userId) => this.UserId = userId;
}
