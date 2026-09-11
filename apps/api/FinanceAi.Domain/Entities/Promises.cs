using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>Doc 02 §3.1.</summary>
public enum PtpStatus { Proposed, Active, Kept, PartiallyKept, Broken, Cancelled, Rejected }

public enum PtpEvent
{
    Confirm,                    // Proposed → Active (human)
    Reject,                     // Proposed → Rejected (human)
    PaymentCoversPromise,       // Active → Kept (system)
    PartialPaymentAtDeadline,   // Active → PartiallyKept (system)
    DeadlinePassedUnpaid,       // Active → Broken (system)
    Cancel,                     // Active → Cancelled (user)
    SupersededByNewPtp,         // Active → Cancelled (system, SM-36)
    ChequeBounced,              // Active → Broken (system, SM-51 / E2)
}

public static class PtpSources
{
    public const string Call = "call";
    public const string Email = "email";
    public const string Whatsapp = "whatsapp";
    public const string InPerson = "in_person";
    public const string Cheque = "cheque";
    public const string AiSuggested = "ai_suggested";

    public static readonly IReadOnlyList<string> All = [Call, Email, Whatsapp, InPerson, Cheque, AiSuggested];

    /// <summary>What a user may name when recording a promise; <c>cheque</c> comes from the cheque path, <c>ai_suggested</c> from slice 9.</summary>
    public static readonly IReadOnlyList<string> UserRecordable = [Call, Email, Whatsapp, InPerson];
}

/// <summary>The PTP machine of doc 02 §3.2 as one table (T-10). Terminal states have no rows.</summary>
public static class PtpMachine
{
    private static readonly IReadOnlyDictionary<(PtpStatus, PtpEvent), PtpStatus> Table = new Dictionary<(PtpStatus, PtpEvent), PtpStatus>
    {
        [(PtpStatus.Proposed, PtpEvent.Confirm)] = PtpStatus.Active,
        [(PtpStatus.Proposed, PtpEvent.Reject)] = PtpStatus.Rejected,
        [(PtpStatus.Active, PtpEvent.PaymentCoversPromise)] = PtpStatus.Kept,
        [(PtpStatus.Active, PtpEvent.PartialPaymentAtDeadline)] = PtpStatus.PartiallyKept,
        [(PtpStatus.Active, PtpEvent.DeadlinePassedUnpaid)] = PtpStatus.Broken,
        [(PtpStatus.Active, PtpEvent.Cancel)] = PtpStatus.Cancelled,
        [(PtpStatus.Active, PtpEvent.SupersededByNewPtp)] = PtpStatus.Cancelled,
        [(PtpStatus.Active, PtpEvent.ChequeBounced)] = PtpStatus.Broken,
    };

    public static IReadOnlyDictionary<(PtpStatus From, PtpEvent Event), PtpStatus> Transitions => Table;

    public static bool IsTerminal(PtpStatus s) => s is not (PtpStatus.Proposed or PtpStatus.Active);

    public static PtpStatus? Peek(PtpStatus from, PtpEvent @event) => Table.TryGetValue((from, @event), out var to) ? to : null;

    public static PtpStatus Next(PtpStatus from, PtpEvent @event) =>
        Peek(from, @event) ?? throw new InvalidTransitionException("promise_to_pay", from.ToString(), @event.ToString());

    /// <summary>Only a human's events reach the API (doc 05 slice 6). Kept / Broken have no endpoint.</summary>
    public static bool IsUserEvent(PtpEvent @event) => @event is PtpEvent.Confirm or PtpEvent.Reject or PtpEvent.Cancel;

    public static string EventName(PtpEvent @event) => @event switch
    {
        PtpEvent.Confirm => "confirm",
        PtpEvent.Reject => "reject",
        PtpEvent.PaymentCoversPromise => "payment_covers_promise",
        PtpEvent.PartialPaymentAtDeadline => "partial_payment_at_deadline",
        PtpEvent.DeadlinePassedUnpaid => "deadline_passed_unpaid",
        PtpEvent.Cancel => "cancel",
        PtpEvent.SupersededByNewPtp => "superseded_by_new_ptp",
        PtpEvent.ChequeBounced => "cheque_bounced",
        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
    };
}

