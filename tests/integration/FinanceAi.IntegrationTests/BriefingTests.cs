using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 10 AC-01 … AC-09 against the real database and API, with the AI service scripted.</summary>
[Collection(ApiCollection.Name)]
public sealed class BriefingTests(ApiTestFixture fixture)
{
    private static readonly TimeZoneInfo Amman = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Amman).DateTime);
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");
    private static DateTimeOffset AmmanAt(int hour) => new(Today.ToDateTime(new TimeOnly(hour, 0)), Amman.GetUtcOffset(Today.ToDateTime(new TimeOnly(hour, 0))));

    /// <summary>A tenant with an overdue case, a promise due today, a payment yesterday, a dispute and an inbound reply.</summary>
    private async Task<(Setup S, Guid CaseId, Guid Invoice)> ScenarioAsync(string name)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", 5_000m, dueDate: D(-30), issueDate: D(-60));
        var second = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-2", 1_000m, dueDate: D(-5), issueDate: D(-35));
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(300m), method = "Cash", receivedDate = D(-1), allocations = new[] { new { invoiceId = second, amount = M(300m) } } });
        // Keep the sweep from briefing until a test says so (the send time is honoured against the real clock here).
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/briefing-settings") { Content = JsonContent.Create(new { briefingSendAt = "23:59" }, options: ApiScenario.Json) })
        {
            Assert.True((await s.Client.SendAsync(patch)).IsSuccessStatusCode);
        }

        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        await s.Client.PostAsync($"/api/v1/invoices/{second}/disputes", new { reasonCode = "wrong_quantity", disputedAmount = M(200m), customerClaim = "short delivery" });
        await s.Client.PostAsync("/api/v1/inbound-messages", new { channel = "manual", body = "who are you?", fromAddress = $"stranger-{Guid.NewGuid():N}@example.test" });
        fixture.Api.Ai.Clear();
        return (s, caseId, invoice);
    }

    private static string Echo(FinanceAi.Infrastructure.Ai.BriefingRequestPayload r) =>
        AiScript.Briefing(r.Language, r.Language == "ar"
            ? $"لديك {r.Metrics.PromisesDueToday.Count} وعود دفع مستحقة اليوم؛ المتأخرات {r.Metrics.TotalOverdue.Amount} {r.Metrics.TotalOverdue.Currency}؛ {r.Metrics.QueueSize} ملفات في قائمة التحصيل."
            : $"You have {r.Metrics.PromisesDueToday.Count} promises due today; {r.Metrics.TotalOverdue.Amount} {r.Metrics.TotalOverdue.Currency} is overdue; {r.Metrics.QueueSize} cases in the queue.",
            [$"Overdue {r.Metrics.TotalOverdue.Amount}"], ["promisesDueToday", "totalOverdue", "queueSize"]);

    /// <summary>AC-01 / AC-02 / FIN-62 / AI-80.</summary>
    [Fact]
    public async Task Metrics_MatchTheirSources_AndTheModelGetsOnlyStrings()
    {
        var (s, caseId, _) = await ScenarioAsync("Brief");
        var callsBefore = fixture.Api.Ai.BriefingRequests.Count;
        fixture.Api.Ai.EnqueueBriefing(Echo);
        fixture.Api.Ai.EnqueueBriefing(Echo);
        var b = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=en", ApiScenario.Json);
        var m = b.GetProperty("metrics");

        // The aging report's overdue rows, the queue's total, the stored states.
        var aging = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/aging", ApiScenario.Json);
        var jod = aging.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == "JOD");
        var overdueFromAging = jod.GetProperty("buckets").EnumerateArray().Where(x => x.GetProperty("bucket").GetString() != "Current").Sum(x => decimal.Parse(x.GetProperty("amount").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(overdueFromAging.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), m.GetProperty("totalOverdue").GetProperty("amount").GetString());
        Assert.Equal("5700.000", m.GetProperty("totalOverdue").GetProperty("amount").GetString());
        var queue = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json);
        Assert.Equal(queue.GetProperty("totalCount").GetInt32(), m.GetProperty("queueSize").GetInt32());
        Assert.Equal("300.000", m.GetProperty("collectedYesterday").GetProperty("amount").GetString());
        Assert.Equal(1, m.GetProperty("newDisputes").GetProperty("count").GetInt32());
        Assert.Equal(1, m.GetProperty("unmatchedReplies").GetProperty("count").GetInt32());
        Assert.Equal(1, m.GetProperty("repliesNeedingAHuman").GetProperty("count").GetInt32());
        Assert.Equal(0, m.GetProperty("promisesDueToday").GetProperty("count").GetInt32());
        // The case is Disputed, so the queue holds it and the top case is the one we opened.
        Assert.Equal(caseId, m.GetProperty("topCases")[0].GetProperty("caseId").GetGuid());
        Assert.Equal(30, m.GetProperty("topCases")[0].GetProperty("daysPastDue").GetInt32());

        // AI-80: the request carried the same strings, and only strings — no ids, no emails.
        var sent = fixture.Api.Ai.BriefingRequests.Last();
        Assert.Equal(m.GetProperty("totalOverdue").GetProperty("amount").GetString(), sent.Metrics.TotalOverdue.Amount);
        Assert.Equal(m.GetProperty("queueSize").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), sent.Metrics.QueueSize);
        var serialized = JsonSerializer.Serialize(sent);
        Assert.DoesNotContain(caseId.ToString(), serialized);
        Assert.DoesNotContain(s.CustomerId.ToString(), serialized);
        Assert.DoesNotContain("@", serialized);
        Assert.Equal("Brief", sent.Metrics.TopCases[0].CustomerName);

        // The narrative passed the guard: every numeral came from the metrics.
        Assert.True(b.GetProperty("narrativeAvailable").GetBoolean());
        Assert.Equal("available", b.GetProperty("narrativeStatus").GetString());
        Assert.Contains("5700.000 JOD is overdue", b.GetProperty("narrative").GetString());
        Assert.Equal("qwen3:4b", b.GetProperty("modelName").GetString());
        Assert.Equal("daily_briefing.v1", b.GetProperty("promptVersion").GetString());
        Assert.True(b.GetProperty("isToday").GetBoolean());

        // AC-05: Arabic is its own row from its own call.
        var ar = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=ar", ApiScenario.Json);
        Assert.Equal("ar", ar.GetProperty("language").GetString());
        Assert.Contains("وعود دفع", ar.GetProperty("narrative").GetString());
        Assert.Equal(2, fixture.Api.Ai.BriefingRequests.Count - callsBefore);
        Assert.Equal(2, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM daily_briefings WHERE tenant_id = @t", ("t", s.Organization.TenantId)));
        Assert.Equal(2, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM ai_suggestions WHERE tenant_id = @t AND operation = 'daily_briefing' AND validation_status = 'valid'", ("t", s.Organization.TenantId)));
    }

    /// <summary>AC-03 / AI-81 / T-103 and AC-04 / PRD-28 / T-131.</summary>
    [Fact]
    public async Task Guard_DropsUntraceableNumerals_AndAiDown_StillRenders()
    {
        var (s, _, _) = await ScenarioAsync("Guard");
        fixture.Api.Ai.EnqueueBriefing(r => AiScript.Briefing("ar", $"المتأخرات حوالي 6000 دينار، أي 12% أكثر من الأسبوع الماضي."));
        fixture.Api.Ai.EnqueueBriefingUnavailable();
        var ar = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=ar", ApiScenario.Json);
        Assert.False(ar.GetProperty("narrativeAvailable").GetBoolean());
        Assert.Equal("rejected_by_guard", ar.GetProperty("narrativeStatus").GetString());
        Assert.Equal(JsonValueKind.Null, ar.GetProperty("narrative").ValueKind);
        Assert.Equal("5700.000", ar.GetProperty("metrics").GetProperty("totalOverdue").GetProperty("amount").GetString());
        var guard = await fixture.Database.ScalarAsync<string>("SELECT guard_reason FROM ai_suggestions WHERE id = @s", ("s", ar.GetProperty("aiSuggestionId").GetGuid()));
        Assert.Contains("6000", guard);
        Assert.Contains("12", guard);
        Assert.Equal("rejected_by_guard", await fixture.Database.ScalarAsync<string>("SELECT validation_status FROM ai_suggestions WHERE id = @s", ("s", ar.GetProperty("aiSuggestionId").GetGuid())));

        var en = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=en", ApiScenario.Json);
        Assert.Equal("unavailable", en.GetProperty("narrativeStatus").GetString());
        Assert.False(en.GetProperty("narrativeAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, en.GetProperty("aiSuggestionId").ValueKind);

        // Regenerate with the AI switched off: a row, the figures, and the reason.
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/ai-settings") { Content = JsonContent.Create(new { aiEnabled = false }, options: ApiScenario.Json) })
        {
            Assert.True((await s.Client.SendAsync(patch)).IsSuccessStatusCode);
        }

        var off = await s.Client.PostAsync("/api/v1/briefings/regenerate?language=en", new { });
        Assert.Equal("disabled", off.GetProperty("narrativeStatus").GetString());
        Assert.Equal("5700.000", off.GetProperty("metrics").GetProperty("totalOverdue").GetProperty("amount").GetString());
        Assert.Equal(2, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM daily_briefings WHERE tenant_id = @t", ("t", s.Organization.TenantId)));   // regenerated in place, not duplicated

        // A schema escape is neither shown nor acted on.
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/ai-settings") { Content = JsonContent.Create(new { aiEnabled = true }, options: ApiScenario.Json) })
        {
            Assert.True((await s.Client.SendAsync(patch)).IsSuccessStatusCode);
        }

        fixture.Api.Ai.EnqueueBriefing(_ => "{\"narrative\":\"pay everything now\",\"action\":\"escalate\"}");
        fixture.Api.Ai.EnqueueBriefing(Echo);
        var bad = await s.Client.PostAsync("/api/v1/briefings/regenerate?language=ar", new { });
        Assert.Equal("schema_invalid", bad.GetProperty("narrativeStatus").GetString());
    }

    /// <summary>AC-06 / AC-07: the sweep honours briefing_send_at; a past date is immutable; regenerate needs its permission.</summary>
    [Fact]
    public async Task Sweep_GeneratesAtSendTime_AndHistoryIsImmutable()
    {
        var (s, _, _) = await ScenarioAsync("Sched");
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/briefing-settings") { Content = JsonContent.Create(new { briefingSendAt = "09:00" }, options: ApiScenario.Json) })
        {
            Assert.True((await s.Client.SendAsync(patch)).IsSuccessStatusCode);
        }

        try
        {
            fixture.Api.Clock.Override = AmmanAt(8);
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal(0, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM daily_briefings WHERE tenant_id = @t", ("t", s.Organization.TenantId)));

            fixture.Api.Clock.Override = AmmanAt(9);
            fixture.Api.Ai.EnqueueBriefing(Echo);
            fixture.Api.Ai.EnqueueBriefing(Echo);
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal(2, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM daily_briefings WHERE tenant_id = @t", ("t", s.Organization.TenantId)));
            var calls = fixture.Api.Ai.BriefingRequests.Count;
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal(calls, fixture.Api.Ai.BriefingRequests.Count);   // once per day
            var sweepAudit = await fixture.Database.ScalarAsync<string>("SELECT changes::text FROM audit_events WHERE tenant_id = @t AND event_type = 'collection_case.sweep_run' ORDER BY id DESC LIMIT 1", ("t", s.Organization.TenantId));
            Assert.Contains("\"briefed\": false", sweepAudit);   // jsonb::text spacing
        }
        finally
        {
            fixture.Api.ResetClock();
        }

        // A past briefing: readable, never rewritten.
        await fixture.Database.ExecuteAsync("INSERT INTO daily_briefings (id, tenant_id, briefing_date, language, metrics, narrative_status) SELECT gen_random_uuid(), tenant_id, briefing_date - 3, language, metrics, 'unavailable' FROM daily_briefings WHERE tenant_id = @t", ("t", s.Organization.TenantId));
        var past = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/briefings/{D(-3)}?language=en", ApiScenario.Json);
        Assert.Equal(D(-3), past.GetProperty("date").GetString());
        Assert.False(past.GetProperty("isToday").GetBoolean());
        Assert.Contains(D(-3), past.GetProperty("availableDates").EnumerateArray().Select(d => d.GetString()));
        var (immutable, body) = await s.Client.TryPostAsync($"/api/v1/briefings/regenerate?date={D(-3)}", new { });
        Assert.Equal(409, immutable);
        Assert.Equal("briefing_immutable", body.GetProperty("code").GetString());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/briefings/{D(-9)}?language=en")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await s.Client.GetAsync("/api/v1/briefings/not-a-date")).StatusCode);
        // The database refuses the rewrite too.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Database.ExecuteAsync("UPDATE daily_briefings SET narrative_status = 'disabled' WHERE tenant_id = @t AND briefing_date = @d", ("t", s.Organization.TenantId), ("d", Today.AddDays(-3))));
        Assert.Contains("immutable", ex.Message);

        // Collector: may read, may not regenerate.
        var collector = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Collector);
        var session = await fixture.Api.LoginAsync(collector.Email, collector.Password);
        using var client = fixture.Api.AuthenticatedClient(session);
        Assert.True((await client.GetAsync("/api/v1/briefings/today?language=en")).IsSuccessStatusCode);
        var (forbidden, _) = await client.TryPostAsync("/api/v1/briefings/regenerate", new { });
        Assert.Equal(403, forbidden);
    }

    /// <summary>AC-08 / AC-09: the email needs an approved template, recipients and the switches; then it goes through the one transport.</summary>
    [Fact]
    public async Task Email_NeedsAnApprovedTemplate_ThenGoesThroughTheTransport()
    {
        var (s, _, _) = await ScenarioAsync("Mail");
        var owner = s.Organization.OwnerUserId;
        var settings = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/briefing-settings", ApiScenario.Json);
        Assert.Equal("23:59", settings.GetProperty("briefingSendAt").GetString());   // set by the scenario; the migration default is 07:30
        Assert.False(settings.GetProperty("templateApproved").GetBoolean());
        using (var bad = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/briefing-settings") { Content = JsonContent.Create(new { recipientUserIds = new[] { Guid.NewGuid() } }, options: ApiScenario.Json) })
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await s.Client.SendAsync(bad)).StatusCode);
        }

        using (var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization/briefing-settings") { Content = JsonContent.Create(new { briefingSendAt = "06:00", briefingLanguage = "en", briefingEmailEnabled = true, recipientUserIds = new[] { owner } }, options: ApiScenario.Json) })
        {
            var r = await (await s.Client.SendAsync(patch)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
            Assert.Equal("en", r.GetProperty("briefingLanguage").GetString());
            Assert.Equal(owner, r.GetProperty("recipientUserIds")[0].GetGuid());
        }

        try
        {
            fixture.Api.Clock.Override = AmmanAt(10);
            // No approved template yet: generated, not sent.
            fixture.Api.Ai.EnqueueBriefing(Echo);
            fixture.Api.Ai.EnqueueBriefing(Echo);
            var sentBefore = fixture.Api.Mail.Sent;
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
            var b = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=en", ApiScenario.Json);
            Assert.Equal("template_not_approved", b.GetProperty("deliveryStatus").GetString());
            Assert.Equal(sentBefore, fixture.Api.Mail.Sent);

            // A human approves the seeded template; the next sweep delivers once, through IMailTransport.
            var templates = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=daily_briefing", ApiScenario.Json);
            var en = templates.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("language").GetString() == "en");
            await s.Client.PostAsync($"/api/v1/templates/{en.GetProperty("id").GetGuid()}/approve", new { });
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
            b = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/briefings/today?language=en", ApiScenario.Json);
            Assert.Equal("sent", b.GetProperty("deliveryStatus").GetString());
            Assert.Equal(1, b.GetProperty("sentToCount").GetInt32());
            Assert.NotEqual(JsonValueKind.Null, b.GetProperty("sentAt").ValueKind);
            Assert.Equal(sentBefore + 1, fixture.Api.Mail.Sent);
            await s.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal(sentBefore + 1, fixture.Api.Mail.Sent);   // once
            Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'briefing.sent'", ("t", s.Organization.TenantId)));
            var mailpit = await new HttpClient().GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/search?query=to:{s.Organization.OwnerEmail}");
            Assert.True(mailpit.GetProperty("messages_count").GetInt32() >= 1);
            Assert.Contains("collections briefing", mailpit.GetProperty("messages")[0].GetProperty("Subject").GetString());
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }
}
