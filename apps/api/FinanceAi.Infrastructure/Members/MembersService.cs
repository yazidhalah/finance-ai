using System.Globalization;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Members;

/// <summary>
/// Invitations, role changes and deactivation inside a tenant (slice 12). Accepting an invitation is not here —
/// it runs before the invitee has a tenant, in <see cref="PlatformIdentityStore"/>.
/// </summary>
public sealed class MembersService(TenantDbContext db, IAuditWriter audit, TimeProvider time, IMailTransport mail)
{
    public sealed record InviteOutcome(string Outcome, MemberInvitation? Invitation);

    /// <summary>The first configured web origin: where the accept link points. Never the API's own host.</summary>
    public static string WebOrigin => (Environment.GetEnvironmentVariable("WEB_ORIGINS") ?? "http://localhost:5173").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>
    /// Always succeeds from the caller's point of view (SEC-07). Server-side the outcome is one of
    /// <c>invited</c> (a row and an email), <c>already_member</c> (nothing sent), and it is audited either way.
    /// </summary>
    public async Task<InviteOutcome> InviteAsync(string email, TenantRole role, string locale, Guid actorUserId, CancellationToken ct)
    {
        if (!RoleAssignment.CanAssign(role)) throw new CaseException("owner_via_transfer_only", "role");
        if (!Locales.IsSupported(locale)) throw new CaseException("unsupported_locale", "locale");
        var normalized = email.Trim();
        var now = time.GetUtcNow();

        var member = await db.TenantMemberships.Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.Status, u.Email })
            .AnyAsync(x => x.Email == normalized && x.Status == MembershipStatus.Active, ct);
        if (member)
        {
            await audit.WriteAsync(Event("membership.invite_requested", "tenant", db.CurrentTenantId, actorUserId, "already_member"), ct);
            return new InviteOutcome("already_member", null);
        }

        // One live invitation per address: the previous one is superseded so only the newest link works.
        foreach (var old in await db.MemberInvitations.Where(i => i.Email == normalized && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct))
        {
            old.RevokedAt = now;
            old.RevokedBy = actorUserId;
        }

        var token = InvitationTokens.NewToken();
        var invitation = new MemberInvitation
        {
            TenantId = db.CurrentTenantId,
            Email = normalized,
            Role = role,
            Locale = locale,
            TokenHash = InvitationTokens.Hash(token),
            InvitedBy = actorUserId,
            ExpiresAt = now.AddDays(MemberInvitation.ValidDays),
            CreatedAt = now,
        };
        db.MemberInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        var tenantName = await db.Tenants.Select(t => t.Name).FirstAsync(ct);
        var link = $"{WebOrigin}/accept-invitation?token={token}";
        var (subject, body) = InvitationMail(locale, tenantName, role, link, invitation.ExpiresAt);
        // D-4: staff mail rides the global switch only; the tenant's outbound switch and cap are for customers.
        if (OutboundSwitch.GloballyEnabled)
        {
            await mail.SendAsync(new OutgoingMail(normalized, subject, body, locale.StartsWith("ar", StringComparison.Ordinal) ? "ar" : "en", $"invitation-{invitation.Id}"), ct);
        }

        await audit.WriteAsync(Event("membership.invite_requested", "member_invitation", invitation.Id, actorUserId, "invited", role.ToString()), ct);
        return new InviteOutcome("invited", invitation);
    }

    public async Task<MemberInvitation> RevokeAsync(Guid invitationId, Guid actorUserId, CancellationToken ct)
    {
        var invitation = await db.MemberInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct) ?? throw new CaseException("invitation_not_found");
        var now = time.GetUtcNow();
        if (!invitation.IsPending(now)) throw new CaseException("invitation_not_pending");
        invitation.RevokedAt = now;
        invitation.RevokedBy = actorUserId;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("membership.invitation_revoked", "member_invitation", invitation.Id, actorUserId), ct);
        return invitation;
    }

    public async Task<TenantMembership> ChangeRoleAsync(Guid membershipId, TenantRole role, Guid actorUserId, CancellationToken ct)
    {
        if (!RoleAssignment.CanAssign(role)) throw new CaseException("owner_via_transfer_only", "role");
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tenant_memberships WHERE id = {membershipId} FOR UPDATE", ct);
        var m = await db.TenantMemberships.FirstOrDefaultAsync(x => x.Id == membershipId, ct) ?? throw new CaseException("member_not_found");
        if (m.Status == MembershipStatus.Disabled) throw new CaseException("member_disabled");
        var otherOwners = await db.TenantMemberships.CountAsync(x => x.Id != m.Id && x.Role == TenantRole.Owner && x.Status != MembershipStatus.Disabled, ct);
        if (TenantMembership.WouldRemoveLastOwner(m.Role, m.Status, role, m.Status, otherOwners)) throw new CaseException("last_owner", "role");
        if (m.Role == role) return m;
        var from = m.Role;
        m.Role = role;
        if (FinanceAi.Domain.Security.MfaPolicy.Required(role) && m.MfaGraceUntil is null) m.MfaGraceUntil = time.GetUtcNow().Add(FinanceAi.Domain.Security.MfaPolicy.Grace);   // SEC-02, slice 13
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("membership.role_changed", "tenant_membership", m.Id, actorUserId, null, null, from.ToString(), role.ToString()), ct);
        return m;
    }

    public async Task<TenantMembership> DeactivateAsync(Guid membershipId, Guid actorUserId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tenant_memberships WHERE id = {membershipId} FOR UPDATE", ct);
        var m = await db.TenantMemberships.FirstOrDefaultAsync(x => x.Id == membershipId, ct) ?? throw new CaseException("member_not_found");
        if (m.UserId == actorUserId) throw new CaseException("cannot_deactivate_self");
        if (m.Status == MembershipStatus.Disabled) return m;
        var otherOwners = await db.TenantMemberships.CountAsync(x => x.Id != m.Id && x.Role == TenantRole.Owner && x.Status != MembershipStatus.Disabled, ct);
        if (TenantMembership.WouldRemoveLastOwner(m.Role, m.Status, m.Role, MembershipStatus.Disabled, otherOwners)) throw new CaseException("last_owner");
        var now = time.GetUtcNow();
        m.Status = MembershipStatus.Disabled;
        // Their sessions in this tenant end now: refresh is refused, and every request re-resolves the membership.
        foreach (var token in await db.RefreshTokens.Where(t => t.UserId == m.UserId && t.RevokedAt == null).ToListAsync(ct))
        {
            token.RevokedAt = now;
            token.RevokedReason = "membership_deactivated";
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("membership.deactivated", "tenant_membership", m.Id, actorUserId, null, null, MembershipStatus.Active.ToString(), MembershipStatus.Disabled.ToString()), ct);
        return m;
    }

    /// <summary>
    /// Slice 13: the Owner hands over to an Active member who already has a second factor (SEC-02 will bind them the
    /// moment they are Owner). One transaction, two updates in the order the one_owner_per_tenant index needs.
    /// </summary>
    public async Task<(TenantMembership NewOwner, TenantMembership PreviousOwner)> TransferOwnershipAsync(Guid targetMembershipId, Guid actorUserId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM tenant_memberships WHERE tenant_id = {db.CurrentTenantId} FOR UPDATE", ct);
        var current = await db.TenantMemberships.FirstOrDefaultAsync(m => m.UserId == actorUserId && m.Status == MembershipStatus.Active, ct) ?? throw new CaseException("member_not_found");
        if (current.Role != TenantRole.Owner) throw new CaseException("not_owner");
        var target = await db.TenantMemberships.FirstOrDefaultAsync(m => m.Id == targetMembershipId, ct) ?? throw new CaseException("member_not_found");
        if (target.Id == current.Id) throw new CaseException("cannot_transfer_to_self");
        if (target.Status != MembershipStatus.Active) throw new CaseException("member_disabled");
        var targetUser = await db.Users.FirstAsync(u => u.Id == target.UserId, ct);
        if (!targetUser.MfaEnrolled) throw new CaseException("target_mfa_required");

        var now = time.GetUtcNow();
        var targetFrom = target.Role.ToString();
        current.Role = TenantRole.Admin;              // first: the index allows one Owner
        current.MfaGraceUntil ??= now.Add(FinanceAi.Domain.Security.MfaPolicy.Grace);
        await db.SaveChangesAsync(ct);
        target.Role = TenantRole.Owner;
        target.MfaGraceUntil = now;                   // already enrolled; the deadline is moot
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("tenant.ownership_transferred", "tenant_membership", target.Id, actorUserId, null, null, targetFrom, "Owner"), ct);
        await audit.WriteAsync(Event("membership.role_changed", "tenant_membership", current.Id, actorUserId, "ownership_transferred", null, "Owner", "Admin"), ct);
        return (target, current);
    }

    /// <summary>System strings in the invitee's locale (not a customer template, slice 12 §1): the organization's name, the role, the link, the expiry.</summary>
    public static (string Subject, string Body) InvitationMail(string locale, string tenantName, TenantRole role, string link, DateTimeOffset expiresAt)
    {
        var until = expiresAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return locale.StartsWith("ar", StringComparison.Ordinal)
            ? ($"دعوة للانضمام إلى {tenantName} على finance-ai", $"تمت دعوتك للانضمام إلى {tenantName} بصفة {RoleNameAr(role)}.\n\nلقبول الدعوة افتح الرابط التالي قبل {until}:\n{link}\n\nإن لم تكن تتوقع هذه الدعوة فتجاهل هذه الرسالة.")
            : ($"You are invited to join {tenantName} on finance-ai", $"You have been invited to join {tenantName} as {role}.\n\nTo accept, open this link before {until}:\n{link}\n\nIf you were not expecting this invitation, ignore this message.");
    }

    private static string RoleNameAr(TenantRole role) => role switch
    {
        TenantRole.Admin => "مسؤول",
        TenantRole.Accountant => "محاسب",
        TenantRole.Collector => "محصّل",
        TenantRole.Viewer => "مطّلع",
        _ => role.ToString(),
    };

    private AuditEvent Event(string eventType, string entityType, Guid entityId, Guid actor, string? reasonCode = null, string? note = null, string? fromState = null, string? toState = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = ActorKinds.User,
        EventType = eventType,
        EntityType = entityType,
        EntityId = entityId,
        ReasonCode = reasonCode,
        Note = note,
        FromState = fromState,
        ToState = toState,
    };
}
