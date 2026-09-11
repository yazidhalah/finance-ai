using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace FinanceAi.SecurityTests;

/// <summary>Slice 12 AC-07: invitations and memberships are the organization's; the accept token is the only cross-tenant key, and it is the invitee's alone.</summary>
[Collection(ApiCollection.Name)]
public sealed class MemberIsolationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task CrossTenantMembers_IsRejected()
    {
        var a = await fixture.Api.CreateOrganizationAsync("Members A");
        var b = await fixture.Api.CreateOrganizationAsync("Members B");
        using var ownerA = fixture.Api.AuthenticatedClient(a.OwnerSession);
        using var ownerB = fixture.Api.AuthenticatedClient(b.OwnerSession);
        var invitee = $"secret-{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.Accepted, (await ownerA.PostAsJsonAsync("/api/v1/organization/members/invite", new { email = invitee, role = "Viewer", locale = "en-JO" }, ApiScenario.Json)).StatusCode);
        var invitationA = (await ownerA.GetFromJsonAsync<JsonElement>("/api/v1/organization/invitations", ApiScenario.Json)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var membershipA = (await ownerA.GetFromJsonAsync<JsonElement>("/api/v1/organization/members", ApiScenario.Json)).GetProperty("items")[0].GetProperty("id").GetGuid();

        // Layer 1: B sees no invitation of A's and cannot act on A's ids.
        Assert.DoesNotContain(invitee, await ownerB.GetStringAsync("/api/v1/organization/invitations"));
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsJsonAsync($"/api/v1/organization/invitations/{invitationA}/revoke", new { }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PatchAsJsonAsync($"/api/v1/organization/members/{membershipA}", new { role = "Viewer" }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsJsonAsync($"/api/v1/organization/members/{membershipA}/deactivate", new { }, ApiScenario.Json)).StatusCode);
        Assert.DoesNotContain(a.OwnerEmail, await ownerB.GetStringAsync("/api/v1/organization/members"));

        // Layer 2 alone: with B's tenant set, A's invitation does not exist.
        await using var asB = fixture.Database.OpenApp();
        await using var tx = await asB.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", asB, tx))
        {
            set.Parameters.AddWithValue("t", b.TenantId.ToString());
            await set.ExecuteNonQueryAsync();
        }

        await using (var count = new NpgsqlCommand("SELECT count(*) FROM member_invitations WHERE id = @id", asB, tx))
        {
            count.Parameters.AddWithValue("id", invitationA);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // ...and B's scope cannot forge a row for A's tenant either (WITH CHECK).
        await using (var forge = new NpgsqlCommand("INSERT INTO member_invitations (id, tenant_id, email, role, locale, token_hash, invited_by, expires_at) VALUES (gen_random_uuid(), @t, 'x@example.test', 'Viewer', 'en-JO', repeat('a', 64), @u, now() + interval '1 day')", asB, tx))
        {
            forge.Parameters.AddWithValue("t", a.TenantId);
            forge.Parameters.AddWithValue("u", b.OwnerUserId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => forge.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);   // row-level security violation
        }
    }
}
