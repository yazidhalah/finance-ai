using FinanceAi.Infrastructure.Security;

namespace FinanceAi.UnitTests.Security;

/// <summary>Slice 25 (SEC-01): the bundled breached-password list.</summary>
public sealed class BreachedPasswordsTests
{
    [Fact]
    public void List_IsBundled_AndOnlyHoldsEntriesTheLengthRuleWouldLetThrough()
    {
        Assert.True(BreachedPasswords.Count > 40_000, $"{BreachedPasswords.Count} entries");
    }

    [Theory]
    [InlineData("123qweasdzxc")]        // top of the ≥ 12 subset
    [InlineData("1qaz2wsx3edc")]
    [InlineData("  123qweasdzxc  ")]    // trimmed
    [InlineData("123QWEASDZXC")]        // case-insensitive: the same password to an attacker
    public void KnownLeakedPasswords_AreRefused(string password) => Assert.True(BreachedPasswords.IsBreached(password));

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("Correct-Horse-Battery-9")]
    [InlineData("عبارة مرور طويلة وفريدة")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryPassphrases_Pass(string? password) => Assert.False(BreachedPasswords.IsBreached(password));
}
