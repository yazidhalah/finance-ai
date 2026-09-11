using System.Globalization;
using System.Text.Json;

namespace FinanceAi.Domain.Ai;

/// <summary>The closed vocabularies of doc 07 §4.2, byte-for-byte the response schema's enums (AI-02).</summary>
public static class AiClassifications
{
    public const string PaymentClaimed = "payment_claimed";
    public const string PromiseToPay = "promise_to_pay";
    public const string PartialPaymentOffer = "partial_payment_offer";
    public const string PaymentPlanRequest = "payment_plan_request";
    public const string DisputeRaised = "dispute_raised";
    public const string InvoiceNotReceived = "invoice_not_received";
    public const string InformationRequest = "information_request";
    public const string WrongRecipient = "wrong_recipient";
    public const string OutOfOffice = "out_of_office";
    public const string Acknowledgement = "acknowledgement";
    public const string RefusalToPay = "refusal_to_pay";
    public const string HardshipOrDelayNotice = "hardship_or_delay_notice";
    public const string ComplaintOrEscalation = "complaint_or_escalation";
    public const string Unrelated = "unrelated";
    public const string Unclassified = "unclassified";

    public static readonly IReadOnlyList<string> All =
    [
        PaymentClaimed, PromiseToPay, PartialPaymentOffer, PaymentPlanRequest, DisputeRaised, InvoiceNotReceived, InformationRequest,
        WrongRecipient, OutOfOffice, Acknowledgement, RefusalToPay, HardshipOrDelayNotice, ComplaintOrEscalation, Unrelated, Unclassified,
    ];

    /// <summary>AI-40: these always need a human, whatever the confidence.</summary>
    public static bool AlwaysReviewed(string c) => c is PaymentClaimed or DisputeRaised or RefusalToPay or ComplaintOrEscalation;
}

public static class AiReasonCodes
{
    public static readonly IReadOnlyList<string> All =
    [
        "explicit_payment_statement", "explicit_future_date_commitment", "explicit_amount_offer", "installment_request_language",
        "explicit_disagreement_with_amount", "explicit_goods_or_service_issue", "claims_never_received_invoice", "asks_for_document_or_detail",
        "states_not_the_right_contact", "automated_absence_reply", "acknowledges_without_commitment", "explicit_refusal", "cites_financial_difficulty",
        "expresses_dissatisfaction", "no_collections_content", "ambiguous_or_conflicting_signals", "below_confidence_threshold",
        "text_too_short_to_classify", "language_not_supported",
    ];

    public const string BelowConfidenceThreshold = "below_confidence_threshold";
}

public static class AiVocabularies
{
    public static readonly IReadOnlyList<string> Languages = ["ar", "en", "ar_latin", "mixed", "other"];
    public static readonly IReadOnlyList<string> Sentiments = ["cooperative", "neutral", "frustrated", "hostile"];
    public static readonly IReadOnlyList<string> PaymentMethods = ["bank_transfer", "cheque", "cash", "cliq", "card", "other"];
    public const string SchemaVersion = "classify_customer_reply.v1";
}

public sealed record ExtractedFields(
    string? MentionedAmountText,
    string? MentionedAmountNumeric,
    string? MentionedCurrency,
    string? MentionedDateText,
    string? MentionedDateIso,
    bool? DateIsRelative,
    IReadOnlyList<string> ReferencedInvoiceNumbers,
    string? PaymentMethodMentioned,
    string? PaymentReferenceText)
{
    public static readonly ExtractedFields Empty = new(null, null, null, null, null, null, [], null, null);
}

public sealed record SecondaryClassification(string Classification, decimal Confidence);

public sealed record AiModelInfo(string Name, string Digest, string PromptVersion, int? LatencyMs);

/// <summary>A validated <c>classify_customer_reply.v1</c> response. Only ever constructed by <see cref="AiResponseValidator"/>.</summary>
public sealed record ClassifyResponse(
    string Classification,
    decimal Confidence,
    string ReasonCode,
    string DetectedLanguage,
    ExtractedFields Extracted,
    IReadOnlyList<SecondaryClassification> Secondary,
    string? Sentiment,
    bool RequiresHumanReview,
    bool ContainsSuspiciousInstructions,
    string? Rationale,
    AiModelInfo Model);

