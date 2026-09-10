using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FinanceAi.IntegrationTests;

/// <summary>AC-19, AC-20, AC-41: what the API accepts, what it refuses, and what its refusals
/// look like.</summary>
[Collection(ApiCollection.Name)]
public sealed class RequestContractTests(ApiTestFixture fixture)
{
    /// <summary>
    /// AC-19 / API-01. A body carrying <c>tenantId</c> is rejected rather than ignored. Silently
    /// dropping it would let a client believe it had chosen a tenant, which is worse than failing:
    /// the client would go on to display the wrong organization's data as if it had asked for it.
    /// </summary>
    [Fact]
    public async Task Body_WithTenantId_Returns400UnexpectedField()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PatchAsync(
            "/api/v1/organization",
            JsonContent.Create(new { name = "Renamed", tenantId = Guid.NewGuid() }, options: ApiScenario.Json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("unexpected_field", problem!.Code);

        // ...and nothing was written.
        var name = await fixture.Database.ScalarAsync<string>(
            "SELECT name FROM tenants WHERE id = @t", ("t", organization.TenantId));
        Assert.Equal(organization.Name, name);
    }

    /// <summary>AC-20 / SEC-17: mass assignment is prevented by the shape of the contract.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("baseCurrency")]
    [InlineData("rowVersion")]
    [InlineData("approvedBy")]
    [InlineData("somethingInvented")]
    public async Task Body_WithUnknownField_Returns400UnexpectedField(string field)
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var body = $$"""{"name":"Renamed","{{field}}":"whatever"}""";

        var response = await client.PatchAsync(
            "/api/v1/organization",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("unexpected_field", problem!.Code);
    }

    /// <summary>AC-41 / API-04, doc 05 §0.2.</summary>
    [Fact]
    public async Task Errors_AreProblemJsonWithMessageKeys()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        var viewer = await fixture.Database.AddMemberAsync(
            organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Viewer);

        var viewerSession = await fixture.Api.LoginAsync(viewer.Email, viewer.Password);
        using var client = fixture.Api.AuthenticatedClient(viewerSession);

        var response = await client.GetAsync("/api/v1/organization/members");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal("forbidden", root.GetProperty("code").GetString());
        Assert.Equal(403, root.GetProperty("status").GetInt32());
        Assert.Equal("/api/v1/organization/members", root.GetProperty("instance").GetString());
        Assert.StartsWith("https://finance-ai/errors/", root.GetProperty("type").GetString()!, StringComparison.Ordinal);

        // API-04: the client renders the user-facing sentence from the key, in the user's locale.
        // Anything else would put translation in the backend and guarantee it drifts from the UI.
        Assert.Equal("errors.forbidden", root.GetProperty("messageKey").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task ValidationErrors_NameTheFieldAndCarryAMessageKeyPerError()
    {
        using var client = fixture.Api.CreateCookieClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "not-an-email",
            password = "short",
            fullName = "",
            organizationName = "",
            baseCurrency = "JODX",
            timezone = "Mars/Olympus",
            locale = "fr-FR",
        }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = document.RootElement.GetProperty("errors");

        // Every problem is reported at once, so a user fixes the form in one pass.
        Assert.True(errors.GetArrayLength() >= 6);

        foreach (var error in errors.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(error.GetProperty("field").GetString()));
            Assert.False(string.IsNullOrEmpty(error.GetProperty("code").GetString()));
            Assert.StartsWith("errors.", error.GetProperty("messageKey").GetString()!, StringComparison.Ordinal);
        }
    }

    /// <summary>SEC-61: the headers every response carries.</summary>
    [Fact]
    public async Task Responses_CarrySecurityHeaders()
    {
        using var client = fixture.Api.CreateCookieClient();
        var response = await client.GetAsync("/api/v1/me");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    /// <summary>API-09: concurrent edits of the organization profile conflict rather than overwrite.</summary>
    [Fact]
    public async Task StaleIfMatch_Returns409ConcurrencyConflict()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var current = await client.GetFromJsonAsync<OrganizationResponse>("/api/v1/organization", ApiScenario.Json);

        var firstEdit = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization")
        {
            Content = JsonContent.Create(new { legalName = "First Edit" }, options: ApiScenario.Json),
        };
        firstEdit.Headers.TryAddWithoutValidation("If-Match", current!.RowVersion);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstEdit)).StatusCode);

        // The second editor still holds the version they read before the first edit landed.
        var secondEdit = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/organization")
        {
            Content = JsonContent.Create(new { legalName = "Second Edit" }, options: ApiScenario.Json),
        };
        secondEdit.Headers.TryAddWithoutValidation("If-Match", current.RowVersion);

        var conflict = await client.SendAsync(secondEdit);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var problem = await conflict.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("concurrency_conflict", problem!.Code);

        var legalName = await fixture.Database.ScalarAsync<string>(
            "SELECT legal_name FROM tenants WHERE id = @t", ("t", organization.TenantId));
        Assert.Equal("First Edit", legalName);
    }

    /// <summary>
    /// PATCH /organization does not accept <c>baseCurrency</c>: doc 06 §6.1 makes it immutable once
    /// invoices exist, and that guard cannot be written before invoices do (slice 3).
    /// </summary>
    [Fact]
    public async Task Organization_BaseCurrency_IsNotEditableInThisSlice()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PatchAsync(
            "/api/v1/organization",
            JsonContent.Create(new { baseCurrency = "USD" }, options: ApiScenario.Json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var currency = await fixture.Database.ScalarAsync<string>(
            "SELECT base_currency FROM tenants WHERE id = @t", ("t", organization.TenantId));
        Assert.Equal("JOD", currency);
    }

    [Fact]
    public async Task Organization_CanBeUpdatedByAnOwner()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PatchAsync(
            "/api/v1/organization",
            JsonContent.Create(
                new { name = "Al Amal Trading Co.", legalName = "Al Amal LLC", timezone = "Asia/Amman", defaultLocale = "en-JO" },
                options: ApiScenario.Json));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<OrganizationResponse>(ApiScenario.Json);
        Assert.Equal("Al Amal Trading Co.", updated!.Name);
        Assert.Equal("Al Amal LLC", updated.LegalName);
        Assert.Equal("en-JO", updated.DefaultLocale);
        Assert.Equal("2", updated.RowVersion);
    }

    [Fact]
    public async Task Me_CanUpdateNameAndLocale()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PatchAsync(
            "/api/v1/me",
            JsonContent.Create(new { fullName = "رنا العلي", preferredLocale = "ar-JO" }, options: ApiScenario.Json));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<UserDto>(ApiScenario.Json);
        Assert.Equal("رنا العلي", updated!.FullName);
        Assert.Equal("ar-JO", updated.PreferredLocale);
    }
}
