using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

public static class NarrativeStatus
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
    public const string RejectedByGuard = "rejected_by_guard";
    public const string SchemaInvalid = "schema_invalid";
    public const string Disabled = "disabled";
}

public static class BriefingDeliveryStatus
{
    public const string Sent = "sent";
    public const string NotEnabled = "not_enabled";
    public const string NoRecipients = "no_recipients";
    public const string TemplateNotApproved = "template_not_approved";
    public const string OutboundDisabled = "outbound_disabled";
    public const string Failed = "failed";
}

/// <summary>Money inside the metrics document: the F3 string and the code, exactly as the API writes money everywhere (FIN-02).</summary>
public sealed record MetricMoney([property: JsonPropertyName("amount")] string Amount, [property: JsonPropertyName("currency")] string Currency)
{
    public static MetricMoney From(decimal amount, string currency) => new(amount.ToString("F3", CultureInfo.InvariantCulture), currency);
}

public sealed record MetricCountAmount([property: JsonPropertyName("count")] int Count, [property: JsonPropertyName("amount")] MetricMoney Amount);

public sealed record MetricCount([property: JsonPropertyName("count")] int Count);

public sealed record TopCaseMetric(
    [property: JsonPropertyName("caseId")] Guid CaseId,
    [property: JsonPropertyName("caseNumber")] long CaseNumber,
    [property: JsonPropertyName("customerName")] string CustomerName,
    [property: JsonPropertyName("amount")] MetricMoney Amount,
    [property: JsonPropertyName("daysPastDue")] int DaysPastDue,
    [property: JsonPropertyName("status")] string Status);

public sealed record CurrencyOverdue([property: JsonPropertyName("currency")] string Currency, [property: JsonPropertyName("amount")] string Amount, [property: JsonPropertyName("invoiceCount")] int InvoiceCount);

/// <summary>
/// The metrics document (doc 05 slice 10). Every value is computed in C# from stored decimals and serialized once;
/// this exact JSON is what is stored, what the API returns, and what the model is handed as strings (AI-80).
/// </summary>
public sealed record BriefingMetrics(
    [property: JsonPropertyName("totalOverdue")] MetricMoney TotalOverdue,
    [property: JsonPropertyName("overdueByCurrency")] IReadOnlyList<CurrencyOverdue> OverdueByCurrency,
    [property: JsonPropertyName("overdueChange")] MetricMoney? OverdueChange,
    [property: JsonPropertyName("collectedYesterday")] MetricMoney CollectedYesterday,
    [property: JsonPropertyName("promisesDueToday")] MetricCountAmount PromisesDueToday,
    [property: JsonPropertyName("promisesBrokenYesterday")] MetricCount PromisesBrokenYesterday,
    [property: JsonPropertyName("newDisputes")] MetricCount NewDisputes,
    [property: JsonPropertyName("disputesBreachingSla")] MetricCount DisputesBreachingSla,
    [property: JsonPropertyName("queueSize")] int QueueSize,
    [property: JsonPropertyName("unverifiedPaymentClaims")] MetricCount UnverifiedPaymentClaims,
    [property: JsonPropertyName("unmatchedReplies")] MetricCount UnmatchedReplies,
    [property: JsonPropertyName("repliesNeedingAHuman")] MetricCount RepliesNeedingAHuman,
    [property: JsonPropertyName("pendingAiSuggestions")] MetricCount PendingAiSuggestions,
    [property: JsonPropertyName("topCases")] IReadOnlyList<TopCaseMetric> TopCases)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static BriefingMetrics? Deserialize(string json) => JsonSerializer.Deserialize<BriefingMetrics>(json, Json);

    /// <summary>The numeric vocabulary of the briefing: everything a narrative may legitimately quote (AI-81). Keys name the metric a numeral belongs to and are the <c>numbers_used</c> vocabulary.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NumeralsByKey()
    {
        var d = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["totalOverdue"] = [this.TotalOverdue.Amount],
            ["collectedYesterday"] = [this.CollectedYesterday.Amount],
            ["promisesDueToday"] = [this.PromisesDueToday.Count.ToString(CultureInfo.InvariantCulture), this.PromisesDueToday.Amount.Amount],
            ["promisesBrokenYesterday"] = [this.PromisesBrokenYesterday.Count.ToString(CultureInfo.InvariantCulture)],
            ["newDisputes"] = [this.NewDisputes.Count.ToString(CultureInfo.InvariantCulture)],
            ["disputesBreachingSla"] = [this.DisputesBreachingSla.Count.ToString(CultureInfo.InvariantCulture)],
            ["queueSize"] = [this.QueueSize.ToString(CultureInfo.InvariantCulture)],
            ["unverifiedPaymentClaims"] = [this.UnverifiedPaymentClaims.Count.ToString(CultureInfo.InvariantCulture)],
            ["unmatchedReplies"] = [this.UnmatchedReplies.Count.ToString(CultureInfo.InvariantCulture)],
            ["repliesNeedingAHuman"] = [this.RepliesNeedingAHuman.Count.ToString(CultureInfo.InvariantCulture)],
            ["pendingAiSuggestions"] = [this.PendingAiSuggestions.Count.ToString(CultureInfo.InvariantCulture)],
            ["overdueByCurrency"] = this.OverdueByCurrency.SelectMany(c => new[] { c.Amount, c.InvoiceCount.ToString(CultureInfo.InvariantCulture) }).ToList(),
            ["topCases"] = this.TopCases.SelectMany(c => new[] { c.Amount.Amount, c.DaysPastDue.ToString(CultureInfo.InvariantCulture), c.CaseNumber.ToString(CultureInfo.InvariantCulture) }).ToList(),
        };
        if (this.OverdueChange is { } change) d["overdueChange"] = [change.Amount];
        return d;
    }
}

