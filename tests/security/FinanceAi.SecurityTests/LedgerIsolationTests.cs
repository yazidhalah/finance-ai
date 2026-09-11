using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 3b AC-12 / INV-05, T-75: money cannot cross a tenant boundary even with RLS bypassed.</summary>
[Collection(ApiCollection.Name)]
public sealed class LedgerIsolationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task CrossTenantAllocation_IsRejectedByCompositeForeignKey()
    {
        var a = await fixture.Api.NewCustomerAsync("A Co.");
        var b = await fixture.Api.NewCustomerAsync("B Co.");

        var invoiceOfB = await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 100m);
        var paymentOfA = (await a.Client.PostAsync("/api/v1/payments", new { customerId = a.CustomerId, amount = LedgerScenario.M(100m), method = "Cash", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();   // superuser: RLS bypassed

        // A's payment allocated to B's invoice, with tenant A: fk_alloc_invoice refuses.
        await using (var cross = new NpgsqlCommand(
            "INSERT INTO payment_allocations (id, tenant_id, payment_id, invoice_id, amount, currency, effective_date) VALUES (@id, @t, @p, @i, 10, 'JOD', '2026-09-10')", connection))
        {
            cross.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cross.Parameters.AddWithValue("t", a.Organization.TenantId);
            cross.Parameters.AddWithValue("p", paymentOfA);
            cross.Parameters.AddWithValue("i", invoiceOfB);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_alloc_invoice", ex.ConstraintName);
        }

        // ...and with tenant B, fk_alloc_payment refuses. There is no tenant value that makes it fit.
        await using (var cross = new NpgsqlCommand(
            "INSERT INTO payment_allocations (id, tenant_id, payment_id, invoice_id, amount, currency, effective_date) VALUES (@id, @t, @p, @i, 10, 'JOD', '2026-09-10')", connection))
        {
            cross.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cross.Parameters.AddWithValue("t", b.Organization.TenantId);
            cross.Parameters.AddWithValue("p", paymentOfA);
            cross.Parameters.AddWithValue("i", invoiceOfB);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cross.ExecuteNonQueryAsync());
            Assert.Equal("fk_alloc_payment", ex.ConstraintName);
        }

        // Through the API, B's invoice simply does not exist for A.
        var (status, _) = await a.Client.TryPostAsync($"/api/v1/payments/{paymentOfA}/allocations", new { lines = new[] { new { invoiceId = invoiceOfB, amount = LedgerScenario.M(10m) } } });
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task CustomerPosition_IsComputedWithinOneTenant()
    {
        var a = await fixture.Api.NewCustomerAsync("Pos A");
        var b = await fixture.Api.NewCustomerAsync("Pos B");
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "P-A", 100m);
        await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "P-B", 900m);
        await b.Client.PostAsync("/api/v1/payments", new { customerId = b.CustomerId, amount = LedgerScenario.M(50m), method = "Cash", receivedDate = "2026-09-10" });

        var customer = await a.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{a.CustomerId}", ApiScenario.Json);
        var jod = customer.GetProperty("balances")[0];
        Assert.Equal("100.000", jod.GetProperty("openBalance").GetProperty("amount").GetString());
        Assert.Equal("0.000", jod.GetProperty("unappliedCash").GetProperty("amount").GetString());
    }
}
