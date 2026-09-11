using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Security;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 13 AC-02 … AC-06: TOTP MFA end to end, enforcement after the grace period, password reset, re-authentication and transfer of ownership.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthCompletionTests(ApiTestFixture fixture)
{
    private static readonly HttpClient Mailpit = new();

    private static string CodeFor(string secretBase32, DateTimeOffset? at = null) => Totp.Code(Base32.Decode(secretBase32), (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());

    private static string AmrOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.GetProperty("amr").GetString()!;
    }

    private static async Task<(string Secret, IReadOnlyList<string> Recovery)> EnrolAsync(HttpClient client)
    {
        var enrol = await (await client.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        var secret = enrol.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/finance-ai:", enrol.GetProperty("provisioningUri").GetString());
        var verify = await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = CodeFor(secret) }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var body = await verify.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        return (secret, body.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList());
    }

    private static async Task<(int Status, JsonElement Body)> LoginAsync(HttpClient anonymous, string email, string password, string? totp = null)
    {
        var r = await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password, totp }, ApiScenario.Json);
        var text = await r.Content.ReadAsStringAsync();
        return ((int)r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>AC-02.</summary>
    [Fact]
    public async Task Mfa_Enroll_Login_Recovery()
    {
        var org = await fixture.Api.CreateOrganizationAsync("MFA Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var me = await owner.GetFromJsonAsync<JsonElement>("/api/v1/me", ApiScenario.Json);
        Assert.False(me.GetProperty("mfaEnrolled").GetBoolean());
        Assert.True(me.GetProperty("mfaRequired").GetBoolean());
        Assert.False(me.GetProperty("mfaEnforced").GetBoolean());

        // A wrong code does not activate; the secret stays pending.
        var enrol = await (await owner.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = "000000" }, ApiScenario.Json)).StatusCode);
        Assert.Null(await fixture.Database.ScalarAsync<DateTime?>("SELECT mfa_enabled_at FROM users WHERE id = @u", ("u", org.OwnerUserId)));
        Assert.Equal(32, enrol.GetProperty("secret").GetString()!.Length);

        var (secret, recovery) = await EnrolAsync(owner);
        Assert.Equal(8, recovery.Count);
        Assert.Equal(8, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM user_recovery_codes WHERE user_id = @u AND used_at IS NULL", ("u", org.OwnerUserId)));
        var stored = await fixture.Database.ScalarAsync<byte[]>("SELECT mfa_secret_enc FROM users WHERE id = @u", ("u", org.OwnerUserId));
        Assert.NotEqual(Base32.Decode(secret), stored);   // encrypted at rest, not the raw secret
        Assert.True(stored!.Length > 20);
        Assert.True((await owner.GetFromJsonAsync<JsonElement>("/api/v1/me", ApiScenario.Json)).GetProperty("mfaEnrolled").GetBoolean());

        using var anonymous = fixture.Api.CreateClient();
        var (noCode, noCodeBody) = await LoginAsync(anonymous, org.OwnerEmail, ApiScenario.ValidPassword);
        Assert.Equal(401, noCode);
        Assert.Equal("mfa_required", noCodeBody.GetProperty("code").GetString());
        var (wrongPassword, wrongPasswordBody) = await LoginAsync(anonymous, org.OwnerEmail, "definitely-not-the-password!");
        Assert.Equal(401, wrongPassword);
        Assert.Equal("invalid_credentials", wrongPasswordBody.GetProperty("code").GetString());   // never mfa_required without the password
        var (wrongCode, wrongCodeBody) = await LoginAsync(anonymous, org.OwnerEmail, ApiScenario.ValidPassword, "000000");
        Assert.Equal(401, wrongCode);
        Assert.Equal("invalid_credentials", wrongCodeBody.GetProperty("code").GetString());
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>("SELECT failed_login_count FROM users WHERE id = @u", ("u", org.OwnerUserId)));   // the wrong password and the wrong code both count (SEC-06)
        var (ok, okBody) = await LoginAsync(anonymous, org.OwnerEmail, ApiScenario.ValidPassword, CodeFor(secret));
        Assert.Equal(200, ok);
        Assert.Equal("mfa", AmrOf(okBody.GetProperty("accessToken").GetString()!));

        // A recovery code works once.
        var (rec, _) = await LoginAsync(anonymous, org.OwnerEmail, ApiScenario.ValidPassword, recovery[0]);
        Assert.Equal(200, rec);
        var (recAgain, recAgainBody) = await LoginAsync(anonymous, org.OwnerEmail, ApiScenario.ValidPassword, recovery[0]);
        Assert.Equal(401, recAgain);
        Assert.Equal("invalid_credentials", recAgainBody.GetProperty("code").GetString());
        Assert.Equal(7, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM user_recovery_codes WHERE user_id = @u AND used_at IS NULL", ("u", org.OwnerUserId)));
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'auth.mfa_enabled'", ("t", org.TenantId)));
    }

    /// <summary>AC-03: after the grace period an Owner without MFA can only enrol.</summary>
    [Fact]
    public async Task Mfa_IsEnforcedAfterGrace()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Grace Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var collector = await fixture.Database.AddMemberAsync(org.TenantId, TenantRole.Collector);
        using var collectorClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(collector.Email, collector.Password));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/customers")).StatusCode);
        try
        {
            fixture.Api.Clock.Override = DateTimeOffset.UtcNow.AddDays(8);
            var blocked = await owner.GetAsync("/api/v1/customers");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal("mfa_enrollment_required", (await blocked.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("code").GetString());
            var me = await owner.GetFromJsonAsync<JsonElement>("/api/v1/me", ApiScenario.Json);
            Assert.True(me.GetProperty("mfaEnforced").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await collectorClient.GetAsync("/api/v1/customers")).StatusCode);   // not required of a Collector

            // Enrolling is allowed, and then everything is.
            var enrol = await (await owner.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
            var code = CodeFor(enrol.GetProperty("secret").GetString()!, fixture.Api.Clock.GetUtcNow());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code }, ApiScenario.Json)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/customers")).StatusCode);
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-04.</summary>
    [Fact]
    public async Task PasswordReset_Flow_IsUniform()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Reset Co");
        using var anonymous = fixture.Api.CreateClient();
        var known = await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = org.OwnerEmail }, ApiScenario.Json);
        var unknown = await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = $"nobody-{Guid.NewGuid():N}@example.test" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        string? token = null;
        for (var attempt = 0; attempt < 20 && token is null; attempt++)
        {
            var search = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/search?query={Uri.EscapeDataString("to:" + org.OwnerEmail)}");
            if (search.GetProperty("messages_count").GetInt32() > 0)
            {
                var message = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/message/{search.GetProperty("messages")[0].GetProperty("ID").GetString()}");
                token = Regex.Match(message.GetProperty("Text").GetString() ?? string.Empty, @"reset-password\?token=([0-9a-f]{64})").Groups[1].Value;
                if (token.Length == 0) token = null;
            }

            await Task.Delay(250);
        }

        Assert.NotNull(token);
        // The existing session's refresh cookie dies with the reset.
        using var cookieClient = fixture.Api.CreateCookieClient();
        Assert.True((await cookieClient.PostAsJsonAsync("/api/v1/auth/login", new { email = org.OwnerEmail, password = ApiScenario.ValidPassword }, ApiScenario.Json)).IsSuccessStatusCode);
        var newPassword = "a-brand-new-password-2026";
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "short" }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = newPassword }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await cookieClient.PostAsync("/api/v1/auth/refresh", null)).StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens WHERE user_id = @u AND revoked_at IS NULL", ("u", org.OwnerUserId)));
        Assert.Equal(401, (await LoginAsync(anonymous, org.OwnerEmail, ApiScenario.ValidPassword)).Status);
        Assert.Equal(200, (await LoginAsync(anonymous, org.OwnerEmail, newPassword)).Status);

        var bodies = new List<string>();
        foreach (var bad in new[] { token!, new string('0', 64), "nope" })
        {
            var r = await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new { token = bad, password = newPassword }, ApiScenario.Json);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            bodies.Add((await r.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("code").GetString()!);
        }

        Assert.Single(bodies.Distinct());
        Assert.Equal("reset_invalid", bodies[0]);
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'auth.password_reset'", ("t", org.TenantId)));
    }

    /// <summary>AC-05 / AC-06: re-authentication proofs, transfer of ownership, and the write-off gate.</summary>
    [Fact]
    public async Task TransferOwnership_NeedsReauth_AndMfa()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Transfer Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var accountant = await fixture.Database.AddMemberAsync(org.TenantId, TenantRole.Accountant);
        using var accountantClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));

        var (noProof, noProofBody) = await owner.TryPostAsync("/api/v1/organization/transfer-ownership", new { targetMembershipId = accountant.MembershipId });
        Assert.Equal(403, noProof);
        Assert.Equal("reauthentication_required", noProofBody.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/v1/auth/reauthenticate", new { password = "wrong-password-for-sure" }, ApiScenario.Json)).StatusCode);

        // Another user's proof is not this user's.
        await accountantClient.ReauthAsync(accountant.Password);
        var stolen = accountantClient.DefaultRequestHeaders.GetValues("X-Reauth").First();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/organization/transfer-ownership") { Content = JsonContent.Create(new { targetMembershipId = accountant.MembershipId }, options: ApiScenario.Json) };
        request.Headers.Add("X-Reauth", stolen);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(request)).StatusCode);

        await owner.ReauthAsync();
        var (noMfa, noMfaBody) = await owner.TryPostAsync("/api/v1/organization/transfer-ownership", new { targetMembershipId = accountant.MembershipId });
        Assert.Equal(422, noMfa);
        Assert.Equal("target_mfa_required", noMfaBody.GetProperty("errors")[0].GetProperty("code").GetString());

        // The target enrols; the transfer goes through; roles are swapped and audited.
        await EnrolAsync(accountantClient);
        var transferred = await owner.PostAsync("/api/v1/organization/transfer-ownership", new { targetMembershipId = accountant.MembershipId });
        Assert.Equal("Owner", transferred.GetProperty("newOwner").GetProperty("role").GetString());
        Assert.Equal("Admin", transferred.GetProperty("previousOwner").GetProperty("role").GetString());
        Assert.Equal("Admin", (await owner.GetFromJsonAsync<JsonElement>("/api/v1/me", ApiScenario.Json)).GetProperty("role").GetString());
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'tenant.ownership_transferred'", ("t", org.TenantId)));
        Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM tenant_memberships WHERE tenant_id = @t AND role = 'Owner' AND status <> 'Disabled'", ("t", org.TenantId)));
        // The former Owner, now Admin, no longer holds tenant.transfer_ownership.
        var (again, _) = await owner.TryPostAsync("/api/v1/organization/transfer-ownership", new { targetMembershipId = accountant.MembershipId });
        Assert.Equal(403, again);

        // AC-06: write-off approval needs the proof too (slice 3b D-6).
        var s = await fixture.Api.NewCustomerAsync("Writeoff Reauth");
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "WO-1", 10m);
        var proposal = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectable" });
        var (gated, gatedBody) = await s.Client.TryPostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        Assert.Equal(403, gated);
        Assert.Equal("reauthentication_required", gatedBody.GetProperty("code").GetString());
        await s.Client.ReauthAsync();
        Assert.Equal("Approved", (await s.Client.PostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true })).GetProperty("status").GetString());
    }
}
