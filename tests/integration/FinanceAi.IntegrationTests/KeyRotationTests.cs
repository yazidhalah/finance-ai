using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceAi.Domain.Security;
using FinanceAi.Infrastructure.Security;
using FinanceAi.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 17 AC-02 … AC-04: key rotation on the running host, and the bulk rotation the migrator runs.</summary>
[Collection(ApiCollection.Name)]
public sealed class KeyRotationTests(ApiTestFixture fixture)
{
    private static string NewKek() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string CodeFor(string secretBase32) => Totp.Code(Base32.Decode(secretBase32), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static async Task<string> EnrolAsync(HttpClient client)
    {
        var enrol = await (await client.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        var secret = enrol.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new { code = CodeFor(secret) }, ApiScenario.Json)).StatusCode);
        return secret;
    }

    private static async Task<int> LoginAsync(HttpClient anonymous, string email, string totp) =>
        (int)(await anonymous.PostAsJsonAsync("/api/v1/auth/login", new { email, password = ApiScenario.ValidPassword, totp }, ApiScenario.Json)).StatusCode;

    private Task<byte[]?> StoredAsync(Guid userId) => fixture.Database.ScalarAsync<byte[]>("SELECT mfa_secret_enc FROM users WHERE id = @u", ("u", userId));

    /// <summary>AC-02: enrolled under A; the host moves to B (+ previous A); the next sign-in re-seals; then A can go.</summary>
    [Fact]
    public async Task MfaKek_RotatesOnUse()
    {
        var original = fixture.Api.Secrets.Inner;
        var a = NewKek();
        var b = NewKek();
        try
        {
            fixture.Api.Secrets.Inner = new AesGcmSecretBox(a);
            var org = await fixture.Api.CreateOrganizationAsync("Rotate Co");
            using var owner = fixture.Api.AuthenticatedClient(org.OwnerSession);
            var secret = await EnrolAsync(owner);
            var underA = (await StoredAsync(org.OwnerUserId))!;
            Assert.Equal(fixture.Api.Secrets.Inner.CurrentKeyId, Convert.ToHexStringLower(underA[5..13]));

            // The rotation: B is current, A is being retired.
            var rotated = new AesGcmSecretBox(b, a);
            fixture.Api.Secrets.Inner = rotated;
            using var anonymous = fixture.Api.CreateClient();
            Assert.Equal(200, await LoginAsync(anonymous, org.OwnerEmail, CodeFor(secret)));
            var underB = (await StoredAsync(org.OwnerUserId))!;
            Assert.NotEqual(underA, underB);
            Assert.Equal(rotated.CurrentKeyId, Convert.ToHexStringLower(underB[5..13]));
            Assert.False(rotated.NeedsReseal(underB));

            // A retired for good: the resealed row still works; a row still under A would not, and never falls back to clear text.
            fixture.Api.Secrets.Inner = new AesGcmSecretBox(b);
            Assert.Equal(200, await LoginAsync(anonymous, org.OwnerEmail, CodeFor(secret)));
            await fixture.Database.ExecuteAsync("UPDATE users SET mfa_secret_enc = @e WHERE id = @u", ("e", underA), ("u", org.OwnerUserId));
            Assert.Equal(401, await LoginAsync(anonymous, org.OwnerEmail, CodeFor(secret)));

            // Leave nothing under a discarded key in the shared database: this user is un-enrolled.
            await fixture.Database.ExecuteAsync("UPDATE users SET mfa_secret_enc = NULL, mfa_pending_secret_enc = NULL, mfa_enabled_at = NULL WHERE id = @u", ("u", org.OwnerUserId));
        }
        finally
        {
            fixture.Api.Secrets.Inner = original;
        }
    }

    /// <summary>AC-03: the migrator's bulk rotation — counts, idempotence, and the refusal that protects the operator.</summary>
    [Fact]
    public async Task MfaKek_BulkRotation()
    {
        var original = fixture.Api.Secrets.Inner;
        var b = NewKek();
        try
        {
            // Enrol under the fixture's key (other tests' users are under it too — the database is shared).
            var one = await fixture.Api.CreateOrganizationAsync("Bulk One");
            var two = await fixture.Api.CreateOrganizationAsync("Bulk Two");
            using (var c1 = fixture.Api.AuthenticatedClient(one.OwnerSession)) await EnrolAsync(c1);
            using (var c2 = fixture.Api.AuthenticatedClient(two.OwnerSession))
            {
                await EnrolAsync(c2);
                await c2.PostAsJsonAsync("/api/v1/auth/mfa/enroll", new { }, ApiScenario.Json);   // a pending secret as well
            }

            var before1 = await StoredAsync(one.OwnerUserId);
            var before2 = await StoredAsync(two.OwnerUserId);

            // Without the previous key, nothing is touched and the message says why.
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => KekRotation.RunAsync(fixture.Database.MigratorConnectionString, new AesGcmSecretBox(b)));
            Assert.Contains("MFA_KEK_BASE64_PREVIOUS", refused.Message, StringComparison.Ordinal);
            Assert.Equal(before1, await StoredAsync(one.OwnerUserId));

            var originalKek = Environment.GetEnvironmentVariable("MFA_KEK_BASE64")!;
            var rotated = new AesGcmSecretBox(b, originalKek);
            var outcome = await KekRotation.RunAsync(fixture.Database.MigratorConnectionString, rotated);
            Assert.True(outcome.Resealed >= 2, $"resealed {outcome.Resealed}");
            Assert.Equal(0, outcome.Unreadable);
            var after1 = (await StoredAsync(one.OwnerUserId))!;
            var after2 = (await StoredAsync(two.OwnerUserId))!;
            Assert.NotEqual(before1, after1);
            Assert.NotEqual(before2, after2);
            Assert.False(rotated.NeedsReseal(after1));
            Assert.False(rotated.NeedsReseal(after2));
            var pending = (await fixture.Database.ScalarAsync<byte[]>("SELECT mfa_pending_secret_enc FROM users WHERE id = @u", ("u", two.OwnerUserId)))!;
            Assert.False(rotated.NeedsReseal(pending));

            // Idempotent, and complete: with only B every row opens.
            var again = await KekRotation.RunAsync(fixture.Database.MigratorConnectionString, new AesGcmSecretBox(b));
            Assert.Equal(0, again.Resealed);
            Assert.Equal(after1, await StoredAsync(one.OwnerUserId));

            // And back, so the rest of the suite finds its users under the host's key: a rotation is just another rotation.
            var back = await KekRotation.RunAsync(fixture.Database.MigratorConnectionString, new AesGcmSecretBox(originalKek, b));
            Assert.Equal(outcome.Resealed, back.Resealed);
            Assert.False(new AesGcmSecretBox(originalKek).NeedsReseal((await StoredAsync(one.OwnerUserId))!));
        }
        finally
        {
            fixture.Api.Secrets.Inner = original;
        }
    }

    /// <summary>AC-04: the host validates the retired key's signatures only for tokens that predate it.</summary>
    [Fact]
    public async Task Jwt_PreviousKey_IsAcceptedOnlyForOlderTokens()
    {
        var org = await fixture.Api.CreateOrganizationAsync("Old Key Co");
        using var retired = RSA.Create();
        retired.ImportFromPem(fixture.RetiredSigningKeyPem);
        var retiredKey = new RsaSecurityKey(retired) { KeyId = Convert.ToHexStringLower(SHA256.HashData(retired.ExportSubjectPublicKeyInfo()))[..16] };

        string Mint(DateTime issuedAt)
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = RsaAccessTokenIssuer.Issuer,
                Audience = RsaAccessTokenIssuer.Audience,
                IssuedAt = issuedAt,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(10),
                SigningCredentials = new SigningCredentials(retiredKey, SecurityAlgorithms.RsaSha256) { CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } },
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [JwtRegisteredClaimNames.Sub] = org.OwnerUserId.ToString(),
                    [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
                    [FinanceAiClaims.TenantId] = org.TenantId.ToString(),
                    [FinanceAiClaims.Role] = "Owner",
                    [FinanceAiClaims.PermissionSetVersion] = RsaAccessTokenIssuer.CurrentPermissionSetVersion,
                    [FinanceAiClaims.Amr] = "pwd",
                },
            };
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        async Task<HttpStatusCode> CallAsync(string token)
        {
            using var client = fixture.Api.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await client.GetAsync("/api/v1/me")).StatusCode;
        }

        // The host started long before "one hour ago": a token the old process could have issued is honoured.
        Assert.Equal(HttpStatusCode.OK, await CallAsync(Mint(DateTime.UtcNow.AddHours(-1))));
        // A token signed with the retired key after the host started is not — whoever holds that key. (The host may
        // have started seconds ago in this run, so "after" is stated as an issued-at two minutes ahead; nbf is separate.)
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(Mint(DateTime.UtcNow.AddMinutes(2))));
        // The current key's tokens carry its kid and always work.
        var current = new JsonWebToken(org.OwnerSession.AccessToken);
        Assert.NotEqual(retiredKey.KeyId, current.Kid);
        Assert.Equal(HttpStatusCode.OK, await CallAsync(org.OwnerSession.AccessToken));
    }
}
