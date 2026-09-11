using System.Security.Cryptography;
using FinanceAi.Domain.Authorization;
using FinanceAi.Infrastructure.Security;
using FinanceAi.TestSupport;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinanceAi.UnitTests.Security;

/// <summary>Slice 17 AC-01: the versioned MFA envelope and the previous-key path of the secret box.</summary>
public sealed class SecretBoxRotationTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(20);

    [Fact]
    public void Envelope_SealedUnderA_OpensUnderBWithPreviousA_AndNeedsReseal()
    {
        var a = NewKey();
        var b = NewKey();
        var boxA = new AesGcmSecretBox(a);
        var sealedUnderA = boxA.Seal(Secret);
        Assert.False(boxA.NeedsReseal(sealedUnderA));
        Assert.Equal("FKEK1", System.Text.Encoding.ASCII.GetString(sealedUnderA[..5]));

        var rotated = new AesGcmSecretBox(b, a);
        Assert.Equal(Secret, rotated.Open(sealedUnderA));
        Assert.True(rotated.NeedsReseal(sealedUnderA));
        Assert.True(rotated.CanOpen(sealedUnderA));

        var resealed = rotated.Seal(rotated.Open(sealedUnderA));
        Assert.False(rotated.NeedsReseal(resealed));
        Assert.Equal(rotated.CurrentKeyId, Convert.ToHexStringLower(resealed[5..13]));
        Assert.NotEqual(boxA.CurrentKeyId, rotated.CurrentKeyId);

        // After the previous key is retired, only the resealed envelope opens.
        var onlyB = new AesGcmSecretBox(b);
        Assert.Equal(Secret, onlyB.Open(resealed));
        Assert.False(onlyB.CanOpen(sealedUnderA));
        Assert.Throws<CryptographicException>(() => onlyB.Open(sealedUnderA));
    }

    [Fact]
    public void Slice13Envelope_WithoutHeader_StillOpens_AndNeedsReseal()
    {
        var a = Convert.FromBase64String(NewKey());
        // The slice 13 layout: nonce · tag · ciphertext, no key id.
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[Secret.Length];
        using (var aes = new AesGcm(a, 16)) aes.Encrypt(nonce, Secret, cipher, tag);
        byte[] legacy = [.. nonce, .. tag, .. cipher];

        var current = new AesGcmSecretBox(Convert.ToBase64String(a));
        Assert.Equal(Secret, current.Open(legacy));
        Assert.True(current.NeedsReseal(legacy));   // no header → reseal even under the same key

        var rotated = new AesGcmSecretBox(NewKey(), Convert.ToBase64String(a));
        Assert.Equal(Secret, rotated.Open(legacy));   // current key fails the tag, the previous key opens it

        var stranger = new AesGcmSecretBox(NewKey(), NewKey());
        Assert.False(stranger.CanOpen(legacy));
    }

    [Fact]
    public void Keys_MustBe32Bytes()
    {
        Assert.Throws<InvalidOperationException>(() => new AesGcmSecretBox(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
        Assert.Throws<InvalidOperationException>(() => new AesGcmSecretBox(NewKey(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(31))));
        Assert.False(new AesGcmSecretBox(null).Available);
    }
}

/// <summary>Slice 17 AC-04: the previous signing key verifies only tokens that predate the process.</summary>
public sealed class SigningKeyRotationTests
{
    private static string NewPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static async Task<bool> ValidatesAsync(RsaAccessTokenIssuer host, string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = RsaAccessTokenIssuer.Issuer,
            ValidAudience = RsaAccessTokenIssuer.Audience,
            IssuerSigningKeys = host.ValidationKeys,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = false,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });
        if (!result.IsValid) return false;
        var jwt = (JsonWebToken)result.SecurityToken;
        return host.AcceptsSignature(jwt.Kid, jwt.IssuedAt);
    }

    [Fact]
    public async Task PreviousKey_IsAcceptedOnlyForTokensIssuedBeforeTheProcessStarted()
    {
        var oldPem = NewPem();
        var newPem = NewPem();
        var clock = new SettableTimeProvider { Override = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero) };

        using var oldIssuer = new RsaAccessTokenIssuer(oldPem, clock);
        var beforeRestart = oldIssuer.Issue(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner).Token;

        clock.Override = clock.Override!.Value.AddMinutes(5);   // the rotation: the new process starts here
        using var host = new RsaAccessTokenIssuer(newPem, oldPem, clock);
        Assert.NotEqual(oldIssuer.KeyId, host.KeyId);
        Assert.Equal(2, host.ValidationKeys.Count());

        Assert.True(await ValidatesAsync(host, beforeRestart), "a token from before the restart, signed by the old key, is still good for its lifetime");
        Assert.True(await ValidatesAsync(host, host.Issue(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner).Token), "the current key always");

        clock.Override = clock.Override!.Value.AddMinutes(1);   // someone with the old private key signs after the rotation
        var afterRestart = oldIssuer.Issue(Guid.NewGuid(), Guid.NewGuid(), TenantRole.Owner).Token;
        Assert.False(await ValidatesAsync(host, afterRestart), "the retired key cannot mint anything the new process accepts");

        // Without the previous key configured, nothing from the old key is accepted at all.
        using var strict = new RsaAccessTokenIssuer(newPem, clock);
        Assert.False(await ValidatesAsync(strict, beforeRestart));

        // The re-auth proof follows the same rule.
        clock.Override = clock.Override!.Value.AddMinutes(-3);
        var proofBefore = oldIssuer.IssueReauth(Guid.NewGuid()).Token;   // iat before the restart, still within five minutes
        Assert.NotNull(await host.ValidateReauthAsync(proofBefore));
        clock.Override = clock.Override!.Value.AddMinutes(3);
        Assert.Null(await host.ValidateReauthAsync(oldIssuer.IssueReauth(Guid.NewGuid()).Token));
        Assert.NotNull(await host.ValidateReauthAsync(host.IssueReauth(Guid.NewGuid()).Token));
    }

    [Fact]
    public void PreviousKey_MustDifferFromTheCurrentOne()
    {
        var pem = NewPem();
        Assert.Throws<InvalidOperationException>(() => new RsaAccessTokenIssuer(pem, pem));
    }
}
