using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.TestSupport;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// Slice 26 — doc 05 <c>/organization/ai-settings</c> "per-operation enablement", T-105: an operation that fails its
/// gate can be switched off for the pilot on its own. <c>aiEnabled</c> stays the kill switch: an operation runs only
/// when both it and its own switch are on.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AiPerOperationTests(ApiTestFixture fixture)
{
    private static string D(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Echo(FinanceAi.Infrastructure.Ai.BriefingRequestPayload r) =>
        AiScript.Briefing(r.Language, r.Language == "ar"
            ? $"لديك {r.Metrics.PromisesDueToday.Count} وعود دفع مستحقة اليوم؛ المتأخرات {r.Metrics.TotalOverdue.Amount} {r.Metrics.TotalOverdue.Currency}."
            : $"You have {r.Metrics.PromisesDueToday.Count} promises due today; {r.Metrics.TotalOverdue.Amount} {r.Metrics.TotalOverdue.Currency} is overdue.");

    private static async Task<JsonElement> PatchAsync(HttpClient client, object body)
    {
        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/ai-settings") { Content = JsonContent.Create(body, options: ApiScenario.Json) };
        var response = await client.SendAsync(patch);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
    }

    private async Task<(Setup S, Guid MessageId)> OpenAsync(string name)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var email = $"{Guid.NewGuid():N}@example.test";
        await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts", new { name = "Accounts", email, isPrimary = true, isBilling = true });
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", 1_500m, dueDate: D(-20), issueDate: D(-50));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var message = await s.Client.PostAsync("/api/v1/inbound-messages", new { channel = "email", fromAddress = email, subject = "Re: reminder", body = "we will pay next week" });
        fixture.Api.Ai.Clear();
        return (s, message.GetProperty("id").GetGuid());
    }

    /// <summary>AC-01: both switches default on; the response and the health carry the effective states.</summary>
    [Fact]
    public async Task Defaults_AreOn_AndHealthReportsEffectiveStates()
    {
        var (s, _) = await OpenAsync("Defaults");
        var settings = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/ai-settings", ApiScenario.Json);
        Assert.True(settings.GetProperty("aiEnabled").GetBoolean());
        Assert.True(settings.GetProperty("aiClassificationEnabled").GetBoolean());
        Assert.True(settings.GetProperty("aiBriefingEnabled").GetBoolean());

        var health = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/ai/health", ApiScenario.Json);
        Assert.True(health.GetProperty("classificationActive").GetBoolean());
        Assert.True(health.GetProperty("briefingActive").GetBoolean());
    }

    /// <summary>AC-02: classification off on its own — the classifier refuses without a call, the briefing still narrates.</summary>
    [Fact]
    public async Task ClassificationOff_LeavesBriefingOn()
    {
        var (s, messageId) = await OpenAsync("ClassOff");
        var r = await PatchAsync(s.Client, new { aiClassificationEnabled = false });
        Assert.True(r.GetProperty("aiEnabled").GetBoolean());
        Assert.False(r.GetProperty("aiClassificationEnabled").GetBoolean());

        var calls = fixture.Api.Ai.Requests.Count;
        var (status, body) = await s.Client.TryPostAsync($"/api/v1/inbound-messages/{messageId}/classify", new { });
        Assert.Equal(409, status);
        Assert.Equal("ai_disabled", body.GetProperty("code").GetString());
        Assert.Equal(calls, fixture.Api.Ai.Requests.Count);

        var health = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/ai/health", ApiScenario.Json);
        Assert.False(health.GetProperty("classificationActive").GetBoolean());
        Assert.True(health.GetProperty("briefingActive").GetBoolean());

        fixture.Api.Ai.EnqueueBriefing(Echo);   // regenerate narrates both languages
        fixture.Api.Ai.EnqueueBriefing(Echo);
        var briefing = await s.Client.PostAsync("/api/v1/briefings/regenerate?language=en", new { });
        Assert.Equal("available", briefing.GetProperty("narrativeStatus").GetString());
    }

    /// <summary>AC-03: briefing off on its own — the narrative is <c>disabled</c>, the classifier still runs.</summary>
    [Fact]
    public async Task BriefingOff_LeavesClassificationOn()
    {
        var (s, messageId) = await OpenAsync("BriefOff");
        await PatchAsync(s.Client, new { aiBriefingEnabled = false });

        var calls = fixture.Api.Ai.Requests.Count;
        var briefing = await s.Client.PostAsync("/api/v1/briefings/regenerate?language=en", new { });
        Assert.Equal("disabled", briefing.GetProperty("narrativeStatus").GetString());
        Assert.Equal(calls, fixture.Api.Ai.Requests.Count);

        fixture.Api.Ai.Enqueue(AiScript.Response("promise_to_pay", 0.93m, "explicit_future_date_commitment", amountText: "1500 JOD", amountNumeric: "1500.000", currency: "JOD", dateText: D(7), dateIso: D(7), dateRelative: false, invoiceNumbers: ["BriefOff-1"]));
        var (status, body) = await s.Client.TryPostAsync($"/api/v1/inbound-messages/{messageId}/classify", new { });
        Assert.Equal(200, status);
        Assert.Equal("promise_to_pay", body.GetProperty("classification").GetString());
    }

    /// <summary>AC-04: the kill switch wins — both operations off even when their own switches are on; the audit trail carries every switch.</summary>
    [Fact]
    public async Task KillSwitch_OverridesBoth_AndIsAudited()
    {
        var (s, messageId) = await OpenAsync("Kill");
        await PatchAsync(s.Client, new { aiEnabled = false, aiClassificationEnabled = true, aiBriefingEnabled = true });

        var health = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/ai/health", ApiScenario.Json);
        Assert.False(health.GetProperty("aiEnabled").GetBoolean());
        Assert.False(health.GetProperty("classificationActive").GetBoolean());
        Assert.False(health.GetProperty("briefingActive").GetBoolean());

        var (status, body) = await s.Client.TryPostAsync($"/api/v1/inbound-messages/{messageId}/classify", new { });
        Assert.Equal(409, status);
        Assert.Equal("ai_disabled", body.GetProperty("code").GetString());
        var briefing = await s.Client.PostAsync("/api/v1/briefings/regenerate?language=en", new { });
        Assert.Equal("disabled", briefing.GetProperty("narrativeStatus").GetString());

        var changes = await fixture.Database.ScalarAsync<string>(
            "SELECT changes::text FROM audit_events WHERE tenant_id = @t AND event_type = 'tenant.ai_settings_changed' ORDER BY occurred_at DESC LIMIT 1",
            ("t", s.Organization.TenantId));
        using var doc = JsonDocument.Parse(changes!);
        Assert.True(doc.RootElement.GetProperty("before").GetProperty("aiEnabled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("after").GetProperty("aiEnabled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("after").GetProperty("aiClassificationEnabled").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("after").GetProperty("aiBriefingEnabled").GetBoolean());
    }

    /// <summary>AC-05 / SEC-13: a tenant's switches are its own — tenant B's PATCH does not touch tenant A.</summary>
    [Fact]
    public async Task Switches_AreTenantScoped()
    {
        var (a, _) = await OpenAsync("TenantA");
        var (b, _) = await OpenAsync("TenantB");
        await PatchAsync(b.Client, new { aiClassificationEnabled = false, aiBriefingEnabled = false });

        var settingsA = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/ai-settings", ApiScenario.Json);
        Assert.True(settingsA.GetProperty("aiClassificationEnabled").GetBoolean());
        Assert.True(settingsA.GetProperty("aiBriefingEnabled").GetBoolean());
        var settingsB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/ai-settings", ApiScenario.Json);
        Assert.False(settingsB.GetProperty("aiClassificationEnabled").GetBoolean());
    }
}
