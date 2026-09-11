using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 5 AC-15: cases, scope rows and the sweep are tenant-bound at every layer.</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseIsolationTests(ApiTestFixture fixture)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);

    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    [Fact]
    public async Task CrossTenantCase_IsRejectedByCompositeForeignKey()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-1", 100m, dueDate: D(10), issueDate: D(40));
        var invoiceOfB = await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 100m, dueDate: D(10), issueDate: D(40));
        await a.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseOfA = (await a.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();   // superuser: RLS bypassed, layer 3 alone

        // A's case scoped to B's invoice, under tenant A: fk_ci_invoice refuses.
        await using (var cross = new NpgsqlCommand("INSERT INTO case_invoices (tenant_id, case_id, invoice_id) VALUES (@t, @c, @i)", connection))
        {
            cross.Parameters.AddWithValue("t", a.Organization.TenantId);
            cross.Parameters.AddWithValue("c", caseOfA);
            cross.Parameters.AddWithValue("i", invoiceOfB);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_ci_invoice", ex.ConstraintName);
        }

        // ...and under tenant B, fk_ci_case refuses. No tenant value fits.
        await using (var cross = new NpgsqlCommand("INSERT INTO case_invoices (tenant_id, case_id, invoice_id) VALUES (@t, @c, @i)", connection))
        {
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("c", caseOfA);
            cross.Parameters.AddWithValue("i", invoiceOfB);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_ci_case", ex.ConstraintName);
        }

        // A case in A for B's customer: fk_case_customer refuses.
        await using (var cross = new NpgsqlCommand("INSERT INTO collection_cases (id, tenant_id, customer_id, case_number) VALUES (gen_random_uuid(), @t, @cu, 77)", connection))
        {
            cross.Parameters.AddWithValue("t", a.Organization.TenantId);
            cross.Parameters.AddWithValue("cu", b.CustomerId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_case_customer", ex.ConstraintName);
        }

        // An activity on A's case under tenant B: fk_activity_case refuses.
        await using (var cross = new NpgsqlCommand("INSERT INTO case_activities (id, tenant_id, case_id, kind, summary) VALUES (gen_random_uuid(), @t, @c, 'note', 'x')", connection))
        {
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("c", caseOfA);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_activity_case", ex.ConstraintName);
        }
    }

    /// <summary>The sweep is the first code that creates rows for many customers at once; it must stay inside its tenant.</summary>
    [Fact]
    public async Task Sweep_IsTenantBound()
    {
        var a = await fixture.Api.NewCustomerAsync("Sweep A");
        var b = await fixture.Api.NewCustomerAsync("Sweep B");
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "A-1", 100m, dueDate: D(10), issueDate: D(40));
        await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 100m, dueDate: D(10), issueDate: D(40));

        var swept = await a.Client.PostAsync("/api/v1/cases/sweep", new { });
        Assert.Equal(1, swept.GetProperty("created").GetInt32());
        Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM collection_cases WHERE tenant_id = @t", ("t", a.Organization.TenantId)));
        Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM collection_cases WHERE tenant_id = @t", ("t", b.Organization.TenantId)));

        // B sees an empty queue; B's own sweep then creates B's case with B's own numbering.
        Assert.Empty((await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items").EnumerateArray());
        await b.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseOfB = (await b.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items")[0];
        Assert.Equal(1, caseOfB.GetProperty("caseNumber").GetInt64());
        Assert.DoesNotContain("Sweep A", (await b.Client.GetStringAsync("/api/v1/cases")));

        // A cannot act on B's case by id (404, never 403), even with cases.assign / cases.escalate.
        var idOfB = caseOfB.GetProperty("caseId").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await a.Client.GetAsync($"/api/v1/cases/{idOfB}")).StatusCode);
        var (assign, _) = await a.Client.TryPostAsync($"/api/v1/cases/{idOfB}/assign", new { userId = a.Organization.OwnerUserId });
        Assert.Equal(404, assign);
        var (escalate, _) = await a.Client.TryPostAsync($"/api/v1/cases/{idOfB}/transitions", new { @event = "escalate", reasonCode = "x" });
        Assert.Equal(404, escalate);
    }
}
