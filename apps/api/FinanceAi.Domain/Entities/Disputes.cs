using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>Doc 02 §4.1.</summary>
public enum DisputeStatus { Open, UnderReview, PendingCustomer, Accepted, PartiallyAccepted, Rejected, Withdrawn, Cancelled }

public enum DisputeEvent
{
    Assign,             // Open → UnderReview (first response, SM-48)
    Cancel,             // Open → Cancelled (raised in error)
    CustomerWithdrew,   // Open | UnderReview → Withdrawn
    RequestInfo,        // UnderReview → PendingCustomer (pauses the clock)
    InfoReceived,       // PendingCustomer → UnderReview
    Timeout,            // PendingCustomer → UnderReview (sweep)
    Accept,             // UnderReview → Accepted (credit note, SM-45)
    PartiallyAccept,    // UnderReview → PartiallyAccepted (credit note, SM-45)
    Reject,             // UnderReview → Rejected (reason)
}

/// <summary>SM-43: the closed set. AI may only ever emit one of these (doc 07 §4).</summary>
public static class DisputeReasons
{
    public static readonly IReadOnlyList<string> All =
    [
        "wrong_amount", "wrong_quantity", "price_mismatch", "goods_not_received", "goods_damaged", "service_not_delivered",
        "duplicate_invoice", "already_paid", "missing_po_reference", "wrong_tax_treatment", "wrong_entity_billed", "contract_terms", "other",
    ];

    public const string AlreadyPaid = "already_paid";
}

/// <summary>The dispute machine of doc 02 §4.2 as one table (T-10). Terminal states have no rows.</summary>
public static class DisputeMachine
{
    private static readonly IReadOnlyDictionary<(DisputeStatus, DisputeEvent), DisputeStatus> Table = new Dictionary<(DisputeStatus, DisputeEvent), DisputeStatus>
    {
        [(DisputeStatus.Open, DisputeEvent.Assign)] = DisputeStatus.UnderReview,
        [(DisputeStatus.Open, DisputeEvent.Cancel)] = DisputeStatus.Cancelled,
        [(DisputeStatus.Open, DisputeEvent.CustomerWithdrew)] = DisputeStatus.Withdrawn,
        [(DisputeStatus.UnderReview, DisputeEvent.RequestInfo)] = DisputeStatus.PendingCustomer,
        [(DisputeStatus.PendingCustomer, DisputeEvent.InfoReceived)] = DisputeStatus.UnderReview,
        [(DisputeStatus.PendingCustomer, DisputeEvent.Timeout)] = DisputeStatus.UnderReview,
        [(DisputeStatus.UnderReview, DisputeEvent.Accept)] = DisputeStatus.Accepted,
        [(DisputeStatus.UnderReview, DisputeEvent.PartiallyAccept)] = DisputeStatus.PartiallyAccepted,
        [(DisputeStatus.UnderReview, DisputeEvent.Reject)] = DisputeStatus.Rejected,
        [(DisputeStatus.UnderReview, DisputeEvent.CustomerWithdrew)] = DisputeStatus.Withdrawn,
    };

    public static IReadOnlyDictionary<(DisputeStatus From, DisputeEvent Event), DisputeStatus> Transitions => Table;

    public static bool IsOpen(DisputeStatus s) => s is DisputeStatus.Open or DisputeStatus.UnderReview or DisputeStatus.PendingCustomer;

    public static bool IsTerminal(DisputeStatus s) => !IsOpen(s);

    public static DisputeStatus? Peek(DisputeStatus from, DisputeEvent @event) => Table.TryGetValue((from, @event), out var to) ? to : null;

    public static DisputeStatus Next(DisputeStatus from, DisputeEvent @event) =>
        Peek(from, @event) ?? throw new InvalidTransitionException("dispute", from.ToString(), @event.ToString());

    /// <summary>Doc 05: `disputes.write` events by name; the three resolutions go through <c>/resolve</c> with <c>disputes.resolve</c>.</summary>
    public static readonly IReadOnlyDictionary<string, DisputeEvent> UpdateEvents = new Dictionary<string, DisputeEvent>(StringComparer.Ordinal)
    {
        ["assign"] = DisputeEvent.Assign,
        ["request_info"] = DisputeEvent.RequestInfo,
        ["info_received"] = DisputeEvent.InfoReceived,
        ["withdraw"] = DisputeEvent.CustomerWithdrew,
        ["cancel"] = DisputeEvent.Cancel,
    };

    public static readonly IReadOnlyDictionary<string, DisputeEvent> ResolutionOutcomes = new Dictionary<string, DisputeEvent>(StringComparer.Ordinal)
    {
        ["accepted"] = DisputeEvent.Accept,
        ["partially_accepted"] = DisputeEvent.PartiallyAccept,
        ["rejected"] = DisputeEvent.Reject,
    };

    public static bool IsResolution(DisputeEvent e) => e is DisputeEvent.Accept or DisputeEvent.PartiallyAccept or DisputeEvent.Reject;

