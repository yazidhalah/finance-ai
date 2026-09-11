using System.Globalization;
using FinanceAi.Domain.Entities;

namespace FinanceAi.Domain.Ai;

/// <summary>An invoice the case has in scope, as the policy sees it. Money is the stored decimal, never the model's.</summary>
public sealed record ScopedInvoice(Guid InvoiceId, string InvoiceNumber, string Currency, decimal OpenBalance);

/// <summary>
/// What the backend will do with a validated classification. Pure: the decision is a function of the response,
/// the scope and today — so the safety table (doc 07 §4.3) is unit-testable without a database or a model.
/// </summary>
public sealed record AiDecision(
    string OutcomeType,
    string? GuardReason,
    Guid? InvoiceId = null,
    IReadOnlyList<Guid>? InvoiceIds = null,
    decimal? Amount = null,
    string? Currency = null,
    DateOnly? PromisedDate = null,
    string? DisputeReasonCode = null)
{
    public static AiDecision None(string? guard = null) => new(AiOutcomeTypes.None, guard);

    public static AiDecision Activity() => new(AiOutcomeTypes.Activity, null);
}

public static class AiPolicy
{
    public const string GuardAmountMissing = "amount_missing";
    public const string GuardAmountInvalid = "amount_invalid";
    public const string GuardAmountExceedsBalance = "amount_exceeds_covered_balance";
    public const string GuardCurrencyMismatch = "currency_mismatch";
    public const string GuardDateMissing = "date_missing";
    public const string GuardDateRelative = "date_relative";
    public const string GuardDateInvalid = "date_invalid";
    public const string GuardDatePast = "date_in_past";
    public const string GuardInvoiceAmbiguous = "invoice_ambiguous";
    public const string GuardInvoiceUnknown = "invoice_not_in_scope";
    public const string GuardNoScope = "no_invoice_in_scope";
    public const string GuardNoCase = "no_open_case";

    /// <summary>
    /// AI-41: the model's number must parse as decimal, be &gt; 0, be ≤ the covered balance and match the scope's
    /// currency; failing any of these it is dropped and a human types it. The value that comes back is the
    /// parsed decimal of the customer's own words — never something the model computed.
    /// </summary>
    public static (decimal? Amount, string? Guard) ValidateAmount(string? numeric, string? mentionedCurrency, decimal covered, string scopeCurrency)
    {
        if (string.IsNullOrEmpty(numeric)) return (null, GuardAmountMissing);
        if (!AiResponseValidator.IsThreeDecimalAmount(numeric) || !decimal.TryParse(numeric, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)) return (null, GuardAmountInvalid);
        if (amount <= 0m) return (null, GuardAmountInvalid);
        if (mentionedCurrency is not null && !string.Equals(mentionedCurrency, scopeCurrency, StringComparison.Ordinal)) return (null, GuardCurrencyMismatch);
        if (amount > covered) return (null, GuardAmountExceedsBalance);
        return (amount, null);
    }

