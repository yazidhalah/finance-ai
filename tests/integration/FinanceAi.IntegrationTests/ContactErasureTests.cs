using System.Net.Http.Json;
using System.Text.Json;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// Slice 31 — SEC-93: a contact's personal data is anonymized while the financial and audit records that referenced it
/// stay intact; every copy of the address goes with it; the row is read-only afterwards; the act is audited without PII
/// and needs a fresh re-authentication proof.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ContactErasureTests(ApiTestFixture fixture)
{
    private static string D(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Setup S, Guid ContactId, string Email, Guid CaseId)> OpenAsync(string name)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var email = $"{Guid.NewGuid():N}@example.test";
        var contact = await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts", new { name = "Rana Accounts", roleTitle = "AP", email, phoneE164 = "+962791234567", isPrimary = true, isBilling = true });
        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET require_approval_before_send = false WHERE tenant_id = @t", ("t", s.Organization.TenantId));
        await fixture.Database.ExecuteAsync("UPDATE customers SET preferred_language = 'en' WHERE id = @c", ("c", s.CustomerId));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", 1_000m, dueDate: D(-7), issueDate: D(-37));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        return (s, contact.GetProperty("id").GetGuid(), email, caseId);
    }

    [Fact]
    public async Task Erase_AnonymizesTheContact_AndEveryCopyOfTheAddress()
    {
        var (s, contactId, email, caseId) = await OpenAsync("Erase");
        var tpl = (await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/templates?key=dunning_7&language=en&channel=email", ApiScenario.Json)).GetProperty("items")[0];
        var outbound = (await s.Client.PostAsync($"/api/v1/cases/{caseId}/messages", new { channel = "email", templateId = tpl.GetProperty("id").GetGuid() })).GetProperty("id").GetGuid();
        var inbound = (await s.Client.PostAsync("/api/v1/inbound-messages", new { channel = "email", fromAddress = email, subject = "Re: reminder", body = "we will pay next week" })).GetProperty("id").GetGuid();
        Assert.Equal(email, await fixture.Database.ScalarAsync<string>("SELECT to_address FROM messages WHERE id = @m", ("m", outbound)));

        // Without a proof: refused before anything happens.
        var (unproven, _) = await s.Client.TryPostAsync($"/api/v1/customers/{s.CustomerId}/contacts/{contactId}/erase", new { });
        Assert.Equal(403, unproven);   // reauth_required (SEC-09), as for transfer of ownership
        Assert.Equal("Rana Accounts", await fixture.Database.ScalarAsync<string>("SELECT name FROM customer_contacts WHERE id = @c", ("c", contactId)));

        await s.Client.ReauthAsync();
        var erased = await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts/{contactId}/erase", new { });
        Assert.Equal("Erased contact", erased.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, erased.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, erased.GetProperty("phoneE164").ValueKind);
        Assert.Equal(JsonValueKind.Null, erased.GetProperty("roleTitle").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, erased.GetProperty("erasedAt").ValueKind);
        Assert.True(erased.GetProperty("isPrimary").GetBoolean());                   // structure stays; only the person goes

        // Every copy of the address, on the way out and on the way in.
        Assert.Null(await fixture.Database.ScalarAsync<string>("SELECT to_address FROM messages WHERE id = @m", ("m", outbound)));
        Assert.Null(await fixture.Database.ScalarAsync<string>("SELECT from_address FROM inbound_messages WHERE id = @m", ("m", inbound)));
        Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM customer_contacts WHERE tenant_id = @t AND (email = @e OR phone_e164 = '+962791234567')", ("t", s.Organization.TenantId), ("e", email)));
        // The financial record and the frozen body are untouched (SEC-57): the message still exists on its case.
        Assert.Equal(caseId, await fixture.Database.ScalarAsync<Guid>("SELECT case_id FROM messages WHERE id = @m", ("m", outbound)));
        Assert.Equal("we will pay next week", await fixture.Database.ScalarAsync<string>("SELECT body_raw FROM inbound_messages WHERE id = @m", ("m", inbound)));

        // Read-only from here: an edit and a second erasure are refused; the audit carries the id, not the person.
        using (var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/customers/{s.CustomerId}/contacts/{contactId}") { Content = JsonContent.Create(new { name = "Someone" }, options: ApiScenario.Json) })
        {
            var response = await s.Client.SendAsync(patch);
            Assert.Equal(422, (int)response.StatusCode);
            Assert.Equal("contact_erased", (await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("errors")[0].GetProperty("code").GetString());
        }

        var (again, againBody) = await s.Client.TryPostAsync($"/api/v1/customers/{s.CustomerId}/contacts/{contactId}/erase", new { });
        Assert.Equal(422, again);
        Assert.Equal("contact_erased", againBody.GetProperty("errors")[0].GetProperty("code").GetString());
        var changes = await fixture.Database.ScalarAsync<string>("SELECT changes::text FROM audit_events WHERE tenant_id = @t AND event_type = 'customer.contact_erased'", ("t", s.Organization.TenantId));
        Assert.NotNull(changes);
        Assert.Contains(contactId.ToString(), changes);
        Assert.DoesNotContain(email, changes);
        Assert.DoesNotContain("Rana", changes);
        using (var doc = JsonDocument.Parse(changes))
        {
            var outboundWithAddress = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM messages WHERE tenant_id = @t AND contact_id = @c", ("t", s.Organization.TenantId), ("c", contactId));
            Assert.Equal(outboundWithAddress, doc.RootElement.GetProperty("outboundAddressesCleared").GetInt64());   // every message to the contact, including the sweep's reminder
            Assert.Equal(1, doc.RootElement.GetProperty("inboundAddressesCleared").GetInt32());
            Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM messages WHERE tenant_id = @t AND to_address IS NOT NULL", ("t", s.Organization.TenantId)));
        }
    }

    /// <summary>SEC-13: tenant B cannot erase (or discover) tenant A's contact.</summary>
    [Fact]
    public async Task Erase_IsTenantScoped()
    {
        var (a, contactId, _, _) = await OpenAsync("EraseA");
        var (b, _, _, _) = await OpenAsync("EraseB");
        await b.Client.ReauthAsync();
        var (status, _) = await b.Client.TryPostAsync($"/api/v1/customers/{a.CustomerId}/contacts/{contactId}/erase", new { });
        Assert.Equal(404, status);
        Assert.Equal("Rana Accounts", await fixture.Database.ScalarAsync<string>("SELECT name FROM customer_contacts WHERE id = @c", ("c", contactId)));
    }
}
