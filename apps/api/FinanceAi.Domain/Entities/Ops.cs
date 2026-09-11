using System.Text.Json.Serialization;
using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>One invariant's outcome inside a run (doc 03 §7). Counts and sample ids only — never an amount.</summary>
public sealed record InvariantCheckResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("violations")] long Violations,
    [property: JsonPropertyName("samples")] IReadOnlyList<string> Samples);

public static class InvariantRunTrigger
{
    public const string Sweep = "sweep";
    public const string Manual = "manual";
}

public static class InvariantRunStatus
{
    public const string Ok = "ok";
    public const string Violations = "violations";
}

/// <summary>A recorded run of the invariant job. Immutable once written (no UPDATE grant).</summary>
public sealed class InvariantRun : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
    public required string Trigger { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Status { get; set; }
    public required string ChecksJson { get; set; }
    public int DurationMs { get; set; }
}

public static class AlertKinds
{
    public const string InvariantViolation = "invariant_violation";
    public const string AuditChainBreak = "audit_chain_break";
    public const string AiGuardRejectionSpike = "ai_guard_rejection_spike";
    public const string SendVolumeAnomaly = "send_volume_anomaly";
}

public static class AlertSeverity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
}

public static class AlertDelivery
{
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>A page-worthy condition (SEC-102), one row per tenant, kind and UTC day.</summary>
public sealed class Alert : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Kind { get; set; }
    public required string Severity { get; set; }
    public required string Summary { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public required string DedupeKey { get; set; }
    public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.UtcNow;
    public string EmailDelivery { get; set; } = AlertDelivery.Skipped;
    public string WebhookDelivery { get; set; } = AlertDelivery.Skipped;
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public long RowVersion { get; set; } = 1;

    /// <summary>The dedupe key: the kind and the UTC day, so one condition pages once a day (slice 15 §2).</summary>
    public static string DedupeKeyFor(string kind, DateTimeOffset now) => $"{kind}:{now.UtcDateTime:yyyy-MM-dd}";
}

/// <summary>
/// The two statistical detectors of SEC-102, as pure functions with their thresholds beside them. Counts only —
/// no money passes through here.
/// </summary>
public static class OpsRules
{
    /// <summary>At least this many suggestions in the window before a rejection share means anything.</summary>
    public const int GuardSpikeMinimumSuggestions = 10;

    /// <summary>The rejected share (schema_invalid + rejected_by_guard) at or above which the model is having a bad day.</summary>
    public const decimal GuardSpikeRejectedShare = 0.5m;

    /// <summary>Below this many sends in a day nothing is an anomaly, whatever the history.</summary>
    public const int SendAnomalyMinimumSends = 20;

    /// <summary>Today's sends at or above this multiple of the trailing seven-day mean is an anomaly.</summary>
    public const decimal SendAnomalyMultiple = 3m;

    public static bool IsGuardRejectionSpike(int suggestionsInWindow, int rejectedInWindow)
    {
        if (suggestionsInWindow < GuardSpikeMinimumSuggestions) return false;
        return rejectedInWindow * 1m / suggestionsInWindow >= GuardSpikeRejectedShare;
    }

    /// <param name="sentToday">Sends in the last 24 hours.</param>
    /// <param name="sentPriorSevenDays">Sends in the seven days before that window.</param>
    public static bool IsSendVolumeAnomaly(int sentToday, int sentPriorSevenDays)
    {
        if (sentToday < SendAnomalyMinimumSends) return false;
        var mean = sentPriorSevenDays / 7m;
        return sentToday >= mean * SendAnomalyMultiple;
    }
}