    /// <summary>AI-51/52: a relative date ("next week", "بعد العيد") is a strong signal the human must set it; a past date is a capture error.</summary>
    public static (DateOnly? Date, string? Guard) ValidateDate(string? iso, bool? relative, DateOnly today)
    {
        if (relative == true) return (null, GuardDateRelative);
        if (string.IsNullOrEmpty(iso)) return (null, GuardDateMissing);
        if (!DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return (null, GuardDateInvalid);
        if (date < today) return (null, GuardDatePast);
        return (date, null);
    }

    /// <summary>
    /// Which invoices the customer's words point at. Numbers the model lists are matched against the scope only;
    /// a number that is not in scope makes the reference unusable rather than "close enough".
    /// </summary>
    public static (IReadOnlyList<ScopedInvoice> Invoices, string? Guard) ResolveInvoices(IReadOnlyList<string> referenced, IReadOnlyList<ScopedInvoice> scope)
    {
        if (scope.Count == 0) return ([], GuardNoScope);
        if (referenced.Count == 0) return (scope, null);
        var matched = new List<ScopedInvoice>();
        foreach (var number in referenced.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hit = scope.FirstOrDefault(s => string.Equals(s.InvoiceNumber, number, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return ([], GuardInvoiceUnknown);
            matched.Add(hit);
        }

        return matched.Count == 0 ? (scope, null) : (matched, null);
    }

    /// <summary>SM-43 from the model's closed reason set. Anything the set does not say precisely is <c>other</c> for the human to refine.</summary>
    public static string DisputeReasonFor(string aiReasonCode) => aiReasonCode switch
    {
        "explicit_disagreement_with_amount" => "wrong_amount",
        _ => "other",
    };

    /// <summary>The safety table. Every branch returns something a human must still act on.</summary>
    public static AiDecision Decide(ClassifyResponse r, IReadOnlyList<ScopedInvoice> scope, bool hasOpenCase, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(scope);
        switch (r.Classification)
        {
            case AiClassifications.PaymentClaimed:
                {
                    // SM-44: a task to find the payment. Never a paid mark, never a payment row.
                    var (invoices, guard) = ResolveInvoices(r.Extracted.ReferencedInvoiceNumbers, scope);
                    if (guard is not null) return AiDecision.None(guard);
                    return new AiDecision(AiOutcomeTypes.VerificationTask, invoices.Count == 1 ? null : GuardInvoiceAmbiguous, InvoiceId: invoices.Count == 1 ? invoices[0].InvoiceId : null, InvoiceIds: invoices.Select(i => i.InvoiceId).ToList());
                }

            case AiClassifications.PromiseToPay:
                {
                    if (!hasOpenCase) return AiDecision.None(GuardNoCase);
                    var (invoices, guard) = ResolveInvoices(r.Extracted.ReferencedInvoiceNumbers, scope);
                    if (guard is not null) return AiDecision.None(guard);
                    if (invoices.Select(i => i.Currency).Distinct().Count() > 1) return AiDecision.None(GuardCurrencyMismatch);
                    var currency = invoices[0].Currency;
                    var covered = invoices.Sum(i => i.OpenBalance);
                    var (amount, amountGuard) = ValidateAmount(r.Extracted.MentionedAmountNumeric, r.Extracted.MentionedCurrency, covered, currency);
                    var (date, dateGuard) = ValidateDate(r.Extracted.MentionedDateIso, r.Extracted.DateIsRelative, today);
                    if (amountGuard is not null || dateGuard is not null) return AiDecision.None(dateGuard ?? amountGuard);
                    // SM-31: Proposed only. Active needs a human's confirmedBy (slice 6 CHECK).
                    return new AiDecision(AiOutcomeTypes.PromiseProposed, null, InvoiceIds: invoices.Select(i => i.InvoiceId).ToList(), Amount: amount, Currency: currency, PromisedDate: date);
                }

            case AiClassifications.DisputeRaised:
                {
                    var (invoices, guard) = ResolveInvoices(r.Extracted.ReferencedInvoiceNumbers, scope);
                    if (guard is not null) return AiDecision.None(guard);
                    if (invoices.Count != 1) return AiDecision.None(GuardInvoiceAmbiguous);
                    // SM-49 / the safety table: disputed_amount is the stored open balance, never the model's figure.
                    return new AiDecision(AiOutcomeTypes.DisputeOpen, null, InvoiceId: invoices[0].InvoiceId, Amount: invoices[0].OpenBalance, Currency: invoices[0].Currency, DisputeReasonCode: DisputeReasonFor(r.ReasonCode));
                }

            case AiClassifications.Unclassified:
                return AiDecision.None();

            default:
                // partial offer, plan request, not received, information, wrong recipient, out of office, acknowledgement,
                // refusal, hardship, complaint, unrelated: timeline + a suggestion for a human. No priority change, no hold,
                // no escalation, no send (doc 07 §4.3 "MUST NOT" column).
                return AiDecision.Activity();
        }
    }
}
