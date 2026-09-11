using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 1: the caller's profile, their organization, its members, and the audit log.
/// <para>
/// Every handler here runs inside the tenant scope opened by <c>TenantScopeMiddleware</c>, so every
/// query is filtered by the EF global filter (layer 1) and again by RLS (layer 2). None of them
/// mentions a tenant id: there is nowhere for one to come from except the token.
/// </para>
/// </summary>
public static class OrganizationEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/me", GetMeAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("GetMe");
        api.MapPatch("/me", UpdateMeAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("UpdateMe");

        return api;
    }

    public static RouteGroupBuilder MapOrganizationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var organization = api.MapGroup("/organization");

        organization.MapGet("/", GetOrganizationAsync)
            .RequiresPermission(Permissions.TenantRead).WithName("GetOrganization");
        organization.MapPatch("/", UpdateOrganizationAsync)
            .RequiresPermission(Permissions.TenantSettingsWrite).WithName("UpdateOrganization");
        organization.MapGet("/members", ListMembersAsync)
            .RequiresPermission(Permissions.UsersRead).WithName("ListMembers");
        organization.MapGet("/members/{id:guid}", GetMemberAsync)
            .RequiresPermission(Permissions.UsersRead).WithName("GetMember");
        // Slice 12
        organization.MapPost("/members/invite", InviteAsync).RequiresPermission(Permissions.UsersInvite).WithName("InviteMember");
        organization.MapGet("/invitations", ListInvitationsAsync).RequiresPermission(Permissions.UsersRead).WithName("ListInvitations");
        organization.MapPost("/invitations/{id:guid}/revoke", RevokeInvitationAsync).RequiresPermission(Permissions.UsersInvite).WithName("RevokeInvitation");
        organization.MapPatch("/members/{id:guid}", ChangeRoleAsync).RequiresPermission(Permissions.UsersRoleWrite).WithName("ChangeMemberRole");
        organization.MapPost("/members/{id:guid}/deactivate", DeactivateMemberAsync).RequiresPermission(Permissions.UsersDeactivate).WithName("DeactivateMember");
        // Slice 13 (SEC-09): ownership moves only with a fresh re-authentication.
        organization.MapPost("/transfer-ownership", TransferOwnershipAsync).RequiresPermission(Permissions.TenantTransferOwnership).RequiresReauth().WithName("TransferOwnership");

        return api;
    }

    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/audit", ListAuditAsync)
            .RequiresPermission(Permissions.AuditRead).WithName("ListAudit");

        return api;
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetMeAsync(
        CurrentUser currentUser, TenantDbContext db, TimeProvider time, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);
        var membership = await db.TenantMemberships.FirstAsync(m => m.UserId == currentUser.UserId && m.Status == MembershipStatus.Active, ct);
        var now = time.GetUtcNow();

        return TypedResults.Ok(new MeResponse(
            new UserDto(user.Id, user.FullName, user.PreferredLocale, user.Email),
            new TenantDto(tenant.Id, tenant.Name, tenant.BaseCurrency, tenant.Timezone, tenant.DefaultLocale),
            currentUser.Role.ToString(),
            currentUser.Permissions,
            user.MfaEnrolled,
            FinanceAi.Domain.Security.MfaPolicy.Required(currentUser.Role),
            membership.MfaGraceUntil?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            FinanceAi.Domain.Security.MfaPolicy.Enforced(currentUser.Role, user.MfaEnrolled, membership.MfaGraceUntil, now)));
    }

    private static async Task<Results<Ok<UserDto>, ProblemHttpResult>> UpdateMeAsync(
        UpdateMeRequest request,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        CancellationToken ct)
    {
        var validation = new Validation()
            .MaxLength("fullName", request.FullName, 200)
            .Locale("preferredLocale", request.PreferredLocale);

        if (request.FullName is not null && string.IsNullOrWhiteSpace(request.FullName))
        {
            validation.Require("fullName", request.FullName);
        }

        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, ct);

        user.FullName = request.FullName?.Trim() ?? user.FullName;
        user.PreferredLocale = request.PreferredLocale ?? user.PreferredLocale;

        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new UserDto(user.Id, user.FullName, user.PreferredLocale, user.Email));
    }

    private static async Task<Results<Ok<OrganizationResponse>, ProblemHttpResult>> GetOrganizationAsync(
        HttpContext context, CurrentUser currentUser, TenantDbContext db, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == currentUser.TenantId, ct);

        return tenant is null
            ? ApiProblems.NotFoundProblem(context)   // typed results (slice 22) caught a bare 404 here: every failure is a problem (doc 05 §0.2)
            : TypedResults.Ok(ToResponse(tenant));
    }

    private static async Task<Results<Ok<OrganizationResponse>, ProblemHttpResult>> UpdateOrganizationAsync(
        UpdateOrganizationRequest request,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        CancellationToken ct)
    {
        var validation = new Validation()
            .MaxLength("name", request.Name, 200)
            .MaxLength("legalName", request.LegalName, 200)
            .MaxLength("taxRegistrationNo", request.TaxRegistrationNo, 50)
            .Timezone("timezone", request.Timezone)
            .Locale("defaultLocale", request.DefaultLocale);

        if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
        {
            validation.Require("name", request.Name);
        }

        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == currentUser.TenantId, ct);
        if (tenant is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        // API-09: a mutation names the version it read, so two people editing the organization
        // profile cannot silently overwrite one another.
        if (IfMatch(context) is { } expected && expected != tenant.RowVersion)
        {
            return ApiProblems.Create(
                context, StatusCodes.Status409Conflict, ApiProblems.ConcurrencyConflict,
                "The organization was modified since it was read.", "errors.concurrency_conflict");
        }

        var changes = new Dictionary<string, object>(StringComparer.Ordinal);
        Track(changes, "name", tenant.Name, request.Name?.Trim());
        Track(changes, "legalName", tenant.LegalName, request.LegalName);
        Track(changes, "taxRegistrationNo", tenant.TaxRegistrationNo, request.TaxRegistrationNo);
        Track(changes, "timezone", tenant.Timezone, request.Timezone);
        Track(changes, "defaultLocale", tenant.DefaultLocale, request.DefaultLocale);

        tenant.Name = request.Name?.Trim() ?? tenant.Name;
        tenant.LegalName = request.LegalName ?? tenant.LegalName;
        tenant.TaxRegistrationNo = request.TaxRegistrationNo ?? tenant.TaxRegistrationNo;
        tenant.Timezone = request.Timezone ?? tenant.Timezone;
        tenant.DefaultLocale = request.DefaultLocale ?? tenant.DefaultLocale;

        if (changes.Count > 0)
        {
            tenant.RowVersion++;
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(new AuditEvent
            {
                TenantId = currentUser.TenantId,
                ActorUserId = currentUser.UserId,
                ActorKind = ActorKinds.User,
                ActorIp = context.ClientIp(),
                EventType = AuditEventTypes.OrganizationUpdated,
                EntityType = "tenant",
                EntityId = tenant.Id,
                Changes = System.Text.Json.JsonSerializer.Serialize(changes),
                RequestId = context.RequestId(),
            }, ct);
        }

        return TypedResults.Ok(ToResponse(tenant));
    }

    private static async Task<Results<Ok<MemberListResponse>, ProblemHttpResult>> ListMembersAsync(TenantDbContext db, CancellationToken ct)
    {
        // No tenant predicate is written here on purpose: the global query filter supplies it, RLS
        // repeats it, and a member of another organization is not reachable through either.
        //
        // The role and status enums are formatted after the query rather than inside it: PostgreSQL
        // has no equivalent of ToString(), and pushing the projection into SQL would silently become
        // a client evaluation of the whole table.
        var rows = await db.TenantMemberships
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new
            {
                m.Id,
                UserId = u.Id,
                u.Email,
                u.FullName,
                m.Role,
                m.Status,
                m.CreatedAt,
            })
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var members = rows
            .Select(r => new MemberDto(
                r.Id, r.UserId, r.Email, r.FullName, r.Role.ToString(), r.Status.ToString(), r.CreatedAt))
            .ToList();

        return TypedResults.Ok(new MemberListResponse(members, members.Count));
    }

    private static async Task<Results<Ok<MemberDto>, ProblemHttpResult>> GetMemberAsync(
        Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var row = await db.TenantMemberships
            .Where(m => m.Id == id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new
            {
                m.Id,
                UserId = u.Id,
                u.Email,
                u.FullName,
                m.Role,
                m.Status,
                m.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);

        // A membership of another organization is missing, not forbidden (API-03, SEC-13, AC-26).
        return row is null
            ? ApiProblems.NotFoundProblem(context)
            : TypedResults.Ok(new MemberDto(
                row.Id, row.UserId, row.Email, row.FullName,
                row.Role.ToString(), row.Status.ToString(), row.CreatedAt));
    }

    private static async Task<Results<Ok<AuditListResponse>, ProblemHttpResult>> ListAuditAsync(
        TenantDbContext db,
        CancellationToken ct,
        string? entityType = null,
        Guid? entityId = null,
        string? eventType = null,
        Guid? actorUserId = null,
        int limit = 50,
        long? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);

        var query = db.AuditEvents.AsQueryable();

        if (entityType is not null)
        {
            query = query.Where(e => e.EntityType == entityType);
        }

        // Slice 15 (doc 06 §6.11): the viewer also filters by event type and actor.
        if (eventType is not null)
        {
            query = query.Where(e => e.EventType == eventType);
        }

        if (actorUserId is not null)
        {
            query = query.Where(e => e.ActorUserId == actorUserId);
        }

        if (entityId is not null)
        {
            query = query.Where(e => e.EntityId == entityId);
        }

        if (cursor is not null)
        {
            query = query.Where(e => e.Id < cursor);
        }

        var page = await query
            .OrderByDescending(e => e.Id)
            .Take(pageSize + 1)
            .Select(e => new { e.Id, e.OccurredAt, e.ActorUserId, e.ActorKind, e.EventType, e.EntityType, e.EntityId, e.FromState, e.ToState, e.ReasonCode, e.RequestId, e.Hash, e.Changes, e.Note, e.AiSuggestionId })
            .ToListAsync(ct);

        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page[..pageSize] : page)
            .Select(e => new AuditEventDto(
                e.Id, e.OccurredAt, e.ActorUserId, e.ActorKind, e.EventType, e.EntityType, e.EntityId, e.FromState, e.ToState, e.ReasonCode, e.RequestId, e.Hash,
                e.Changes is null ? null : JsonDocument.Parse(e.Changes).RootElement.Clone(), e.Note, e.AiSuggestionId))   // slice 19: before/after values on the viewer
            .ToList();

        return TypedResults.Ok(new AuditListResponse(
            items,
            hasMore ? items[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
    }

    private static OrganizationResponse ToResponse(Tenant tenant) =>
        new(tenant.Id, tenant.Name, tenant.LegalName, tenant.TaxRegistrationNo,
            tenant.BaseCurrency, tenant.Timezone, tenant.DefaultLocale, tenant.Status.ToString(),
            tenant.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static long? IfMatch(HttpContext context) =>
        long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"', 'W', '/'), out var version)
            ? version
            : null;

    private static void Track(
        Dictionary<string, object> changes, string field, string? oldValue, string? newValue)
    {
        if (newValue is not null && newValue != oldValue)
        {
            changes[field] = new { old = oldValue, @new = newValue };
        }
    }

    // ---------------------------------------------------------------------------------------
    // Slice 12 — invitations, role change, deactivation
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Accepted<InviteAcceptedResponse>, ProblemHttpResult>> InviteAsync(InviteMemberRequest request, HttpContext context, CurrentUser user, FinanceAi.Infrastructure.Members.MembersService members, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var validation = new Validation().Require("email", request.Email).Email("email", request.Email).MaxLength("email", request.Email, 254).Require("role", request.Role).Locale("locale", request.Locale);
        if (request.Role is not null && (!Enum.TryParse<TenantRole>(request.Role, out var parsed) || !RoleAssignment.CanAssign(parsed))) validation.Require("role", null, "invalid");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        var outcome = await members.InviteAsync(request.Email!, Enum.Parse<TenantRole>(request.Role!), request.Locale ?? Locales.Default, user.UserId, ct);
        // Recorded, never rendered (SEC-07): the body is the same for a new address and an existing member.
        loggerFactory.CreateLogger(typeof(OrganizationEndpoints)).LogInformation("Invitation processed with outcome {Outcome}.", outcome.Outcome);
        return TypedResults.Accepted((string?)null, new InviteAcceptedResponse(true));
    }

    private static async Task<Results<Ok<InvitationListResponse>, ProblemHttpResult>> ListInvitationsAsync(TenantDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.MemberInvitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(ct);
        return TypedResults.Ok(new InvitationListResponse(rows.Select(i => Invitation(i, now)).ToList()));
    }

    private static async Task<Results<Ok<InvitationDto>, ProblemHttpResult>> RevokeInvitationAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Members.MembersService members, TimeProvider time, CancellationToken ct)
    {
        if (!await db.MemberInvitations.AnyAsync(i => i.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try
        {
            var i = await members.RevokeAsync(id, user.UserId, ct);
            return TypedResults.Ok(Invitation(i, time.GetUtcNow()));
        }
        catch (FinanceAi.Infrastructure.Cases.CaseException ex) { return MemberRule(context, ex); }
    }

    private static async Task<Results<Ok<MemberDto>, ProblemHttpResult>> ChangeRoleAsync(Guid id, ChangeRoleRequest request, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Members.MembersService members, CancellationToken ct)
    {
        if (!await db.TenantMemberships.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (request.Role is null || !Enum.TryParse<TenantRole>(request.Role, out var role) || !Enum.IsDefined(role)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("role", "invalid", "errors.validation.role.invalid")]);
        try
        {
            var m = await members.ChangeRoleAsync(id, role, user.UserId, ct);
            return TypedResults.Ok(await MemberAsync(m, db, ct));
        }
        catch (FinanceAi.Infrastructure.Cases.CaseException ex) { return MemberRule(context, ex); }
    }

    private static async Task<Results<Ok<MemberDto>, ProblemHttpResult>> DeactivateMemberAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Members.MembersService members, CancellationToken ct)
    {
        if (!await db.TenantMemberships.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try
        {
            var m = await members.DeactivateAsync(id, user.UserId, ct);
            return TypedResults.Ok(await MemberAsync(m, db, ct));
        }
        catch (FinanceAi.Infrastructure.Cases.CaseException ex) { return MemberRule(context, ex); }
    }

    private static async Task<MemberDto> MemberAsync(TenantMembership m, TenantDbContext db, CancellationToken ct)
    {
        var u = await db.Users.Where(x => x.Id == m.UserId).Select(x => new { x.Email, x.FullName }).FirstAsync(ct);
        return new MemberDto(m.Id, m.UserId, u.Email, u.FullName, m.Role.ToString(), m.Status.ToString(), m.CreatedAt);
    }

    private static InvitationDto Invitation(MemberInvitation i, DateTimeOffset now) => new(
        i.Id, i.Email, i.Role.ToString(), i.Locale, i.Status(now), i.InvitedBy, i.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        i.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), i.AcceptedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), i.RevokedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static ProblemHttpResult MemberRule(HttpContext context, FinanceAi.Infrastructure.Cases.CaseException ex) => ex.Code switch
    {
        "member_not_found" or "invitation_not_found" => ApiProblems.NotFoundProblem(context),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };

    private static async Task<Results<Ok<TransferOwnershipResponse>, ProblemHttpResult>> TransferOwnershipAsync(TransferOwnershipRequest request, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Members.MembersService members, CancellationToken ct)
    {
        if (request.TargetMembershipId is not { } target) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("targetMembershipId", "required", "errors.validation.targetMembershipId.required")]);
        if (!await db.TenantMemberships.AnyAsync(m => m.Id == target, ct)) return ApiProblems.NotFoundProblem(context);
        try
        {
            var (newOwner, previous) = await members.TransferOwnershipAsync(target, user.UserId, ct);
            return TypedResults.Ok(new TransferOwnershipResponse(await MemberAsync(newOwner, db, ct), await MemberAsync(previous, db, ct)));
        }
        catch (FinanceAi.Infrastructure.Cases.CaseException ex) { return MemberRule(context, ex); }
    }
}
