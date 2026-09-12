using FinanceAi.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinanceAi.Infrastructure.Cases;

/// <summary>
/// Slice 32 — the scheduler slice 5 deferred (D-2): the daily job (case creation, follow-ups, reminders, briefings,
/// the invariant run — <see cref="CaseService.SweepAsync"/>) runs by itself. Every <c>SWEEP_INTERVAL_MINUTES</c>
/// (default 60; 0 disables, which the test suites do) the runner lists the active tenants on the platform scope and
/// enters each one separately — its own DI scope, its own <see cref="TenantContext"/>, its own database scope — so a
/// failure in one tenant is logged and the next tenant still runs. The sweep is idempotent (SM-06) and its own
/// checks (quiet hours, duplicate windows, the briefing's send time) decide what an hourly pass actually does; the
/// pass exists so that a tenant's local morning is never missed whatever the server's clock says. <c>POST /cases/sweep</c>
/// stays for an operator who wants it now. The API hosts it (<c>SweepScheduler</c>); lifecycle steps (slice 33) hook in here.
/// </summary>
public sealed class SweepRunner(IServiceScopeFactory scopes, ILogger<SweepRunner> logger)
{
    public const string IntervalVariable = "SWEEP_INTERVAL_MINUTES";

    public sealed record TenantOutcome(Guid TenantId, bool Succeeded, string? Error);

    public static int IntervalMinutes =>
        int.TryParse(Environment.GetEnvironmentVariable(IntervalVariable), out var minutes) && minutes >= 0 ? minutes : 60;

    /// <summary>One pass over every active tenant. Public so a test can drive it without waiting for the timer.</summary>
    public async Task<IReadOnlyList<TenantOutcome>> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<(Guid Id, string Timezone)> tenants;
        await using (var listing = scopes.CreateAsyncScope())
        {
            tenants = await listing.ServiceProvider.GetRequiredService<PlatformIdentityStore>().ListActiveTenantsAsync(ct);
        }

        var outcomes = new List<TenantOutcome>(tenants.Count);
        foreach (var (tenantId, _) in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
                tenantContext.Set(tenantId, null);
                var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                await using var databaseScope = await DatabaseScope.EnterTenantAsync(db, tenantId, null, ct);
                await scope.ServiceProvider.GetRequiredService<CaseService>().SweepAsync(null, ct);
                await databaseScope.CompleteAsync(ct);
                outcomes.Add(new TenantOutcome(tenantId, true, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sweep failed for tenant {OrganizationId}.", tenantId);
                outcomes.Add(new TenantOutcome(tenantId, false, ex.GetType().Name));
            }
        }

        return outcomes;
    }
}
