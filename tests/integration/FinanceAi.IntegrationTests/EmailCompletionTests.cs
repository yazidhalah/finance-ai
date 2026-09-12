using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceAi.TestSupport;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 24 AC-01 … AC-03: email verification, tenant SMTP settings, the signed MTA webhook.</summary>
[Collection(ApiCollection.Name)]
public sealed class EmailCompletionTests(ApiTestFixture fixture)
{
    private static readonly HttpClient Mailpit = new() { BaseAddress = new Uri("http://127.0.0.1:8025/") };

    private static string D(int daysFromToday) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysFromToday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(int Status, JsonElement Body)> LoginAsync(HttpClient anonymous, string email, string password)
    {
        var r = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, ApiScenario.Json);
        var text = await r.Content.ReadAsStringAsync();
        return ((int)r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<string> VerificationTokenAsync(string email)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var search = await Mailpit.GetFromJsonAsync<JsonElement>($"api/v1/search?query={Uri.EscapeDataString("to:" + email)}&limit=5");
            foreach (var m in search.GetProperty("messages").EnumerateArray())
            {
                var message = await Mailpit.GetFromJsonAsync<JsonElement>($"api/v1/message/{m.GetProperty("ID").GetString()}");
                var match = Regex.Match(message.GetProperty("Text").GetString() ?? string.Empty, @"verify-email\?token=([0-9a-f]{64})");
                if (match.Success) return match.Groups[1].Value;
            }

            await Task.Delay(150);
        }

