namespace FinanceAi.Domain.Entities;

/// <summary>A recovery code (SEC-02): one of eight, hashed, single use. Platform-level: it belongs to the person.</summary>
public sealed class UserRecoveryCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A password-reset link's hash (SEC-07): one hour, single use, touched only by the identity flows.</summary>
public sealed class PasswordResetToken
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(1);

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUsable(DateTimeOffset now) => this.UsedAt is null && this.ExpiresAt > now;
}

/// <summary>Slice 24: the verification link a registration sends. Same shape and lifetime discipline as the reset token.</summary>
public sealed class EmailVerificationToken
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUsable(DateTimeOffset now) => this.UsedAt is null && now < this.ExpiresAt;
}
