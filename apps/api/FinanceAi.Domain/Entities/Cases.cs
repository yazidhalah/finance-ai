using System.Globalization;
using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>Doc 02 §2.1. Stored as text with a CHECK (SM-01).</summary>
public enum CaseStatus { Open, InProgress, AwaitingCustomer, PromiseActive, Disputed, OnHold, Escalated, Resolved, Abandoned }

/// <summary>Doc 02 §2.2 / §2.3 — every event the machine knows, including the ones later slices fire.</summary>
public enum CaseEvent
{
    InvoiceBecameOverdue,   // C1
    ContactLogged,          // C2
    ReplyReceived,          // C2 (slice 8)
    FollowUpDue,            // C2 (sweep)
    MessageSent,            // C3 (slice 8)
    PtpRecorded,            // C4 (slice 6)
    PtpBroken,              // C5 (slice 6)
    PtpCancelled,           // C5 (slice 6)
    PtpKeptAndBalanceZero,  // slice 6
    DisputeOpened,          // C6 (slice 7)
    AllDisputesResolved,    // C7 (slice 7)
    Hold,                   // C8
    Resume,                 // C8
    HoldExpired,            // C8 (sweep)
    Escalate,               // C9
    BalanceZero,            // C10
    Abandon,                // C11
}

/// <summary>Thrown for an illegal (state, event) pair. The API maps it to <c>409 invalid_transition</c>.</summary>
public sealed class InvalidTransitionException(string entity, string from, string @event)
    : InvalidOperationException($"{entity}: no transition from {from} on {@event}.")
{
    public string Entity { get; } = entity;

    public string From { get; } = from;

    public string Event { get; } = @event;
}

