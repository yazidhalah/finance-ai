using System.Net;
using System.Net.Http.Json;

namespace FinanceAi.IntegrationTests;

/// <summary>AC-07 … AC-11: signing in, failing to sign in, and saying as little as possible about
/// which of those happened.</summary>
[Collection(ApiCollection.Name)]
public sealed class LoginTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Login_ReturnsAccessTokenTenantRoleAndPermissions()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = ApiScenario.ValidPassword },
            ApiScenario.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(ApiScenario.Json);

        Assert.False(string.IsNullOrWhiteSpace(session!.AccessToken));
        Assert.Equal(900, session.ExpiresIn);            // SEC-03: 15 minutes
        Assert.Equal("Owner", session.Role);
        Assert.Equal(organization.TenantId, session.Tenant.Id);
        Assert.Equal(organization.Name, session.Tenant.Name);
        Assert.Equal("JOD", session.Tenant.BaseCurrency);
        Assert.Equal("Asia/Amman", session.Tenant.Timezone);
        Assert.Equal(organization.OwnerEmail, session.User.Email);

        // Doc 05: the effective permission list travels with the session so the UI can filter
        // navigation by permission rather than by role name (UI-01).
        Assert.Contains("tenant.read", session.Permissions);
        Assert.Contains("writeoff.approve", session.Permissions);
        Assert.Equal(session.Permissions.Count, session.Permissions.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>AC-08 / SEC-06, SEC-07. The two failures must be indistinguishable to a caller.</summary>
    [Fact]
    public async Task Login_WrongPassword_And_UnknownEmail_AreIndistinguishable()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = "definitely not the password" },
            ApiScenario.Json);

        var unknownEmail = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = $"nobody-{Guid.NewGuid():N}@example.jo", password = "definitely not the password" },
            ApiScenario.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        var first = await wrongPassword.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        var second = await unknownEmail.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);

        Assert.Equal(first!.Code, second!.Code);
        Assert.Equal(first.Detail, second.Detail);
        Assert.Equal(first.MessageKey, second.MessageKey);
        Assert.Equal("invalid_credentials", first.Code);

        // Neither response sets a session cookie.
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
        Assert.False(unknownEmail.Headers.Contains("Set-Cookie"));
    }

    /// <summary>AC-09 / SEC-06.</summary>
    [Fact]
    public async Task Login_LocksAccountAfterTenFailures()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var failed = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email = organization.OwnerEmail, password = $"wrong-{attempt}" },
                ApiScenario.Json);

            // The first nine are ordinary failures; the tenth is the one that trips the lock, and
            // doc 06 §6.1 asks for that state to be distinguishable so the UI can show an unlock
            // time. It is only ever reachable after ten failures against an address that exists, and
            // the auth rate limit (API-13) bites long before that is a practical enumeration route.
            var expected = attempt < 10 ? HttpStatusCode.Unauthorized : HttpStatusCode.Locked;
            Assert.Equal(expected, failed.StatusCode);
        }

        // The correct password now fails too: that is the whole point of a lockout.
        var withCorrectPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = ApiScenario.ValidPassword },
            ApiScenario.Json);

        Assert.Equal(HttpStatusCode.Locked, withCorrectPassword.StatusCode);

        var lockedUntil = await fixture.Database.ScalarAsync<DateTime?>(
            "SELECT locked_until FROM users WHERE email = @e", ("e", organization.OwnerEmail));
        Assert.NotNull(lockedUntil);
    }

    /// <summary>AC-10: a legitimate user who mistypes twice is not one failure closer to a lockout
    /// forever.</summary>
    [Fact]
    public async Task Login_Success_ResetsFailedLoginCount()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { email = organization.OwnerEmail, password = "wrong" },
                ApiScenario.Json);
        }

        var beforeSuccess = await fixture.Database.ScalarAsync<int>(
            "SELECT failed_login_count FROM users WHERE email = @e", ("e", organization.OwnerEmail));
        Assert.Equal(3, beforeSuccess);

        var success = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = ApiScenario.ValidPassword },
            ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        var afterSuccess = await fixture.Database.ScalarAsync<int>(
            "SELECT failed_login_count FROM users WHERE email = @e", ("e", organization.OwnerEmail));
        Assert.Equal(0, afterSuccess);
    }

    /// <summary>AC-11 / SEC-04, SEC-05.</summary>
    [Fact]
    public async Task Login_SetsHardenedRefreshCookie_AndNoAccessTokenCookie()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail, password = ApiScenario.ValidPassword },
            ApiScenario.Json);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        var refreshCookie = Assert.Single(cookies, c => c.StartsWith("fa_refresh=", StringComparison.Ordinal));

        Assert.Contains("httponly", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", refreshCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", refreshCookie, StringComparison.OrdinalIgnoreCase);

        // SEC-05: the access token lives in memory in the SPA. A cookie would survive a tab close
        // and be sent automatically, which is exactly what we do not want for a bearer credential.
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>(ApiScenario.Json);
        Assert.DoesNotContain(cookies, c => c.Contains(session!.AccessToken, StringComparison.Ordinal));
        Assert.Single(cookies);
    }

    [Fact]
    public async Task Login_IsCaseInsensitiveOnTheEmailAddress()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = organization.OwnerEmail.ToUpperInvariant(), password = ApiScenario.ValidPassword },
            ApiScenario.Json);

        // citext in the database rather than lower() scattered through queries (doc 04 §4).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