        throw new Xunit.Sdk.XunitException("no verification mail");
    }

    /// <summary>AC-01: the link, the ordering behind SEC-07, one shape for every token, and verification by invitation.</summary>
    [Fact]
    public async Task EmailVerification_GatesTheFirstSignIn()
    {
        using var anonymous = fixture.Api.CreateClient();
        var email = $"verify-{Guid.NewGuid():N}@example.jo";
        var registered = await anonymous.PostAsJsonAsync("/api/v1/auth/register", new { email, password = ApiScenario.ValidPassword, fullName = "Unverified Owner", organizationName = "Verify Co", baseCurrency = "JOD", timezone = "Asia/Amman", locale = "en-JO" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        // The wrong password is still invalid_credentials: the unverified state is only disclosed to the password's owner (SEC-07).
        var (wrong, wrongBody) = await LoginAsync(anonymous, email, "not-the-password-at-all");
        Assert.Equal(401, wrong);
        Assert.Equal("invalid_credentials", wrongBody.GetProperty("code").GetString());
        var (unverified, unverifiedBody) = await LoginAsync(anonymous, email, ApiScenario.ValidPassword);
        Assert.Equal(401, unverified);
        Assert.Equal("email_unverified", unverifiedBody.GetProperty("code").GetString());

        var garbage = await (await anonymous.PostAsJsonAsync("/api/v1/auth/verify-email", new { token = new string('0', 64) }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.False(garbage.GetProperty("accepted").GetBoolean());
        var token = await VerificationTokenAsync(email);
        var verified = await (await anonymous.PostAsJsonAsync("/api/v1/auth/verify-email", new { token }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.True(verified.GetProperty("accepted").GetBoolean());
        var reused = await (await anonymous.PostAsJsonAsync("/api/v1/auth/verify-email", new { token }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.False(reused.GetProperty("accepted").GetBoolean());
        var (ok, _) = await LoginAsync(anonymous, email, ApiScenario.ValidPassword);
        Assert.Equal(200, ok);

        // An accepted invitation verifies the address as a side effect (slice 12 D-2): the invitee signs in at once.
        var org = await fixture.Api.CreateOrganizationAsync("Invite Verify Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var invitee = $"invitee-{Guid.NewGuid():N}@example.jo";
        await owner.PostAsJsonAsync("/api/v1/organization/members/invite", new { email = invitee, role = "Viewer", locale = "en-JO" }, ApiScenario.Json);
        string? inviteToken = null;
        for (var attempt = 0; attempt < 40 && inviteToken is null; attempt++)
        {
            var search = await Mailpit.GetFromJsonAsync<JsonElement>($"api/v1/search?query={Uri.EscapeDataString("to:" + invitee)}");
            foreach (var m in search.GetProperty("messages").EnumerateArray())
            {
                var message = await Mailpit.GetFromJsonAsync<JsonElement>($"api/v1/message/{m.GetProperty("ID").GetString()}");
                var match = Regex.Match(message.GetProperty("Text").GetString() ?? string.Empty, @"accept-invitation\?token=([0-9a-f]{64})");
                if (match.Success) inviteToken = match.Groups[1].Value;
            }

            if (inviteToken is null) await Task.Delay(150);
        }

        Assert.NotNull(inviteToken);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/v1/auth/accept-invitation", new { token = inviteToken, fullName = "Invitee", password = ApiScenario.ValidPassword }, ApiScenario.Json)).StatusCode);
        var (inviteeLogin, _) = await LoginAsync(anonymous, invitee, ApiScenario.ValidPassword);
        Assert.Equal(200, inviteeLogin);
    }

    /// <summary>AC-02: write-only secret, re-authentication, the host policy, the test probe, and the tenant's mail through the tenant's host.</summary>
    [Fact]
    public async Task TenantEmailSettings_AreWriteOnly_Reauthenticated_AndUsed()
    {
        var s = await fixture.Api.NewCustomerAsync("SMTP Co.");
        var before = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/email-settings", ApiScenario.Json);
        Assert.False(before.GetProperty("configured").GetBoolean());

        var body = new { smtpHost = "127.0.0.1", smtpPort = 1025, smtpTls = false, smtpUsername = "tenant-user", smtpPassword = "s3cret-p@ss", fromAddress = "billing@smtp-co.example" };
        var noProof = await s.Client.PutAsJsonAsync("/api/v1/organization/email-settings", body, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Forbidden, noProof.StatusCode);
        Assert.Equal("reauthentication_required", (await noProof.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("code").GetString());

        await s.Client.ReauthAsync();
        var saved = await (await s.Client.PutAsJsonAsync("/api/v1/organization/email-settings", body, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.True(saved.GetProperty("configured").GetBoolean());
        Assert.True(saved.GetProperty("hasPassword").GetBoolean());
        Assert.False(saved.TryGetProperty("smtpPassword", out _));
        Assert.DoesNotContain("s3cret-p@ss", await s.Client.GetStringAsync("/api/v1/organization/email-settings"), StringComparison.Ordinal);
        var stored = await fixture.Database.ScalarAsync<byte[]>("SELECT smtp_password_enc FROM tenant_email_settings WHERE tenant_id = @t", ("t", s.Organization.TenantId));
        Assert.NotNull(stored);
        Assert.DoesNotContain("s3cret-p@ss", Encoding.UTF8.GetString(stored!), StringComparison.Ordinal);
        Assert.StartsWith("FKEK1", Encoding.ASCII.GetString(stored![..5]), StringComparison.Ordinal);   // the MFA KEK envelope (slice 17)
        var audit = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?eventType=tenant.email_settings_changed", ApiScenario.Json);
        Assert.DoesNotContain("s3cret-p@ss", audit.GetRawText(), StringComparison.Ordinal);

        // Omitting the password keeps it; the host policy refuses a private host unless the dev switch is on (it is, here — prove the policy directly).
        await s.Client.ReauthAsync();
        var kept = await (await s.Client.PutAsJsonAsync("/api/v1/organization/email-settings", new { smtpHost = "127.0.0.1", smtpPort = 1025, smtpTls = false, fromAddress = "billing@smtp-co.example" }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.True(kept.GetProperty("hasPassword").GetBoolean());
        Assert.False(FinanceAi.Infrastructure.Messaging.SmtpHostPolicy.IsPublic(IPAddress.Parse("169.254.169.254")));
        Assert.False(FinanceAi.Infrastructure.Messaging.SmtpHostPolicy.IsPublic(IPAddress.Parse("10.1.2.3")));
        Assert.False(FinanceAi.Infrastructure.Messaging.SmtpHostPolicy.IsPublic(IPAddress.Parse("fd12::1")));
        Assert.True(FinanceAi.Infrastructure.Messaging.SmtpHostPolicy.IsPublic(IPAddress.Parse("203.0.113.9")));

        var probe = await (await s.Client.PostAsJsonAsync("/api/v1/organization/email-settings/test", new { }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.True(probe.GetProperty("ok").GetBoolean(), probe.GetRawText());

        // The tenant's customer mail now goes through the tenant's host and From.
        var email = $"{Guid.NewGuid():N}@example.test";
        await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts", new { name = "Rana", email, isPrimary = true, isBilling = true });
        await fixture.Database.ExecuteAsync("UPDATE customers SET preferred_language = 'en' WHERE id = @c", ("c", s.CustomerId));
        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET require_approval_before_send = false WHERE tenant_id = @t", ("t", s.Organization.TenantId));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "SMTP-1", 100m, dueDate: D(-7), issueDate: D(-37));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        var tpl = (await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=dunning_7&language=en&channel=email", ApiScenario.Json)).GetProperty("items")[0];
        await s.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });
        var message = await s.Client.PostAsync($"/api/v1/cases/{caseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() });
        var accountant = await fixture.Database.AddMemberAsync(s.Organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Accountant);
        using (var approver = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password)))
        {
            await approver.PostAsync($"/api/v1/messages/{message.GetProperty("id").GetGuid()}/approve", new { });   // the first message to a customer needs a second pair of eyes
        }

        using (var _ = fixture.Api.PinClock(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(7))))   // 10:00 Amman: outside quiet hours
        {
            using var send = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{message.GetProperty("id").GetGuid()}/send") { Content = JsonContent.Create(new { }, options: ApiScenario.Json) };
            send.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            var sendResponse = await s.Client.SendAsync(send);
            Assert.True(sendResponse.StatusCode == HttpStatusCode.OK, await sendResponse.Content.ReadAsStringAsync());
            var viaBefore = fixture.Api.Mail.ViaTenant.Count;
            await s.Client.PostAsync("/api/v1/messages/dispatch", new { });
            Assert.Equal(viaBefore + 1, fixture.Api.Mail.ViaTenant.Count);
            Assert.Equal("billing@smtp-co.example", fixture.Api.Mail.ViaTenant[^1].From);
        }

        var sent = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{message.GetProperty("id").GetGuid()}", ApiScenario.Json);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
    }

    /// <summary>One customer with one message dispatched to Sent — the state the MTA reports on.</summary>
    private async Task<(Setup S, Guid MessageId)> SentMessageAsync(string name)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var email = $"{Guid.NewGuid():N}@example.test";
        await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts", new { name = "Rana", email, isPrimary = true, isBilling = true });
        await fixture.Database.ExecuteAsync("UPDATE customers SET preferred_language = 'en' WHERE id = @c", ("c", s.CustomerId));
        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET require_approval_before_send = false WHERE tenant_id = @t", ("t", s.Organization.TenantId));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", 100m, dueDate: D(-7), issueDate: D(-37));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        var tpl = (await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=dunning_7&language=en&channel=email", ApiScenario.Json)).GetProperty("items")[0];
        await s.Client.PostAsync($"/api/v1/templates/{tpl.GetProperty("id").GetGuid()}/approve", new { });
        var accountant = await fixture.Database.AddMemberAsync(s.Organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Accountant);
        using var approver = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));
        using var _ = fixture.Api.PinClock(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(7)));
        var m = await s.Client.PostAsync($"/api/v1/cases/{caseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() });
        var id = m.GetProperty("id").GetGuid();
        await approver.PostAsync($"/api/v1/messages/{id}/approve", new { });
        using var send = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{id}/send") { Content = JsonContent.Create(new { }, options: ApiScenario.Json) };
        send.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        var sent = await s.Client.SendAsync(send);
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        await s.Client.PostAsync("/api/v1/messages/dispatch", new { });
        Assert.Equal("Sent", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{id}", ApiScenario.Json)).GetProperty("status").GetString());
        return (s, id);
    }

    /// <summary>AC-03: the signature gate, then Delivered and Bounced through the machine, with the rest counted and untouched.</summary>
    [Fact]
    public async Task Webhook_AppliesDeliveredAndBounced_OnlyWhenSigned()
    {
        var (s, deliveredId) = await SentMessageAsync("Webhook Co.");
        var (b, bouncedId) = await SentMessageAsync("Webhook Bounce Co.");
        var ids = new List<Guid> { deliveredId, bouncedId };

        var payload = JsonSerializer.Serialize(new
        {
            events = new object[]
            {
                new { messageId = $"{ids[0]:N}.{s.Organization.TenantId:N}", @event = "delivered", occurredAt = DateTimeOffset.UtcNow },
                new { messageId = $"{ids[1]:N}.{b.Organization.TenantId:N}", @event = "bounced", reason = "550 5.1.1 user unknown" },
                new { messageId = $"{Guid.NewGuid():N}.{s.Organization.TenantId:N}", @event = "delivered" },   // unknown message
                new { messageId = $"{ids[0]:N}.{Guid.NewGuid():N}", @event = "bounced" },   // wrong tenant in the tag
                new { messageId = "garbage", @event = "opened" },
            },
        });
        using var anonymous = fixture.Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/webhooks/email-events", new StringContent(payload, Encoding.UTF8, "application/json"))).StatusCode);
        using (var badSig = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/email-events") { Content = new StringContent(payload, Encoding.UTF8, "application/json") })
        {
            badSig.Headers.Add("X-Signature", "sha256=" + new string('0', 64));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(badSig)).StatusCode);
        }

        var secret = Environment.GetEnvironmentVariable("EMAIL_WEBHOOK_SECRET")!;
        using var signed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/email-events") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        signed.Headers.Add("X-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))));
        var response = await anonymous.SendAsync(signed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.Equal(2, result.GetProperty("applied").GetInt32());
        Assert.Equal(3, result.GetProperty("skipped").GetInt32());

        var delivered = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{ids[0]}", ApiScenario.Json);
        Assert.Equal("Delivered", delivered.GetProperty("status").GetString());
        var bounced = await b.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{ids[1]}", ApiScenario.Json);
        Assert.Equal("Bounced", bounced.GetProperty("status").GetString());
        Assert.Equal("550 5.1.1 user unknown", await fixture.Database.ScalarAsync<string>("SELECT bounce_reason FROM messages WHERE id = @m", ("m", ids[1])));
        var audit = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?entityType=message", ApiScenario.Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "message.bounced" && e.GetProperty("actorKind").GetString() == "system");

        // Slice 25: the bounce marks the contact; the next send to it is refused until the address is edited.
        var contacts = await b.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{b.CustomerId}/contacts", ApiScenario.Json);
        var contact = contacts.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("550 5.1.1 user unknown", contact.GetProperty("bounceReason").GetString());
        var caseB = (await b.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={b.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        var tplB = (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=dunning_14&language=en&channel=email", ApiScenario.Json)).GetProperty("items")[0];
        await b.Client.PostAsync($"/api/v1/templates/{tplB.GetProperty("id").GetGuid()}/approve", new { });
        using (var _ = fixture.Api.PinClock(new DateTimeOffset(DateTime.UtcNow.Date.AddHours(7))))
        {
            var next = await b.Client.PostAsync($"/api/v1/cases/{caseB}/messages", new { channel = "email", templateId = tplB.GetProperty("id").GetGuid() });
            var accountantB = await fixture.Database.AddMemberAsync(b.Organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Accountant);
            using var approverB = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountantB.Email, accountantB.Password));
            await approverB.PostAsync($"/api/v1/messages/{next.GetProperty("id").GetGuid()}/approve", new { });
            using var send = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{next.GetProperty("id").GetGuid()}/send") { Content = JsonContent.Create(new { }, options: ApiScenario.Json) };
            send.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            var refused = await b.Client.SendAsync(send);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("contact_email_bounced", (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("errors")[0].GetProperty("code").GetString());
        }

        var fixedContact = await b.Client.PatchAsJsonAsync($"/api/v1/customers/{b.CustomerId}/contacts/{contact.GetProperty("id").GetGuid()}", new { email = $"corrected-{Guid.NewGuid():N}@example.test" }, ApiScenario.Json);
        Assert.Equal(JsonValueKind.Null, (await fixedContact.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("bouncedAt").ValueKind);

        // A second delivery of the same event is a no-op: the message is no longer Sent.
        using var again = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/email-events") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        again.Headers.Add("X-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))));
        Assert.Equal(0, (await (await anonymous.SendAsync(again)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("applied").GetInt32());
    }
}
