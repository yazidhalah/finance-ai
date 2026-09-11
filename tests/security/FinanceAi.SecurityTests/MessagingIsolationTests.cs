using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 8 AC-15: templates, messages and the dispatcher are tenant-bound; nothing of A's is visible to or sent by B.</summary>
[Collection(ApiCollection.Name)]
public sealed class MessagingIsolationTests(ApiTestFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    [Fact]
    public async Task CrossTenantMessage_IsRejected()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        await a.Client.PostAsync($"/api/v1/customers/{a.CustomerId}/contacts", new { name = "A contact", email = $"{Guid.NewGuid():N}@example.test", isPrimary = true });
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-1", 100m, dueDate: D(-10), issueDate: D(-40));
        await a.Client.PostAsync("/api/v1/cases/sweep", new { });
        await b.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseA = (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();

        var templateA = (await a.Client.PostAsync("/api/v1/templates", new { key = "private_note", channel = "email", language = "en", subject = "A only", body = "Hello {{contact_name}}" })).GetProperty("id").GetGuid();
        await a.Client.PostAsync($"/api/v1/templates/{templateA}/approve", new { });
        var messageA = (await a.Client.PostAsync($"/api/v1/cases/{caseA}/messages", new { channel = "email", language = "en", templateId = templateA })).GetProperty("id").GetGuid();

        // B sees none of it and cannot act on it.
        Assert.DoesNotContain("private_note", await b.Client.GetStringAsync("/api/v1/templates"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/templates/{templateA}")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/messages/{messageA}")).StatusCode);
        var (approve, _) = await b.Client.TryPostAsync($"/api/v1/messages/{messageA}/approve", new { });
        Assert.Equal(404, approve);
        // B composing on A's case with A's template: 404 on the case, before the template is even looked at.
        var (compose, _) = await b.Client.TryPostAsync($"/api/v1/cases/{caseA}/messages", new { channel = "email", language = "en", templateId = templateA });
        Assert.Equal(404, compose);
        Assert.Equal(0, (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/messages", ApiScenario.Json)).GetProperty("totalCount").GetInt32());

        // Layer 3 alone: a message in B for A's customer / case / template.
        await using var connection = fixture.Database.OpenAdmin();
        foreach (var (column, value, constraint) in new[] { ("customer_id", a.CustomerId, "fk_message_customer"), ("case_id", caseA, "fk_message_case"), ("template_id", templateA, "fk_message_template") })
        {
            await using var cross = new NpgsqlCommand(
                $"INSERT INTO messages (id, tenant_id, customer_id, {(column == "customer_id" ? "channel" : column + ", channel")}, language, body) VALUES (gen_random_uuid(), @t, @cu, {(column == "customer_id" ? "'email'" : "@v, 'email'")}, 'en', 'x')", connection);
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("cu", column == "customer_id" ? a.CustomerId : b.CustomerId);
            if (column != "customer_id") cross.Parameters.AddWithValue("v", value);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal(constraint, ex.ConstraintName);
        }

        // B's dispatcher never sends A's queued mail.
        await a.Client.PostAsync($"/api/v1/messages/{messageA}/approve", new { });
        try
        {
            fixture.Api.Clock.Override = new DateTimeOffset(Today.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(3));
            using var send = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{messageA}/send") { Content = JsonContent.Create(new { }, options: ApiScenario.Json) };
            send.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            Assert.Equal(System.Net.HttpStatusCode.OK, (await a.Client.SendAsync(send)).StatusCode);
            var byB = await b.Client.PostAsync("/api/v1/messages/dispatch", new { });
            Assert.Equal(0, byB.GetProperty("sent").GetInt32());
            Assert.Equal("Queued", (await a.Client.GetFromJsonAsync<JsonElement>($"/api/v1/messages/{messageA}", ApiScenario.Json)).GetProperty("status").GetString());
            var byA = await a.Client.PostAsync("/api/v1/messages/dispatch", new { });
            Assert.Equal(1, byA.GetProperty("sent").GetInt32());
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }
}
