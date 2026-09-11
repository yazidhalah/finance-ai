using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceAi.Domain.Authorization;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 12 AC-01 … AC-06: invitations, acceptance, role change, deactivation — through Mailpit for the link.</summary>
[Collection(ApiCollection.Name)]
public sealed class MemberTests(ApiTestFixture fixture)
{
    private static readonly HttpClient Mailpit = new();

    private static async Task<string> TokenFromMailAsync(string to)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var search = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/search?query={Uri.EscapeDataString("to:" + to)}");
            if (search.GetProperty("messages_count").GetInt32() > 0)
            {
                var id = search.GetProperty("messages")[0].GetProperty("ID").GetString();
                var message = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/message/{id}");
                var body = message.GetProperty("Text").GetString() ?? string.Empty;
                var m = Regex.Match(body, @"accept-invitation\?token=([0-9a-f]{64})");
                Assert.True(m.Success, "the invitation mail carries the accept link");
                return m.Groups[1].Value;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException($"no invitation mail for {to}");
    }

    private static async Task<(int Status, JsonElement Body)> AcceptAsync(HttpClient anonymous, object body)
    {
        var r = await anonymous.PostAsJsonAsync("/api/v1/auth/accept-invitation", body, ApiScenario.Json);
        var text = await r.Content.ReadAsStringAsync();
        return ((int)r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    /// <summary>AC-01: invite → mail → accept as a new user → sign in with the invited role.</summary>
    [Fact]
    public async Task Invite_Accept_SignIn()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Invite Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var email = $"invitee-{Guid.NewGuid():N}@example.test";
        var r = await owner.PostAsJsonAsync("/api/v1/organization/members/invite", new { email, role = "Accountant", locale = "en-JO" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal("{\"accepted\":true}", await r.Content.ReadAsStringAsync());

        var list = await owner.GetFromJsonAsync<JsonElement>("/api/v1/organization/invitations", ApiScenario.Json);
        var pending = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == email);
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        var hash = await fixture.Database.ScalarAsync<string>("SELECT token_hash FROM member_invitations WHERE id = @i", ("i", pending.GetProperty("id").GetGuid()));
        Assert.Matches("^[0-9a-f]{64}$", hash);

        var token = await TokenFromMailAsync(email);
        Assert.NotEqual(token, hash);   // the mail carries the token; the row carries its hash (D-1)
        using var anonymous = fixture.Api.CreateClient();
        var (needs, needsBody) = await AcceptAsync(anonymous, new { token });
        Assert.Equal(400, needs);
        Assert.Equal("password_required", needsBody.GetProperty("code").GetString());
        var (ok, okBody) = await AcceptAsync(anonymous, new { token, fullName = "New Accountant", password = ApiScenario.ValidPassword });
        Assert.Equal(200, ok);
        Assert.Equal("Invite Co", okBody.GetProperty("organizationName").GetString());
        Assert.True(okBody.GetProperty("createdAccount").GetBoolean());

        var session = await fixture.Api.LoginAsync(email, ApiScenario.ValidPassword);
        Assert.Equal("Accountant", session.Role);
        Assert.Equal(org.TenantId, session.Tenant.Id);
        Assert.Contains("invoices.import", session.Permissions);
        Assert.Equal("Accepted", (await owner.GetFromJsonAsync<JsonElement>("/api/v1/organization/invitations", ApiScenario.Json)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == email).GetProperty("status").GetString());
        Assert.NotNull(await fixture.Database.ScalarAsync<DateTime?>("SELECT email_verified_at FROM users WHERE email = @e", ("e", email)));

        // Single use.
        var (again, againBody) = await AcceptAsync(anonymous, new { token, fullName = "x", password = ApiScenario.ValidPassword });
        Assert.Equal(400, again);
        Assert.Equal("invitation_invalid", againBody.GetProperty("code").GetString());
    }

    /// <summary>AC-02: an existing user of another organization joins without a new account.</summary>
    [Fact]
    public async Task Invite_ExistingUser_JoinsWithoutANewAccount()
    {
        var home = await fixture.Api.CreateOrganizationAsync("Home Co");
        var other = await fixture.Api.CreateOrganizationAsync("Other Co");
        using var otherOwner = fixture.Api.AuthenticatedClient(other.OwnerSession);
        Assert.Equal(HttpStatusCode.Accepted, (await otherOwner.PostAsJsonAsync("/api/v1/organization/members/invite", new { email = home.OwnerEmail, role = "Viewer", locale = "ar-JO" }, ApiScenario.Json)).StatusCode);
        var token = await TokenFromMailAsync(home.OwnerEmail);
        using var anonymous = fixture.Api.CreateClient();
        var (ok, body) = await AcceptAsync(anonymous, new { token });   // no password: the account exists
        Assert.Equal(200, ok);
        Assert.False(body.GetProperty("createdAccount").GetBoolean());
        var session = await fixture.Api.LoginAsync(home.OwnerEmail, ApiScenario.ValidPassword);   // the password is unchanged
        using var client = fixture.Api.AuthenticatedClient(session);
        var tenants = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/tenants", ApiScenario.Json);
        var membership = tenants.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("tenantId").GetGuid() == other.TenantId);
        Assert.Equal("Viewer", membership.GetProperty("role").GetString());
    }

    /// <summary>AC-03 / AC-04: nothing is revealed by the invite response; every bad token gets the same answer.</summary>
    [Fact]
    public async Task Invite_RevealsNothing_AndAccept_RefusesUniformly()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Quiet Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var fresh = $"fresh-{Guid.NewGuid():N}@example.test";
        var bodies = new List<string>();
        foreach (var email in new[] { fresh, org.OwnerEmail, (await fixture.Api.CreateOrganizationAsync("Elsewhere")).OwnerEmail })
        {
            var r = await owner.PostAsJsonAsync("/api/v1/organization/members/invite", new { email, role = "Collector", locale = "en-JO" }, ApiScenario.Json);
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            bodies.Add(await r.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct());
        // The existing member got no mail; the others did.
        await Task.Delay(500);
        var ownMail = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/search?query={Uri.EscapeDataString("to:" + org.OwnerEmail)}");
        Assert.Equal(0, ownMail.GetProperty("messages_count").GetInt32());
        Assert.Equal("already_member", await fixture.Database.ScalarAsync<string>("SELECT reason_code FROM audit_events WHERE tenant_id = @t AND event_type = 'membership.invite_requested' AND reason_code = 'already_member' LIMIT 1", ("t", org.TenantId)));

        // Owner cannot be invited.
        var badRole = await owner.PostAsJsonAsync("/api/v1/organization/members/invite", new { email = fresh, role = "Owner", locale = "en-JO" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);

        var token = await TokenFromMailAsync(fresh);
        var invitationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/organization/invitations", ApiScenario.Json)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("email").GetString() == fresh).GetProperty("id").GetGuid();
        var revoked = await owner.PostAsJsonAsync($"/api/v1/organization/invitations/{invitationId}/revoke", new { }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var expiredId = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsync("INSERT INTO member_invitations (id, tenant_id, email, role, locale, token_hash, invited_by, expires_at) VALUES (@id, @t, 'expired@example.test', 'Viewer', 'en-JO', @h, @u, now() - interval '1 day')",
            ("id", expiredId), ("t", org.TenantId), ("h", FinanceAi.Domain.Entities.InvitationTokens.Hash(new string('e', 64))), ("u", org.OwnerUserId));

        using var anonymous = fixture.Api.CreateClient();
        var answers = new List<string>();
        foreach (var bad in new object[] { new { token = new string('0', 64), fullName = "x", password = ApiScenario.ValidPassword }, new { token, fullName = "x", password = ApiScenario.ValidPassword }, new { token = new string('e', 64), fullName = "x", password = ApiScenario.ValidPassword }, new { token = "not-a-token", fullName = "x", password = ApiScenario.ValidPassword } })
        {
            var (status, body) = await AcceptAsync(anonymous, bad);
            Assert.Equal(400, status);
            answers.Add(body.GetProperty("code").GetString()!);
        }

        Assert.Single(answers.Distinct());
        Assert.Equal("invitation_invalid", answers[0]);
    }

    /// <summary>AC-05 / AC-06: role change and deactivation, their guards, and the immediate loss of access.</summary>
    [Fact]
    public async Task RoleChange_AndDeactivate_Guards_AndCutAccess()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Roles Co");
        using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var collector = await fixture.Database.AddMemberAsync(org.TenantId, TenantRole.Collector);
        var members = await owner.GetFromJsonAsync<JsonElement>("/api/v1/organization/members", ApiScenario.Json);
        var ownerMembership = members.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "Owner").GetProperty("id").GetGuid();
        var collectorSession = await fixture.Api.LoginAsync(collector.Email, collector.Password);
        using var collectorClient = fixture.Api.AuthenticatedClient(collectorSession);
        Assert.Equal(HttpStatusCode.Forbidden, (await collectorClient.GetAsync("/api/v1/imports")).StatusCode);

        // Collector → Accountant: the next request sees it (the role is resolved per request).
        var changed = await owner.PatchAsJsonAsync($"/api/v1/organization/members/{collector.MembershipId}", new { role = "Accountant" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await collectorClient.GetAsync("/api/v1/imports")).StatusCode);

        var toOwner = await owner.PatchAsJsonAsync($"/api/v1/organization/members/{collector.MembershipId}", new { role = "Owner" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, toOwner.StatusCode);
        Assert.Contains("owner_via_transfer_only", await toOwner.Content.ReadAsStringAsync());
        var demote = await owner.PatchAsJsonAsync($"/api/v1/organization/members/{ownerMembership}", new { role = "Viewer" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, demote.StatusCode);
        Assert.Contains("last_owner", await demote.Content.ReadAsStringAsync());
        var self = await owner.PostAsJsonAsync($"/api/v1/organization/members/{ownerMembership}/deactivate", new { }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        Assert.Contains("cannot_deactivate_self", await self.Content.ReadAsStringAsync());

        // A Collector holds none of these permissions.
        var (forbidden, _) = await collectorClient.TryPostAsync($"/api/v1/organization/members/{ownerMembership}/deactivate", new { });
        Assert.Equal(403, forbidden);

        // Deactivate the (now) Accountant: their next request is refused and refresh no longer works.
        var deactivated = await owner.PostAsJsonAsync($"/api/v1/organization/members/{collector.MembershipId}/deactivate", new { }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal("Disabled", (await deactivated.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("status").GetString());
        var after = await collectorClient.GetAsync("/api/v1/imports");
        Assert.True(after.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, after.StatusCode.ToString());
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens WHERE user_id = @u AND tenant_id = @t AND revoked_at IS NULL", ("u", collector.UserId), ("t", org.TenantId)));
        var login = await fixture.Api.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = collector.Email, password = collector.Password }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);   // no usable membership (SEC-06)

        // Re-invited after deactivation: the membership comes back with the new role.
        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync("/api/v1/organization/members/invite", new { email = collector.Email, role = "Viewer", locale = "en-JO" }, ApiScenario.Json)).StatusCode);
        var token = await TokenFromMailAsync(collector.Email);
        using var anonymous = fixture.Api.CreateClient();
        Assert.Equal(200, (await AcceptAsync(anonymous, new { token })).Status);
        Assert.Equal("Viewer", (await fixture.Api.LoginAsync(collector.Email, collector.Password)).Role);
    }
}
