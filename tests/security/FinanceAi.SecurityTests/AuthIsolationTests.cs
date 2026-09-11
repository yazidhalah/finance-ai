using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 13 AC-07: a second factor and a reset link belong to a person; ownership moves only inside one organization.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthIsolationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task SecondFactor_AndOwnership_StayWithTheirOwner()
    {
        var a = await fixture.Api.CreateOrganizationAsync("Auth A");
        var b = await fixture.Api.CreateOrganizationAsync("Auth B");
        using var ownerA = fixture.Api.AuthenticatedClient(a.OwnerSession);
        using var ownerB = fixture.Api.AuthenticatedClient(b.OwnerSession);
        var memberB = await fixture.Database.AddMemberAsync(b.TenantId, TenantRole.Accountant);

        // A's Owner, re-authenticated, cannot transfer to B's membership: it does not exist for A.
        await ownerA.ReauthAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ownerA.PostAsJsonAsync("/api/v1/organization/transfer-ownership", new { targetMembershipId = memberB.MembershipId }, ApiScenario.Json)).StatusCode);

        // Recovery codes are visible only to the platform scope and the person; a co-member's tenant scope sees none.
        var enrol = await (await ownerA.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        var secret = FinanceAi.Domain.Security.Base32.Decode(enrol.GetProperty("secret").GetString()!);
        var code = FinanceAi.Domain.Security.Totp.Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, (await ownerA.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code }, ApiScenario.Json)).StatusCode);
        await using var asOther = fixture.Database.OpenApp();
        await using var tx = await asOther.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)", asOther, tx))
        {
            set.Parameters.AddWithValue("t", a.TenantId.ToString());
            set.Parameters.AddWithValue("u", b.OwnerUserId.ToString());   // a different person, even inside A's tenant scope
            await set.ExecuteNonQueryAsync();
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM user_recovery_codes WHERE user_id = @u", asOther, tx))
        {
            count.Parameters.AddWithValue("u", a.OwnerUserId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using (var resets = new NpgsqlCommand("SELECT count(*) FROM password_reset_tokens", asOther, tx))
        {
            Assert.Equal(0L, (long)(await resets.ExecuteScalarAsync())!);   // never readable from a tenant scope at all
        }

        // A reset link issued for A's Owner does nothing for anyone else: it names its user by hash, not by input.
        using var anonymous = fixture.Api.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = a.OwnerEmail }, ApiScenario.Json)).StatusCode);
        var hash = await fixture.Database.ScalarAsync<string>("SELECT token_hash FROM password_reset_tokens WHERE user_id = @u AND used_at IS NULL", ("u", a.OwnerUserId));
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new { token = hash, password = "the-hash-is-not-the-token-2026" }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ownerB.GetAsync("/api/v1/me")).StatusCode);
    }
}
