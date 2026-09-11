using System.Security.Cryptography;
using System.Text;
using FinanceAi.Domain.Abstractions;
using FinanceAi.Domain.Authorization;

namespace FinanceAi.Domain.Entities;

/// <summary>
/// An invitation to join an organization (slice 12). The row carries the SHA-256 of the token; the token
/// itself exists only in the email (D-1). Single use, seven days.
/// </summary>
public sealed class MemberInvitation : ITenantScoped
{
    public const int ValidDays = 7;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Email { get; set; }
    public TenantRole Role { get; set; }
    public required string Locale { get; set; }
    public required string TokenHash { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPending(DateTimeOffset now) => this.AcceptedAt is null && this.RevokedAt is null && this.ExpiresAt > now;

    public string Status(DateTimeOffset now) => this.AcceptedAt is not null ? "Accepted" : this.RevokedAt is not null ? "Revoked" : this.ExpiresAt <= now ? "Expired" : "Pending";
}

/// <summary>The token and its hash. 256 bits from the CSPRNG; the hash is what the database keeps.</summary>
public static class InvitationTokens
{
    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    /// <summary>A token is 64 lowercase hex characters; anything else is refused before any lookup, uniformly.</summary>
    public static bool LooksLikeToken(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigitLower);
}

/// <summary>Which roles may be given through invite and role change (doc 05). Owner moves only through the transfer flow (slice 12 D-3).</summary>
public static class RoleAssignment
{
    public static readonly IReadOnlyList<TenantRole> Assignable = [TenantRole.Admin, TenantRole.Accountant, TenantRole.Collector, TenantRole.Viewer];

    public static bool CanAssign(TenantRole role) => role != TenantRole.Owner && Enum.IsDefined(role);
}
