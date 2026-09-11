using System.Text;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Security;

namespace FinanceAi.UnitTests.Security;

/// <summary>Slice 13 AC-01: the RFC 6238 appendix B vectors (SHA-1, 8 digits truncated to our 6), skew, Base32, recovery codes, the policy.</summary>
public sealed class TotpTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]            // RFC: 94287082
    [InlineData(1111111109L, "081804")]    // RFC: 07081804
    [InlineData(1111111111L, "050471")]    // RFC: 14050471
    [InlineData(1234567890L, "005924")]    // RFC: 89005924
    [InlineData(2000000000L, "279037")]    // RFC: 69279037
    [InlineData(20000000000L, "353130")]   // RFC: 65353130
    public void Rfc6238_Vectors(long unixSeconds, string expectedSixDigits) => Assert.Equal(expectedSixDigits, Totp.Code(Secret, unixSeconds));

    [Fact]
    public void Verify_AcceptsOneStepOfSkew_NotTwo()
    {
        var now = 1_700_000_000L;
        Assert.True(Totp.Verify(Secret, Totp.Code(Secret, now), now));
        Assert.True(Totp.Verify(Secret, Totp.Code(Secret, now - 30), now));
        Assert.True(Totp.Verify(Secret, Totp.Code(Secret, now + 30), now));
        Assert.False(Totp.Verify(Secret, Totp.Code(Secret, now - 60), now));
        Assert.False(Totp.Verify(Secret, Totp.Code(Secret, now + 90), now));
        Assert.False(Totp.Verify(Secret, "12345", now));
        Assert.False(Totp.Verify(Secret, "abcdef", now));
        Assert.False(Totp.Verify(Secret, null, now));
    }

    [Fact]
    public void Base32_RoundTrips_AndTheUriIsWhatAppsExpect()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(Secret));
        Assert.Equal(Secret, Base32.Decode("gezdgnbvgy3tqojqgezdgnbvgy3tqojq"));
        Assert.Equal(Secret, Base32.Decode(Base32.Encode(Secret) + "===="));
        var fresh = Totp.NewSecret();
        Assert.Equal(20, fresh.Length);
        Assert.Equal(fresh, Base32.Decode(Base32.Encode(fresh)));
        var uri = Totp.ProvisioningUri(Secret, "finance-ai", "rana@example.jo");
        Assert.StartsWith("otpauth://totp/finance-ai:rana%40example.jo?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=finance-ai", uri);
        Assert.Contains("algorithm=SHA1&digits=6&period=30", uri);
    }

    [Fact]
    public void RecoveryCodes_AreEightDistinct_AndHashNormalized()
    {
        var codes = RecoveryCodes.New();
        Assert.Equal(8, codes.Count);
        Assert.Equal(8, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[0-9a-f]{5}-[0-9a-f]{5}-[0-9a-f]{5}-[0-9a-f]{5}$", c));
        Assert.Equal(RecoveryCodes.Hash(codes[0]), RecoveryCodes.Hash(" " + codes[0].ToUpperInvariant().Replace("-", string.Empty) + " "));
        Assert.True(RecoveryCodes.LooksLikeRecoveryCode(codes[0]));
        Assert.False(RecoveryCodes.LooksLikeRecoveryCode("123456"));
    }

    [Fact]
    public void Policy_BindsOwnersAndAdmins_AfterGrace()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(MfaPolicy.Required(TenantRole.Owner));
        Assert.True(MfaPolicy.Required(TenantRole.Admin));
        Assert.False(MfaPolicy.Required(TenantRole.Collector));
        Assert.False(MfaPolicy.Enforced(TenantRole.Owner, mfaEnrolled: false, now.AddDays(1), now));      // in grace
        Assert.True(MfaPolicy.Enforced(TenantRole.Owner, mfaEnrolled: false, now.AddSeconds(-1), now));   // past grace
        Assert.True(MfaPolicy.Enforced(TenantRole.Admin, mfaEnrolled: false, null, now));                 // no grace recorded: enforced
        Assert.False(MfaPolicy.Enforced(TenantRole.Owner, mfaEnrolled: true, null, now));
        Assert.False(MfaPolicy.Enforced(TenantRole.Viewer, mfaEnrolled: false, null, now));
    }
}
