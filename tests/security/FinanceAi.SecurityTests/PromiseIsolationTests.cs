using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 6 AC-15.</summary>
[Collection(ApiCollection.Name)]
public sealed class PromiseIsolationTests(ApiTestFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);

    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    [Fact]
    public async Task CrossTenantPromise_IsRejectedByCompositeForeignKey_AndEvaluationStaysHome()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        var invoiceA = await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-1", 100m, dueDate: D(-10), issueDate: D(-40));
        var invoiceB = await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 100m, dueDate: D(-10), issueDate: D(-40));
        await a.Client.PostAsync("/api/v1/cases/sweep", new { });
        await b.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseA = (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        var caseB = (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();

        // Through the API: A cannot promise on B's case, nor cover B's invoice from A's case.
        var (foreignCase, _) = await a.Client.TryPostAsync($"/api/v1/cases/{caseB}/promises", new { invoiceIds = new[] { invoiceB }, promisedAmount = M(10m), promisedDate = D(3), source = "call" });
        Assert.Equal(404, foreignCase);
        var (foreignInvoice, _) = await a.Client.TryPostAsync($"/api/v1/cases/{caseA}/promises", new { invoiceIds = new[] { invoiceB }, promisedAmount = M(10m), promisedDate = D(3), source = "call" });
        Assert.Equal(422, foreignInvoice);   // not in scope — and it could not be, RLS never showed it

        var promiseA = (await a.Client.PostAsync($"/api/v1/cases/{caseA}/promises", new { invoiceIds = new[] { invoiceA }, promisedAmount = M(100m), promisedDate = D(1), source = "call" })).GetProperty("id").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();   // superuser: layer 3 alone
        await using (var cross = new NpgsqlCommand("INSERT INTO ptp_invoices (tenant_id, ptp_id, invoice_id) VALUES (@t, @p, @i)", connection))
        {
            cross.Parameters.AddWithValue("t", a.Organization.TenantId);
            cross.Parameters.AddWithValue("p", promiseA);
            cross.Parameters.AddWithValue("i", invoiceB);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_pi_invoice", ex.ConstraintName);
        }

        await using (var cross = new NpgsqlCommand("INSERT INTO promises_to_pay (id, tenant_id, case_id, customer_id, status, promised_amount, currency, promised_date, deadline_date, source) VALUES (gen_random_uuid(), @t, @c, @cu, 'Proposed', 1, 'JOD', current_date, current_date, 'call')", connection))
        {
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("c", caseA);
            cross.Parameters.AddWithValue("cu", b.CustomerId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_ptp_case", ex.ConstraintName);
        }

        // B cannot read, confirm or cancel A's promise; B's history knows nothing of A.
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/v1/promises/{promiseA}")).StatusCode);
        var (cancel, _) = await b.Client.TryPostAsync($"/api/v1/promises/{promiseA}/cancel", new { reason = "x" });
        Assert.Equal(404, cancel);
        Assert.Equal(0, (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/promises", ApiScenario.Json)).GetProperty("totalCount").GetInt32());

        // B's sweep, run past A's deadline, does not judge A's promise.
        try
        {
            var deadline = DateOnly.Parse((await a.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promiseA}", ApiScenario.Json)).GetProperty("deadlineDate").GetString()!);
            fixture.Api.Clock.Override = new DateTimeOffset(deadline.AddDays(1).ToDateTime(new TimeOnly(7, 0)), TimeSpan.FromHours(3));
            await b.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal("Active", (await a.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promiseA}", ApiScenario.Json)).GetProperty("status").GetString());
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }
}
