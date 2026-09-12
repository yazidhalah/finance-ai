using System.Net;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Database;

/// <summary>
/// The identity flows that necessarily run before a tenant is known: registration, login, refresh,
/// logout, "which organizations do I belong to", and tenant switching.
/// <para>
/// <b>This is the only file permitted to open a platform scope</b> (DM-06). Everything else in the
/// codebase runs inside a tenant scope and is protected by all three isolation layers. AC-35's
/// sibling test walks the source tree and fails the build if that ever stops being true.
/// </para>
/// <para>
/// Even here the escape is narrow: <c>app.platform_scope</c> unlocks only <c>tenants</c>,
/// <c>users</c>, <c>tenant_memberships</c> and <c>refresh_tokens</c>. The policies on
/// <c>tenant_settings</c> and <c>audit_events</c> have no platform clause at all, so writing an
/// audit row still requires a real tenant to be bound first.
/// </para>
/// </summary>
public sealed class PlatformIdentityStore(
    IDbContextFactory<TenantDbContext> contexts,
    IPasswordHasher passwordHasher,
    TimeProvider time,
    FinanceAi.Infrastructure.Security.ISecretBox secrets)
{
    /// <summary>SEC-06: lock the account after 10 consecutive failures, for a bounded window.</summary>
    public const int MaxFailedLogins = 10;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // ---------------------------------------------------------------------------------------
    // Registration
    // ---------------------------------------------------------------------------------------

    public sealed record RegisterCommand(
        string Email,
        string Password,
        string FullName,
        string OrganizationName,
        string BaseCurrency,
        string Timezone,
        string Locale,
        IPAddress? ActorIp,
        string? RequestId);

    /// <summary>
    /// The outcome is deliberately not surfaced to the caller of the endpoint: SEC-07 requires that
    /// registration never reveals whether an email already exists. It is returned here so tests and
    /// server-side logs can tell the difference.
    /// </summary>
    public enum RegisterOutcome
    {
        Created,
        EmailAlreadyRegistered,
    }

    public sealed record RegisterResult(RegisterOutcome Outcome, Guid? TenantId, Guid? UserId, string? VerificationToken = null, string? Locale = null);

    /// <summary>
    /// Creates the user, the organization, the Owner membership and the settings row in one
    /// transaction (AC-01). Either all four exist afterwards or none do (AC-02).
    /// </summary>
    public async Task<RegisterResult> RegisterAsync(RegisterCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var db = await contexts.CreateDbContextAsync(ct);

        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        // The tenant is bound up front so the audit rows below can be written: audit_events has no
        // platform escape and would otherwise reject them.
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, tenantId, userId, ct);

        var emailTaken = await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Email == command.Email, ct);

        if (emailTaken)
        {
            return new RegisterResult(RegisterOutcome.EmailAlreadyRegistered, null, null);
        }

        var now = time.GetUtcNow();

        var tenant = new Tenant
        {
            Id = tenantId,
            Name = command.OrganizationName,
            BaseCurrency = command.BaseCurrency,
            Timezone = command.Timezone,
            DefaultLocale = command.Locale,
            Status = TenantStatus.Active,
            CreatedAt = now,
        };

        var user = new User
        {
            Id = userId,
            Email = command.Email,
            FullName = command.FullName,
            PreferredLocale = command.Locale,
            PasswordHash = passwordHasher.Hash(command.Password),
            Status = UserStatus.Active,
            CreatedAt = now,
        };

        var membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Role = TenantRole.Owner,
            Status = MembershipStatus.Active,
            CreatedAt = now,
            MfaGraceUntil = now.Add(FinanceAi.Domain.Security.MfaPolicy.Grace),   // SEC-02, slice 13 D-1
        };

        var settings = new TenantSettings { TenantId = tenantId };

        // Two saves, one transaction. The membership and the settings row carry foreign keys to
        // tenants and users, but they are mapped as plain columns rather than as EF relationships —
        // the composite (tenant_id, user_id) keys that layer 3 depends on do not model cleanly as
        // navigations — so EF has no dependency graph to order the inserts by. Ordering them here is
        // explicit and cheap; atomicity is unaffected, because both saves are inside the same scope.
        db.Tenants.Add(tenant);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsEmailUniquenessViolation(ex))
        {
            // Two registrations for the same address raced past the check above. The unique index is
            // the real arbiter; the loser rolls back on scope disposal and reports the same outcome
            // as any other taken address. Letting this surface as a 500 would make an error page an
            // account-existence oracle (SEC-07).
            return new RegisterResult(RegisterOutcome.EmailAlreadyRegistered, null, null);
        }

        db.TenantMemberships.Add(membership);
        db.TenantSettings.Add(settings);
        await db.SaveChangesAsync(ct);

        var audit = new AuditWriter(db);
        await audit.WriteAsync(Event(AuditEventTypes.TenantCreated, "tenant", tenantId, tenantId, userId, command.ActorIp, command.RequestId, now), ct);
        await audit.WriteAsync(Event(AuditEventTypes.UserRegistered, "user", userId, tenantId, userId, command.ActorIp, command.RequestId, now), ct);
        await audit.WriteAsync(Event(AuditEventTypes.MembershipCreated, "tenant_membership", membership.Id, tenantId, userId, command.ActorIp, command.RequestId, now, toState: nameof(TenantRole.Owner)), ct);

        // Slice 24: the verification link (doc 05 "email verification required"); the caller mails it.
        var verification = InvitationTokens.NewToken();
        db.EmailVerificationTokens.Add(new EmailVerificationToken { UserId = userId, TokenHash = InvitationTokens.Hash(verification), ExpiresAt = now.Add(EmailVerificationToken.Validity), CreatedAt = now });
        await db.SaveChangesAsync(ct);

        await scope.CompleteAsync(ct);
        return new RegisterResult(RegisterOutcome.Created, tenantId, userId, verification, command.Locale);
    }

    // ---------------------------------------------------------------------------------------
    // Accepting an invitation (slice 12) — the one identity flow that starts from a token
    // ---------------------------------------------------------------------------------------

    public sealed record AcceptInvitationCommand(string Token, string? FullName, string? Password, IPAddress? ActorIp, string? RequestId);

    /// <summary>Like <see cref="RegisterOutcome"/>: the endpoint renders every failure identically (SEC-07).</summary>
    public enum AcceptInvitationOutcome
    {
        Accepted,
        Invalid,
        PasswordRequired,
    }

    public sealed record AcceptInvitationResult(AcceptInvitationOutcome Outcome, Guid? TenantId, Guid? UserId, string? Email, string? TenantName, bool CreatedUser);

    /// <summary>
    /// Two transactions by design. The first, with no tenant bound, finds the invitation by the hash of the token
    /// (the only cross-tenant read, and it is by an unguessable 256-bit value). The second binds the invitation's
    /// tenant so the membership, the user and the audit rows are written under a real tenant scope, re-checks the
    /// invitation under FOR UPDATE, and either creates the user or attaches the existing one.
    /// </summary>
    public async Task<AcceptInvitationResult> AcceptInvitationAsync(AcceptInvitationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var invalid = new AcceptInvitationResult(AcceptInvitationOutcome.Invalid, null, null, null, null, false);
        if (!InvitationTokens.LooksLikeToken(command.Token))
        {
            return invalid;
        }

        var hash = InvitationTokens.Hash(command.Token);
        var now = time.GetUtcNow();
        Guid tenantId;
        Guid invitationId;
        await using (var lookupDb = await contexts.CreateDbContextAsync(ct))
        await using (var lookup = await DatabaseScope.EnterPlatformAsync(lookupDb, null, null, ct))
        {
            var found = await lookupDb.MemberInvitations.IgnoreQueryFilters().Where(i => i.TokenHash == hash).Select(i => new { i.Id, i.TenantId }).FirstOrDefaultAsync(ct);
            if (found is null)
            {
                return invalid;
            }

            tenantId = found.TenantId;
            invitationId = found.Id;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, tenantId, null, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM member_invitations WHERE id = {invitationId} FOR UPDATE", ct);
        // No TenantContext is bound in an identity flow, so the EF filters see no tenant; the platform scope's RLS clause is the boundary here.
        var invitation = await db.MemberInvitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == invitationId && i.TenantId == tenantId, ct);
        if (invitation is null || !invitation.IsPending(now))
        {
            return invalid;
        }

        var tenant = await db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId, ct);
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == invitation.Email, ct);
        var createdUser = false;
        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(command.Password) || string.IsNullOrWhiteSpace(command.FullName))
            {
                return new AcceptInvitationResult(AcceptInvitationOutcome.PasswordRequired, null, null, invitation.Email, tenant.Name, false);
            }

            user = new User
            {
                Email = invitation.Email,
                FullName = command.FullName.Trim(),
                PreferredLocale = invitation.Locale,
                PasswordHash = passwordHasher.Hash(command.Password),
                EmailVerifiedAt = now,   // D-2: the address produced the token
                Status = UserStatus.Active,
                CreatedAt = now,
            };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsEmailUniquenessViolation(ex))
            {
                return invalid;   // registered in the meantime; the next attempt attaches the existing user
            }

            createdUser = true;
        }

        var existing = await db.TenantMemberships.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == user.Id, ct);
        TenantMembership membership;
        if (existing is null)
        {
            membership = new TenantMembership { TenantId = tenantId, UserId = user.Id, Role = invitation.Role, Status = MembershipStatus.Active, InvitedBy = invitation.InvitedBy, CreatedAt = now, MfaGraceUntil = FinanceAi.Domain.Security.MfaPolicy.Required(invitation.Role) ? now.Add(FinanceAi.Domain.Security.MfaPolicy.Grace) : null };
            db.TenantMemberships.Add(membership);
        }
        else
        {
            // Re-invited after deactivation: the membership comes back with the invited role.
            membership = existing;
            membership.Role = invitation.Role;
            membership.Status = MembershipStatus.Active;
            membership.InvitedBy = invitation.InvitedBy;
        }

        invitation.AcceptedAt = now;
        invitation.AcceptedUserId = user.Id;
        await db.SaveChangesAsync(ct);

        var audit = new AuditWriter(db);
        if (createdUser)
        {
            await audit.WriteAsync(Event(AuditEventTypes.UserRegistered, "user", user.Id, tenantId, user.Id, command.ActorIp, command.RequestId, now, note: "via invitation"), ct);
        }

        await audit.WriteAsync(Event(AuditEventTypes.MembershipCreated, "tenant_membership", membership.Id, tenantId, user.Id, command.ActorIp, command.RequestId, now, toState: invitation.Role.ToString(), reasonCode: "invitation_accepted"), ct);
        await audit.WriteAsync(Event("membership.invitation_accepted", "member_invitation", invitation.Id, tenantId, user.Id, command.ActorIp, command.RequestId, now), ct);
        await scope.CompleteAsync(ct);
        return new AcceptInvitationResult(AcceptInvitationOutcome.Accepted, tenantId, user.Id, invitation.Email, tenant.Name, createdUser);
    }

    // ---------------------------------------------------------------------------------------
    // Login
    // ---------------------------------------------------------------------------------------

    public enum AuthenticationOutcome
    {
        Succeeded,

        /// <summary>The password matched but the address was never verified (slice 24). Only ever after a correct password.</summary>
        EmailUnverified,

        /// <summary>Wrong password, unknown email, disabled user, or no usable membership. The
        /// caller MUST render all of these identically (SEC-06, SEC-07).</summary>
        Failed,

        /// <summary>Too many failures. Reported separately so the UI can show an unlock time
        /// (doc 06 §6.1) — only ever for an account that has already proved it exists.</summary>
        Locked,

        /// <summary>The password matched and the user has a second factor enrolled, but no code was given (slice 13).
        /// Reported only after the password matched, so it discloses nothing to anyone without it.</summary>
        MfaRequired,
    }

    public sealed record AuthenticatedSession(
        Guid UserId,
        string Email,
        string FullName,
        string PreferredLocale,
        Guid TenantId,
        string TenantName,
        string BaseCurrency,
        string Timezone,
        string TenantDefaultLocale,
        TenantRole Role,
        bool MfaUsed = false);

    public sealed record AuthenticationResult(
        AuthenticationOutcome Outcome,
        AuthenticatedSession? Session,
        DateTimeOffset? LockedUntil);

    /// <summary>
    /// Verifies credentials and resolves the tenant the session will be scoped to.
    /// <para>
    /// A user with several memberships is logged into their oldest active one; the client then
    /// calls <c>/auth/switch-tenant</c> (API-02). There is no way to name a tenant here, which is
    /// what makes AC-28 hold: correct credentials can never produce a token for an organization the
    /// user is not a member of.
    /// </para>
    /// </summary>
    public Task<AuthenticationResult> AuthenticateAsync(
        string email, string password, IPAddress? actorIp, string? requestId, CancellationToken ct = default) =>
        this.AuthenticateAsync(email, password, null, actorIp, requestId, ct);

    public async Task<AuthenticationResult> AuthenticateAsync(
        string email, string password, string? secondFactor, IPAddress? actorIp, string? requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, null, ct);

        var now = time.GetUtcNow();

        var user = await db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            // Spend comparable work on an unknown email so the response time does not distinguish
            // it from a wrong password (SEC-06). The value is a fixed, never-matching hash.
            passwordHasher.Verify(password, DummyHash.Value);
            await scope.CompleteAsync(ct);
            return new AuthenticationResult(AuthenticationOutcome.Failed, null, null);
        }

        var membership = await db.TenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == user.Id && m.Status == MembershipStatus.Active)
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (user.IsLockedAt(now))
        {
            await this.RecordFailureAsync(db, user, membership, actorIp, requestId, now, ct);
            await scope.CompleteAsync(ct);
            return new AuthenticationResult(AuthenticationOutcome.Locked, null, user.LockedUntil);
        }

        var passwordMatches = user.PasswordHash is not null &&
                              passwordHasher.Verify(password, user.PasswordHash);

        if (!passwordMatches || user.Status != UserStatus.Active || membership is null)
        {
            await this.RecordFailureAsync(db, user, membership, actorIp, requestId, now, ct);
            await scope.CompleteAsync(ct);

            return user.IsLockedAt(time.GetUtcNow()) || user.FailedLoginCount >= MaxFailedLogins
                ? new AuthenticationResult(AuthenticationOutcome.Locked, null, user.LockedUntil)
                : new AuthenticationResult(AuthenticationOutcome.Failed, null, null);
        }

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstAsync(t => t.Id == membership.TenantId, ct);

        if (tenant.Status != TenantStatus.Active)
        {
            await scope.CompleteAsync(ct);
            return new AuthenticationResult(AuthenticationOutcome.Failed, null, null);
        }

        // Slice 24: the address must be verified — judged only once the password has matched (SEC-07 ordering).
        if (user.EmailVerifiedAt is null)
        {
            await scope.CompleteAsync(ct);
            return new AuthenticationResult(AuthenticationOutcome.EmailUnverified, null, null);
        }

        // Slice 13 (SEC-02): the second factor, only once the password has matched.
        var mfaUsed = false;
        if (user.MfaEnrolled)
        {
            if (string.IsNullOrWhiteSpace(secondFactor))
            {
                await scope.CompleteAsync(ct);
                return new AuthenticationResult(AuthenticationOutcome.MfaRequired, null, null);
            }

            if (!await this.VerifySecondFactorAsync(db, user, secondFactor, now, ct))
            {
                // A wrong code counts like a wrong password: the same counter, the same lockout (SEC-06).
                await this.RecordFailureAsync(db, user, membership, actorIp, requestId, now, ct);
                await scope.CompleteAsync(ct);
                return user.IsLockedAt(time.GetUtcNow()) || user.FailedLoginCount >= MaxFailedLogins
                    ? new AuthenticationResult(AuthenticationOutcome.Locked, null, user.LockedUntil)
                    : new AuthenticationResult(AuthenticationOutcome.Failed, null, null);
            }

            mfaUsed = true;
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(ct);

        await DatabaseScope.SetTenantAsync(db, tenant.Id, user.Id, ct);
        await new AuditWriter(db).WriteAsync(
            Event(AuditEventTypes.LoginSucceeded, "user", user.Id, tenant.Id, user.Id, actorIp, requestId, now), ct);

        await scope.CompleteAsync(ct);

        return new AuthenticationResult(
            AuthenticationOutcome.Succeeded,
            new AuthenticatedSession(
                user.Id, user.Email, user.FullName, user.PreferredLocale,
                tenant.Id, tenant.Name, tenant.BaseCurrency, tenant.Timezone, tenant.DefaultLocale, membership.Role, mfaUsed),
            null);
    }

    /// <summary>A TOTP against the active secret, or a recovery code — which is burnt on use.</summary>
    private async Task<bool> VerifySecondFactorAsync(TenantDbContext db, User user, string code, DateTimeOffset now, CancellationToken ct)
    {
        if (user.MfaSecretEnc is null || !secrets.Available) return false;
        var trimmed = code.Trim();
        if (trimmed.Length == FinanceAi.Domain.Security.Totp.Digits)
        {
            byte[] secret;
            try
            {
                secret = secrets.Open(user.MfaSecretEnc);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false;   // an envelope under a key this host no longer holds: no bypass, no clear-text fallback (slice 17)
            }

            var ok = FinanceAi.Domain.Security.Totp.Verify(secret, trimmed, now.ToUnixTimeSeconds());
            if (ok && secrets.NeedsReseal(user.MfaSecretEnc))
            {
                user.MfaSecretEnc = secrets.Seal(secret);   // rotate on use (slice 17 S2); saved with the successful login
            }

            return ok;
        }

        if (!FinanceAi.Domain.Security.RecoveryCodes.LooksLikeRecoveryCode(trimmed)) return false;
        var hash = FinanceAi.Domain.Security.RecoveryCodes.Hash(trimmed);
        var recovery = await db.UserRecoveryCodes.FirstOrDefaultAsync(r => r.UserId == user.Id && r.UsedAt == null && r.CodeHash == hash, ct);
        if (recovery is null) return false;
        recovery.UsedAt = now;
        return true;
    }

    // ---------------------------------------------------------------------------------------
    // MFA enrolment (slice 13, SEC-02)
    // ---------------------------------------------------------------------------------------

    public sealed record Enrolment(string SecretBase32, string ProvisioningUri);

    /// <summary>A fresh pending secret; the active one, if any, is untouched until <see cref="VerifyMfaAsync"/> succeeds.</summary>
    public async Task<Enrolment?> EnrollMfaAsync(Guid userId, CancellationToken ct = default)
    {
        if (!secrets.Available) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var secret = FinanceAi.Domain.Security.Totp.NewSecret();
        user.MfaPendingSecretEnc = secrets.Seal(secret);
        await db.SaveChangesAsync(ct);
        await scope.CompleteAsync(ct);
        return new Enrolment(FinanceAi.Domain.Security.Base32.Encode(secret), FinanceAi.Domain.Security.Totp.ProvisioningUri(secret, "finance-ai", user.Email));
    }

    public sealed record MfaActivation(bool Activated, IReadOnlyList<string> RecoveryCodes);

    /// <summary>Activates the pending secret when the code matches it; issues the recovery codes exactly once.</summary>
    public async Task<MfaActivation> VerifyMfaAsync(Guid userId, Guid tenantId, string code, IPAddress? actorIp, string? requestId, CancellationToken ct = default)
    {
        if (!secrets.Available) return new MfaActivation(false, []);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, tenantId, userId, ct);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var now = time.GetUtcNow();
        if (user.MfaPendingSecretEnc is null || !FinanceAi.Domain.Security.Totp.Verify(secrets.Open(user.MfaPendingSecretEnc), code?.Trim(), now.ToUnixTimeSeconds()))
        {
            return new MfaActivation(false, []);
        }

        user.MfaSecretEnc = user.MfaPendingSecretEnc;
        user.MfaPendingSecretEnc = null;
        user.MfaEnabledAt = now;
        foreach (var old in await db.UserRecoveryCodes.Where(r => r.UserId == userId && r.UsedAt == null).ToListAsync(ct))
        {
            old.UsedAt = now;   // a re-enrolment invalidates the previous set
        }

        var codes = FinanceAi.Domain.Security.RecoveryCodes.New();
        foreach (var c in codes)
        {
            db.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = userId, CodeHash = FinanceAi.Domain.Security.RecoveryCodes.Hash(c), CreatedAt = now });
        }

        await db.SaveChangesAsync(ct);
        await new AuditWriter(db).WriteAsync(Event("auth.mfa_enabled", "user", userId, tenantId, userId, actorIp, requestId, now), ct);
        await scope.CompleteAsync(ct);
        return new MfaActivation(true, codes);
    }

    /// <summary>SEC-09: the same person, now — the password and, when enrolled, the second factor.</summary>
    public async Task<bool> ReauthenticateAsync(Guid userId, string password, string? secondFactor, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var now = time.GetUtcNow();
        var ok = user.PasswordHash is not null && passwordHasher.Verify(password, user.PasswordHash) && !user.IsLockedAt(now);
        if (ok && user.MfaEnrolled)
        {
            ok = !string.IsNullOrWhiteSpace(secondFactor) && await this.VerifySecondFactorAsync(db, user, secondFactor, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await scope.CompleteAsync(ct);
        return ok;
    }

    // ---------------------------------------------------------------------------------------
    // Password reset (slice 13, SEC-07)
    // ---------------------------------------------------------------------------------------

    public sealed record ResetRequest(Guid UserId, string Email, string Locale, string Token);

    /// <summary>Null when the address is not a user — the endpoint answers 202 either way and only the mail differs.</summary>
    public async Task<ResetRequest?> ForgotPasswordAsync(string email, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, null, ct);
        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email && u.Status == UserStatus.Active, ct);
        if (user is null)
        {
            await scope.CompleteAsync(ct);
            return null;
        }

        var now = time.GetUtcNow();
        foreach (var old in await db.PasswordResetTokens.Where(t => t.UserId == user.Id && t.UsedAt == null).ToListAsync(ct))
        {
            old.UsedAt = now;   // one live link per user
        }

        var token = InvitationTokens.NewToken();
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = user.Id, TokenHash = InvitationTokens.Hash(token), ExpiresAt = now.Add(PasswordResetToken.Validity), CreatedAt = now });
        await db.SaveChangesAsync(ct);
        await scope.CompleteAsync(ct);
        return new ResetRequest(user.Id, user.Email, user.PreferredLocale, token);
    }

    /// <summary>Slice 24: marks the address verified. Every unusable token is one outcome: false — and the same time budget.</summary>
    public async Task<bool> VerifyEmailAsync(string token, CancellationToken ct = default)
    {
        if (!InvitationTokens.LooksLikeToken(token)) return false;
        var hash = InvitationTokens.Hash(token);
        var now = time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, null, ct);
        var verification = await db.EmailVerificationTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (verification is null || !verification.IsUsable(now))
        {
            await scope.CompleteAsync(ct);
            return false;
        }

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == verification.UserId, ct);
        user.EmailVerifiedAt ??= now;
        verification.UsedAt = now;
        await db.SaveChangesAsync(ct);
        await scope.CompleteAsync(ct);
        return true;
    }

    /// <summary>Sets the new hash and ends every session of the user. Every unusable token is one outcome: false.</summary>
    public async Task<bool> ResetPasswordAsync(string token, string newPassword, IPAddress? actorIp, string? requestId, CancellationToken ct = default)
    {
        if (!InvitationTokens.LooksLikeToken(token)) return false;
        var hash = InvitationTokens.Hash(token);
        var now = time.GetUtcNow();
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, null, ct);
        var reset = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (reset is null || !reset.IsUsable(now))
        {
            await scope.CompleteAsync(ct);
            return false;
        }

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == reset.UserId, ct);
        user.PasswordHash = passwordHasher.Hash(newPassword);
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.EmailVerifiedAt ??= now;
        reset.UsedAt = now;
        Guid? anyTenant = null;
        foreach (var rt in await db.RefreshTokens.IgnoreQueryFilters().Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct))
        {
            rt.RevokedAt = now;
            rt.RevokedReason = "password_reset";
            anyTenant ??= rt.TenantId;
        }

        await db.SaveChangesAsync(ct);
        anyTenant ??= await db.TenantMemberships.IgnoreQueryFilters().Where(m => m.UserId == user.Id).Select(m => (Guid?)m.TenantId).FirstOrDefaultAsync(ct);
        if (anyTenant is { } tid)
        {
            await DatabaseScope.SetTenantAsync(db, tid, user.Id, ct);
            await new AuditWriter(db).WriteAsync(Event("auth.password_reset", "user", user.Id, tid, user.Id, actorIp, requestId, now), ct);
        }

        await scope.CompleteAsync(ct);
        return true;
    }

    // ---------------------------------------------------------------------------------------
    // Session state for the middleware (slice 13)
    // ---------------------------------------------------------------------------------------

    public sealed record SessionState(TenantRole Role, bool MfaEnrolled, DateTimeOffset? MfaGraceUntil);

    /// <summary>Everything the middleware decides on, in one round trip: the active role, whether a second factor is enrolled, and the grace deadline.</summary>
    public async Task<SessionState?> ResolveSessionAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);
        var membership = await db.TenantMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId && m.Status == MembershipStatus.Active, ct);
        var user = membership is null ? null : await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId && u.Status == UserStatus.Active).Select(u => new { u.MfaEnabledAt, u.MfaSecretEnc }).FirstOrDefaultAsync(ct);
        var tenantIsActive = user is not null && await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct);
        await scope.CompleteAsync(ct);
        return tenantIsActive ? new SessionState(membership!.Role, user!.MfaEnabledAt is not null && user.MfaSecretEnc is not null, membership.MfaGraceUntil) : null;
    }

    private async Task RecordFailureAsync(
        TenantDbContext db,
        User user,
        TenantMembership? membership,
        IPAddress? actorIp,
        string? requestId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var justLocked = false;

        if (!user.IsLockedAt(now))
        {
            user.FailedLoginCount++;

            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockedUntil = now.Add(LockoutDuration);
                justLocked = true;
            }
        }

        await db.SaveChangesAsync(ct);

        // A failed login is only auditable against an organization the user actually belongs to.
        // An attempt on an unknown email has no tenant and is left to the structured log, which is
        // also what stops the audit log from becoming an account-enumeration oracle.
        if (membership is null)
        {
            return;
        }

        await DatabaseScope.SetTenantAsync(db, membership.TenantId, user.Id, ct);
        var audit = new AuditWriter(db);

        await audit.WriteAsync(
            Event(AuditEventTypes.LoginFailed, "user", user.Id, membership.TenantId, user.Id, actorIp, requestId, now), ct);

        if (justLocked)
        {
            await audit.WriteAsync(
                Event(AuditEventTypes.AccountLocked, "user", user.Id, membership.TenantId, user.Id, actorIp, requestId, now,
                    reasonCode: "too_many_failed_logins"), ct);
        }

        await DatabaseScope.SetTenantAsync(db, null, null, ct);
    }

    // ---------------------------------------------------------------------------------------
    // Memberships and tenant switching
    // ---------------------------------------------------------------------------------------

    public sealed record MembershipSummary(Guid TenantId, string TenantName, TenantRole Role, string BaseCurrency, string Timezone);

    /// <summary>
    /// Slice 32: the active tenants, for the scheduler that runs each tenant's daily job in its own scope. Ids and
    /// timezones only — the scheduler enters every tenant separately and never carries one tenant's data into another.
    /// </summary>
    public async Task<IReadOnlyList<(Guid Id, string Timezone)>> ListActiveTenantsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, null, ct);
        var tenants = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => t.Status == TenantStatus.Active)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Timezone })
            .ToListAsync(ct);
        await scope.CompleteAsync(ct);
        return tenants.Select(t => (t.Id, t.Timezone)).ToList();
    }

    public async Task<IReadOnlyList<MembershipSummary>> ListMembershipsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);

        // Two straightforward queries rather than one join: the cardinality is "how many
        // organizations does one person belong to", and a join whose projection mixes both entities
        // is the kind of expression EF quietly refuses to translate.
        var memberships = await db.TenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .Select(m => new { m.TenantId, m.Role })
            .ToListAsync(ct);

        var tenantIds = memberships.Select(m => m.TenantId).ToList();

        var tenants = await db.Tenants
            .IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id) && t.Status == TenantStatus.Active)
            .Select(t => new { t.Id, t.Name, t.BaseCurrency, t.Timezone })
            .ToListAsync(ct);

        var result = memberships
            .Join(tenants, m => m.TenantId, t => t.Id,
                (m, t) => new MembershipSummary(t.Id, t.Name, m.Role, t.BaseCurrency, t.Timezone))
            .OrderBy(m => m.TenantName, StringComparer.Ordinal)
            .ToList();

        await scope.CompleteAsync(ct);
        return (IReadOnlyList<MembershipSummary>)result;
    }

    /// <summary>
    /// Resolves a membership for a tenant the caller named. This is the one place a tenant id is
    /// accepted from a request body (API-01), and it is only ever matched against the caller's own
    /// membership list — an id from another organization simply finds nothing (AC-18, AC-28).
    /// </summary>
    public async Task<AuthenticatedSession?> SwitchTenantAsync(
        Guid userId, Guid targetTenantId, IPAddress? actorIp, string? requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);

        var membership = await db.TenantMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                m => m.UserId == userId && m.TenantId == targetTenantId && m.Status == MembershipStatus.Active,
                ct);

        var target = membership is null
            ? null
            : await db.Tenants
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == membership.TenantId && t.Status == TenantStatus.Active, ct);

        if (membership is null || target is null)
        {
            await scope.CompleteAsync(ct);
            return null;
        }

        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId, ct);
        var now = time.GetUtcNow();

        await DatabaseScope.SetTenantAsync(db, target.Id, userId, ct);
        await new AuditWriter(db).WriteAsync(
            Event(AuditEventTypes.TenantSwitched, "user", userId, target.Id, userId, actorIp, requestId, now), ct);

        await scope.CompleteAsync(ct);

        return new AuthenticatedSession(
            user.Id, user.Email, user.FullName, user.PreferredLocale,
            target.Id, target.Name, target.BaseCurrency, target.Timezone,
            target.DefaultLocale, membership.Role, user.MfaEnrolled);
    }

    /// <summary>
    /// Re-reads the membership behind a presented access token. Called on every authenticated
    /// request, which is what makes a role change or a deactivation take effect immediately rather
    /// than merely within 60 seconds (SEC-08, AC-17).
    /// </summary>
    public async Task<TenantRole?> ResolveActiveRoleAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);

        // All three must still hold for the session to be valid: an active membership, an active
        // user, and an active organization. A token proves what was true when it was issued.
        var membership = await db.TenantMemberships
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                m => m.UserId == userId && m.TenantId == tenantId && m.Status == MembershipStatus.Active,
                ct);

        var userIsActive = membership is not null && await db.Users
            .IgnoreQueryFilters()
            .AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct);

        var tenantIsActive = userIsActive && await db.Tenants
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct);

        await scope.CompleteAsync(ct);
        return tenantIsActive ? membership!.Role : null;
    }

    // ---------------------------------------------------------------------------------------
    // Refresh tokens
    // ---------------------------------------------------------------------------------------

    public sealed record IssuedRefreshToken(string RawToken, DateTimeOffset ExpiresAt);

    public async Task<IssuedRefreshToken> IssueRefreshTokenAsync(
        Guid tenantId, Guid userId, Guid? familyId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, tenantId, userId, ct);

        var issued = await IssueAsync(db, tenantId, userId, familyId, ct);

        await scope.CompleteAsync(ct);
        return issued;
    }

    public enum RefreshOutcome
    {
        Rotated,

        /// <summary>Unknown, expired or revoked token.</summary>
        Rejected,

        /// <summary>A token that had already been rotated was presented again: evidence of theft.
        /// The whole family is revoked (SEC-04, AC-13).</summary>
        ReuseDetected,
    }

    public sealed record RefreshResult(
        RefreshOutcome Outcome,
        AuthenticatedSession? Session,
        IssuedRefreshToken? Replacement);

    public async Task<RefreshResult> RedeemRefreshTokenAsync(
        string rawToken, IPAddress? actorIp, string? requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, null, ct);

        var hash = RefreshTokens.HashOf(rawToken);
        var now = time.GetUtcNow();

        var token = await db.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
        {
            await scope.CompleteAsync(ct);
            return new RefreshResult(RefreshOutcome.Rejected, null, null);
        }

        // Reuse means a token that was already *spent* is presented again: two parties hold it, and
        // we cannot tell which is the thief. A token that was merely revoked — by a logout, or by an
        // earlier reuse that already killed this family — is a client retrying with a dead cookie.
        // Alerting on that too would bury the real signal under the victim's own retries.
        if (token.RotatedAt is not null)
        {
            await RevokeFamilyAsync(db, token.FamilyId, "reuse_detected", now, ct);

            await DatabaseScope.SetTenantAsync(db, token.TenantId, token.UserId, ct);
            await new AuditWriter(db).WriteAsync(
                Event(AuditEventTypes.RefreshReuseDetected, "refresh_token", token.Id, token.TenantId, token.UserId, actorIp, requestId, now,
                    reasonCode: "refresh_token_reuse", note: "Token family revoked."), ct);

            await scope.CompleteAsync(ct);
            return new RefreshResult(RefreshOutcome.ReuseDetected, null, null);
        }

        if (token.RevokedAt is not null || token.ExpiresAt <= now)
        {
            await scope.CompleteAsync(ct);
            return new RefreshResult(RefreshOutcome.Rejected, null, null);
        }

        var membership = await db.TenantMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UserId == token.UserId
                        && m.TenantId == token.TenantId
                        && m.Status == MembershipStatus.Active)
            .FirstOrDefaultAsync(ct);

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == token.UserId, ct);
        var tenant = await db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == token.TenantId, ct);

        if (membership is null || user is null || tenant is null ||
            user.Status != UserStatus.Active || tenant.Status != TenantStatus.Active)
        {
            await RevokeFamilyAsync(db, token.FamilyId, "superseded", now, ct);
            await scope.CompleteAsync(ct);
            return new RefreshResult(RefreshOutcome.Rejected, null, null);
        }

        token.RotatedAt = now;
        token.RevokedAt = now;
        token.RevokedReason = "rotated";
        await db.SaveChangesAsync(ct);

        var replacement = await IssueAsync(db, token.TenantId, token.UserId, token.FamilyId, ct);

        await DatabaseScope.SetTenantAsync(db, tenant.Id, user.Id, ct);
        await new AuditWriter(db).WriteAsync(
            Event(AuditEventTypes.TokenRefreshed, "refresh_token", token.Id, tenant.Id, user.Id, actorIp, requestId, now), ct);

        await scope.CompleteAsync(ct);

        return new RefreshResult(
            RefreshOutcome.Rotated,
            new AuthenticatedSession(
                user.Id, user.Email, user.FullName, user.PreferredLocale,
                tenant.Id, tenant.Name, tenant.BaseCurrency, tenant.Timezone, tenant.DefaultLocale, membership.Role, user.MfaEnrolled),   // an enrolled user only ever logged in with the second factor
            replacement);
    }

    /// <summary>Logout: revokes the presented token's whole family (doc 05, AC-14).</summary>
    public async Task LogoutAsync(
        string? rawToken, Guid userId, IPAddress? actorIp, string? requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var scope = await DatabaseScope.EnterPlatformAsync(db, null, userId, ct);

        var now = time.GetUtcNow();

        var token = rawToken is null
            ? null
            : await db.RefreshTokens
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TokenHash == RefreshTokens.HashOf(rawToken), ct);

        // A logout is only honoured for the caller's own session; a stolen cookie cannot be used to
        // sign someone else out.
        if (token is not null && token.UserId == userId)
        {
            await RevokeFamilyAsync(db, token.FamilyId, "logout", now, ct);

            await DatabaseScope.SetTenantAsync(db, token.TenantId, userId, ct);
            await new AuditWriter(db).WriteAsync(
                Event(AuditEventTypes.LoggedOut, "user", userId, token.TenantId, userId, actorIp, requestId, now), ct);
        }

        await scope.CompleteAsync(ct);
    }

    private async Task<IssuedRefreshToken> IssueAsync(
        TenantDbContext db, Guid tenantId, Guid userId, Guid? familyId, CancellationToken ct)
    {
        var raw = RefreshTokens.Generate();
        var expiresAt = time.GetUtcNow().Add(RefreshTokens.Lifetime);

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            FamilyId = familyId ?? Guid.CreateVersion7(),
            TokenHash = RefreshTokens.HashOf(raw),
            IssuedAt = time.GetUtcNow(),
            ExpiresAt = expiresAt,
        });

        await db.SaveChangesAsync(ct);
        return new IssuedRefreshToken(raw, expiresAt);
    }

    private static async Task RevokeFamilyAsync(
        TenantDbContext db, Guid familyId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var family = await db.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in family)
        {
            token.RevokedAt = now;
            token.RevokedReason = reason;
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsEmailUniquenessViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } postgres &&
        postgres.ConstraintName?.Contains("email", StringComparison.OrdinalIgnoreCase) == true;

    private static AuditEvent Event(
        string eventType,
        string entityType,
        Guid entityId,
        Guid tenantId,
        Guid? actorUserId,
        IPAddress? actorIp,
        string? requestId,
        DateTimeOffset occurredAt,
        string? toState = null,
        string? reasonCode = null,
        string? note = null) =>
        new()
        {
            TenantId = tenantId,
            OccurredAt = occurredAt,
            ActorUserId = actorUserId,
            ActorKind = ActorKinds.User,
            ActorIp = actorIp,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            ToState = toState,
            ReasonCode = reasonCode,
            Note = note,
            RequestId = requestId,
        };

    /// <summary>
    /// A real Argon2id hash of a random value, computed once. Verifying against it costs the same
    /// as verifying a real password, so an unknown email and a wrong password take the same time.
    /// </summary>
    private static class DummyHash
    {
        public static string Value { get; } =
            new Argon2idPasswordHasher().Hash(Guid.NewGuid().ToString());
    }
}
