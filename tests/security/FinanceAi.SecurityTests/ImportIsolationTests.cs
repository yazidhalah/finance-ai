using System.Net.Http.Json;
using Npgsql;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 3 AC-19, AC-21. Targeted tests for the new tables beyond the generic enumeration.</summary>
[Collection(ApiCollection.Name)]
public sealed class ImportIsolationTests(ApiTestFixture fixture)
{
    /// <summary>AC-19 / T-75: layer 3 alone. An import row in A cannot point at a batch of B, nor an invoice at a customer of B.</summary>
    [Fact]
    public async Task CrossTenantImportRow_IsRejectedByCompositeForeignKey()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("FK Import A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("FK Import B");

        using var clientB = fixture.Api.AuthenticatedClient(organizationB.OwnerSession);
        var customerOfB = (await (await clientB.PostAsJsonAsync("/api/v1/customers", new { nameEn = "B's customer" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();

        // A batch in B, created as superuser so RLS is out of the picture entirely.
        var batchOfB = Guid.CreateVersion7();
        await using (var batch = new NpgsqlCommand(
            """
            INSERT INTO import_batches (id, tenant_id, file_name, file_hash, file_size, file_kind, file_content, headers, uploaded_by)
            VALUES (@id, @t, 'b.csv', @h, 10, 'csv', '\x00'::bytea, '[]'::jsonb, @u)
            """, connection))
        {
            batch.Parameters.AddWithValue("id", batchOfB);
            batch.Parameters.AddWithValue("t", organizationB.TenantId);
            batch.Parameters.AddWithValue("h", Guid.NewGuid().ToString("N"));
            batch.Parameters.AddWithValue("u", organizationB.OwnerUserId);
            await batch.ExecuteNonQueryAsync();
        }

        // A row in tenant A pointing at B's batch.
        await using (var row = new NpgsqlCommand(
            "INSERT INTO import_rows (id, tenant_id, batch_id, row_no, raw) VALUES (@id, @t, @b, 1, '{}'::jsonb)", connection))
        {
            row.Parameters.AddWithValue("id", Guid.CreateVersion7());
            row.Parameters.AddWithValue("t", organizationA.TenantId);
            row.Parameters.AddWithValue("b", batchOfB);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => row.ExecuteNonQueryAsync());
            Assert.Equal("23503", ex.SqlState);
            Assert.Equal("fk_row_batch", ex.ConstraintName);
        }

        // An invoice in tenant A billed to B's customer.
        await using (var invoice = new NpgsqlCommand(
            """
            INSERT INTO invoices (id, tenant_id, customer_id, invoice_number, issue_date, due_date, currency, net_amount, tax_amount, total_amount, base_currency)
            VALUES (@id, @t, @c, 'X-1', '2026-09-01', '2026-09-01', 'JOD', 1, 0, 1, 'JOD')
            """, connection))
        {
            invoice.Parameters.AddWithValue("id", Guid.CreateVersion7());
            invoice.Parameters.AddWithValue("t", organizationA.TenantId);
            invoice.Parameters.AddWithValue("c", customerOfB);

            var ex = await Assert.ThrowsAsync<PostgresException>(() => invoice.ExecuteNonQueryAsync());
            Assert.Equal("fk_invoice_customer", ex.ConstraintName);
        }
    }

    /// <summary>AC-21 / INV-11, T-23: every money column in the database is numeric(19,3); no floating type anywhere.</summary>
    [Fact]
    public async Task EveryMoneyColumn_IsNumeric19_3()
    {
        await using var connection = fixture.Database.OpenAdmin();

        await using var floats = new NpgsqlCommand(
            """
            SELECT string_agg(table_name || '.' || column_name, ', ')
            FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type IN ('real', 'double precision', 'money')
            """, connection);
        Assert.Null(await floats.ExecuteScalarAsync() as string);

        await using var wrongScale = new NpgsqlCommand(
            """
            SELECT string_agg(table_name || '.' || column_name || ' numeric(' || numeric_precision || ',' || numeric_scale || ')', ', ')
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (column_name LIKE '%\_amount' OR column_name LIKE '%\_balance' OR column_name LIKE '%\_total' OR column_name = 'balance_cache')
              AND NOT (data_type = 'numeric' AND numeric_precision = 19 AND numeric_scale = 3)
            """, connection);
        Assert.Null(await wrongScale.ExecuteScalarAsync() as string);

        // Sanity: the rule is actually checking something.
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'public' AND column_name LIKE '%\\_amount'", connection);
        Assert.True((long)(await count.ExecuteScalarAsync())! >= 4);
    }

    /// <summary>The frozen-after-commit trigger binds the table owner too.</summary>
    [Fact]
    public async Task CommittedImportRows_AreImmutable_EvenForTheOwner()
    {
        var organization = await fixture.Api.CreateOrganizationAsync("Frozen");
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);
        await client.PostAsJsonAsync("/api/v1/customers", new { nameEn = "Frozen Co." }, ApiScenario.Json);

        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("Invoice No,Customer,Issue Date,Total\nFR-1,Frozen Co.,2026-09-01,1\n"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "frozen.csv");
        var batchId = (await (await client.PostAsync("/api/v1/imports", form)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();
        await client.PostAsJsonAsync($"/api/v1/imports/{batchId}/mapping", new
        {
            columnMap = new Dictionary<string, string> { ["Invoice No"] = "invoice_number", ["Customer"] = "customer_name", ["Issue Date"] = "issue_date", ["Total"] = "total_amount" },
        }, ApiScenario.Json);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).StatusCode);

        await using var owner = fixture.Database.OpenMigrator();
        await using (var bind = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", owner))
        {
            bind.Parameters.AddWithValue("t", organization.TenantId.ToString());
            await bind.ExecuteScalarAsync();
        }

        await using var tamper = new NpgsqlCommand("UPDATE import_rows SET outcome = 'Rejected' WHERE batch_id = @b", owner);
        tamper.Parameters.AddWithValue("b", batchId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
        Assert.Contains("immutable", ex.MessageText, StringComparison.OrdinalIgnoreCase);

        await using var remove = new NpgsqlCommand("DELETE FROM import_rows WHERE batch_id = @b", owner);
        remove.Parameters.AddWithValue("b", batchId);
        await Assert.ThrowsAsync<PostgresException>(() => remove.ExecuteNonQueryAsync());
    }
}
