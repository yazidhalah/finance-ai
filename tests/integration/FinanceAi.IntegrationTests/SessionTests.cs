using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FinanceAi.Domain.Authorization;
using FinanceAi.Infrastructure.Security;

namespace FinanceAi.IntegrationTests;

/// <summary>AC-12 … AC-17: what a session is, how it rotates, and how it ends.</summary>
[Collection(ApiCollection.Name)]
public sealed class SessionTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Refresh_RotatesToken()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        await SignInAsync(client, organization.OwnerEmail);

        // Assertions are scoped to this sign-in's family. A user may hold several sessions at once
        // (another browser, another device); rotation and revocation are per family, by design.
        var familyId = await this.FamilyOfAsync(client);

        var firstRefresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, firstRefresh.StatusCode);

        var refreshed = await firstRefresh.Content.ReadFromJsonAsync<SessionResponse>(ApiScenario.Json);
        Assert.Equal(organization.TenantId, refreshed!.Tenant.Id);
        Assert.Equal("Owner", refreshed.Role);

        // The presented token is spent, and a replacement was issued in the same family.
        var rotated = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE family_id = @f AND rotated_at IS NOT NULL",
            ("f", familyId));
        Assert.Equal(1, rotated);

        var inFamily = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE family_id = @f", ("f", familyId));
        Assert.Equal(2, inFamily);

        var live = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE family_id = @f AND revoked_at IS NULL",
            ("f", familyId));
        Assert.Equal(1, live);

        // The new cookie still works, which is what makes rotation invisible to a legitimate user.
        var secondRefresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, secondRefresh.StatusCode);
    }

    /// <summary>
    /// AC-13 / SEC-04 / T-85. A rotated token presented again means two parties hold it, and we
    /// cannot tell which one is the thief — so the whole family dies and both must sign in again.
    /// </summary>
    [Fact]
    public async Task Refresh_Reuse_RevokesFamily_AndAudits()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var victim = fixture.Api.CreateCookieClient();
        await SignInAsync(victim, organization.OwnerEmail);

        // Capture the raw token the way a thief would, then let the legitimate client rotate it.
        var rawStolen = this.CapturedRawToken(victim);
        var familyId = await this.FamilyOfAsync(victim);

        var legitimateRotation = await victim.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, legitimateRotation.StatusCode);

        // The thief now presents the token that was already rotated.
        using var thief = fixture.Api.CreateDefaultClient();
        var reuse = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        reuse.Headers.Add("Cookie", $"fa_refresh={rawStolen}");

        var reuseResponse = await thief.SendAsync(reuse);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseResponse.StatusCode);

        // Every token in the family is revoked, including the one the victim was still using.
        var live = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE family_id = @f AND revoked_at IS NULL",
            ("f", familyId));
        Assert.Equal(0, live);

        var reuseReason = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE family_id = @f AND revoked_reason = 'reuse_detected'",
            ("f", familyId));
        Assert.True(reuseReason > 0, "The family should be revoked with reason 'reuse_detected'.");

        // ...and the victim's next refresh fails, which is the visible symptom of the alert.
        var victimAfter = await victim.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, victimAfter.StatusCode);

        // SEC-50: the detection is on the record, not only in a log line.
        var audited = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'auth.refresh_reuse_detected'",
            ("t", organization.TenantId));
        Assert.Equal(1, audited);
    }

    [Fact]
    public async Task Logout_RevokesFamily()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        var session = await SignInAsync(client, organization.OwnerEmail);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

        var familyId = await this.FamilyOfAsync(client);

        var logout = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var live = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE family_id = @f AND revoked_at IS NULL",
            ("f", familyId));
        Assert.Equal(0, live);

        var afterLogout = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    /// <summary>AC-15: a database reader cannot mint a session.</summary>
    [Fact]
    public async Task RefreshTokens_AreStoredHashed()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        using var client = fixture.Api.CreateCookieClient();
        await SignInAsync(client, organization.OwnerEmail);

        var raw = this.CapturedRawToken(client);
        var stored = await fixture.Database.ScalarAsync<string>(
            "SELECT token_hash FROM refresh_tokens WHERE token_hash = @h", ("h", RefreshTokens.HashOf(raw)));

        Assert.NotNull(stored);
        Assert.NotEqual(raw, stored);
        Assert.Equal(RefreshTokens.HashOf(raw), stored);

        var rawAnywhere = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM refresh_tokens WHERE token_hash = @raw", ("raw", raw));
        Assert.Equal(0, rawAnywhere);
    }

    /// <summary>AC-16 / T-85: a token we did not sign is not a token.</summary>
    [Fact]
    public async Task ForgedToken_Rejected()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        var valid = organization.OwnerSession.AccessToken;

        var parts = valid.Split('.');
        var tamperedSignature = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";

        // A token signed by a different key of the right shape.
        using var foreignIssuer = new RsaAccessTokenIssuer(
            System.Security.Cryptography.RSA.Create(2048).ExportPkcs8PrivateKeyPem());
        var (foreignToken, _) = foreignIssuer.Issue(
            organization.OwnerUserId, organization.TenantId, TenantRole.Owner);

        // An unsigned "alg: none" token carrying the same claims.
        var unsigned = $"{Convert.ToBase64String("""{"alg":"none","typ":"JWT"}"""u8).TrimEnd('=')}.{parts[1]}.";

        foreach (var candidate in new[] { tamperedSignature, foreignToken, unsigned, "not-a-token" })
        {
            using var client = fixture.Api.CreateDefaultClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", candidate);

            var response = await client.GetAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>AC-16: expiry is enforced, not merely encoded.</summary>
    [Fact]
    public async Task ExpiredToken_Rejected()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        // Issued with the API's own key, but an hour ago — so it is valid in every respect except
        // the one that matters.
        var pem = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            Environment.GetEnvironmentVariable("JWT_SIGNING_KEY_PEM_BASE64")!));

        using var pastIssuer = new RsaAccessTokenIssuer(
            pem, new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-1)));

        var (expired, expiresAt) = pastIssuer.Issue(
            organization.OwnerUserId, organization.TenantId, TenantRole.Owner);

        Assert.True(expiresAt < DateTimeOffset.UtcNow);

        using var client = fixture.Api.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);

        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// AC-17 / SEC-08. The token is still cryptographically valid; the membership behind it is not.
    /// Re-reading the membership per request is what makes a deactivation take effect immediately
    /// rather than whenever the 15-minute token happens to expire.
    /// </summary>
    [Fact]
    public async Task DisabledMembership_TokenRejected()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        var member = await fixture.Database.AddMemberAsync(organization.TenantId, TenantRole.Accountant);

        var session = await fixture.Api.LoginAsync(member.Email, member.Password);

        using var client = fixture.Api.AuthenticatedClient(session);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        await using (var connection = fixture.Database.OpenAdmin())
        {
            await using var disable = new Npgsql.NpgsqlCommand(
                "UPDATE tenant_memberships SET status = 'Disabled' WHERE id = @id", connection);
            disable.Parameters.AddWithValue("id", member.MembershipId);
            await disable.ExecuteNonQueryAsync();
        }

        var afterDisable = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);
    }

    /// <summary>SEC-08: the same applies to a role change — it takes effect on the next request.</summary>
    [Fact]
    public async Task RoleChange_TakesEffectImmediately()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        var member = await fixture.Database.AddMemberAsync(organization.TenantId, TenantRole.Admin);

        var session = await fixture.Api.LoginAsync(member.Email, member.Password);
        using var client = fixture.Api.AuthenticatedClient(session);

        // Admin holds users.read.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/organization/members")).StatusCode);

        await using (var connection = fixture.Database.OpenAdmin())
        {
            await using var demote = new Npgsql.NpgsqlCommand(
                "UPDATE tenant_memberships SET role = 'Collector' WHERE id = @id", connection);
            demote.Parameters.AddWithValue("id", member.MembershipId);
            await demote.ExecuteNonQueryAsync();
        }

        // Collector does not, and the token was never re-issued.
        var afterDemotion = await client.GetAsync("/api/v1/organization/members");
        Assert.Equal(HttpStatusCode.Forbidden, afterDemotion.StatusCode);

        var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var profile = await me.Content.ReadFromJsonAsync<MeResponse>(ApiScenario.Json);
        Assert.Equal("Collector", profile!.Role);
        Assert.DoesNotContain("users.read", profile.Permissions);
    }

    private static async Task<SessionResponse> SignInAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = ApiScenario.ValidPassword },
            ApiScenario.Json);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(ApiScenario.Json))!;
    }

    /// <summary>The token family behind the cookie this client is holding.</summary>
    private Task<Guid> FamilyOfAsync(HttpClient client) =>
        fixture.Database.ScalarAsync<Guid>(
            "SELECT family_id FROM refresh_tokens WHERE token_hash = @h",
            ("h", RefreshTokens.HashOf(this.CapturedRawToken(client))));

    /// <summary>Reads the refresh cookie the client is holding, as a thief with the cookie would.</summary>
    private string CapturedRawToken(HttpClient client) =>
        fixture.Api.RefreshTokenOf(client)
        ?? throw new InvalidOperationException("No refresh cookie was captured.");
}
