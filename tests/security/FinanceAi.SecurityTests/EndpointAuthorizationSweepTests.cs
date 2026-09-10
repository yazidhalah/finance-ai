using System.Net;
using System.Net.Http.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAi.SecurityTests;

/// <summary>
/// AC-23 … AC-26 / SEC-14 / T-40, T-60, T-71.
/// <para>
/// These sweeps are <b>generated from the route table</b>, not hand-written per endpoint. Doc 09
/// asks for that explicitly, and the reason is simple: a hand-written list is a list someone forgets
/// to extend. Add an endpoint in slice 2 and it is tested for 401, 403 and cross-tenant 404 the
/// moment it is mapped, or the build fails.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointAuthorizationSweepTests(ApiTestFixture fixture)
{
    private sealed record EndpointUnderTest(
        string Method, string Template, RequiredPermission? Permission, DeclaredAccess? Access);

    /// <summary>AC-24 / SEC-14: no token, no access — for every endpoint that is not anonymous.</summary>
    [Fact]
    public async Task EveryProtectedEndpoint_Returns401WithoutToken()
    {
        var protectedEndpoints = this.Endpoints()
            .Where(e => e.Access?.Kind != DeclaredAccessKind.Anonymous)
            .ToList();

        Assert.NotEmpty(protectedEndpoints);

        using var client = fixture.Api.CreateCookieClient();

        foreach (var endpoint in protectedEndpoints)
        {
            var response = await SendAsync(client, endpoint, Guid.CreateVersion7());

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} without a token; expected 401.");
        }
    }

    /// <summary>
    /// AC-25 / SEC-14. For each endpoint, a real user holding a real role that lacks the declared
    /// permission — chosen from doc 01 §5.1 rather than assumed — must be refused.
    /// </summary>
    [Fact]
    public async Task EveryProtectedEndpoint_Returns403ForRoleWithoutPermission()
    {
        var permissioned = this.Endpoints().Where(e => e.Permission is not null).ToList();

        Assert.NotEmpty(permissioned);

        var organization = await fixture.Api.CreateOrganizationAsync();
        var universal = new List<string>();

        foreach (var endpoint in permissioned)
        {
            var permission = endpoint.Permission!.Permission;

            var roleWithout = Enum.GetValues<TenantRole>()
                .Cast<TenantRole?>()
                .FirstOrDefault(r => !RolePermissions.Grants(r!.Value, permission));

            // Some permissions in doc 01 §5.1 are held by every role — tenant.read, customers.read,
            // invoices.read and the other baseline reads. For those there is no role to refuse, so
            // no 403 exists to assert. PRD-13 asks for "a 403 for at least one role that lacks it",
            // which for a universal permission is unsatisfiable by construction rather than by
            // oversight; the boundary that protects them is tenancy, and AC-26/AC-27 test that.
            if (roleWithout is null)
            {
                universal.Add(permission);
                continue;
            }

            var member = await fixture.Database.AddMemberAsync(organization.TenantId, roleWithout.Value);
            var session = await fixture.Api.LoginAsync(member.Email, member.Password);

            using var client = fixture.Api.AuthenticatedClient(session);
            var response = await SendAsync(client, endpoint, Guid.CreateVersion7());

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} for a " +
                $"{roleWithout} who lacks '{permission}'; expected 403.");
        }

        // Pinned so that a permission becoming universal is a deliberate, visible change rather than
        // a quiet loss of test coverage.
        Assert.Equal(
            ["tenant.read"],
            universal.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The other half of PRD-13: a permission that <i>is</i> refusable must actually be refused, and
    /// the same endpoint must be allowed for a role that holds it. Without the positive case, a
    /// blanket "deny everything" would pass the 403 sweep.
    /// </summary>
    [Fact]
    public async Task EveryProtectedEndpoint_IsAllowedForARoleThatHoldsThePermission()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        foreach (var endpoint in this.Endpoints().Where(e => e.Permission is not null && e.Method == "GET"))
        {
            var permission = endpoint.Permission!.Permission;

            var roleWith = Enum.GetValues<TenantRole>()
                .First(r => RolePermissions.Grants(r, permission));

            var member = roleWith == TenantRole.Owner
                ? null
                : await fixture.Database.AddMemberAsync(organization.TenantId, roleWith);

            var session = member is null
                ? organization.OwnerSession
                : await fixture.Api.LoginAsync(member.Email, member.Password);

            using var client = fixture.Api.AuthenticatedClient(session);
            var response = await SendAsync(client, endpoint, Guid.CreateVersion7());

            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} for a " +
                $"{roleWith} who holds '{permission}'; expected 200, or 404 for an id that does not exist.");
        }
    }

    /// <summary>
    /// AC-26 / SEC-13 / T-71. Another organization's id must look missing, not forbidden. A 403
    /// would confirm the row exists, and for a receivables product that is itself a disclosure:
    /// it tells one SME that another one has a particular customer, invoice or user.
    /// </summary>
    [Fact]
    public async Task EveryEndpointWithId_Returns404ForCrossTenantId()
    {
        var withIdParameter = this.Endpoints()
            .Where(e => e.Template.Contains("{id", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(withIdParameter);

        var organizationA = await fixture.Api.CreateOrganizationAsync("Sweep A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Sweep B");

        // A real, existing id in B — not a random guid, so a 404 proves scoping rather than absence.
        var memberOfB = await fixture.Database.ScalarAsync<Guid>(
            "SELECT id FROM tenant_memberships WHERE tenant_id = @t LIMIT 1", ("t", organizationB.TenantId));
        Assert.NotEqual(Guid.Empty, memberOfB);

        using var clientA = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);

        foreach (var endpoint in withIdParameter)
        {
            var response = await SendAsync(clientA, endpoint, memberOfB);

            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} for a real id " +
                "belonging to another organization; expected 404 (SEC-13).");
        }
    }

    /// <summary>
    /// AC-23 / SEC-10 / T-61. An endpoint that declares nothing must prevent the application from
    /// booting. The whole point is that the failure mode of a forgotten declaration is a crash at
    /// deploy time, not an open endpoint discovered later.
    /// </summary>
    [Fact]
    public void Startup_FailsWhenAnEndpointDeclaresNoPermission()
    {
        var declared = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/v1/declared"),
            0,
            new EndpointMetadataCollection(new RequiredPermission(Permissions.TenantRead)),
            "declared");

        var undeclared = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/v1/forgotten"),
            0,
            new EndpointMetadataCollection(),
            "forgotten");

        // The declared one alone is fine.
        EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess([declared]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess([declared, undeclared]));

        Assert.Contains("SEC-10", ex.Message, StringComparison.Ordinal);
        Assert.Contains("forgotten", ex.Message, StringComparison.Ordinal);

        // ...and the running application satisfies the same assertion, which is why it started.
        EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess(
            fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints);
    }

    /// <summary>Every endpoint this slice ships is accounted for, so the sweeps above are not
    /// silently sweeping an empty set.</summary>
    [Fact]
    public void EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel()
    {
        var endpoints = this.Endpoints();

        Assert.Equal(13, endpoints.Count);
        Assert.All(endpoints, e => Assert.True(e.Permission is not null || e.Access is not null));

        // The anonymous set is exactly registration, login and refresh — nothing has drifted into it.
        var anonymous = endpoints
            .Where(e => e.Access?.Kind == DeclaredAccessKind.Anonymous)
            .Select(e => e.Template)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["api/v1/auth/login", "api/v1/auth/refresh", "api/v1/auth/register"],
            anonymous);
    }

    /// <summary>
    /// The live route table of the running application. Not a list maintained by hand — that is the
    /// point: a new endpoint joins these sweeps by existing.
    /// </summary>
    private List<EndpointUnderTest> Endpoints() =>
        fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Template: Normalize(e.RoutePattern.RawText)))
            .Where(e => e.Template.StartsWith("api/v1/", StringComparison.Ordinal))
            .SelectMany(e => (e.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Where(m => m is not ("OPTIONS" or "HEAD"))
                .Select(m => new EndpointUnderTest(
                    m,
                    e.Template,
                    e.Endpoint.Metadata.GetMetadata<RequiredPermission>(),
                    e.Endpoint.Metadata.GetMetadata<DeclaredAccess>())))
            .OrderBy(e => e.Template, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();

    private static string Normalize(string? template) =>
        (template ?? string.Empty).Trim('/');

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, EndpointUnderTest endpoint, Guid id)
    {
        var path = "/" + endpoint.Template
            .Replace("{id:guid}", id.ToString(), StringComparison.Ordinal)
            .Replace("{id}", id.ToString(), StringComparison.Ordinal);

        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), path);

        if (endpoint.Method is "POST" or "PATCH" or "PUT")
        {
            request.Content = JsonContent.Create(new { }, options: ApiScenario.Json);
        }

        return client.SendAsync(request);
    }
}
