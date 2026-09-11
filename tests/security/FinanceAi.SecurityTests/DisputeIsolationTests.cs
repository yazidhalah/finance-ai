using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 7 AC-15.</summary>
[Collection(ApiCollection.Name)]
public sealed class DisputeIsolationTests(ApiTestFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);

    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    [Fact]
    public async Task CrossTenantDispute_IsRejected()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        var invoiceA = await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-1", 1_000m, dueDate: D(-10), issueDate: D(-40));
        var invoiceB = await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 1_000m, dueDate: D(-10), issueDate: D(-40));
        await a.Client.PostAsync("/api/v1/cases/sweep", new { });
        await b.Client.PostAsync("/api/v1/cases/sweep", new { });

        // Through the API: A cannot dispute B's invoice (404, never 403).
        var (foreign, _) = await a.Client.TryPostAsync($"/api/v1/invoices/{invoiceB}/disputes", new { reasonCode = "other", disputedAmount = M(10m) });
        Assert.Equal(404, foreign);

        var disputeA = (await a.Client.PostAsync($"/api/v1/invoices/{invoiceA}/disputes", new { reasonCode = "goods_damaged", disputedAmount = M(400m) })).GetProperty("id").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();   // superuser: layer 3 alone
        await using (var cross = new NpgsqlCommand(
            "INSERT INTO disputes (id, tenant_id, invoice_id, customer_id, reason_code, disputed_amount, currency, first_response_due_at, resolution_due_at) VALUES (gen_random_uuid(), @t, @i, @cu, 'other', 1, 'JOD', now(), now())", connection))
        {
            cross.Parameters.AddWithValue("t", a.Organization.TenantId);
            cross.Parameters.AddWithValue("i", invoiceB);
            cross.Parameters.AddWithValue("cu", a.CustomerId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_dispute_invoice", ex.ConstraintName);
        }

        await using (var cross = new NpgsqlCommand("INSERT INTO dispute_evidence (id, tenant_id, dispute_id, file_name, content_type, size_bytes, sha256, content) VALUES (gen_random_uuid(), @t, @d, 'x.pdf', 'application/pdf', 1, 'a', '\\x25')", connection))
        {
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("d", disputeA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_evidence_dispute", ex.ConstraintName);
        }

        await using (var cross = new NpgsqlCommand("INSERT INTO payment_verification_tasks (id, tenant_id, invoice_id, customer_id, dispute_id) VALUES (gen_random_uuid(), @t, @i, @cu, @d)", connection))
        {
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("i", invoiceB);
            cross.Parameters.AddWithValue("cu", b.CustomerId);
            cross.Parameters.AddWithValue("d", disputeA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_pvt_dispute", ex.ConstraintName);
        }

        // B sees nothing of A's dispute: list, detail, resolve, aging, queue.
        Assert.Equal(0, (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/disputes", ApiScenario.Json)).GetProperty("totalCount").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/disputes/{disputeA}")).StatusCode);
        var (resolve, _) = await b.Client.TryPostAsync($"/api/v1/disputes/{disputeA}/resolve", new { outcome = "rejected", reason = "x" });
        Assert.Equal(404, resolve);
        var agingB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/aging", ApiScenario.Json);
        Assert.Equal("0.000", agingB.GetProperty("currencies")[0].GetProperty("disputedTotal").GetProperty("amount").GetString());
        var agingA = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/aging", ApiScenario.Json);
        Assert.Equal("400.000", agingA.GetProperty("currencies")[0].GetProperty("disputedTotal").GetProperty("amount").GetString());
        var queueB = (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items")[0];
        Assert.Equal(0, queueB.GetProperty("openDisputes").GetInt32());
    }
}
