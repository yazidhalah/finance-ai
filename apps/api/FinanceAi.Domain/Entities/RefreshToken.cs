using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>
/// An opaque, rotating refresh token with reuse detection (SEC-04). Tokens issued from one
/// login form a <see cref="FamilyId"/>; presenting a token that has already been rotated is
/// evidence of theft, so the whole family is revoked at once.
/// <para>
/// Only <see cref="TokenHash"/> is stored — the raw token exists in the client cookie and
/// nowhere else (AC-15). A database reader cannot mint a session.
/// </para>
/// Not in doc 04; added by this slice and recorded as deviation D-2.
/// </summary>
public sealed class RefreshToken : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The tenant this session is scoped to; switching tenants starts a new family (API-02).</summary>
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }

    /// <summary>SHA-256 of the raw token. Base64. Never the raw value.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>A machine-readable reason: <c>rotated</c>, <c>logout</c>, <c>reuse_detected</c>.</summary>
    public string? RevokedReason { get; set; }

    public bool IsActiveAt(DateTimeOffset now) =>
        RevokedAt is null && RotatedAt is null && ExpiresAt > now;
}