    public static string EventName(DisputeEvent e) => e switch
    {
        DisputeEvent.Assign => "assign",
        DisputeEvent.Cancel => "cancel",
        DisputeEvent.CustomerWithdrew => "customer_withdrew",
        DisputeEvent.RequestInfo => "request_info",
        DisputeEvent.InfoReceived => "info_received",
        DisputeEvent.Timeout => "timeout",
        DisputeEvent.Accept => "accept",
        DisputeEvent.PartiallyAccept => "partially_accept",
        DisputeEvent.Reject => "reject",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}

/// <summary>SM-48, slice 7 D-1: business days; constants until settings grow columns for them.</summary>
public static class DisputeSla
{
    public const int FirstResponseBusinessDays = 2;
    public const int ResolutionBusinessDays = 10;
    public const int PendingCustomerTimeoutBusinessDays = 5;

    /// <summary>The due moment is the end of the business day (18:00 tenant time) so a same-day response is on time.</summary>
    public static DateTimeOffset DueAt(DateOnly raisedOn, int businessDays, IReadOnlySet<DateOnly> holidays, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var day = BusinessDays.Add(raisedOn, businessDays, holidays);
        var local = day.ToDateTime(new TimeOnly(18, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

/// <summary>SM-45 / SM-46: what a resolution may carry.</summary>
public static class DisputeRules
{
    /// <summary>Returns a rule code, or null when the amount is acceptable. Never rounds: every figure is a stored decimal.</summary>
    public static string? CheckResolutionAmount(DisputeEvent outcome, decimal? amount, decimal disputed, decimal openBalance) => outcome switch
    {
        DisputeEvent.Reject => amount is null ? null : "amount_not_allowed",
        DisputeEvent.Accept => disputed > openBalance ? "exceeds_open_balance" : null,                       // D-6: accept = the claim as made
        DisputeEvent.PartiallyAccept when amount is null || amount <= 0m => "amount_required",
        DisputeEvent.PartiallyAccept when amount >= disputed => "partial_must_be_below_disputed",
        DisputeEvent.PartiallyAccept when amount > openBalance => "exceeds_open_balance",
        DisputeEvent.PartiallyAccept => null,
        _ => "not_a_resolution",
    };

    /// <summary>FIN-56: the disputed figure shown in aging never exceeds what is still owed on the invoice.</summary>
    public static decimal DisputedForAging(decimal disputed, decimal openBalance) => Math.Min(disputed, openBalance);
}

public sealed class Dispute : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? CaseId { get; set; }
    public DisputeStatus Status { get; set; } = DisputeStatus.Open;
    public required string ReasonCode { get; set; }
    public decimal DisputedAmount { get; set; }
    public required string Currency { get; set; }
    public string? CustomerClaim { get; set; }
    public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? RaisedBy { get; set; }
    public string Source { get; set; } = "user";
    public Guid? AiSuggestionId { get; set; }
    public Guid? AssignedTo { get; set; }
    public DateTimeOffset FirstResponseDueAt { get; set; }
    public DateTimeOffset? FirstResponseAt { get; set; }
    public DateTimeOffset ResolutionDueAt { get; set; }
    public DateTimeOffset? PendingSince { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public decimal? ResolutionAmount { get; set; }
    public string? ResolutionNote { get; set; }
    public Guid? CreditNoteId { get; set; }
    public string? CloseReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;

    /// <summary>SM-48: breached when the first response is late, or the (unpaused) resolution clock has run out.</summary>
    public bool IsSlaBreached(DateTimeOffset now) =>
        DisputeMachine.IsOpen(this.Status)
        && ((this.FirstResponseAt is null && now > this.FirstResponseDueAt)
            || (this.PendingSince is null && now > this.ResolutionDueAt));
}

public sealed class DisputeEvidence : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid DisputeId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public int SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required byte[] Content { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum VerificationOutcome { PaymentFound, NoPaymentFound, Partial }

/// <summary>SM-44: "the customer says they paid" is a task to find the payment — never a paid mark (SM-10).</summary>
public sealed class PaymentVerificationTask : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? DisputeId { get; set; }
    public string Source { get; set; } = "dispute";
    public Guid? AiSuggestionId { get; set; }
    public string Status { get; set; } = "Open";
    public string? Claim { get; set; }
    public string? Outcome { get; set; }
    public Guid? PaymentId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
}

/// <summary>SEC-44: the evidence allowlist, decided by magic bytes, never by the name the client sent.</summary>
public static class EvidenceInspector
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public static string? SniffContentType(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 5 && content[0] == (byte)'%' && content[1] == (byte)'P' && content[2] == (byte)'D' && content[3] == (byte)'F' && content[4] == (byte)'-') return "application/pdf";
        if (content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A) return "image/png";
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return "image/jpeg";
        return null;
    }

    /// <summary>The extension must agree with the bytes; a renamed file is refused whatever it claims.</summary>
    public static bool ExtensionMatches(string fileName, string contentType)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return contentType switch
        {
            "application/pdf" => ext == ".pdf",
            "image/png" => ext == ".png",
            "image/jpeg" => ext is ".jpg" or ".jpeg",
            _ => false,
        };
    }

    public static string SafeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var cleaned = new string(name.Where(ch => !char.IsControl(ch) && ch is not ('/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')).ToArray()).Trim();
        return cleaned.Length == 0 ? "evidence" : cleaned.Length > 200 ? cleaned[^200..] : cleaned;
    }
}
