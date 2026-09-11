using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>
/// Slice 9 AC-14: inbound messages and AI suggestions are tenant-bound in all three layers, and what A sends to the
/// model never contains anything of B's.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AiIsolationTests(ApiTestFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    [Fact]
    public async Task CrossTenantInbound_IsRejected()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        var sharedEmail = $"{Guid.NewGuid():N}@example.test";
        // The same contact email exists in both tenants: a reply matches within the caller's tenant only.
        await a.Client.PostAsync($"/api/v1/customers/{a.CustomerId}/contacts", new { name = "A contact", email = sharedEmail, isPrimary = true });
        await b.Client.PostAsync($"/api/v1/customers/{b.CustomerId}/contacts", new { name = "B contact", email = sharedEmail, isPrimary = true });
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-SECRET-1", 100m, dueDate: D(-10), issueDate: D(-40));
        await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-SECRET-1", 200m, dueDate: D(-10), issueDate: D(-40));
        await a.Client.PostAsync("/api/v1/cases/sweep", new { });
        await b.Client.PostAsync("/api/v1/cases/sweep", new { });
        fixture.Api.Ai.Clear();

        var inA = await a.Client.PostAsync("/api/v1/inbound-messages", new { channel = "email", fromAddress = sharedEmail, body = "A's private reply" });
        Assert.Equal(a.CustomerId, inA.GetProperty("customerId").GetGuid());
        var inB = await b.Client.PostAsync("/api/v1/inbound-messages", new { channel = "email", fromAddress = sharedEmail, body = "B's private reply" });
        Assert.Equal(b.CustomerId, inB.GetProperty("customerId").GetGuid());

        fixture.Api.Ai.Enqueue(AiScript.Response("acknowledgement", 0.9m));
        var suggestionA = await a.Client.PostAsync($"/api/v1/inbound-messages/{inA.GetProperty("id").GetGuid()}/classify", new { });
        // AI-07: the projection A sent names A's invoice and nothing of B's.
        var sent = JsonSerializer.Serialize(fixture.Api.Ai.Requests.Last());
        Assert.Contains("A-SECRET-1", sent);
        Assert.DoesNotContain("B-SECRET-1", sent);
        Assert.DoesNotContain("B's private reply", sent);

        // Layer 1: B sees none of it and cannot act on it.
        var idA = inA.GetProperty("id").GetGuid();
        var sidA = suggestionA.GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/inbound-messages/{idA}")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/ai/suggestions/{sidA}")).StatusCode);
        foreach (var path in new[] { $"/api/v1/inbound-messages/{idA}/classify", $"/api/v1/ai/suggestions/{sidA}/approve", $"/api/v1/ai/suggestions/{sidA}/reject", $"/api/v1/ai/suggestions/{sidA}/edit-and-approve" })
        {
            var (status, _) = await b.Client.TryPostAsync(path, new { });
            Assert.Equal(404, status);
        }

        var listB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/inbound-messages", ApiScenario.Json);
        Assert.DoesNotContain(listB.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == idA);
        Assert.DoesNotContain("A's private reply", await b.Client.GetStringAsync("/api/v1/ai/suggestions"));
        // B cannot bind its own message to A's customer: 404, the customer does not exist for B.
        var (match, _) = await b.Client.TryPostAsync($"/api/v1/inbound-messages/{inB.GetProperty("id").GetGuid()}/match-customer", new { customerId = a.CustomerId });
        Assert.Equal(404, match);
        var (create, _) = await b.Client.TryPostAsync("/api/v1/inbound-messages", new { channel = "manual", body = "x", customerId = a.CustomerId });
        Assert.Equal(404, create);

        // Layer 3 alone: a row in B pointing at A's customer / case / message / suggestion is refused by the composite keys.
        var caseA = inA.GetProperty("caseId").GetGuid();
        await using var connection = fixture.Database.OpenAdmin();
        foreach (var (column, value, constraint) in new[] { ("customer_id", a.CustomerId, "fk_inbound_customer"), ("case_id", caseA, "fk_inbound_case"), ("last_suggestion_id", sidA, "fk_inbound_last_suggestion") })
        {
            await using var cross = new NpgsqlCommand($"INSERT INTO inbound_messages (id, tenant_id, channel, body_raw, received_at, {column}) VALUES (gen_random_uuid(), @t, 'manual', 'x', now(), @v)", connection);
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("v", value);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal(constraint, ex.ConstraintName);
        }

        // Slice 20: the polymorphic subject has no composite key, so a trigger stands in — a suggestion in B whose
        // subject is A's message, a message that does not exist, or A's case or tenant, is refused.
        foreach (var (subjectType, subjectId) in new[] { ("inbound_message", idA), ("inbound_message", Guid.NewGuid()), ("case", caseA), ("tenant", a.Organization.TenantId) })
        {
            await using var smuggle = new NpgsqlCommand(
                """
                INSERT INTO ai_suggestions (id, tenant_id, operation, subject_type, subject_id, model_name, model_digest, prompt_version, schema_version,
                                            input_ref, input_hash, output_json, confidence, validation_status, latency_ms)
                VALUES (gen_random_uuid(), @t, 'classify_customer_reply', @st, @sid, 'm', 'd', 'v1', 'v1', '{}', repeat('0', 64), '{}', 0.5, 'valid', 1)
                """, connection);
            smuggle.Parameters.AddWithValue("t", b.Organization.TenantId);
            smuggle.Parameters.AddWithValue("st", subjectType);
            smuggle.Parameters.AddWithValue("sid", subjectId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => smuggle.ExecuteNonQueryAsync());
            Assert.Equal("ai_suggestions_subject_exists", ex.ConstraintName);
        }

        // Layer 2 alone: with B's tenant set, A's rows do not exist.
        await using var asB = fixture.Database.OpenApp();
        await using var tx = await asB.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", asB, tx))
        {
            set.Parameters.AddWithValue("t", b.Organization.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM inbound_messages WHERE id = @id", asB, tx))
        {
            count.Parameters.AddWithValue("id", idA);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM ai_suggestions WHERE id = @id", asB, tx))
        {
            count.Parameters.AddWithValue("id", sidA);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }
    }
}
