using System.Security.Claims;
using System.Security.Cryptography;
using FinanceAi.Domain.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FinanceAi.Infrastructure.Security;

/// <summary>The claim names of SEC-03.</summary>
public static class FinanceAiClaims
{
    public const string TenantId = "tid";
    public const string Role = "role";

    /// <summary>
    /// Permission-set version. SEC-03 allows "<c>perms</c> (or a permission-set version)"; we carry
    /// the version, and resolve the effective permission list from the membership on every request.
    /// A role change or a deactivation therefore takes effect on the <i>next request</i> rather
    /// than within 60 seconds (SEC-08), and the token stays small.
    /// </summary>
    public const string PermissionSetVersion = "psv";
}

/// <summary>Issues the short-lived access token of SEC-03.</summary>
public interface IAccessTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid tenantId, TenantRole role);

    TimeSpan Lifetime { get; }
}

public sealed class RsaAccessTokenIssuer : IAccessTokenIssuer, IDisposable
{
    /// <summary>SEC-03: 15 minutes. Short enough that revocation latency is bounded by design.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public const string Issuer = "finance-ai";
    public const string Audience = "finance-ai-api";
    public const int CurrentPermissionSetVersion = 1;

    private readonly RSA rsa;
    private readonly SigningCredentials credentials;
    private readonly TimeProvider time;

    public RsaAccessTokenIssuer(string privateKeyPem, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        this.rsa = RSA.Create();
        this.rsa.ImportFromPem(privateKeyPem);

        if (this.rsa.KeySize < 2048)
        {
            throw new InvalidOperationException(
                $"The JWT signing key is {this.rsa.KeySize} bits; RS256 requires at least 2048 (SEC-03).");
        }

        var key = new RsaSecurityKey(this.rsa) { KeyId = KeyIdOf(this.rsa) };

        // Signature providers are cached globally by key id. Two issuers built from the same key —
        // which is exactly what a test suite does — would then share one provider, and disposing
        // either would leave the other signing with a disposed RSA instance. Opting out of the
        // shared cache keeps disposal a local matter.
        this.credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        this.time = time ?? TimeProvider.System;
    }

    public TimeSpan Lifetime => TokenLifetime;

    /// <summary>The public half, for the API's token validation parameters.</summary>
    public RsaSecurityKey PublicKey => new(this.rsa.ExportParameters(false)) { KeyId = KeyIdOf(this.rsa) };

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid tenantId, TenantRole role)
    {
        var issuedAt = this.time.GetUtcNow();
        var expiresAt = issuedAt.Add(TokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = this.credentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
                [FinanceAiClaims.TenantId] = tenantId.ToString(),
                [FinanceAiClaims.Role] = role.ToString(),
                [FinanceAiClaims.PermissionSetVersion] = CurrentPermissionSetVersion,
            },
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }

    public void Dispose() => this.rsa.Dispose();

    private static string KeyIdOf(RSA key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16];
}

/// <summary>Reads the tenant and user out of a validated principal — and from nowhere else (SEC-20).</summary>
public static class PrincipalExtensions
{
    public static Guid? TenantIdOrNull(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal?.FindFirst(FinanceAiClaims.TenantId)?.Value, out var id) ? id : null;

    public static Guid? UserIdOrNull(this ClaimsPrincipal principal) =>
        Guid.TryParse(
            principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            out var id)
            ? id
            : null;
}
