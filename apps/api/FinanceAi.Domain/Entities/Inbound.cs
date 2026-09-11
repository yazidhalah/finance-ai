using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>Doc 04 §5.6.</summary>
public enum InboundStatus { Unprocessed, Classified, Unclassified, HumanClassified, Ignored }

public static class InboundChannels
{
    public const string Email = "email";
    public const string WhatsappPasted = "whatsapp_pasted";
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All = [Email, WhatsappPasted, Manual];
}

/// <summary>
/// A customer reply. <see cref="BodyRaw"/> is untrusted input (SEC-40): stored verbatim, shown as quoted text
/// (SEC-42), sent to the model only inside a data block (AI-20), and never treated as an instruction anywhere.
/// </summary>
public sealed class InboundMessage : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? CaseId { get; set; }
    public required string Channel { get; set; }
    public string? FromAddress { get; set; }
    public string? Subject { get; set; }
    public required string BodyRaw { get; set; }
    public string? BodyNormalized { get; set; }
    public string? DetectedLanguage { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? InReplyToMessageId { get; set; }
    public decimal? MatchConfidence { get; set; }
    public string? MatchMethod { get; set; }
    public Guid? MatchedBy { get; set; }
    public InboundStatus ClassificationStatus { get; set; } = InboundStatus.Unprocessed;
    public string? Classification { get; set; }
    public string? HumanClassification { get; set; }
    public Guid? HumanClassifiedBy { get; set; }
    public DateTimeOffset? HumanClassifiedAt { get; set; }
    public Guid? LastSuggestionId { get; set; }
    public bool HasAttachments { get; set; }
    public bool TruncatedForAi { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public static class AiOperations
{
    public const string ClassifyCustomerReply = "classify_customer_reply";
}

public static class AiValidationStatus
{
    public const string Valid = "valid";
    public const string SchemaInvalid = "schema_invalid";
    public const string BelowThreshold = "below_threshold";
    public const string RejectedByGuard = "rejected_by_guard";
}

public static class AiHumanDecisions
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Edited = "edited";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
}

public static class AiOutcomeTypes
{
    public const string VerificationTask = "verification_task";
    public const string PromiseProposed = "promise_proposed";
    public const string DisputeOpen = "dispute_open";
    public const string Activity = "activity";
    public const string None = "none";
}

/// <summary>
/// One model call, with everything needed to attribute it later (AI-06, CLAUDE.md → Financial Safety): exact
/// weights, exact prompt, exact schema, the hash of what was sent, the validated output, and what a human did.
/// </summary>
public sealed class AiSuggestion : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Operation { get; set; }
    public required string SubjectType { get; set; }
    public Guid SubjectId { get; set; }
    public required string ModelName { get; set; }
    public required string ModelDigest { get; set; }
    public required string PromptVersion { get; set; }
    public required string SchemaVersion { get; set; }
    public required string InputRef { get; set; }
    public required string InputHash { get; set; }
    public required string OutputJson { get; set; }
    public decimal Confidence { get; set; }
    public string? Classification { get; set; }
    public string? ReasonCode { get; set; }
    public required string ValidationStatus { get; set; }
    public bool RequiresHumanReview { get; set; } = true;
    public bool Suspicious { get; set; }
    public int LatencyMs { get; set; }
    public string? OutcomeType { get; set; }
    public Guid? OutcomeId { get; set; }
    public string? GuardReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string HumanDecision { get; set; } = AiHumanDecisions.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
    public string? HumanCorrection { get; set; }
}
