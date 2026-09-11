using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ops;

/// <summary>
/// What the sweep runs last (slice 15 D-2): the invariant job and the two statistical detectors of SEC-102. Counts
/// come from the stored states; the thresholds are <see cref="OpsRules"/>.
/// </summary>
public sealed class OpsMonitor(TenantDbContext db, InvariantService invariants, AlertService alerts, TimeProvider time) : IOpsHooks
{
    public async Task<InvariantRun> RunAsync(string trigger, Guid? actorUserId, CancellationToken ct)
    {
        var run = await invariants.RunAsync(trigger, actorUserId, ct);
        await DetectGuardSpikeAsync(ct);
        await DetectSendAnomalyAsync(ct);
        return run;
    }

    private async Task DetectGuardSpikeAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-24);
        var window = db.AiSuggestions.Where(s => s.CreatedAt >= since);
        var total = await window.CountAsync(ct);
        var rejected = await window.CountAsync(s => s.ValidationStatus == AiValidationStatus.SchemaInvalid || s.ValidationStatus == AiValidationStatus.RejectedByGuard, ct);
        if (OpsRules.IsGuardRejectionSpike(total, rejected))
        {
            await alerts.RaiseAsync(AlertKinds.AiGuardRejectionSpike, AlertSeverity.Warning,
                $"{rejected} of {total} AI suggestions in the last 24 hours were rejected by the schema check or the guard.",
                new { suggestions = total, rejected, minimum = OpsRules.GuardSpikeMinimumSuggestions, share = OpsRules.GuardSpikeRejectedShare }, ct);
        }
    }

    private async Task DetectSendAnomalyAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var dayAgo = now.AddHours(-24);
        var weekBefore = dayAgo.AddDays(-7);
        var today = await db.Messages.CountAsync(m => m.SentAt != null && m.SentAt >= dayAgo, ct);
        var prior = await db.Messages.CountAsync(m => m.SentAt != null && m.SentAt >= weekBefore && m.SentAt < dayAgo, ct);
        if (OpsRules.IsSendVolumeAnomaly(today, prior))
        {
            await alerts.RaiseAsync(AlertKinds.SendVolumeAnomaly, AlertSeverity.Warning,
                $"{today} messages sent in the last 24 hours against {prior} in the previous seven days.",
                new { sentToday = today, sentPriorSevenDays = prior, minimum = OpsRules.SendAnomalyMinimumSends, multiple = OpsRules.SendAnomalyMultiple }, ct);
        }
    }
}
