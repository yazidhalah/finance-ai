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

    /// <summary>Authentication method reference (slice 13): <c>pwd</c> or <c>mfa</c>.</summary>
    public const string Amr = "amr";

    /// <summary>Marks the five-minute re-authentication proof (SEC-09); never present on an access token.</summary>
    public const string Purpose = "purpose";

    public const string ReauthPurpose = "reauth";
}

/// <summary>Issues the short-lived access token of SEC-03.</summary>
public interface IAccessTokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid tenantId, TenantRole role, string amr);

    (string Token, DateTimeOffset ExpiresAt) IssueReauth(Guid userId);

    Task<Guid?> ValidateReauthAsync(string? token);

    (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid tenantId, TenantRole role);

    TimeSpan Lifetime { get; }

    /// <summary>The keys a signature may verify against: the current key and, during a rotation, the previous one (slice 17).</summary>
    IEnumerable<SecurityKey> ValidationKeys { get; }

    /// <summary>
    /// Slice 17 S4: the current key always; the previous key only for a token issued before this process started —
    /// the old process was the last thing to sign with it, so the window closes itself after one token lifetime.
    /// </summary>
    bool AcceptsSignature(string? keyId, DateTime issuedAtUtc);
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
    private readonly RsaSecurityKey? previousPublicKey;
    private readonly string? previousKeyId;
    private readonly DateTimeOffset processStartedAt;

    /// <summary>Tokens issued up to this long before the process started are still attributable to the old process (clock skew between hosts).</summary>
    public static readonly TimeSpan PreviousKeySkew = TimeSpan.FromSeconds(30);

    public RsaAccessTokenIssuer(string privateKeyPem, TimeProvider? time = null) : this(privateKeyPem, null, time)
    {
    }

    /// <param name="previousPrivateKeyPem">The key being retired (<c>JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS</c>), or null. Only its public half is kept.</param>
    public RsaAccessTokenIssuer(string privateKeyPem, string? previousPrivateKeyPem, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        this.rsa = RSA.Create();
        this.rsa.ImportFromPem(privateKeyPem);
        this.time = time ?? TimeProvider.System;
        this.processStartedAt = this.time.GetUtcNow();

        if (!string.IsNullOrWhiteSpace(previousPrivateKeyPem))
        {
            using var previous = RSA.Create();
            previous.ImportFromPem(previousPrivateKeyPem);
            this.previousKeyId = KeyIdOf(previous);
            this.previousPublicKey = new RsaSecurityKey(previous.ExportParameters(false)) { KeyId = this.previousKeyId };
            if (this.previousKeyId == KeyIdOf(this.rsa))
            {
                throw new InvalidOperationException("JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS is the same key as JWT_SIGNING_KEY_PEM_BASE64; a rotation needs a new current key.");
            }
        }

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
    }

    public string KeyId => KeyIdOf(this.rsa);

    public IEnumerable<SecurityKey> ValidationKeys => this.previousPublicKey is null ? [this.PublicKey] : [this.PublicKey, this.previousPublicKey];

    public bool AcceptsSignature(string? keyId, DateTime issuedAtUtc)
    {
        if (keyId == this.KeyId) return true;
        if (this.previousKeyId is null || keyId != this.previousKeyId) return false;
        return issuedAtUtc <= (this.processStartedAt + PreviousKeySkew).UtcDateTime;
    }

    public TimeSpan Lifetime => TokenLifetime;

    /// <summary>The public half, for the API's token validation parameters.</summary>
    public RsaSecurityKey PublicKey => new(this.rsa.ExportParameters(false)) { KeyId = KeyIdOf(this.rsa) };

    public static readonly TimeSpan ReauthLifetime = TimeSpan.FromMinutes(5);

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid tenantId, TenantRole role) => this.Issue(userId, tenantId, role, "pwd");

    public (string Token, DateTimeOffset ExpiresAt) Issue(Guid userId, Guid tenantId, TenantRole role, string amr)
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
                [FinanceAiClaims.Amr] = amr,
            },
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }

    /// <summary>
    /// The re-authentication proof (SEC-09, slice 13 D-4): same key, five minutes, no tenant and no role — the
    /// middleware never accepts it as a session, and a sensitive endpoint accepts it only for its own user.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) IssueReauth(Guid userId)
    {
        var issuedAt = this.time.GetUtcNow();
        var expiresAt = issuedAt.Add(ReauthLifetime);
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
                [FinanceAiClaims.Purpose] = FinanceAiClaims.ReauthPurpose,
            },
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expiresAt);
    }

    /// <summary>Validates a proof and returns its subject, or null. Signature, lifetime and purpose are all checked.</summary>
    public async Task<Guid?> ValidateReauthAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKeys = this.ValidationKeys,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (_, expires, _, _) => expires is { } e && e > this.time.GetUtcNow().UtcDateTime,
        });
        if (!result.IsValid) return null;
        if (result.SecurityToken is JsonWebToken jwt && !this.AcceptsSignature(jwt.Kid, jwt.IssuedAt)) return null;
        var claims = result.Claims;
        if (!claims.TryGetValue(FinanceAiClaims.Purpose, out var purpose) || purpose?.ToString() != FinanceAiClaims.ReauthPurpose) return null;
        return claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub) && Guid.TryParse(sub?.ToString(), out var id) ? id : null;
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
