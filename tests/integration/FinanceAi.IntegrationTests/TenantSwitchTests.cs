using System.Net;
using System.Net.Http.Json;
using FinanceAi.Domain.Authorization;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// AC-18, AC-28. <c>/auth/switch-tenant</c> is the only endpoint in the system that accepts a tenant
/// id from a request (API-01), so it is the only place where "which organization am I in?" could be
/// attacked. These tests are about that one door.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantSwitchTests(ApiTestFixture fixture)
{
    /// <summary>AC-18 / SEC-13: another organization's id is not forbidden, it is absent.</summary>
    [Fact]
    public async Task SwitchTenant_ToForeignTenant_Returns404()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Tenant A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Tenant B");

        using var client = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/switch-tenant", new { tenantId = organizationB.TenantId }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("not_found", problem!.Code);

        // No session was minted for B, and nothing was audited into B's log.
        var tokensForB = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE tenant_id = @t AND user_id = @u",
            ("t", organizationB.TenantId), ("u", organizationA.OwnerUserId));
        Assert.Equal(0, tokensForB);
    }

    /// <summary>
    /// AC-28. The headline guarantee: correct credentials for organization A never yield a session
    /// scoped to organization B. Login does not take a tenant at all, and switching is matched
    /// against the caller's own memberships — so there is no input that could carry one.
    /// </summary>
    [Fact]
    public async Task UserOfTenantA_CannotObtainTokenForTenantB()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Isolation A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Isolation B");

        // Correct credentials, but nothing anywhere lets them name a tenant.
        var session = await fixture.Api.LoginAsync(organizationA.OwnerEmail, ApiScenario.ValidPassword);
        Assert.Equal(organizationA.TenantId, session.Tenant.Id);
        Assert.NotEqual(organizationB.TenantId, session.Tenant.Id);

        using var client = fixture.Api.AuthenticatedClient(session);

        // The tenant list contains only what they are a member of.
        var tenants = await client.GetFromJsonAsync<TenantListResponse>("/api/v1/auth/tenants", ApiScenario.Json);
        Assert.Equal(organizationA.TenantId, Assert.Single(tenants!.Items).TenantId);

        // Switching to B fails...
        var switchResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/switch-tenant", new { tenantId = organizationB.TenantId }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.NotFound, switchResponse.StatusCode);

        // ...and passing B's id in a login body is rejected outright rather than honoured or ignored.
        using var anonymous = fixture.Api.CreateCookieClient();
        var loginWithTenant = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organizationA.OwnerEmail, password = ApiScenario.ValidPassword, tenantId = organizationB.TenantId },
            ApiScenario.Json);

        Assert.Equal(HttpStatusCode.BadRequest, loginWithTenant.StatusCode);

        var problem = await loginWithTenant.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("unexpected_field", problem!.Code);

        // The session in hand still sees only A.
        var me = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", ApiScenario.Json);
        Assert.Equal(organizationA.TenantId, me!.Tenant.Id);
    }

    /// <summary>A genuine multi-organization user switches, and the new session is scoped to the
    /// role they hold <i>there</i> — not the one they hold anywhere else (doc 01 §5).</summary>
    [Fact]
    public async Task SwitchTenant_ToOwnTenant_IssuesASessionWithThatTenantsRole()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Multi A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Multi B");

        // The same person, an Owner in A, joins B as a Collector.
        await using (var connection = fixture.Database.OpenAdmin())
        {
            await using var join = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO tenant_memberships (id, tenant_id, user_id, role, status)
                VALUES (@id, @tenant, @user, 'Collector', 'Active')
                """, connection);
            join.Parameters.AddWithValue("id", Guid.CreateVersion7());
            join.Parameters.AddWithValue("tenant", organizationB.TenantId);
            join.Parameters.AddWithValue("user", organizationA.OwnerUserId);
            await join.ExecuteNonQueryAsync();
        }

        using var client = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);

        var tenants = await client.GetFromJsonAsync<TenantListResponse>("/api/v1/auth/tenants", ApiScenario.Json);
        Assert.Equal(2, tenants!.Items.Count);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/switch-tenant", new { tenantId = organizationB.TenantId }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var switched = await response.Content.ReadFromJsonAsync<SessionResponse>(ApiScenario.Json);
        Assert.Equal(organizationB.TenantId, switched!.Tenant.Id);
        Assert.Equal("Collector", switched.Role);

        // A Collector has no users.read, so the very same person now cannot list members in B.
        Assert.DoesNotContain(Permissions.UsersRead, switched.Permissions);

        using var inB = fixture.Api.AuthenticatedClient(switched);
        Assert.Equal(HttpStatusCode.Forbidden, (await inB.GetAsync("/api/v1/organization/members")).StatusCode);

        // ...while the old session for A still works and still sees A.
        var stillInA = await client.GetFromJsonAsync<MeResponse>("/api/v1/me", ApiScenario.Json);
        Assert.Equal(organizationA.TenantId, stillInA!.Tenant.Id);
        Assert.Equal("Owner", stillInA.Role);
    }

    [Fact]
    public async Task SwitchTenant_WithoutATenantId_Returns400()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/switch-tenant", new { tenantId = (Guid?)null }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SwitchTenant_WithoutAToken_Returns401()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/switch-tenant", new { tenantId = organization.TenantId }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
