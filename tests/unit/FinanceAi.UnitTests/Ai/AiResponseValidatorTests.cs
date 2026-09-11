using System.Text.Json;
using FinanceAi.Domain.Ai;
using FinanceAi.TestSupport;

namespace FinanceAi.UnitTests.Ai;

/// <summary>Slice 9 AC-05 / AI-04 / AI-110: the backend's validator is strict and pinned to the schema file the service uses.</summary>
public sealed class AiResponseValidatorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Accepts_TheServiceShape()
    {
        var ok = AiResponseValidator.TryParse(Parse(AiScript.Response("promise_to_pay", 0.92m, "explicit_future_date_commitment", amountText: "1500", amountNumeric: "1500.000", currency: "JOD", dateIso: "2026-09-18", dateRelative: false, invoiceNumbers: ["INV-1"])), out var r, out var errors);
        Assert.True(ok, string.Join("; ", errors));
        Assert.Equal("promise_to_pay", r!.Classification);
        Assert.Equal(0.92m, r.Confidence);
        Assert.Equal("1500.000", r.Extracted.MentionedAmountNumeric);
        Assert.Equal(["INV-1"], r.Extracted.ReferencedInvoiceNumbers);
        Assert.Equal("classify_customer_reply.v1", r.Model.PromptVersion);
    }

    [Theory]
    [InlineData("classification", "\"invoice_paid\"", "classification: enum")]
    [InlineData("classification", "\"mark_paid\"", "classification: enum")]
    [InlineData("confidence", "1.2", "confidence: range")]
    [InlineData("confidence", "\"high\"", "confidence: type")]
    [InlineData("reason_code", "\"because\"", "reason_code: enum")]
    [InlineData("schema_version", "\"classify_customer_reply.v2\"", "schema_version: const")]
    [InlineData("detected_language", "\"fr\"", "detected_language: enum")]
    [InlineData("rationale", "\"" + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" + "\"", "rationale: maxLength")]
    public void Rejects_ValuesOutsideTheSchema(string field, string value, string expectedError)
    {
        var json = Replace(AiScript.Response("acknowledgement", 0.9m), field, value);
        Assert.False(AiResponseValidator.TryParse(Parse(json), out _, out var errors));
        Assert.Contains(expectedError, errors);
    }

    [Fact]
    public void Rejects_ExtraProperties_MissingModel_AndMalformedAmounts()
    {
        var extra = AiScript.Response("acknowledgement", 0.9m).Replace("\"schema_version\"", "\"action\":\"mark_paid\",\"schema_version\"", StringComparison.Ordinal);
        Assert.False(AiResponseValidator.TryParse(Parse(extra), out _, out var e1));
        Assert.Contains("action: additional property", e1);

        using var doc = JsonDocument.Parse(AiScript.Response("acknowledgement", 0.9m));
        var withoutModel = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject()) if (p.Name != "model") withoutModel[p.Name] = p.Value;
        Assert.False(AiResponseValidator.TryParse(Parse(JsonSerializer.Serialize(withoutModel)), out _, out var e2));
        Assert.Contains("model: required", e2);

        Assert.False(AiResponseValidator.TryParse(Parse(AiScript.Response("promise_to_pay", 0.9m, amountNumeric: "1500.00")), out _, out var e3));
        Assert.Contains("extracted/mentioned_amount_numeric: pattern", e3);
        Assert.False(AiResponseValidator.TryParse(Parse(AiScript.Response("promise_to_pay", 0.9m, amountNumeric: "1,500.000")), out _, out var e4));
        Assert.Contains("extracted/mentioned_amount_numeric: pattern", e4);
        Assert.False(AiResponseValidator.TryParse(Parse(AiScript.Response("promise_to_pay", 0.9m, currency: "jod")), out _, out var e5));
        Assert.Contains("extracted/mentioned_currency: pattern", e5);
        Assert.False(AiResponseValidator.TryParse(Parse(AiScript.Response("promise_to_pay", 0.9m, dateIso: "18/09/2026")), out _, out var e6));
        Assert.Contains("extracted/mentioned_date_iso: format", e6);
        Assert.False(AiResponseValidator.TryParse(Parse("[]"), out _, out var e7));
        Assert.Contains("(root): not an object", e7);
        Assert.False(AiResponseValidator.TryParse(Parse(AiScript.Response("acknowledgement", 0.9m, secondary: [("dispute_raised", 0.4m), ("promise_to_pay", 0.3m), ("unrelated", 0.1m)])), out _, out var e8));
        Assert.Contains("secondary_classifications: maxItems", e8);
    }

    /// <summary>AI-110: the two validators cannot drift — the C# enums are the schema file's enums.</summary>
    [Fact]
    public void Enums_MatchTheSchemaFile()
    {
        var path = Path.Combine(RepositoryPaths.Root, "services", "ai", "schemas", "classify_customer_reply.response.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var props = doc.RootElement.GetProperty("properties");
        Assert.Equal(Strings(props.GetProperty("classification").GetProperty("enum")), AiClassifications.All);
        Assert.Equal(Strings(props.GetProperty("reason_code").GetProperty("enum")), AiReasonCodes.All);
        Assert.Equal(Strings(props.GetProperty("detected_language").GetProperty("enum")), AiVocabularies.Languages);
        Assert.Equal(Strings(props.GetProperty("sentiment").GetProperty("enum")), AiVocabularies.Sentiments);
        var methods = Strings(props.GetProperty("extracted").GetProperty("properties").GetProperty("payment_method_mentioned").GetProperty("enum"));
        Assert.Equal(methods, AiVocabularies.PaymentMethods);
        Assert.Equal(AiVocabularies.SchemaVersion, props.GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Equal(300, props.GetProperty("rationale").GetProperty("maxLength").GetInt32());
    }

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();

    private static string Replace(string json, string field, string value)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object?>();
        foreach (var p in doc.RootElement.EnumerateObject()) dict[p.Name] = p.Name == field ? JsonDocument.Parse(value).RootElement.Clone() : p.Value.Clone();
        return JsonSerializer.Serialize(dict);
    }
}
