using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Briefings;
using FinanceAi.TestSupport;

namespace FinanceAi.UnitTests.Briefings;

/// <summary>Slice 10 AC-03 / AI-81: the guard as a table. Anything not in the figures is a rejection.</summary>
public sealed class NumericFidelityGuardTests
{
    private static readonly DateOnly Date = new(2026, 9, 11);

    private static BriefingMetrics Metrics() => new(
        MetricMoney.From(47350.750m, "JOD"),
        [new CurrencyOverdue("JOD", "47350.750", 12), new CurrencyOverdue("USD", "300.000", 1)],
        MetricMoney.From(-1200m, "JOD"),
        MetricMoney.From(3200m, "JOD"),
        new MetricCountAmount(4, MetricMoney.From(11500m, "JOD")),
        new MetricCount(1), new MetricCount(2), new MetricCount(1), 41, new MetricCount(3), new MetricCount(0), new MetricCount(5), new MetricCount(2),
        [new TopCaseMetric(Guid.NewGuid(), 17, "Petra Supplies", MetricMoney.From(8200m, "JOD"), 62, "InProgress")]);

    [Theory]
    [InlineData("You have 4 promises due today worth 11,500.000 JOD.", true)]
    [InlineData("Overdue stands at 47350.75 JOD, down 1200 since yesterday; 3,200 collected.", true)]
    [InlineData("لديك ٤ وعود دفع مستحقة اليوم بقيمة ١١٥٠٠٫٠٠٠ دينار، والمتأخرات ٤٧٬٣٥٠٫٧٥٠.", true)]
    [InlineData("Petra Supplies is 62 days past due on 8200.000 JOD (case #17).", true)]
    [InlineData("Briefing for 2026-09-11 (2026/09/11, year 2026).", true)]
    [InlineData("Briefing for 11 September.", false)]   // a bare day would let any small number through
    [InlineData("Twelve invoices are overdue; 12 in JOD and 1 in USD worth 300.", true)]
    [InlineData("Roughly 47,000 JOD is overdue.", false)]
    [InlineData("Collections are up 15% on last week.", false)]
    [InlineData("5 promises are due today.", true)]   // 5 is repliesNeedingAHuman — traceable, even if misattributed; the guard is about provenance, not grammar
    [InlineData("7 promises are due today.", false)]
    [InlineData("Total exposure is 58,850.750 JOD.", false)]   // a sum of two figures is not a figure
    [InlineData("The oldest case is 90 days past due.", false)]
    [InlineData("Nothing to report.", true)]
    public void Narrative_IsAcceptedOnlyWhenEveryNumeralTraces(string narrative, bool accepted)
    {
        var r = NumericFidelityGuard.Check(narrative, [], ["totalOverdue"], Metrics(), Date);
        Assert.Equal(accepted, r.Accepted);
    }

    [Fact]
    public void Highlights_AndUnknownKeys_AreChecked()
    {
        var r = NumericFidelityGuard.Check("All good.", ["Queue: 41", "Overdue: 47,350.750 JOD", "Cash: 999"], ["queueSize", "revenue"], Metrics(), Date);
        Assert.False(r.Accepted);
        Assert.Equal(["999"], r.Untraceable);
        Assert.Equal(["revenue"], r.UnknownKeys);
    }

    [Fact]
    public void Metrics_RoundTrip_AsTheApiShape()
    {
        var m = Metrics();
        var json = m.Serialize();
        Assert.Contains("\"totalOverdue\":{\"amount\":\"47350.750\",\"currency\":\"JOD\"}", json);
        Assert.Contains("\"queueSize\":41", json);
        Assert.DoesNotContain("47350.75,", json);   // money is never a JSON number
        var back = BriefingMetrics.Deserialize(json)!;
        Assert.Equal(m.TotalOverdue, back.TotalOverdue);
        Assert.Equal(m.TopCases[0].CaseId, back.TopCases[0].CaseId);
    }

    [Fact]
    public void Renderer_UsesTheStoredStrings_AndTheGuardedNarrativeOnly()
    {
        var text = "{{company_name}} {{briefing_date}}: overdue {{total_overdue}}, collected {{collected_yesterday}}, {{promises_due_today}} due ({{promises_due_amount}}), queue {{queue_size}}\n{{top_cases}}\n{{narrative}}";
        var rendered = BriefingPlaceholders.Render(text, "Live Check", Metrics(), Date, null);
        Assert.Contains("Live Check 2026-09-11: overdue 47350.750 JOD, collected 3200.000 JOD, 4 due (11500.000 JOD), queue 41", rendered);
        Assert.Contains("#17 Petra Supplies: 8200.000 JOD, 62 days", rendered);
        Assert.EndsWith("\n", rendered);   // the narrative placeholder rendered empty
        Assert.Null(BriefingPlaceholders.FirstUnknown(text));
        Assert.Equal("amount_due", BriefingPlaceholders.FirstUnknown("{{amount_due}}"));   // the customer set is not the briefing set
        Assert.Equal("narrative", Placeholders.FirstUnknown("{{narrative}}"));
    }

    [Fact]
    public void Validator_IsStrict_AndPinnedToTheSchemaFile()
    {
        Assert.True(BriefingResponseValidator.TryParse(JsonDocument.Parse(AiScript.Briefing("en", "4 promises due.")).RootElement, out var ok, out var errors), string.Join(";", errors));
        Assert.Equal("4 promises due.", ok!.Narrative);
        Assert.False(BriefingResponseValidator.TryParse(JsonDocument.Parse(AiScript.Briefing("fr", "x")).RootElement, out _, out var e1));
        Assert.Contains("language: enum", e1);
        Assert.False(BriefingResponseValidator.TryParse(JsonDocument.Parse(AiScript.Briefing("en", new string('x', 1201))).RootElement, out _, out var e2));
        Assert.Contains("narrative: length", e2);
        Assert.False(BriefingResponseValidator.TryParse(JsonDocument.Parse(AiScript.Briefing("en", "x", numbersUsed: ["revenue"])).RootElement, out _, out var e3));
        Assert.Contains("numbers_used: enum", e3);
        Assert.False(BriefingResponseValidator.TryParse(JsonDocument.Parse(AiScript.Briefing("en", "x").Replace("\"schema_version\"", "\"total\":\"1\",\"schema_version\"", StringComparison.Ordinal)).RootElement, out _, out var e4));
        Assert.Contains("total: additional property", e4);

        var path = Path.Combine(RepositoryPaths.Root, "services", "ai", "schemas", "daily_briefing.response.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var props = doc.RootElement.GetProperty("properties");
        Assert.Equal(BriefingResponseValidator.SchemaVersion, props.GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Equal(1200, props.GetProperty("narrative").GetProperty("maxLength").GetInt32());
        var keys = props.GetProperty("numbers_used").GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("topCases", keys);
        Assert.Equal(keys.Count, Metrics().NumeralsByKey().Count + 1);   // every metric key plus "date"
    }
}
