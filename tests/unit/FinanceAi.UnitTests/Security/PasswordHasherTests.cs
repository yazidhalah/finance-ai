using FinanceAi.Infrastructure.Security;

namespace FinanceAi.UnitTests.Security;

/// <summary>AC-05, AC-06 / SEC-01.</summary>
public sealed class PasswordHasherTests
{
    private readonly Argon2idPasswordHasher hasher = new();

    [Fact]
    public void PasswordHasher_UsesArgon2idWithSpecifiedParameters()
    {
        var encoded = this.hasher.Hash("correct horse battery staple");

        Assert.True(Argon2idPasswordHasher.TryParse(encoded, out var parameters, out var salt, out var hash));

        // SEC-01 states these as minimums. Raising them later must not silently lower them.
        Assert.True(parameters.MemoryKib >= 65536, $"m={parameters.MemoryKib} KiB is below the 64 MB minimum.");
        Assert.True(parameters.Iterations >= 3, $"t={parameters.Iterations} is below the minimum of 3.");
        Assert.True(parameters.Parallelism >= 1);
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
        Assert.StartsWith("$argon2id$v=19$", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Hash_IsSaltedSoEqualPasswordsProduceDifferentHashes()
    {
        var first = this.hasher.Hash("correct horse battery staple");
        var second = this.hasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
        Assert.True(this.hasher.Verify("correct horse battery staple", first));
        Assert.True(this.hasher.Verify("correct horse battery staple", second));
    }

    [Fact]
    public void Verify_RejectsAWrongPassword() =>
        Assert.False(this.hasher.Verify("wrong password entirely", this.hasher.Hash("correct horse battery staple")));

    [Fact]
    public void Verify_RejectsATamperedHash()
    {
        var encoded = this.hasher.Hash("correct horse battery staple");
        var tampered = encoded[..^4] + (encoded.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        Assert.False(this.hasher.Verify("correct horse battery staple", tampered));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2d$v=19$m=65536,t=3,p=1$c2FsdA==$aGFzaA==")]
    [InlineData("$argon2id$v=16$m=65536,t=3,p=1$c2FsdA==$aGFzaA==")]
    public void Verify_RejectsAMalformedOrForeignEncoding(string encoded) =>
        Assert.False(this.hasher.Verify("correct horse battery staple", encoded));

    [Fact]
    public void MinimumPasswordLength_IsTwelveCharacters() =>
        // SEC-01: length, not composition rules. "P@ss1!" is weaker than "correct horse battery staple".
        Assert.Equal(12, Argon2idPasswordHasher.MinimumPasswordLength);
}

/// <summary>AC-15 / SEC-04.</summary>
public sealed class RefreshTokenTests
{
    [Fact]
    public void Generate_ProducesUnpredictableUrlSafeTokens()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => RefreshTokens.Generate()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]+$", t));

        // 32 random bytes in base64url, unpadded.
        Assert.All(tokens, t => Assert.Equal(43, t.Length));
    }

    [Fact]
    public void HashOf_IsDeterministicAndDoesNotContainTheToken()
    {
        var token = RefreshTokens.Generate();
        var hash = RefreshTokens.HashOf(token);

        Assert.Equal(hash, RefreshTokens.HashOf(token));
        Assert.NotEqual(token, hash);
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Lifetime_IsFourteenDays() => Assert.Equal(TimeSpan.FromDays(14), RefreshTokens.Lifetime);
}
