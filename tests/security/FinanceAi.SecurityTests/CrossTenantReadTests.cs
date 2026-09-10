using System.Net;
using System.Net.Http.Json;
using FinanceAi.Domain.Authorization;

namespace FinanceAi.SecurityTests;

/// <summary>
/// AC-27, AC-28 / SEC-14, T-71. The requirement in the plainest form it can be stated:
/// <b>a user of one organization cannot read, or authenticate into, another organization's data.</b>
/// <para>
/// The sweeps in <c>EndpointAuthorizationSweepTests</c> prove it mechanically over the route table.
/// This class proves it narratively, over a scenario with two real organizations whose data sits in
/// the same tables, so the guarantee is legible without reading a reflection helper.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CrossTenantReadTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task TenantA_CannotReadTenantB_Members_Organization_Or_Audit()
    {
        var alAmal = await fixture.Api.CreateOrganizationAsync("Al Amal Trading");
        var petraSupplies = await fixture.Api.CreateOrganizationAsync("Petra Supplies");

        // Each organization has staff of its own.
        var alAmalAccountant = await fixture.Database.AddMemberAsync(alAmal.TenantId, TenantRole.Accountant);
        var petraAccountant = await fixture.Database.AddMemberAsync(petraSupplies.TenantId, TenantRole.Accountant);

        using var alAmalClient = fixture.Api.AuthenticatedClient(alAmal.OwnerSession);

        // 1. The member list contains only Al Amal's people.
        var members = await alAmalClient.GetFromJsonAsync<MemberListResponse>(
            "/api/v1/organization/members", ApiScenario.Json);

        Assert.Equal(2, members!.Items.Count);
        Assert.Contains(members.Items, m => m.Email == alAmal.OwnerEmail);
        Assert.Contains(members.Items, m => m.Email == alAmalAccountant.Email);
        Assert.DoesNotContain(members.Items, m => m.Email == petraAccountant.Email);
        Assert.DoesNotContain(members.Items, m => m.Email == petraSupplies.OwnerEmail);

        // 2. Petra's membership id, fetched directly, is not found — not forbidden (SEC-13).
        var petraMembershipId = await fixture.Database.ScalarAsync<Guid>(
            "SELECT id FROM tenant_memberships WHERE user_id = @u", ("u", petraAccountant.UserId));

        var direct = await alAmalClient.GetAsync($"/api/v1/organization/members/{petraMembershipId}");
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);

        // 3. The organization profile is always the caller's own; there is no way to ask for another.
        var organization = await alAmalClient.GetFromJsonAsync<OrganizationResponse>(
            "/api/v1/organization", ApiScenario.Json);

        Assert.Equal(alAmal.TenantId, organization!.Id);
        Assert.Equal("Al Amal Trading", organization.Name);

        // 4. The audit log shows Al Amal's history and nothing else, though both organizations'
        //    rows live in one table, interleaved by time.
        var audit = await alAmalClient.GetFromJsonAsync<AuditListResponse>(
            "/api/v1/audit?limit=200", ApiScenario.Json);

        Assert.NotEmpty(audit!.Items);

        foreach (var entry in audit.Items)
        {
            var owner = await fixture.Database.ScalarAsync<Guid>(
                "SELECT tenant_id FROM audit_events WHERE id = @id", ("id", entry.Id));
            Assert.Equal(alAmal.TenantId, owner);
        }

        var petraAuditRows = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t", ("t", petraSupplies.TenantId));
        Assert.True(petraAuditRows > 0, "Petra must have audit rows for this assertion to mean anything.");

        // 5. An Accountant of Al Amal — a different role, a different user — sees the same boundary.
        var accountantSession = await fixture.Api.LoginAsync(alAmalAccountant.Email, alAmalAccountant.Password);
        using var accountantClient = fixture.Api.AuthenticatedClient(accountantSession);

        var accountantView = await accountantClient.GetFromJsonAsync<MemberListResponse>(
            "/api/v1/organization/members", ApiScenario.Json);

        Assert.Equal(2, accountantView!.Items.Count);
        Assert.DoesNotContain(accountantView.Items, m => m.Email == petraAccountant.Email);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await accountantClient.GetAsync($"/api/v1/organization/members/{petraMembershipId}")).StatusCode);
    }

    /// <summary>
    /// AC-28. Even holding correct credentials for Petra, nothing lets a session for Al Amal reach
    /// Petra's data — and nothing lets Petra's own credentials produce a session for Al Amal.
    /// </summary>
    [Fact]
    public async Task UserOfTenantA_CannotAuthenticateIntoTenantB()
    {
        var alAmal = await fixture.Api.CreateOrganizationAsync("Auth Al Amal");
        var petraSupplies = await fixture.Api.CreateOrganizationAsync("Auth Petra");

        // Petra's owner signs in with entirely valid credentials.
        var petraSession = await fixture.Api.LoginAsync(petraSupplies.OwnerEmail, ApiScenario.ValidPassword);
        Assert.Equal(petraSupplies.TenantId, petraSession.Tenant.Id);

        using var petraClient = fixture.Api.AuthenticatedClient(petraSession);

        // Their tenant list has one entry, and it is not Al Amal.
        var tenants = await petraClient.GetFromJsonAsync<TenantListResponse>("/api/v1/auth/tenants", ApiScenario.Json);
        Assert.Equal(petraSupplies.TenantId, Assert.Single(tenants!.Items).TenantId);

        // Naming Al Amal's id at the one endpoint that accepts a tenant id gets a 404.
        var switchAttempt = await petraClient.PostAsJsonAsync(
            "/api/v1/auth/switch-tenant", new { tenantId = alAmal.TenantId }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.NotFound, switchAttempt.StatusCode);

        // And their session still reports Petra.
        var me = await petraClient.GetFromJsonAsync<MeResponse>("/api/v1/me", ApiScenario.Json);
        Assert.Equal(petraSupplies.TenantId, me!.Tenant.Id);

        // No session for Al Amal was created for Petra's owner at any point.
        var tokens = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM refresh_tokens r
            JOIN users u ON u.id = r.user_id
            WHERE r.tenant_id = @t AND u.email = @e
            """, ("t", alAmal.TenantId), ("e", petraSupplies.OwnerEmail));
        Assert.Equal(0, tokens);
    }

    /// <summary>
    /// A member of one organization writing to another's data is prevented at the database, not only
    /// at the endpoint: an update scoped to A cannot touch a row of B even with the id in hand.
    /// </summary>
    [Fact]
    public async Task WriteScopedToOneTenant_CannotTouchAnotherTenantsRow()
    {
        var alAmal = await fixture.Api.CreateOrganizationAsync("Write A");
        var petraSupplies = await fixture.Api.CreateOrganizationAsync("Write B");

        await using var connection = fixture.Database.OpenApp();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var set = new Npgsql.NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", alAmal.TenantId.ToString());
            await set.ExecuteScalarAsync();
        }

        // A targeted update at Petra's settings row, from inside Al Amal's scope.
        await using var update = new Npgsql.NpgsqlCommand(
            "UPDATE tenant_settings SET ai_enabled = false WHERE tenant_id = @t", connection, transaction);
        update.Parameters.AddWithValue("t", petraSupplies.TenantId);

        // RLS makes the row invisible, so the update matches nothing rather than raising — which is
        // the right outcome: the caller learns nothing about whether the row exists.
        Assert.Equal(0, await update.ExecuteNonQueryAsync());

        await transaction.CommitAsync();

        var stillEnabled = await fixture.Database.ScalarAsync<bool>(
            "SELECT ai_enabled FROM tenant_settings WHERE tenant_id = @t", ("t", petraSupplies.TenantId));
        Assert.True(stillEnabled);
    }
}
