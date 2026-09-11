using System.Security.Cryptography;
using System.Text;

namespace FinanceAi.Domain.Security;

/// <summary>RFC 4648 Base32 (no padding on output; padding tolerated on input) — what authenticator apps expect.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = text.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0) throw new FormatException("Not Base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return output.ToArray();
    }
}

/// <summary>
/// RFC 6238 TOTP over RFC 4226 HOTP: HMAC-SHA1, 30-second steps, 6 digits, one step of clock skew either way
/// (SEC-02). Sixty lines instead of a dependency (slice 13 D-2); the RFC's test vectors pin it.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20;
    public const int AllowedSkewSteps = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static string Code(ReadOnlySpan<byte> secret, long unixSeconds)
    {
        var counter = unixSeconds / StepSeconds;
        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, message, hash);
        var offset = hash[19] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Constant-time against the codes of the current step and its neighbours; a step already used is the caller's concern.</summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string? code, long unixSeconds)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit)) return false;
        var supplied = Encoding.ASCII.GetBytes(code);
        var ok = false;
        for (var skew = -AllowedSkewSteps; skew <= AllowedSkewSteps; skew++)
        {
            var expected = Encoding.ASCII.GetBytes(Code(secret, unixSeconds + (skew * StepSeconds)));
            ok |= CryptographicOperations.FixedTimeEquals(expected, supplied);
        }

        return ok;
    }

    /// <summary>The URI authenticator apps import; the issuer and account are percent-encoded.</summary>
    public static string ProvisioningUri(ReadOnlySpan<byte> secret, string issuer, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>Recovery codes (SEC-02): eight codes of 80 bits, shown once, stored as SHA-256 (slice 13 D-3).</summary>
public static class RecoveryCodes
{
    public const int Count = 8;

    public static IReadOnlyList<string> New()
    {
        var codes = new List<string>(Count);
        for (var i = 0; i < Count; i++)
        {
            var raw = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10));
            codes.Add($"{raw[..5]}-{raw[5..10]}-{raw[10..15]}-{raw[15..20]}");
        }

        return codes;
    }

    public static string Normalize(string code) => (code ?? string.Empty).Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

    public static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    public static bool LooksLikeRecoveryCode(string? code) => code is not null && Normalize(code) is { Length: 20 } n && n.All(char.IsAsciiHexDigitLower);
}

/// <summary>SEC-02: which roles must have a second factor.</summary>
public static class MfaPolicy
{
    public static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    public static bool Required(Authorization.TenantRole role) => role is Authorization.TenantRole.Owner or Authorization.TenantRole.Admin;

    /// <summary>True when this membership may no longer act without a second factor.</summary>
    public static bool Enforced(Authorization.TenantRole role, bool mfaEnrolled, DateTimeOffset? graceUntil, DateTimeOffset now) =>
        Required(role) && !mfaEnrolled && (graceUntil is null || graceUntil <= now);
}