/// <summary>
/// The collection-case machine of doc 02 §2, as one table. Every legal (state, event) pair is a row here
/// and nowhere else; <see cref="Next"/> throws for anything absent (T-10). Terminal states have no rows.
/// </summary>
public static class CaseMachine
{
    private static readonly IReadOnlyDictionary<(CaseStatus, CaseEvent), CaseStatus> Table = new Dictionary<(CaseStatus, CaseEvent), CaseStatus>
    {
        // C2
        [(CaseStatus.Open, CaseEvent.ContactLogged)] = CaseStatus.InProgress,
        [(CaseStatus.Open, CaseEvent.ReplyReceived)] = CaseStatus.InProgress,
        [(CaseStatus.Open, CaseEvent.FollowUpDue)] = CaseStatus.InProgress,
        [(CaseStatus.AwaitingCustomer, CaseEvent.ContactLogged)] = CaseStatus.InProgress,
        [(CaseStatus.AwaitingCustomer, CaseEvent.ReplyReceived)] = CaseStatus.InProgress,
        [(CaseStatus.AwaitingCustomer, CaseEvent.FollowUpDue)] = CaseStatus.InProgress,
        // C3
        [(CaseStatus.InProgress, CaseEvent.MessageSent)] = CaseStatus.AwaitingCustomer,
        // C4 — "any active" per the table; the diagram draws InProgress and AwaitingCustomer
        [(CaseStatus.Open, CaseEvent.PtpRecorded)] = CaseStatus.PromiseActive,
        [(CaseStatus.InProgress, CaseEvent.PtpRecorded)] = CaseStatus.PromiseActive,
        [(CaseStatus.AwaitingCustomer, CaseEvent.PtpRecorded)] = CaseStatus.PromiseActive,
        // C5
        [(CaseStatus.PromiseActive, CaseEvent.PtpBroken)] = CaseStatus.InProgress,
        [(CaseStatus.PromiseActive, CaseEvent.PtpCancelled)] = CaseStatus.InProgress,
        [(CaseStatus.PromiseActive, CaseEvent.PtpKeptAndBalanceZero)] = CaseStatus.Resolved,
        // C6
        [(CaseStatus.Open, CaseEvent.DisputeOpened)] = CaseStatus.Disputed,
        [(CaseStatus.InProgress, CaseEvent.DisputeOpened)] = CaseStatus.Disputed,
        [(CaseStatus.AwaitingCustomer, CaseEvent.DisputeOpened)] = CaseStatus.Disputed,
        [(CaseStatus.PromiseActive, CaseEvent.DisputeOpened)] = CaseStatus.Disputed,
        // C7
        [(CaseStatus.Disputed, CaseEvent.AllDisputesResolved)] = CaseStatus.InProgress,
        // C8
        [(CaseStatus.Open, CaseEvent.Hold)] = CaseStatus.OnHold,
        [(CaseStatus.InProgress, CaseEvent.Hold)] = CaseStatus.OnHold,
        [(CaseStatus.AwaitingCustomer, CaseEvent.Hold)] = CaseStatus.OnHold,
        [(CaseStatus.PromiseActive, CaseEvent.Hold)] = CaseStatus.OnHold,
        [(CaseStatus.Disputed, CaseEvent.Hold)] = CaseStatus.OnHold,
        [(CaseStatus.OnHold, CaseEvent.Resume)] = CaseStatus.InProgress,
        [(CaseStatus.OnHold, CaseEvent.HoldExpired)] = CaseStatus.InProgress,
        // C9
        [(CaseStatus.Open, CaseEvent.Escalate)] = CaseStatus.Escalated,
        [(CaseStatus.InProgress, CaseEvent.Escalate)] = CaseStatus.Escalated,
        [(CaseStatus.AwaitingCustomer, CaseEvent.Escalate)] = CaseStatus.Escalated,
        [(CaseStatus.Disputed, CaseEvent.Escalate)] = CaseStatus.Escalated,
        [(CaseStatus.PromiseActive, CaseEvent.Escalate)] = CaseStatus.Escalated,
        [(CaseStatus.OnHold, CaseEvent.Escalate)] = CaseStatus.Escalated,
        // C10 — any non-terminal state
        [(CaseStatus.Open, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        [(CaseStatus.InProgress, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        [(CaseStatus.AwaitingCustomer, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        [(CaseStatus.PromiseActive, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        [(CaseStatus.Disputed, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        [(CaseStatus.OnHold, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        [(CaseStatus.Escalated, CaseEvent.BalanceZero)] = CaseStatus.Resolved,
        // C11
        [(CaseStatus.Open, CaseEvent.Abandon)] = CaseStatus.Abandoned,
        [(CaseStatus.InProgress, CaseEvent.Abandon)] = CaseStatus.Abandoned,
        [(CaseStatus.Escalated, CaseEvent.Abandon)] = CaseStatus.Abandoned,
    };

    public static IReadOnlyDictionary<(CaseStatus From, CaseEvent Event), CaseStatus> Transitions => Table;

    public static bool IsTerminal(CaseStatus status) => status is CaseStatus.Resolved or CaseStatus.Abandoned;

    public static CaseStatus? Peek(CaseStatus from, CaseEvent @event) => Table.TryGetValue((from, @event), out var to) ? to : null;

    public static CaseStatus Next(CaseStatus from, CaseEvent @event) =>
        Peek(from, @event) ?? throw new InvalidTransitionException("collection_case", from.ToString(), @event.ToString());

    /// <summary>The events a user may fire through the API; the rest belong to the sweep and to later slices.</summary>
    public static readonly IReadOnlyDictionary<string, CaseEvent> UserEvents = new Dictionary<string, CaseEvent>(StringComparer.Ordinal)
    {
        ["contact_logged"] = CaseEvent.ContactLogged,
        ["hold"] = CaseEvent.Hold,
        ["resume"] = CaseEvent.Resume,
        ["escalate"] = CaseEvent.Escalate,
        ["abandon"] = CaseEvent.Abandon,
    };

    /// <summary>C9 and C11 need <c>cases.escalate</c>; the others <c>cases.write</c>.</summary>
    public static bool RequiresEscalatePermission(CaseEvent @event) => @event is CaseEvent.Escalate or CaseEvent.Abandon;

    public static string EventName(CaseEvent @event) => @event switch
    {
        CaseEvent.InvoiceBecameOverdue => "invoice_became_overdue",
        CaseEvent.ContactLogged => "contact_logged",
        CaseEvent.ReplyReceived => "reply_received",
        CaseEvent.FollowUpDue => "follow_up_due",
        CaseEvent.MessageSent => "message_sent",
        CaseEvent.PtpRecorded => "ptp_recorded",
        CaseEvent.PtpBroken => "ptp_broken",
        CaseEvent.PtpCancelled => "ptp_cancelled",
        CaseEvent.PtpKeptAndBalanceZero => "ptp_kept_and_balance_zero",
        CaseEvent.DisputeOpened => "dispute_opened",
        CaseEvent.AllDisputesResolved => "all_disputes_resolved",
        CaseEvent.Hold => "hold",
        CaseEvent.Resume => "resume",
        CaseEvent.HoldExpired => "hold_expired",
        CaseEvent.Escalate => "escalate",
        CaseEvent.BalanceZero => "balance_zero",
        CaseEvent.Abandon => "abandon",
        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
    };
}

public sealed class CollectionCase : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public long CaseNumber { get; set; }
    public CaseStatus Status { get; set; } = CaseStatus.Open;
    public int PriorityScore { get; set; }
    public int WeightsVersion { get; set; } = 1;
    public string PriorityFactors { get; set; } = "[]";
    public DateTimeOffset? ScoredAt { get; set; }
    public decimal OverdueBalanceBase { get; set; }
    public int MaxDaysPastDue { get; set; }
    public int InvoiceCount { get; set; }
    public Guid? AssignedTo { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NextActionAt { get; set; }
    public string? NextActionReason { get; set; }
    public DateOnly? HoldUntil { get; set; }
    public string? HoldReason { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public Guid? EscalatedBy { get; set; }
    public string? EscalationReason { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CloseReason { get; set; }
    public DateTimeOffset? LastContactAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;

    /// <summary>SM-26: once escalated, automation never comes back — not even after resolution.</summary>
    public bool AutomationDisabled => this.EscalatedAt is not null;
}

public sealed class CaseInvoice : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CaseId { get; set; }
    public Guid InvoiceId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedReason { get; set; }
}

public static class ActivityKinds
{
    public const string Note = "note";
    public const string Call = "call";
    public const string EmailSent = "email_sent";
    public const string EmailReceived = "email_received";
    public const string WhatsappPrepared = "whatsapp_prepared";
    public const string Meeting = "meeting";
    public const string StatusChange = "status_change";
    public const string Ptp = "ptp";
    public const string Dispute = "dispute";
    public const string Payment = "payment";
    public const string System = "system";

    /// <summary>What a user may log through the API. The rest are written by the service.</summary>
    public static readonly IReadOnlyList<string> UserLoggable = [Note, Call, EmailSent, EmailReceived, WhatsappPrepared, Meeting];

    /// <summary>The kinds that count as reaching the customer (C2, <c>last_contact_at</c>). A note is not contact.</summary>
    public static bool IsContact(string kind) => kind is Call or EmailSent or EmailReceived or WhatsappPrepared or Meeting;
}

public sealed class CaseActivity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CaseId { get; set; }
    public string Kind { get; set; } = ActivityKinds.Note;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorUserId { get; set; }
    public string ActorKind { get; set; } = ActorKinds.User;
    public Guid? AiSuggestionId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

/// <summary>
/// FIN-81: the weights are versioned; a case records the version that scored it, so any ordering can be
/// reproduced later. Values are constants per version — a tenant chooses a version, not a weight (slice 5 D-1).
/// </summary>
public sealed record PriorityWeights(
    int Version,
    int Amount, decimal AmountSaturation,
    int DaysPastDue, int DaysSaturation,
    int BrokenPromises, int BrokenPromiseSaturation,
    int BouncedCheques, int BouncedChequeSaturation,
    int CustomerValue,
    int RecentContact, int RecentContactWindowDays,
    int Dispute)
{
    public static PriorityWeights Version1 { get; } = new(1, 35, 10_000m, 30, 120, 15, 3, 10, 2, 10, 10, 7, 20);

    public static PriorityWeights For(int version) => version switch
    {
        1 => Version1,
        _ => throw new ArgumentOutOfRangeException(nameof(version), $"No priority weights version {version}."),
    };
}

/// <summary>Everything the score is computed from — stored facts only, never an AI output (FIN-82).</summary>
public sealed record PriorityInputs(
    decimal OverdueBalanceBase,
    int MaxDaysPastDue,
    int BrokenPromises12m,
    int BouncedCheques12m,
    RiskFlag RiskFlag,
    int? DaysSinceLastContact,
    bool Disputed);

public sealed record PriorityFactor(string Factor, int Contribution, string Detail);

public sealed record PriorityResult(int Score, IReadOnlyList<PriorityFactor> Factors, int WeightsVersion);

/// <summary>
/// FIN-80: an integer 0–100, explainable. Each contribution is rounded to an integer before the sum, so
/// the breakdown shown to the user always adds up to the score (slice 5 D-3). Deterministic (T-31).
/// </summary>
public static class PriorityScore
{
    public static PriorityResult Compute(PriorityInputs inputs, PriorityWeights w)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(w);

        var factors = new List<PriorityFactor>
        {
            new("amount", Part(w.Amount, Saturate(inputs.OverdueBalanceBase, w.AmountSaturation)), $"{inputs.OverdueBalanceBase.ToString("F3", CultureInfo.InvariantCulture)} base overdue"),
            new("days_past_due", Part(w.DaysPastDue, Saturate(inputs.MaxDaysPastDue, w.DaysSaturation)), $"{inputs.MaxDaysPastDue} days"),
            new("broken_promises", Part(w.BrokenPromises, Saturate(inputs.BrokenPromises12m, w.BrokenPromiseSaturation)), $"{inputs.BrokenPromises12m} broken in 12 months"),
            new("bounced_cheques", Part(w.BouncedCheques, Saturate(inputs.BouncedCheques12m, w.BouncedChequeSaturation)), $"{inputs.BouncedCheques12m} bounced in 12 months"),
            new("customer_value", Part(w.CustomerValue, RiskFactor(inputs.RiskFlag)), $"risk flag {inputs.RiskFlag}"),
        };

        if (inputs.DaysSinceLastContact is { } days && days < w.RecentContactWindowDays)
        {
            factors.Add(new("recent_contact", -Part(w.RecentContact, 1m - (decimal)days / w.RecentContactWindowDays), $"contacted {days} days ago"));
        }

        if (inputs.Disputed)
        {
            factors.Add(new("dispute", -w.Dispute, "case is disputed"));
        }

        var score = Math.Clamp(factors.Sum(f => f.Contribution), 0, 100);
        return new PriorityResult(score, factors, w.Version);
    }

    private static decimal Saturate(decimal value, decimal saturation) => value <= 0m ? 0m : value >= saturation ? 1m : value / saturation;

    private static int Part(int weight, decimal normalized) => (int)Math.Round(weight * normalized, 0, MidpointRounding.AwayFromZero);

    /// <summary>"Don't burn a good customer": a flagged customer ranks higher, an unflagged one gets no push.</summary>
    private static decimal RiskFactor(RiskFlag flag) => flag switch
    {
        RiskFlag.None => 0m,
        RiskFlag.Watch => 0.5m,
        _ => 1m,
    };
}

/// <summary>Doc 05 slice 5: rule-based, not AI. Slice 9 adds a separate <c>aiSuggestion</c>.</summary>
public sealed record SuggestedAction(string Kind, string? TemplateKey, string Language);

public static class SuggestedActions
{
    public static SuggestedAction For(CaseStatus status, int maxDaysPastDue, int? daysSinceLastContact, IReadOnlyList<int> cadenceDays, string language)
    {
        ArgumentNullException.ThrowIfNull(cadenceDays);
        switch (status)
        {
            case CaseStatus.Disputed: return new("resolve_dispute", null, language);
            case CaseStatus.PromiseActive: return new("await_promise", null, language);
            case CaseStatus.Escalated: return new("manual_follow_up", null, language);
            case CaseStatus.OnHold: return new("wait", null, language);
        }

        var stage = cadenceDays.Where(d => d <= maxDaysPastDue).DefaultIfEmpty(cadenceDays.Count > 0 ? cadenceDays[0] : 0).Max();
        var lastStage = cadenceDays.Count > 0 ? cadenceDays[^1] : 0;
        if (maxDaysPastDue >= lastStage && (daysSinceLastContact is null || daysSinceLastContact >= 7))
        {
            return new("call", null, language);
        }

        return new("send_reminder", $"dunning_{stage}", language);
    }
}
