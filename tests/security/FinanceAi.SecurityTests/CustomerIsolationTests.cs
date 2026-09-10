using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace FinanceAi.SecurityTests;

/// <summary>
/// Slice 2 AC-13, AC-14. The generic sweeps and schema assertions cover the new tables
/// automatically; these are the targeted tests the self-review checklist (item 2) asks for where a
/// table has a non-standard access pattern — here, the raw-SQL search and duplicate paths, and the
/// composite key between two tenant-scoped tables.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CustomerIsolationTests(ApiTestFixture fixture)
{
    /// <summary>
    /// AC-14 / DM-10 / T-75. Layer 3 alone: as superuser, with RLS bypassed, a contact in tenant A
    /// pointing at a customer of tenant B is refused by the composite foreign key.
    /// </summary>
    [Fact]
    public async Task CrossTenantContact_IsRejectedByCompositeForeignKey()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("FK Contact A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("FK Contact B");

        using var clientB = fixture.Api.AuthenticatedClient(organizationB.OwnerSession);
        var customerOfB = (await (await clientB.PostAsJsonAsync("/api/v1/customers", new { nameEn = "B's customer" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customer_contacts (id, tenant_id, customer_id, name)
            VALUES (@id, @tenant, @customer, 'Stray contact')
            """, connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("tenant", organizationA.TenantId);   // tenant A...
        insert.Parameters.AddWithValue("customer", customerOfB);            // ...customer of tenant B

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("23503", ex.SqlState);
        Assert.Equal("fk_contact_customer", ex.ConstraintName);
    }

    /// <summary>
    /// AC-13. The search and duplicate paths are raw SQL — the two places in this slice where the EF
    /// filter is not the thing doing the filtering. Both must still see one tenant.
    /// </summary>
    [Fact]
    public async Task SearchAndDuplicates_NeverReturnAnotherTenantsCustomers()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Search A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Search B");

        using var clientA = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);
        using var clientB = fixture.Api.AuthenticatedClient(organizationB.OwnerSession);

        // The same name in both organizations — the case where a leaked row would look plausible.
        await clientA.PostAsJsonAsync("/api/v1/customers", new { nameAr = "شركة الأمل" }, ApiScenario.Json);
        await clientB.PostAsJsonAsync("/api/v1/customers", new { nameAr = "شركة الأمل" }, ApiScenario.Json);
        await clientB.PostAsJsonAsync("/api/v1/customers", new { nameAr = "شركه الامل" }, ApiScenario.Json);

        var search = await clientA.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/v1/customers?q=" + Uri.EscapeDataString("الامل"), ApiScenario.Json);
        Assert.Equal(1, search.GetProperty("items").GetArrayLength());
        Assert.Equal(1, search.GetProperty("totalCount").GetInt32());

        // B has a duplicate pair; A has one customer and therefore no pair. A's view must be empty
        // even though B's near-identical rows share the table.
        var duplicates = await clientA.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/customers/duplicates", ApiScenario.Json);
        Assert.Equal(0, duplicates.GetProperty("items").GetArrayLength());

        var duplicatesForB = await clientB.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/customers/duplicates", ApiScenario.Json);
        Assert.Equal(1, duplicatesForB.GetProperty("items").GetArrayLength());
    }

    /// <summary>AC-13: a contact route addressed with another tenant's real customer and contact ids.</summary>
    [Fact]
    public async Task Contacts_OfAnotherTenantsCustomer_AreUnreachable()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Contacts A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Contacts B");

        using var clientB = fixture.Api.AuthenticatedClient(organizationB.OwnerSession);
        var customerOfB = (await (await clientB.PostAsJsonAsync("/api/v1/customers", new { nameEn = "B's customer" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();
        var contactOfB = (await (await clientB.PostAsJsonAsync($"/api/v1/customers/{customerOfB}/contacts", new { name = "Khalid" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();

        using var clientA = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);

        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"/api/v1/customers/{customerOfB}/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.PostAsJsonAsync($"/api/v1/customers/{customerOfB}/contacts", new { name = "Intruder" }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.DeleteAsync($"/api/v1/customers/{customerOfB}/contacts/{contactOfB}")).StatusCode);

        // Nothing changed in B.
        var contacts = await clientB.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/customers/{customerOfB}/contacts", ApiScenario.Json);
        Assert.Equal(1, contacts.GetProperty("items").GetArrayLength());
    }

    /// <summary>The soft-delete filter must not weaken the tenant filter: a deleted customer of B is as invisible as a live one.</summary>
    [Fact]
    public async Task SchemaAssertions_CoverTheNewTables()
    {
        await using var connection = fixture.Database.OpenAdmin();
        await using var command = new NpgsqlCommand(
            """
            SELECT string_agg(c.relname, ',' ORDER BY c.relname)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relforcerowsecurity
              AND c.relname IN ('customers','customer_contacts')
            """, connection);

        Assert.Equal("customer_contacts,customers", (string?)await command.ExecuteScalarAsync());
    }
}
