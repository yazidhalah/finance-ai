using System.Net.Http.Json;
using System.Text.Json;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 4 AC-17: a report is a wide read; it must be as tenant-bound as a single row.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgingIsolationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Aging_IsTenantBound()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-1", 1_000m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 7_000m, dueDate: "2026-08-01", issueDate: "2026-07-01");

        var reportA = await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/aging?asOf=2026-09-11&groupBy=customer", ApiScenario.Json);
        var reportB = await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/aging?asOf=2026-09-11&groupBy=customer", ApiScenario.Json);
        Assert.Equal("1000.000", reportA.GetProperty("currencies")[0].GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("7000.000", reportB.GetProperty("currencies")[0].GetProperty("total").GetProperty("amount").GetString());
        Assert.DoesNotContain("B Co.", reportA.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("A Co.", reportB.GetRawText(), StringComparison.Ordinal);

        // Layer 2 alone: the app role calling fn_aging with the *other* tenant's id, under A's RLS scope, gets nothing.
        await using var connection = fixture.Database.OpenApp();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var scope = new Npgsql.NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, tx))
        {
            scope.Parameters.AddWithValue("t", a.Organization.TenantId.ToString());
            await scope.ExecuteScalarAsync();
        }

        await using var probe = new Npgsql.NpgsqlCommand("SELECT count(*) FROM fn_aging(@other, date '2026-09-11', 'due_date', 'Asia/Amman')", connection, tx);
        probe.Parameters.AddWithValue("other", b.Organization.TenantId);
        Assert.Equal(0L, (long)(await probe.ExecuteScalarAsync())!);

        await using var own = new Npgsql.NpgsqlCommand("SELECT count(*) FROM fn_aging(@own, date '2026-09-11', 'due_date', 'Asia/Amman')", connection, tx);
        own.Parameters.AddWithValue("own", a.Organization.TenantId);
        Assert.Equal(1L, (long)(await own.ExecuteScalarAsync())!);

        // Drill-through with a foreign customer id is a 404, not an empty 200 (API-03).
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/v1/reports/aging/customers/{b.CustomerId}")).StatusCode);
        // Export of the other tenant's data by id filter: the filter cannot widen the scope.
        var filtered = await a.Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging?customerId={b.CustomerId}", ApiScenario.Json);
        Assert.Empty(filtered.GetProperty("currencies").EnumerateArray());
    }
}
