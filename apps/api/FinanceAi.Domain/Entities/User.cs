namespace FinanceAi.Domain.Entities;

public enum UserStatus { Invited, Active, Disabled }

/// <summary>
/// A global identity: one person, one email, across every organization they belong to
/// (doc 01 §5, doc 04 §4). Membership of an organization — and therefore the role — lives in
/// <see cref="TenantMembership"/>. A platform table (DM-06).
/// </summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Stored as <c>citext</c>: comparison is case-insensitive in the database.</summary>
    public required string Email { get; set; }

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    /// <summary>Argon2id encoded hash (SEC-01). Never a plaintext or reversible form.</summary>
    public string? PasswordHash { get; set; }

    public required string FullName { get; set; }
    public string PreferredLocale { get; set; } = Locales.Default;

    /// <summary>Reserved for TOTP enrolment (SEC-02); unused until slice 1b.</summary>
    public byte[]? MfaSecretEnc { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;
}
