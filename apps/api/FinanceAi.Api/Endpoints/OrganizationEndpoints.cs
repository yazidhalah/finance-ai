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

        api.MapGet("/me", GetMeAsync).RequiresAuthenticatedUser().WithName("GetMe");
        api.MapPatch("/me", UpdateMeAsync).RequiresAuthenticatedUser().WithName("UpdateMe");

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

        return api;
    }

    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/audit", ListAuditAsync)
            .RequiresPermission(Permissions.AuditRead).WithName("ListAudit");

        return api;
    }

    private static async Task<IResult> GetMeAsync(
        CurrentUser currentUser, TenantDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId, ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);

        return TypedResults.Ok(new MeResponse(
            new UserDto(user.Id, user.FullName, user.PreferredLocale, user.Email),
            new TenantDto(tenant.Id, tenant.Name, tenant.BaseCurrency, tenant.Timezone, tenant.DefaultLocale),
            currentUser.Role.ToString(),
            currentUser.Permissions));
    }

    private static async Task<IResult> UpdateMeAsync(
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

    private static async Task<IResult> GetOrganizationAsync(
        CurrentUser currentUser, TenantDbContext db, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == currentUser.TenantId, ct);

        return tenant is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToResponse(tenant));
    }

    private static async Task<IResult> UpdateOrganizationAsync(
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

    private static async Task<IResult> ListMembersAsync(TenantDbContext db, CancellationToken ct)
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

    private static async Task<IResult> GetMemberAsync(
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

    private static async Task<IResult> ListAuditAsync(
        TenantDbContext db,
        CancellationToken ct,
        string? entityType = null,
        Guid? entityId = null,
        int limit = 50,
        long? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);

        var query = db.AuditEvents.AsQueryable();

        if (entityType is not null)
        {
            query = query.Where(e => e.EntityType == entityType);
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
            .Select(e => new AuditEventDto(
                e.Id, e.OccurredAt, e.ActorUserId, e.ActorKind, e.EventType, e.EntityType,
                e.EntityId, e.FromState, e.ToState, e.ReasonCode, e.RequestId, e.Hash))
            .ToListAsync(ct);

        var hasMore = page.Count > pageSize;
        var items = hasMore ? page[..pageSize] : page;

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
}