/// <summary>
/// The backend's own strict validator (AI-04, AI-110): the service validated already, and this does it again with no
/// shared code, so a drift or a compromised service still cannot hand the backend an off-schema object. Every
/// check is a rule of <c>services/ai/schemas/classify_customer_reply.response.v1.json</c>; a unit test pins the
/// enums against that file.
/// </summary>
public static class AiResponseValidator
{
    private static readonly HashSet<string> RootKeys =
    [
        "schema_version", "classification", "confidence", "reason_code", "detected_language", "extracted", "secondary_classifications",
        "sentiment", "requires_human_review", "contains_suspicious_instructions", "rationale", "model",
    ];

    private static readonly HashSet<string> ExtractedKeys =
    [
        "mentioned_amount_text", "mentioned_amount_numeric", "mentioned_currency", "mentioned_date_text", "mentioned_date_iso",
        "date_is_relative", "referenced_invoice_numbers", "payment_method_mentioned", "payment_reference_text",
    ];

    public static bool TryParse(JsonElement root, out ClassifyResponse? response, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        response = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors = ["(root): not an object"];
            return false;
        }

        foreach (var p in root.EnumerateObject())
        {
            if (!RootKeys.Contains(p.Name)) errs.Add($"{p.Name}: additional property");
        }

        var schemaVersion = Str(root, "schema_version", errs, required: true);
        if (schemaVersion is not null && schemaVersion != AiVocabularies.SchemaVersion) errs.Add("schema_version: const");
        var classification = Enum(root, "classification", AiClassifications.All, errs, required: true);
        var confidence = Number(root, "confidence", 0m, 1m, errs, required: true);
        var reason = Enum(root, "reason_code", AiReasonCodes.All, errs, required: true);
        var language = Enum(root, "detected_language", AiVocabularies.Languages, errs, required: true);
        var sentiment = Enum(root, "sentiment", AiVocabularies.Sentiments, errs, required: false);
        var review = Bool(root, "requires_human_review", errs, required: false);
        var suspicious = Bool(root, "contains_suspicious_instructions", errs, required: true);
        var rationale = Str(root, "rationale", errs, required: false, maxLength: 300);

        var extracted = ExtractedFields.Empty;
        if (root.TryGetProperty("extracted", out var ex))
        {
            if (ex.ValueKind != JsonValueKind.Object)
            {
                errs.Add("extracted: type");
            }
            else
            {
                foreach (var p in ex.EnumerateObject())
                {
                    if (!ExtractedKeys.Contains(p.Name)) errs.Add($"extracted/{p.Name}: additional property");
                }

                var numeric = NullableStr(ex, "mentioned_amount_numeric", errs, 64);
                if (numeric is not null && !IsThreeDecimalAmount(numeric)) errs.Add("extracted/mentioned_amount_numeric: pattern");
                var currency = NullableStr(ex, "mentioned_currency", errs, 3);
                if (currency is not null && !(currency.Length == 3 && currency.All(ch => ch is >= 'A' and <= 'Z'))) errs.Add("extracted/mentioned_currency: pattern");
                var iso = NullableStr(ex, "mentioned_date_iso", errs, 10);
                if (iso is not null && !DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errs.Add("extracted/mentioned_date_iso: format");
                var method = NullableStr(ex, "payment_method_mentioned", errs, 32);
                if (method is not null && !AiVocabularies.PaymentMethods.Contains(method, StringComparer.Ordinal)) errs.Add("extracted/payment_method_mentioned: enum");
                bool? relative = null;
                if (ex.TryGetProperty("date_is_relative", out var rel))
                {
                    if (rel.ValueKind == JsonValueKind.True) relative = true;
                    else if (rel.ValueKind == JsonValueKind.False) relative = false;
                    else if (rel.ValueKind != JsonValueKind.Null) errs.Add("extracted/date_is_relative: type");
                }

                var numbers = new List<string>();
                if (ex.TryGetProperty("referenced_invoice_numbers", out var refs))
                {
                    if (refs.ValueKind != JsonValueKind.Array) errs.Add("extracted/referenced_invoice_numbers: type");
                    else
                    {
                        foreach (var item in refs.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 64) errs.Add("extracted/referenced_invoice_numbers: items");
                            else numbers.Add(item.GetString()!);
                        }

                        if (numbers.Count > 20) errs.Add("extracted/referenced_invoice_numbers: maxItems");
                    }
                }

                extracted = new ExtractedFields(
                    NullableStr(ex, "mentioned_amount_text", errs, 64), numeric, currency, NullableStr(ex, "mentioned_date_text", errs, 64), iso, relative,
                    numbers, method, NullableStr(ex, "payment_reference_text", errs, 128));
            }
        }

