using System.Security.Cryptography;
using System.Text;

namespace FinanceAi.Infrastructure.Security;

/// <summary>
/// Opaque refresh tokens (SEC-04). Opaque rather than a JWT: revocation must be immediate and
/// server-side, which a self-contained token cannot give you.
/// <para>
/// Only the SHA-256 of the token is stored (AC-15). Unlike a password, a refresh token is a
/// high-entropy random value, so a fast hash is the right choice — there is nothing to
/// brute-force — and it keeps the refresh path off the Argon2 cost curve.
/// </para>
/// </summary>
public static class RefreshTokens
{
    public const int TokenBytes = 32;

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>A URL-safe token with 256 bits of entropy.</summary>
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string HashOf(string rawToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
