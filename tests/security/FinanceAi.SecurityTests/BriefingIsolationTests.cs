using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 10 AC-10: a briefing is one organization's summary of its own figures, in every layer.</summary>
[Collection(ApiCollection.Name)]
public sealed class BriefingIsolationTests(ApiTestFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    [Fact]
    public async Task CrossTenantBriefing_IsRejected()
    {
        var a = await fixture.Api.NewCustomerAsync("A Secret Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-SECRET-1", 9_000m, dueDate: D(-40), issueDate: D(-70));
        await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 10m, dueDate: D(-10), issueDate: D(-40));
        foreach (var s in new[] { a, b })
        {
            using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/briefing-settings") { Content = JsonContent.Create(new { briefingSendAt = "23:59" }, options: ApiScenario.Json) };
            Assert.True((await s.Client.SendAsync(patch)).IsSuccessStatusCode);
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        }

        fixture.Api.Ai.Clear();
        fixture.Api.Ai.EnqueueBriefing(r => AiScript.Briefing(r.Language, $"Overdue {r.Metrics.TotalOverdue.Amount} {r.Metrics.TotalOverdue.Currency}."));
        fixture.Api.Ai.EnqueueBriefing(r => AiScript.Briefing(r.Language, $"Overdue {r.Metrics.TotalOverdue.Amount} {r.Metrics.TotalOverdue.Currency}."));
        var briefingA = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=en", ApiScenario.Json);
        Assert.Equal("9000.000", briefingA.GetProperty("metrics").GetProperty("totalOverdue").GetProperty("amount").GetString());
        var sentForA = JsonSerializer.Serialize(fixture.Api.Ai.BriefingRequests.Last());
        Assert.Contains("A Secret Co.", sentForA);
        Assert.DoesNotContain("B Co.", sentForA);

        // Layer 1: B's briefing is B's; A's rows are not reachable by date from B.
        fixture.Api.Ai.EnqueueBriefingUnavailable();
        fixture.Api.Ai.EnqueueBriefingUnavailable();
        var briefingB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=en", ApiScenario.Json);
        Assert.Equal("10.000", briefingB.GetProperty("metrics").GetProperty("totalOverdue").GetProperty("amount").GetString());
        Assert.DoesNotContain("A Secret Co.", briefingB.GetRawText());
        Assert.NotEqual(briefingA.GetProperty("id").GetGuid(), briefingB.GetProperty("id").GetGuid());
        var sentForB = JsonSerializer.Serialize(fixture.Api.Ai.BriefingRequests.Last());
        Assert.DoesNotContain("A Secret Co.", sentForB);

        // Layer 3 alone: a briefing row in B pointing at A's suggestion.
        var suggestionA = briefingA.GetProperty("aiSuggestionId").GetGuid();
        await using var connection = fixture.Database.OpenAdmin();
        await using (var cross = new NpgsqlCommand("INSERT INTO daily_briefings (id, tenant_id, briefing_date, language, metrics, ai_suggestion_id) VALUES (gen_random_uuid(), @t, @d, 'en', '{}'::jsonb, @s)", connection))
        {
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("d", Today.AddDays(-1));
            cross.Parameters.AddWithValue("s", suggestionA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_briefing_suggestion", ex.ConstraintName);
        }

        // Layer 2 alone: with B's tenant set, A's briefing does not exist.
        await using var asB = fixture.Database.OpenApp();
        await using var tx = await asB.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", asB, tx))
        {
            set.Parameters.AddWithValue("t", b.Organization.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM daily_briefings WHERE id = @id", asB, tx))
        {
            count.Parameters.AddWithValue("id", briefingA.GetProperty("id").GetGuid());
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }
}