/// <summary>
/// AI-81. Every numeral in the prose must be one the briefing already contains. A number is "the same" when, after
/// normalising digits and separators, it equals a metric value, that value's integer part, or a component of the
/// briefing date. Nothing else — not a rounding, not a percentage, not a sum. Pure, so the table test is the spec.
/// </summary>
public static partial class NumericFidelityGuard
{
    public sealed record Result(bool Accepted, IReadOnlyList<string> Untraceable, IReadOnlyList<string> UnknownKeys);

    private static readonly IReadOnlyDictionary<char, char> DigitMap = BuildDigitMap();

    [GeneratedRegex(@"\d{4}[-/]\d{2}[-/]\d{2}|\d+(?:[.,٫٬]\d+)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Numeral();

    public static Result Check(string? narrative, IReadOnlyList<string> highlights, IReadOnlyList<string> numbersUsed, BriefingMetrics metrics, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        ArgumentNullException.ThrowIfNull(numbersUsed);
        ArgumentNullException.ThrowIfNull(metrics);
        var byKey = metrics.NumeralsByKey();
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in byKey.Values.SelectMany(v => v))
        {
            AddForms(allowed, value);
        }

        // The date as a whole and the year alone; a bare day or month would let any small number through.
        AddForms(allowed, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        allowed.Add(date.Year.ToString(CultureInfo.InvariantCulture));

        var untraceable = new List<string>();
        foreach (var text in highlights.Prepend(narrative ?? string.Empty))
        {
            foreach (Match m in Numeral().Matches(Normalise(text)))
            {
                var token = m.Value;
                if (!allowed.Contains(Canonical(token)) && !allowed.Contains(token))
                {
                    untraceable.Add(token);
                }
            }
        }

        var unknownKeys = numbersUsed.Where(k => !byKey.ContainsKey(k) && k != "date").Distinct(StringComparer.Ordinal).ToList();
        return new Result(untraceable.Count == 0 && unknownKeys.Count == 0, untraceable, unknownKeys);
    }

    /// <summary>Arabic-Indic and Eastern Arabic-Indic digits → ASCII; Arabic decimal/thousands separators → '.' / ','.</summary>
    public static string Normalise(string text)
    {
        var chars = (text ?? string.Empty).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (DigitMap.TryGetValue(chars[i], out var ascii)) chars[i] = ascii;
        }

        return new string(chars);
    }

    private static void AddForms(HashSet<string> set, string value)
    {
        value = value.TrimStart('-');   // a negative change is quoted as "1200" or "-1200"; the numeral regex sees digits only
        var canonical = Canonical(value);
        set.Add(canonical);
        set.Add(value);
        // "2026-09-11" as a whole and as a date the prose may write with slashes.
        if (value.Length == 10 && value[4] == '-' && value[7] == '-')
        {
            set.Add(value.Replace('-', '/'));
            return;
        }

        var dot = canonical.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            set.Add(canonical[..dot]);                                  // the integer part: "47350"
            var trimmed = canonical.TrimEnd('0').TrimEnd('.');
            set.Add(trimmed.Length == 0 ? "0" : trimmed);               // "47350.75", "1200"
        }
    }

    /// <summary>Strips thousands separators and unifies the decimal mark, so "47,350.750" and "47350.750" are one number.</summary>
    private static string Canonical(string token)
    {
        var t = token.Replace("٬", ",", StringComparison.Ordinal).Replace('٫', '.');
        // A token with both ',' and '.' uses ',' as thousands; a token with only ',' followed by exactly three digits is thousands too.
        if (t.Contains(',', StringComparison.Ordinal) && t.Contains('.', StringComparison.Ordinal)) t = t.Replace(",", string.Empty, StringComparison.Ordinal);
        else if (t.Contains(',', StringComparison.Ordinal))
        {
            var parts = t.Split(',');
            t = parts.Skip(1).All(p => p.Length == 3) ? string.Concat(parts) : t.Replace(',', '.');
        }

        // A date-like "2026-09-11" is handled by the caller; here only plain numbers arrive.
        if (t.Contains('.', StringComparison.Ordinal))
        {
            t = t.TrimEnd('0').TrimEnd('.');
            if (t.Length == 0) t = "0";
        }

        return t.TrimStart('0') is { Length: > 0 } s && s[0] != '.' ? s : t.Length > 0 && t.All(c => c == '0') ? "0" : t;
    }

