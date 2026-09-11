using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>
/// One row per tenant, typed columns rather than a JSON bag (doc 04 §4). Created with the
/// documented defaults at registration. Only the fields this slice reads are exercised;
/// the rest are carried so later slices do not need a migration to switch them on.
/// </summary>
public sealed class TenantSettings : ITenantScoped
{
    public Guid TenantId { get; set; }

    public string AgingBasis { get; set; } = "due_date";
    public int[] AgingBucketDays { get; set; } = [30, 60, 90];
    public int GraceDaysBeforeCase { get; set; } = 3;
    public int PtpGraceBusinessDays { get; set; } = 2;
    public decimal PtpPartialThresholdPct { get; set; } = 50.00m;

    /// <summary>PRD-15. Defaults to true: a human sees every message before a customer does.</summary>
    public bool RequireApprovalBeforeSend { get; set; } = true;

    public bool ExactMatchAutoAllocation { get; set; } = true;
    public decimal AutoClearResidualBelow { get; set; } = 0.100m;
    public bool AllowSplitDunningDuringDispute { get; set; }

    /// <summary>PRD-14. A server-side filter, never a substitute for tenancy.</summary>
    public bool CollectorSeesOnlyAssigned { get; set; }

    public bool AiEnabled { get; set; } = true;
    public decimal AiMinConfidence { get; set; } = 0.700m;
    public int[] DunningCadenceDays { get; set; } = [0, 7, 14, 30];
    public TimeOnly QuietHoursStart { get; set; } = new(20, 0);
    public TimeOnly QuietHoursEnd { get; set; } = new(8, 0);
    public TimeOnly BriefingSendAt { get; set; } = new(7, 30);
    public int PriorityWeightsVersion { get; set; } = 1;

    /// <summary>SEC-103: the tenant-level kill switch. Off means nothing leaves, queued or not.</summary>
    public bool OutboundSendingEnabled { get; set; } = true;

    /// <summary>SEC-86: messages per tenant day; the dispatcher stops at the cap.</summary>
    public int DailySendCap { get; set; } = 200;

    /// <summary>Slice 10: the language the briefing email goes out in; both languages are always generated (AI-83).</summary>
    public string BriefingLanguage { get; set; } = "ar";

    public bool BriefingEmailEnabled { get; set; }

    /// <summary>Members who receive the briefing email. Never a free address (slice 10 D-4).</summary>
    public Guid[] BriefingRecipientUserIds { get; set; } = [];

    /// <summary>Slice 22: email the organization's active Owners on critical alerts (SEC-102), in addition to the operator.</summary>
    public bool AlertOwnerEmailEnabled { get; set; }
}
