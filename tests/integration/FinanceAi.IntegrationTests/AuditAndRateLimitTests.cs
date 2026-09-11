using System.Net;
using System.Net.Http.Json;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;

namespace FinanceAi.IntegrationTests;

/// <summary>AC-38, AC-39 / SEC-50: the auth events that must be on the record.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthAuditTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task AuthEvents_AreAudited()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        // Registration wrote the organization, the user and the Owner membership.
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.TenantCreated, 1);
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.UserRegistered, 1);
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.MembershipCreated, 1);

        // CreateOrganizationAsync signed the Owner in once.
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.LoginSucceeded, 1);

        using var client = fixture.Api.CreateCookieClient();

        await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = "wrong" },
            ApiScenario.Json);
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.LoginFailed, 1);

        var session = await fixture.Api.LoginAsync(organization.OwnerEmail, ApiScenario.ValidPassword);
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.LoginSucceeded, 2);

        using var authenticated = fixture.Api.CreateCookieClient();
        await authenticated.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = ApiScenario.ValidPassword },
            ApiScenario.Json);
        authenticated.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);

        await authenticated.PostAsync("/api/v1/auth/refresh", null);
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.TokenRefreshed, 1);

        await authenticated.PostAsync("/api/v1/auth/logout", null);
        await AssertEventCountAsync(organization.TenantId, AuditEventTypes.LoggedOut, 1);

        // SEC-52: the actor is recorded, and it is a person, not "the system".
        var actorKinds = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND actor_kind <> 'user'",
            ("t", organization.TenantId));
        Assert.Equal(0, actorKinds);

        var withoutActor = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND actor_user_id IS NULL",
            ("t", organization.TenantId));
        Assert.Equal(0, withoutActor);
    }

    /// <summary>
    /// A failed sign-in for an address nobody owns writes no audit row, deliberately: it has no
    /// tenant to belong to, and inventing one would turn the audit log into the enumeration oracle
    /// that SEC-07 exists to prevent. It is recorded in the structured log instead.
    /// </summary>
    [Fact]
    public async Task FailedLoginForAnUnknownEmail_IsNotAuditedAgainstAnyTenant()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        var before = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events");

        using var client = fixture.Api.CreateCookieClient();
        await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = $"ghost-{Guid.NewGuid():N}@example.jo", password = "wrong" },
            ApiScenario.Json);

        var after = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events");
        Assert.Equal(before, after);

        Assert.NotEqual(Guid.Empty, organization.TenantId);
    }

    [Fact]
    public async Task OrganizationUpdate_IsAuditedWithTheFieldsThatChanged()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        await client.PatchAsync(
            "/api/v1/organization",
            JsonContent.Create(new { legalName = "Al Amal LLC" }, options: ApiScenario.Json));

        var changes = await fixture.Database.ScalarAsync<string>(
            """
            SELECT changes::text FROM audit_events
            WHERE tenant_id = @t AND event_type = 'tenant.updated'
            ORDER BY id DESC LIMIT 1
            """, ("t", organization.TenantId));

        Assert.NotNull(changes);
        Assert.Contains("legalName", changes, StringComparison.Ordinal);
        Assert.Contains("Al Amal LLC", changes, StringComparison.Ordinal);

        // Slice 19 (doc 06 §6.11): the viewer gets the before/after values through the API, not only the database.
        var page = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/audit?eventType=tenant.updated", ApiScenario.Json);
        var row = page.GetProperty("items")[0];
        Assert.Equal("Al Amal LLC", row.GetProperty("changes").GetProperty("legalName").GetProperty("new").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("changes").GetProperty("legalName").GetProperty("old").ValueKind);
    }

    /// <summary>AC-39 / SEC-54: the audit log is readable within one's own organization only.</summary>
    [Fact]
    public async Task Audit_IsReadableOnlyWithinOwnTenant()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Audit A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Audit B");

        using var clientA = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);

        var page = await clientA.GetFromJsonAsync<AuditListResponse>("/api/v1/audit", ApiScenario.Json);

        Assert.NotEmpty(page!.Items);

        // Every visible row belongs to A. B's registration wrote rows at the same time, in the same
        // table, and none of them are reachable.
        var visibleIds = page.Items.Select(i => i.Id).ToHashSet();

        var idsBelongingToB = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t", ("t", organizationB.TenantId));
        Assert.True(idsBelongingToB > 0, "Tenant B should have audit rows for this test to mean anything.");

        foreach (var id in visibleIds)
        {
            var owner = await fixture.Database.ScalarAsync<Guid>(
                "SELECT tenant_id FROM audit_events WHERE id = @id", ("id", id));
            Assert.Equal(organizationA.TenantId, owner);
        }
    }

    [Fact]
    public async Task Audit_RequiresTheAuditReadPermission()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        // Doc 01 §5.1: a Collector has no audit.read.
        var collector = await fixture.Database.AddMemberAsync(organization.TenantId, TenantRole.Collector);
        var session = await fixture.Api.LoginAsync(collector.Email, collector.Password);

        using var client = fixture.Api.AuthenticatedClient(session);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit")).StatusCode);

        // ...but a Viewer does.
        var viewer = await fixture.Database.AddMemberAsync(organization.TenantId, TenantRole.Viewer);
        var viewerSession = await fixture.Api.LoginAsync(viewer.Email, viewer.Password);

        using var viewerClient = fixture.Api.AuthenticatedClient(viewerSession);
        Assert.Equal(HttpStatusCode.OK, (await viewerClient.GetAsync("/api/v1/audit")).StatusCode);
    }

    private async Task AssertEventCountAsync(Guid tenantId, string eventType, long expected)
    {
        var actual = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = @e",
            ("t", tenantId), ("e", eventType));

        Assert.Equal(expected, actual);
    }
}

/// <summary>
/// AC-40 / API-13 / T-86. The suite raises the auth limit so dozens of sign-ins do not trip it; this
/// class runs its own host with the limit set low, so the limiter is exercised for real rather than
/// asserted about in the abstract.
/// </summary>
public sealed class RateLimitTests : IAsyncLifetime
{
    private DatabaseFixture? database;
    private ApiFactory? api;

    public async Task InitializeAsync()
    {
        this.database = await DatabaseFixture.CreateAsync("rate");
        Environment.SetEnvironmentVariable("AUTH_RATE_LIMIT_PER_MINUTE", "3");
        this.api = new ApiFactory();
        _ = this.api.Services;
    }

    public async Task DisposeAsync()
    {
        this.api?.Dispose();
        Environment.SetEnvironmentVariable("AUTH_RATE_LIMIT_PER_MINUTE", "100000");

        if (this.database is not null)
        {
            await this.database.DisposeAsync();
        }
    }

    [Fact]
    public async Task AuthEndpoints_AreRateLimited()
    {
        using var client = this.api!.CreateCookieClient();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email = $"nobody-{Guid.NewGuid():N}@example.jo", password = "wrong password here" },
                ApiScenario.Json);

            statuses.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(response.Headers.Contains("Retry-After"), "429 must tell the caller when to come back.");

                var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
                Assert.Equal("rate_limited", problem!.Code);
            }
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // The limiter is what stops password guessing at network speed; the account lock (AC-09) is
        // the per-account backstop behind it.
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Unauthorized));
    }
}