    private static Dictionary<char, char> BuildDigitMap()
    {
        var map = new Dictionary<char, char>();
        for (var i = 0; i < 10; i++)
        {
            map[(char)('٠' + i)] = (char)('0' + i);   // ٠١٢٣٤٥٦٧٨٩
            map[(char)('۰' + i)] = (char)('0' + i);   // ۰۱۲۳۴۵۶۷۸۹
        }

        return map;
    }
}

public sealed class DailyBriefing : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public DateOnly BriefingDate { get; set; }
    public required string Language { get; set; }
    public required string MetricsJson { get; set; }
    public string? Narrative { get; set; }
    public string HighlightsJson { get; set; } = "[]";
    public string NarrativeStatusValue { get; set; } = NarrativeStatus.Unavailable;
    public Guid? AiSuggestionId { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? GeneratedBy { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int SentToCount { get; set; }
    public Guid? TemplateId { get; set; }
    public int? TemplateVersion { get; set; }
    public string? DeliveryStatus { get; set; }
    public long RowVersion { get; set; } = 1;
}

/// <summary>The briefing email's own closed placeholder set (slice 10 D-3). Money renders from the metrics document's strings; nothing is computed.</summary>
public static class BriefingPlaceholders
{
    public const string TemplateKey = "daily_briefing";

    public static readonly IReadOnlyList<Placeholders.Definition> All =
    [
        new("company_name", "text", "Your organization's name"),
        new("briefing_date", "date", "The briefing date"),
        new("total_overdue", "money", "Total overdue in the base currency (indicative)"),
        new("collected_yesterday", "money", "Payments received yesterday, base currency"),
        new("promises_due_today", "number", "Promises due today"),
        new("promises_due_amount", "money", "Amount promised for today"),
        new("promises_broken_yesterday", "number", "Promises evaluated as broken yesterday"),
        new("new_disputes", "number", "Disputes raised yesterday"),
        new("disputes_breaching_sla", "number", "Open disputes past their SLA"),
        new("queue_size", "number", "Cases in the collection queue"),
        new("unverified_payment_claims", "number", "Open payment-verification tasks"),
        new("replies_needing_a_human", "number", "Inbound replies waiting for a person"),
        new("top_cases", "text", "The top cases, one per line"),
        new("narrative", "text", "The AI summary, when one passed the guard; empty otherwise"),
    ];

    public static bool IsBriefingKey(string key) => string.Equals(key, TemplateKey, StringComparison.Ordinal);

    public static string? FirstUnknown(string text) => Placeholders.Used(text).FirstOrDefault(p => All.All(d => d.Name != p));

    public static string Render(string text, string companyName, BriefingMetrics m, DateOnly date, string? narrative)
    {
        ArgumentNullException.ThrowIfNull(m);
        return Placeholders.Token.Replace(text ?? string.Empty, match => match.Groups[1].Value switch
        {
            "company_name" => companyName,
            "briefing_date" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "total_overdue" => $"{m.TotalOverdue.Amount} {m.TotalOverdue.Currency}",
            "collected_yesterday" => $"{m.CollectedYesterday.Amount} {m.CollectedYesterday.Currency}",
            "promises_due_today" => m.PromisesDueToday.Count.ToString(CultureInfo.InvariantCulture),
            "promises_due_amount" => $"{m.PromisesDueToday.Amount.Amount} {m.PromisesDueToday.Amount.Currency}",
            "promises_broken_yesterday" => m.PromisesBrokenYesterday.Count.ToString(CultureInfo.InvariantCulture),
            "new_disputes" => m.NewDisputes.Count.ToString(CultureInfo.InvariantCulture),
            "disputes_breaching_sla" => m.DisputesBreachingSla.Count.ToString(CultureInfo.InvariantCulture),
            "queue_size" => m.QueueSize.ToString(CultureInfo.InvariantCulture),
            "unverified_payment_claims" => m.UnverifiedPaymentClaims.Count.ToString(CultureInfo.InvariantCulture),
            "replies_needing_a_human" => m.RepliesNeedingAHuman.Count.ToString(CultureInfo.InvariantCulture),
            "top_cases" => string.Join("\n", m.TopCases.Select(c => $"#{c.CaseNumber} {c.CustomerName}: {c.Amount.Amount} {c.Amount.Currency}, {c.DaysPastDue} days")),
            "narrative" => narrative ?? string.Empty,
            var other => throw new ArgumentException($"Unknown placeholder '{other}'."),
        });
    }
}