        var secondary = new List<SecondaryClassification>();
        if (root.TryGetProperty("secondary_classifications", out var sec))
        {
            if (sec.ValueKind != JsonValueKind.Array) errs.Add("secondary_classifications: type");
            else
            {
                foreach (var item in sec.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) { errs.Add("secondary_classifications: items"); continue; }
                    foreach (var p in item.EnumerateObject())
                    {
                        if (p.Name is not ("classification" or "confidence")) errs.Add($"secondary_classifications/{p.Name}: additional property");
                    }

                    var c = Enum(item, "classification", AiClassifications.All, errs, required: true, prefix: "secondary_classifications/");
                    var conf = Number(item, "confidence", 0m, 1m, errs, required: true, prefix: "secondary_classifications/");
                    if (c is not null && conf is not null) secondary.Add(new SecondaryClassification(c, conf.Value));
                }

                if (secondary.Count > 2) errs.Add("secondary_classifications: maxItems");
            }
        }

        AiModelInfo? model = null;
        if (!root.TryGetProperty("model", out var m)) errs.Add("model: required");
        else if (m.ValueKind != JsonValueKind.Object) errs.Add("model: type");
        else
        {
            foreach (var p in m.EnumerateObject())
            {
                if (p.Name is not ("name" or "digest" or "prompt_version" or "latency_ms")) errs.Add($"model/{p.Name}: additional property");
            }

            var name = Str(m, "name", errs, required: true, prefix: "model/");
            var digest = Str(m, "digest", errs, required: true, prefix: "model/");
            var promptVersion = Str(m, "prompt_version", errs, required: true, prefix: "model/");
            int? latency = null;
            if (m.TryGetProperty("latency_ms", out var lat))
            {
                if (lat.ValueKind == JsonValueKind.Number && lat.TryGetInt32(out var l)) latency = l;
                else errs.Add("model/latency_ms: type");
            }

            if (name is not null && digest is not null && promptVersion is not null) model = new AiModelInfo(name, digest, promptVersion, latency);
        }

        errors = errs;
        if (errs.Count > 0) return false;
        response = new ClassifyResponse(classification!, confidence!.Value, reason!, language!, extracted, secondary, sentiment, review ?? false, suspicious!.Value, rationale, model!);
        return true;
    }

    /// <summary>FIN-02: an amount the model wrote must look exactly like money as the system writes it.</summary>
    public static bool IsThreeDecimalAmount(string s)
    {
        var dot = s.IndexOf('.', StringComparison.Ordinal);
        if (dot < 1 || dot > 16 || s.Length - dot - 1 != 3) return false;
        return s.All(ch => ch == '.' || char.IsAsciiDigit(ch)) && s.Count(ch => ch == '.') == 1;
    }

    private static string? Str(JsonElement o, string name, List<string> errs, bool required, int? maxLength = null, string prefix = "")
    {
        if (!o.TryGetProperty(name, out var v))
        {
            if (required) errs.Add($"{prefix}{name}: required");
            return null;
        }

        if (v.ValueKind != JsonValueKind.String) { errs.Add($"{prefix}{name}: type"); return null; }
        var s = v.GetString()!;
        if (maxLength is { } max && s.Length > max) errs.Add($"{prefix}{name}: maxLength");
        return s;
    }

    private static string? NullableStr(JsonElement o, string name, List<string> errs, int maxLength)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) { errs.Add($"extracted/{name}: type"); return null; }
        var s = v.GetString()!;
        if (s.Length > maxLength) errs.Add($"extracted/{name}: maxLength");
        return s;
    }

    private static string? Enum(JsonElement o, string name, IReadOnlyList<string> allowed, List<string> errs, bool required, string prefix = "")
    {
        var s = Str(o, name, errs, required, null, prefix);
        if (s is not null && !allowed.Contains(s, StringComparer.Ordinal)) { errs.Add($"{prefix}{name}: enum"); return null; }
        return s;
    }

    private static decimal? Number(JsonElement o, string name, decimal min, decimal max, List<string> errs, bool required, string prefix = "")
    {
        if (!o.TryGetProperty(name, out var v))
        {
            if (required) errs.Add($"{prefix}{name}: required");
            return null;
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var d)) { errs.Add($"{prefix}{name}: type"); return null; }
        if (d < min || d > max) { errs.Add($"{prefix}{name}: range"); return null; }
        return d;
    }

    private static bool? Bool(JsonElement o, string name, List<string> errs, bool required)
    {
        if (!o.TryGetProperty(name, out var v))
        {
            if (required) errs.Add($"{name}: required");
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Err(errs, $"{name}: type"),
        };
    }

    private static bool? Err(List<string> errs, string message)
    {
        errs.Add(message);
        return null;
    }
}