/// <summary>SM-30. A commitment, never money: nothing here is ever subtracted from a balance.</summary>
public sealed class PromiseToPay : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CaseId { get; set; }
    public Guid CustomerId { get; set; }
    public PtpStatus Status { get; set; } = PtpStatus.Proposed;
    public decimal PromisedAmount { get; set; }
    public required string Currency { get; set; }
    public DateOnly PromisedDate { get; set; }
    public DateOnly DeadlineDate { get; set; }
    public required string Source { get; set; }
    public Guid? CapturedBy { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public Guid? AiSuggestionId { get; set; }
    public Guid? ChequeId { get; set; }
    public Guid? SupersededById { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? EvaluatedAt { get; set; }
    public decimal? ReceivedInWindow { get; set; }
    public string? EvaluationNote { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

public sealed class PtpInvoice : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid PtpId { get; set; }
    public Guid InvoiceId { get; set; }
}

/// <summary>FIN-73: announced, never computed.</summary>
public sealed class TenantHoliday : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
}

/// <summary>A-08 / FIN-71: Sunday–Thursday works; Friday and Saturday do not; holidays are a list.</summary>
public static class BusinessDays
{
    public static bool IsBusinessDay(DateOnly date, IReadOnlySet<DateOnly> holidays) =>
        date.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) && !holidays.Contains(date);

    /// <summary>Adds <paramref name="count"/> business days; zero returns the date itself even on a weekend.</summary>
    public static DateOnly Add(DateOnly date, int count, IReadOnlySet<DateOnly> holidays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var result = date;
        var remaining = count;
        while (remaining > 0)
        {
            result = result.AddDays(1);
            if (IsBusinessDay(result, holidays))
            {
                remaining--;
            }
        }

        return result;
    }
}

public sealed record Reliability(int Kept, int PartiallyKept, int Broken)
{
    public int Denominator => this.Kept + this.PartiallyKept + this.Broken;

    /// <summary>SM-37: no bare percentage on a tiny sample. Below three, only the count is shown.</summary>
    public decimal? Ratio => this.Denominator < 3 ? null : Math.Round((decimal)this.Kept / this.Denominator, 3, MidpointRounding.AwayFromZero);
}

/// <summary>Doc 02 §3.3, the pure parts.</summary>
public static class PtpRules
{
    /// <summary>
    /// SM-34 / SM-35. Before the deadline only <c>Kept</c> can be decided; at or after it, the threshold decides
    /// between <c>PartiallyKept</c> and <c>Broken</c>. Exactly on the threshold is <c>PartiallyKept</c>.
    /// </summary>
    public static PtpEvent? Verdict(decimal promised, decimal receivedInWindow, decimal partialThresholdPct, bool deadlineReached)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(promised);
        if (receivedInWindow >= promised)
        {
            return PtpEvent.PaymentCoversPromise;
        }

        if (!deadlineReached)
        {
            return null;
        }

        // Compared as decimals at scale 3 against an exact product; no rounding is needed to decide ≥.
        var threshold = promised * partialThresholdPct / 100m;
        return receivedInWindow >= threshold ? PtpEvent.PartialPaymentAtDeadline : PtpEvent.DeadlinePassedUnpaid;
    }

    public static DateOnly Deadline(DateOnly promisedDate, int graceBusinessDays, IReadOnlySet<DateOnly> holidays) =>
        BusinessDays.Add(promisedDate, graceBusinessDays, holidays);

    /// <summary>SM-37: PartiallyKept counts as not kept.</summary>
    public static Reliability ReliabilityOf(IEnumerable<PtpStatus> evaluatedStatuses)
    {
        var kept = 0;
        var partial = 0;
        var broken = 0;
        foreach (var s in evaluatedStatuses)
        {
            switch (s)
            {
                case PtpStatus.Kept: kept++; break;
                case PtpStatus.PartiallyKept: partial++; break;
                case PtpStatus.Broken: broken++; break;
            }
        }

        return new Reliability(kept, partial, broken);
    }

    /// <summary>D-2: a cheque names no invoice, so it covers the oldest-due invoices first up to its amount (FIN-25's order).</summary>
    public static IReadOnlyList<Guid> CoverFifo(decimal amount, IEnumerable<(Guid InvoiceId, DateOnly DueDate, string Number, decimal Open)> candidates)
    {
        var covered = new List<Guid>();
        var remaining = amount;
        foreach (var c in candidates.Where(c => c.Open > 0m).OrderBy(c => c.DueDate).ThenBy(c => c.Number, StringComparer.Ordinal))
        {
            if (remaining <= 0m)
            {
                break;
            }

            covered.Add(c.InvoiceId);
            remaining -= c.Open;
        }

        return covered;
    }
}
