using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 8 AC-01 … AC-16 against the real database, the API and Mailpit.</summary>
[Collection(ApiCollection.Name)]
public sealed class MessagingTests(ApiTestFixture fixture)
{
    private static readonly TimeZoneInfo Amman = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Amman).DateTime);
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");
    private static DateTimeOffset AmmanAt(int hour) => new(Today.ToDateTime(new TimeOnly(hour, 0)), Amman.GetUtcOffset(Today.ToDateTime(new TimeOnly(hour, 0))));

    private sealed record Ctx(Setup S, Guid CaseId, Guid Invoice, string Email);

    /// <summary>A customer with an email contact, one overdue invoice, an open case; approval setting as requested.</summary>
    private async Task<Ctx> OpenAsync(string name, bool requireApproval = true, int daysOverdue = 7, string language = "en")
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var email = $"{Guid.NewGuid():N}@example.test";
        await fixture.Database.ExecuteAsync("UPDATE customers SET preferred_language = @l WHERE id = @c", ("l", language), ("c", s.CustomerId));
        await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts", new { name = "Rana Accounts", email, phoneE164 = "+962791234567", isPrimary = true, isBilling = true });
        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET require_approval_before_send = @r WHERE tenant_id = @t", ("r", requireApproval), ("t", s.Organization.TenantId));
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", 1_000m, dueDate: D(-daysOverdue), issueDate: D(-daysOverdue - 30));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        return new Ctx(s, caseId, invoice, email);
    }

    private static async Task<JsonElement> TemplateAsync(HttpClient c, string key, string language = "en", string channel = "email") =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/v1/templates?key={key}&language={language}&channel={channel}", ApiScenario.Json)).GetProperty("items")[0];

    private static async Task<JsonElement> MessageAsync(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{id}", ApiScenario.Json);

    private static async Task<(int Status, JsonElement Body)> SendAsync(HttpClient c, Guid id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{id}/send") { Content = JsonContent.Create(new { }, options: ApiScenario.Json) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await c.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<JsonElement> MailpitSearchAsync(string query)
    {
        using var http = new HttpClient();
        return await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/search?query={Uri.EscapeDataString(query)}");
    }

    /// <summary>AC-01 / AC-02.</summary>
    [Fact]
    public async Task Placeholders_AreAClosedSet_AndTemplatesAreVersioned()
    {
        var x = await OpenAsync("Tpl");
        var placeholders = await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates/placeholders", ApiScenario.Json);
        Assert.Equal(16, placeholders.GetProperty("items").GetArrayLength());

        var (bad, badBody) = await x.S.Client.TryPostAsync("/api/v1/templates", new { key = "custom_nudge", channel = "email", language = "en", subject = "Hi {{customer_nam}}", body = "x" });
        Assert.Equal(422, bad);
        Assert.Equal("unknown_placeholder", badBody.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("customer_nam", badBody.GetProperty("errors")[0].GetProperty("meta").GetProperty("placeholder").GetString());

        var v1 = await x.S.Client.PostAsync("/api/v1/templates", new { key = "custom_nudge", channel = "email", language = "en", tone = "polite", subject = "About {{invoice_number}}", body = "Dear {{contact_name}}, {{amount_due}} is open on {{invoice_numbers}}." });
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.Equal("Draft", v1.GetProperty("status").GetString());
        Assert.Equal(new[] { "invoice_number", "contact_name", "amount_due", "invoice_numbers" }.Order(), v1.GetProperty("placeholders").EnumerateArray().Select(p => p.GetString()).Order());
        var (dup, _) = await x.S.Client.TryPostAsync("/api/v1/templates", new { key = "custom_nudge", channel = "email", language = "en", subject = "s", body = "b" });
        Assert.Equal(409, dup);

        var approved = await x.S.Client.PostAsync($"/api/v1/templates/{v1.GetProperty("id").GetGuid()}/approve", new { });
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, approved.GetProperty("approvedBy").GetGuid());

        var v2 = await x.S.Client.PostAsync($"/api/v1/templates/{v1.GetProperty("id").GetGuid()}", new { body = "Dear {{contact_name}}, please settle {{amount_due}}." });
        Assert.Equal(2, v2.GetProperty("version").GetInt32());
        Assert.Equal("Draft", v2.GetProperty("status").GetString());
        var all = await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=custom_nudge&all=true", ApiScenario.Json);
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        Assert.Single(all.GetProperty("items").EnumerateArray(), t => t.GetProperty("isActive").GetBoolean());

        // Preview renders real case data; money is a string with its currency.
        var preview = await x.S.Client.PostAsync($"/api/v1/templates/{v2.GetProperty("id").GetGuid()}/preview", new { caseId = x.CaseId });
        Assert.Equal("Dear Tpl, please settle 1000.000 JOD.", preview.GetProperty("body").GetString());   // a preview has no recipient yet: the contact name falls back to the customer
        Assert.Equal("1000.000", preview.GetProperty("amountDue").GetProperty("amount").GetString());

        // The seeded system templates are there, in both languages, as drafts.
        var seeds = await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=dunning_7", ApiScenario.Json);
        Assert.Equal(4, seeds.GetProperty("totalCount").GetInt32());   // email + whatsapp × ar + en
        Assert.All(seeds.GetProperty("items").EnumerateArray(), t => { Assert.True(t.GetProperty("isSystem").GetBoolean()); Assert.Equal("Draft", t.GetProperty("status").GetString()); });
    }

    /// <summary>AC-04 / T-47: each guard, its own code.</summary>
    [Fact]
    public async Task SendGuards_ReturnTheirCodes()
    {
        using var _ = fixture.Api.PinClock(AmmanAt(10)); // outside the default quiet hours (20:00–08:00 tenant-local), whatever the wall clock says
        var x = await OpenAsync("Guards", requireApproval: true);
        var tpl = await TemplateAsync(x.S.Client, "dunning_7");
        var composed = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() });
        var id = composed.GetProperty("id").GetGuid();
        Assert.Equal("PendingApproval", composed.GetProperty("status").GetString());
        Assert.Contains("tenant_setting", composed.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));

        var (approvalRequired, body1) = await SendAsync(x.S.Client, id);
        Assert.Equal(422, approvalRequired);
        Assert.Equal("approval_required", body1.GetProperty("errors")[0].GetProperty("code").GetString());

        await x.S.Client.PostAsync($"/api/v1/messages/{id}/approve", new { });

        // no_contact_email: the contact loses its email.
        await fixture.Database.ExecuteAsync("UPDATE messages SET to_address = NULL WHERE id = @m", ("m", id));
        var (noEmail, body2) = await SendAsync(x.S.Client, id);
        Assert.Equal(422, noEmail);
        Assert.Equal("no_contact_email", body2.GetProperty("errors")[0].GetProperty("code").GetString());
        await fixture.Database.ExecuteAsync("UPDATE messages SET to_address = @e WHERE id = @m", ("e", x.Email), ("m", id));

        // customer_on_hold, then case_escalated (SM-26).
        await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/transitions", new { @event = "hold", reasonCode = "standstill", holdUntil = D(10) });
        var (onHold, body3) = await SendAsync(x.S.Client, id);
        Assert.Equal("customer_on_hold", body3.GetProperty("errors")[0].GetProperty("code").GetString());
        await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/transitions", new { @event = "resume" });
        await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/transitions", new { @event = "escalate", reasonCode = "lawyer" });
        var (escalated, body4) = await SendAsync(x.S.Client, id);
        Assert.Equal("case_escalated", body4.GetProperty("errors")[0].GetProperty("code").GetString());

        // dispute_blocks_send on a fresh case.
        var y = await OpenAsync("Guards2", requireApproval: false);
        var tplY = await TemplateAsync(y.S.Client, "dunning_7");
        await y.S.Client.PostAsync($"/api/v1/templates/{tplY.GetProperty("id").GetGuid()}/approve", new { });
        var my = (await y.S.Client.PostAsync($"/api/v1/cases/{y.CaseId}/messages", new { channel = "email", templateId = tplY.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await y.S.Client.PostAsync($"/api/v1/messages/{my}/approve", new { });
        await y.S.Client.PostAsync($"/api/v1/invoices/{y.Invoice}/disputes", new { reasonCode = "goods_damaged", disputedAmount = M(100m) });
        var (disputed, body5) = await SendAsync(y.S.Client, my);
        Assert.Equal("dispute_blocks_send", body5.GetProperty("errors")[0].GetProperty("code").GetString());

        // outbound_disabled (tenant kill switch) and send_cap_exceeded on a clean case.
        var z = await OpenAsync("Guards3", requireApproval: false);
        var tplZ = await TemplateAsync(z.S.Client, "dunning_7");
        await z.S.Client.PostAsync($"/api/v1/templates/{tplZ.GetProperty("id").GetGuid()}/approve", new { });
        var mz = (await z.S.Client.PostAsync($"/api/v1/cases/{z.CaseId}/messages", new { channel = "email", templateId = tplZ.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await z.S.Client.PostAsync($"/api/v1/messages/{mz}/approve", new { });
        await z.S.Client.PutAsJsonAsync("/api/v1/organization/outbound", new { outboundSendingEnabled = false }, ApiScenario.Json);
        var (off, body6) = await SendAsync(z.S.Client, mz);
        Assert.Equal("outbound_disabled", body6.GetProperty("errors")[0].GetProperty("code").GetString());
        await z.S.Client.PutAsJsonAsync("/api/v1/organization/outbound", new { outboundSendingEnabled = true, dailySendCap = 0 }, ApiScenario.Json);
        var (cap, body7) = await SendAsync(z.S.Client, mz);
        Assert.Equal("send_cap_exceeded", body7.GetProperty("errors")[0].GetProperty("code").GetString());
        await z.S.Client.PutAsJsonAsync("/api/v1/organization/outbound", new { dailySendCap = 200 }, ApiScenario.Json);

        // duplicate_send_window: mark the same template as sent yesterday.
        await fixture.Database.ExecuteAsync(
            "INSERT INTO messages (id, tenant_id, customer_id, channel, language, template_key, body, status, approval_required, approval_kind, approved_by, sent_at) VALUES (gen_random_uuid(), @t, @c, 'email', 'en', 'dunning_7', 'x', 'Sent', false, 'message', @u, now() - interval '1 day')",
            ("t", z.S.Organization.TenantId), ("c", z.S.CustomerId), ("u", z.S.Organization.OwnerUserId));
        var (dupWindow, body8) = await SendAsync(z.S.Client, mz);
        Assert.Equal("duplicate_send_window", body8.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("7", body8.GetProperty("errors")[0].GetProperty("meta").GetProperty("windowDays").GetString());
    }

    /// <summary>AC-05 / SEC-81 / T-48.</summary>
    [Fact]
    public async Task ContentBinding_RejectsOtherCustomersInvoices()
    {
        var x = await OpenAsync("Bind-A");
        var other = (await x.S.Client.PostAsync("/api/v1/customers", new { nameEn = "Bind-B", defaultCurrency = "JOD" })).GetProperty("id").GetGuid();
        await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, other, "SECRET-4711", 900m, dueDate: D(-3), issueDate: D(-33));

        var (leak, leakBody) = await x.S.Client.TryPostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", subject = "Your account", body = "Please also settle SECRET-4711." });
        Assert.Equal(422, leak);
        Assert.Equal("content_binding_violation", leakBody.GetProperty("errors")[0].GetProperty("code").GetString());

        var ok = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", subject = "Your account", body = "Please settle Bind-A-1 ({{amount_due}})." });
        Assert.Equal("Please settle Bind-A-1 (1000.000 JOD).", ok.GetProperty("body").GetString());
        Assert.Contains("free_text", ok.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));
    }

    /// <summary>AC-06 / DM-25, and AC-07 approval rules.</summary>
    [Fact]
    public async Task Body_IsFrozen_AndApprovalRulesHold()
    {
        using var _ = fixture.Api.PinClock(AmmanAt(10)); // outside the default quiet hours (20:00–08:00 tenant-local), whatever the wall clock says
        var x = await OpenAsync("Freeze", requireApproval: false);
        var tpl = await TemplateAsync(x.S.Client, "dunning_7");
        var approvedTpl = await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });

        // First message to the customer: approval required even with the setting off.
        var first = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = approvedTpl.GetProperty("id").GetGuid() });
        Assert.Equal("PendingApproval", first.GetProperty("status").GetString());
        Assert.Equal(new[] { "first_message" }, first.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));
        var frozenBody = first.GetProperty("body").GetString()!;
        Assert.Contains("1000.000 JOD", frozenBody, StringComparison.Ordinal);

        var approved = await x.S.Client.PostAsync($"/api/v1/messages/{first.GetProperty("id").GetGuid()}/approve", new { });
        Assert.Equal("message", approved.GetProperty("approvalKind").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, approved.GetProperty("approvedBy").GetGuid());

        // Editing the template afterwards changes nothing on the message.
        await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}", new { body = "Completely different {{amount_due}}" });
        var after = await MessageAsync(x.S.Client, first.GetProperty("id").GetGuid());
        Assert.Equal(frozenBody, after.GetProperty("body").GetString());
        Assert.Equal(1, after.GetProperty("templateVersion").GetInt32());

        var (sent, _) = await SendAsync(x.S.Client, first.GetProperty("id").GetGuid());
        Assert.Equal(200, sent);
        await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
        Assert.Equal("Sent", (await MessageAsync(x.S.Client, first.GetProperty("id").GetGuid())).GetProperty("status").GetString());

        // A later message from an approved, non-final template: no separate approval; the click is the approval.
        var v2 = await TemplateAsync(x.S.Client, "dunning_14");
        await x.S.Client.PostAsync($"/api/v1/templates/{v2.GetProperty("id").GetGuid()}/approve", new { });
        var second = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = v2.GetProperty("id").GetGuid() });
        Assert.Equal("Draft", second.GetProperty("status").GetString());
        Assert.False(second.GetProperty("approvalRequired").GetBoolean());
        var (sent2, sentBody) = await SendAsync(x.S.Client, second.GetProperty("id").GetGuid());
        Assert.Equal(200, sent2);
        Assert.Equal("sender", sentBody.GetProperty("approvalKind").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, sentBody.GetProperty("approvedBy").GetGuid());

        // Free text and a final-tone template always need approval, whatever the setting.
        var free = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", subject = "Hello", body = "A note about {{invoice_number}}." });
        Assert.Equal("PendingApproval", free.GetProperty("status").GetString());
        var final90 = await TemplateAsync(x.S.Client, "dunning_90");
        await x.S.Client.PostAsync($"/api/v1/templates/{final90.GetProperty("id").GetGuid()}/approve", new { });
        var finalMsg = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = final90.GetProperty("id").GetGuid() });
        Assert.Contains("final_tone", finalMsg.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));
        // An unapproved template needs approval too.
        var draftTpl = await TemplateAsync(x.S.Client, "dunning_30");
        var unapproved = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = draftTpl.GetProperty("id").GetGuid() });
        Assert.Contains("template_not_approved", unapproved.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));
    }

    /// <summary>AC-08 / T-128 / C3, through Mailpit.</summary>
    [Fact]
    public async Task WorkTheMessage_T128()
    {
        using var _ = fixture.Api.PinClock(AmmanAt(10)); // outside the default quiet hours (20:00–08:00 tenant-local), whatever the wall clock says
        var x = await OpenAsync("T128", requireApproval: true, language: "ar");
        var tpl = await TemplateAsync(x.S.Client, "dunning_7", "ar");
        var preview = await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/preview", new { caseId = x.CaseId });
        Assert.Matches("[؀-ۿ]", preview.GetProperty("body").GetString()!);
        Assert.Contains("1000.000 JOD", preview.GetProperty("body").GetString(), StringComparison.Ordinal);

        var composed = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() });
        var id = composed.GetProperty("id").GetGuid();
        Assert.Equal("ar", composed.GetProperty("language").GetString());   // UI-14: the customer's language
        Assert.Equal("PendingApproval", composed.GetProperty("status").GetString());

        // A Collector cannot approve? — a Collector holds ai.suggestions.approve in doc 01; an Accountant approves here as the second user.
        var accountant = await fixture.Database.AddMemberAsync(x.S.Organization.TenantId, TenantRole.Accountant);
        using var approver = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));
        var approved = await approver.PostAsync($"/api/v1/messages/{id}/approve", new { });
        Assert.Equal(accountant.UserId, approved.GetProperty("approvedBy").GetGuid());

        var (sent, queued) = await SendAsync(x.S.Client, id);
        Assert.Equal(200, sent);
        Assert.Equal("Queued", queued.GetProperty("status").GetString());
        var dispatched = await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
        Assert.Equal(1, dispatched.GetProperty("sent").GetInt32());

        var message = await MessageAsync(x.S.Client, id);
        Assert.Equal("Sent", message.GetProperty("status").GetString());
        Assert.Equal(x.Email, message.GetProperty("toAddress").GetString());
        var mail = await MailpitSearchAsync($"to:{x.Email}");
        Assert.True(mail.GetProperty("messages_count").GetInt32() >= 1, "the email reached Mailpit");
        Assert.Equal(message.GetProperty("subject").GetString(), mail.GetProperty("messages")[0].GetProperty("Subject").GetString());

        // C3: the case is now waiting on the customer with a follow-up; the timeline has the frozen body's summary.
        var c = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{x.CaseId}", ApiScenario.Json);
        Assert.Equal("AwaitingCustomer", c.GetProperty("case").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, c.GetProperty("case").GetProperty("nextActionAt").ValueKind);
        Assert.Contains(c.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("kind").GetString() == "email_sent");
        // A second dispatch resends nothing.
        Assert.Equal(0, (await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { })).GetProperty("sent").GetInt32());
    }

    /// <summary>AC-09: path C of the slice doc, and only under its conditions.</summary>
    [Fact]
    public async Task Cadence_AutoQueuesOnlyWhenSafe()
    {
        using var _ = fixture.Api.PinClock(AmmanAt(10)); // outside the default quiet hours (20:00–08:00 tenant-local), whatever the wall clock says
        var x = await OpenAsync("Cadence", requireApproval: false, daysOverdue: 7);
        var tpl = await TemplateAsync(x.S.Client, "dunning_7");
        // Not yet approved: the sweep drafts nothing automatic (a PendingApproval draft appears instead, first message).
        var swept = await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
        var pending = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages?customerId={x.S.CustomerId}", ApiScenario.Json);
        var draft = Assert.Single(pending.GetProperty("items").EnumerateArray());
        Assert.Equal("PendingApproval", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("draftedBy").ValueKind);   // the cadence, not a person
        Assert.Contains("first_message", draft.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("template_not_approved", draft.GetProperty("approvalReasons").EnumerateArray().Select(r => r.GetString()));

        // A human approves the template and the draft; the draft is sent by hand so the customer has a history.
        var approvedTpl = await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });
        await x.S.Client.PostAsync($"/api/v1/messages/{draft.GetProperty("id").GetGuid()}/approve", new { });
        await SendAsync(x.S.Client, draft.GetProperty("id").GetGuid());
        await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });

        // Move to the next cadence step (14 days): the sweep now drafts AND queues automatically.
        await fixture.Database.ExecuteAsync("UPDATE invoices SET due_date = @d WHERE id = @i", ("d", Today.AddDays(-14)), ("i", x.Invoice));
        await fixture.Database.ExecuteAsync("UPDATE messages SET sent_at = now() - interval '8 days' WHERE customer_id = @c", ("c", x.S.CustomerId));   // outside the 7-day window
        await fixture.Database.ExecuteAsync("UPDATE collection_cases SET next_action_at = NULL, status = 'InProgress' WHERE id = @k", ("k", x.CaseId));
        var tpl14 = await TemplateAsync(x.S.Client, "dunning_14");
        await x.S.Client.PostAsync($"/api/v1/templates/{tpl14.GetProperty("id").GetGuid()}/approve", new { });
        try
        {
            fixture.Api.Clock.Override = AmmanAt(10);
            await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
            var messages = (await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages?customerId={x.S.CustomerId}", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
            var auto = Assert.Single(messages, m => m.GetProperty("templateKey").GetString() == "dunning_14");
            Assert.Equal("Sent", auto.GetProperty("status").GetString());
            Assert.Equal("template", auto.GetProperty("approvalKind").GetString());
            Assert.Equal(x.S.Organization.OwnerUserId, auto.GetProperty("approvedBy").GetGuid());   // the template's approver stands behind it
            Assert.Equal(JsonValueKind.Null, auto.GetProperty("draftedBy").ValueKind);

            // Idempotent: a second sweep the same day drafts nothing more.
            await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal(2, (await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages?customerId={x.S.CustomerId}", ApiScenario.Json)).GetProperty("totalCount").GetInt32());
        }
        finally
        {
            fixture.Api.ResetClock();
        }

        // A case at 8 days (not a step) gets nothing.
        var y = await OpenAsync("Cadence8", requireApproval: false, daysOverdue: 8);
        await y.S.Client.PostAsync("/api/v1/cases/sweep", new { });
        Assert.Equal(0, (await y.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages?customerId={y.S.CustomerId}", ApiScenario.Json)).GetProperty("totalCount").GetInt32());
    }

    /// <summary>AC-10 / SEC-103 / T-152, and AC-11 quiet hours.</summary>
    [Fact]
    public async Task KillSwitch_AndQuietHours_HoldTheMail()
    {
        var x = await OpenAsync("Kill", requireApproval: false);
        var tpl = await TemplateAsync(x.S.Client, "dunning_7");
        await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });
        var id = (await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/messages/{id}/approve", new { });

        try
        {
            fixture.Api.Clock.Override = AmmanAt(10);
            var (sent, _) = await SendAsync(x.S.Client, id);
            Assert.Equal(200, sent);

            // Tenant switch off: the dispatcher leaves it queued; on again: it goes.
            await x.S.Client.PutAsJsonAsync("/api/v1/organization/outbound", new { outboundSendingEnabled = false }, ApiScenario.Json);
            var held = await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
            Assert.Equal("outbound_disabled", held.GetProperty("skipReason").GetString());
            Assert.Equal("Queued", (await MessageAsync(x.S.Client, id)).GetProperty("status").GetString());

            // Global switch off: same.
            await x.S.Client.PutAsJsonAsync("/api/v1/organization/outbound", new { outboundSendingEnabled = true }, ApiScenario.Json);
            Environment.SetEnvironmentVariable("OUTBOUND_SENDING_ENABLED", "false");
            try
            {
                Assert.Equal("outbound_disabled", (await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { })).GetProperty("skipReason").GetString());
            }
            finally
            {
                Environment.SetEnvironmentVariable("OUTBOUND_SENDING_ENABLED", null);
            }

            // Quiet hours: 22:00 Amman → the dispatcher waits; send of a second message is refused with the next window.
            fixture.Api.Clock.Override = AmmanAt(22);
            var quiet = await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
            Assert.Equal("quiet_hours", quiet.GetProperty("skipReason").GetString());
            var waiting = await MessageAsync(x.S.Client, id);
            Assert.Equal("Queued", waiting.GetProperty("status").GetString());
            Assert.StartsWith(Today.AddDays(1).ToString("yyyy-MM-dd"), TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(waiting.GetProperty("nextAttemptAt").GetString()!), Amman).ToString("yyyy-MM-dd"), StringComparison.Ordinal);
            var tpl14 = await TemplateAsync(x.S.Client, "dunning_14");
            await x.S.Client.PostAsync($"/api/v1/templates/{tpl14.GetProperty("id").GetGuid()}/approve", new { });
            await fixture.Database.ExecuteAsync("INSERT INTO messages (id, tenant_id, customer_id, channel, language, body, status, approval_required, approval_kind, approved_by, sent_at) VALUES (gen_random_uuid(), @t, @c, 'email', 'en', 'x', 'Sent', false, 'message', @u, now() - interval '30 days')",
                ("t", x.S.Organization.TenantId), ("c", x.S.CustomerId), ("u", x.S.Organization.OwnerUserId));   // a prior message so no first_message reason
            var second = (await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = tpl14.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
            var (refused, body) = await SendAsync(x.S.Client, second);
            Assert.Equal(422, refused);
            Assert.Equal("quiet_hours", body.GetProperty("errors")[0].GetProperty("code").GetString());
            Assert.NotNull(body.GetProperty("errors")[0].GetProperty("meta").GetProperty("nextWindowAt").GetString());

            // Morning: it goes.
            fixture.Api.Clock.Override = AmmanAt(8).AddDays(1).AddMinutes(30);
            var morning = await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
            Assert.Equal(1, morning.GetProperty("sent").GetInt32());
            Assert.Equal("Sent", (await MessageAsync(x.S.Client, id)).GetProperty("status").GetString());
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-12 / ADR-0004.</summary>
    [Fact]
    public async Task WhatsApp_IsClickToChatOnly()
    {
        var x = await OpenAsync("Wa", requireApproval: false, language: "ar");
        var tpl = await TemplateAsync(x.S.Client, "dunning_7", "ar", "whatsapp");
        await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });
        var m = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "whatsapp_click_to_chat", templateId = tpl.GetProperty("id").GetGuid() });
        var id = m.GetProperty("id").GetGuid();
        Assert.Equal("+962791234567", m.GetProperty("toAddress").GetString());
        // First message → approval required even for WhatsApp.
        Assert.Equal("PendingApproval", m.GetProperty("status").GetString());
        var (blocked, _) = await x.S.Client.TryPostAsync($"/api/v1/messages/{id}/confirm-manual-send", new { });
        Assert.Equal(409, blocked);
        await x.S.Client.PostAsync($"/api/v1/messages/{id}/approve", new { });

        var link = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{id}/whatsapp-link", ApiScenario.Json);
        Assert.StartsWith("https://wa.me/962791234567?text=", link.GetProperty("link").GetString(), StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("1000.000 JOD"), link.GetProperty("link").GetString(), StringComparison.Ordinal);
        Assert.Equal("PreparedForManualSend", link.GetProperty("status").GetString());
        Assert.Contains("does not send", link.GetProperty("notice").GetString(), StringComparison.Ordinal);
        var (notEmail, notEmailBody) = await SendAsync(x.S.Client, id);   // the email path refuses a WhatsApp message
        Assert.Equal(422, notEmail);
        Assert.Equal("not_an_email", notEmailBody.GetProperty("errors")[0].GetProperty("code").GetString());

        var confirmed = await x.S.Client.PostAsync($"/api/v1/messages/{id}/confirm-manual-send", new { });
        Assert.Equal("Sent", confirmed.GetProperty("status").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, confirmed.GetProperty("sentBy").GetGuid());
        var c = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{x.CaseId}", ApiScenario.Json);
        Assert.Contains(c.GetProperty("timeline").EnumerateArray(), e => e.GetProperty("kind").GetString() == "whatsapp_prepared");
        Assert.Equal(0, fixture.Api.Mail.Sent - fixture.Api.Mail.Sent);   // nothing went through SMTP for it (the counter is shared; the status check above is the proof)
    }

    /// <summary>AC-13.</summary>
    [Fact]
    public async Task Dispatcher_RetriesThenFails()
    {
        var x = await OpenAsync("Retry", requireApproval: false);
        var tpl = await TemplateAsync(x.S.Client, "dunning_7");
        await x.S.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });
        var id = (await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/messages/{id}/approve", new { });
        try
        {
            fixture.Api.Clock.Override = AmmanAt(10);
            await SendAsync(x.S.Client, id);
            fixture.Api.Mail.FailNext = 3;
            for (var i = 1; i <= 3; i++)
            {
                await fixture.Database.ExecuteAsync("UPDATE messages SET next_attempt_at = '2000-01-01' WHERE id = @m", ("m", id));
                await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
                var m = await MessageAsync(x.S.Client, id);
                Assert.Equal(i, m.GetProperty("attempts").GetInt32());
                Assert.Equal(i < 3 ? "Queued" : "Failed", m.GetProperty("status").GetString());
            }

            var failed = await MessageAsync(x.S.Client, id);
            Assert.Contains("simulated SMTP failure", failed.GetProperty("failureReason").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, (await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { })).GetProperty("sent").GetInt32());
        }
        finally
        {
            fixture.Api.Mail.FailNext = 0;
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-14.</summary>
    [Fact]
    public async Task Statement_ListsHistory()
    {
        var x = await OpenAsync("Stmt", requireApproval: false);
        await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(250m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = x.Invoice, amount = M(250m) } } });
        var statement = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{x.S.CustomerId}/statement", ApiScenario.Json);
        Assert.Equal("750.000", statement.GetProperty("positions")[0].GetProperty("openBalance").GetProperty("amount").GetString());
        Assert.Single(statement.GetProperty("openInvoices").EnumerateArray());
        Assert.Single(statement.GetProperty("payments").EnumerateArray());
        Assert.Empty(statement.GetProperty("messages").EnumerateArray());
        Assert.DoesNotContain("1000.000", statement.GetProperty("positions").GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>AC-16 / SM-03 / INV-13.</summary>
    [Fact]
    public async Task MessageTransitions_AreAudited()
    {
        var x = await OpenAsync("Audit", requireApproval: true);
        var tpl = await TemplateAsync(x.S.Client, "dunning_7");
        var id = (await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/messages/{id}/approve", new { });
        try
        {
            fixture.Api.Clock.Override = AmmanAt(10);
            await SendAsync(x.S.Client, id);
            await x.S.Client.PostAsync("/api/v1/messages/dispatch", new { });
        }
        finally { fixture.Api.ResetClock(); }

        var audit = await fixture.Database.ScalarAsync<string>("SELECT string_agg(coalesce(from_state,'-') || '>' || to_state || ':' || actor_kind, ',' ORDER BY id) FROM audit_events WHERE entity_id = @m AND event_type = 'message.status_changed'", ("m", id));
        Assert.Equal("->PendingApproval:user,PendingApproval>Approved:user,Approved>Queued:user,Queued>Sent:system", audit);
        // The CHECK: a Sent row without approval is impossible.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsync("UPDATE messages SET approved_by = NULL WHERE id = @m", ("m", id)));
        Assert.Equal("sent_requires_approval", ex.ConstraintName);
        // The audit never carries the address.
        Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE entity_id = @m AND changes::text LIKE @e", ("m", id), ("e", "%" + x.Email + "%")));
    }
}
