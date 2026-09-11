using FinanceAi.Domain.Abstractions;
using FinanceAi.Domain.Authorization;

namespace FinanceAi.Domain.Entities;

public enum MembershipStatus { Invited, Active, Disabled }

/// <summary>
/// The link between a global <see cref="User"/> and one <see cref="Tenant"/>, carrying
/// exactly one role (doc 01 §5). Exactly one Active Owner per tenant is enforced by the
/// partial unique index <c>one_owner_per_tenant</c>, not only by application code (AC-03).
/// </summary>
public sealed class TenantMembership : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public TenantRole Role { get; set; }
    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    public Guid? InvitedBy { get; set; }

    /// <summary>SEC-02 with a grace period (slice 13 D-1): Owner and Admin memberships must enrol a second factor by this moment.</summary>
    public DateTimeOffset? MfaGraceUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }

    public bool CanAuthenticate => Status == MembershipStatus.Active;

    /// <summary>
    /// The last Owner of a tenant may not be demoted or deactivated (doc 10 slice 1 §7).
    /// The database index guarantees at most one Owner; this guards the "at least one" half.
    /// </summary>
    public static bool WouldRemoveLastOwner(
        TenantRole currentRole,
        MembershipStatus currentStatus,
        TenantRole newRole,
        MembershipStatus newStatus,
        int otherActiveOwnerCount)
    {
        var wasActiveOwner = currentRole == TenantRole.Owner && currentStatus != MembershipStatus.Disabled;
        var staysActiveOwner = newRole == TenantRole.Owner && newStatus != MembershipStatus.Disabled;
        return wasActiveOwner && !staysActiveOwner && otherActiveOwnerCount == 0;
    }
}
