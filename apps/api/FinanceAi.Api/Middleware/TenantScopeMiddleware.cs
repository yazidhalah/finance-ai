using FinanceAi.Api.Authorization;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Security;

namespace FinanceAi.Api.Middleware;

/// <summary>
/// The request pipeline SEC-11 prescribes, in the order it prescribes:
/// <list type="number">
///   <item>authenticate;</item>
///   <item>resolve the tenant from the validated <c>tid</c> claim — and from nothing else (SEC-20);</item>
///   <item>re-read the membership, so a disabled user or a changed role takes effect at once (SEC-08);</item>
///   <item>open a transaction with <c>app.tenant_id</c> set (layer 2);</item>
///   <item>check the permission (SEC-12);</item>
///   <item>run the handler.</item>
/// </list>
/// A handler therefore never receives an unscoped context, and a permission check never runs
/// against data that was fetched outside the tenant scope.
/// </summary>
public sealed class TenantScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        TenantContext tenantContext,
        TenantDbContext db,
        CurrentUser currentUser,
        PlatformIdentityStore identity)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        var declaredAccess = endpoint?.Metadata.GetMetadata<DeclaredAccess>();
        var requiredPermission = endpoint?.Metadata.GetMetadata<RequiredPermission>();

        // Anonymous endpoints run outside any tenant scope. They reach the database only through
        // PlatformIdentityStore, which opens its own scope on its own connection.
        if (endpoint is null || declaredAccess?.Kind == DeclaredAccessKind.Anonymous)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await ApiProblems.UnauthenticatedProblem(context).ExecuteAsync(context);
            return;
        }

        // SEC-20: the only source of a tenant id is the validated token claim.
        var tenantId = context.User.TenantIdOrNull();
        var userId = context.User.UserIdOrNull();

        if (tenantId is null || userId is null)
        {
            await ApiProblems.UnauthenticatedProblem(context).ExecuteAsync(context);
            return;
        }

        // A token outlives a membership change by up to its 15-minute lifetime, so the membership
        // is re-read here rather than trusted from the token (SEC-08, AC-17).
        var role = await identity.ResolveActiveRoleAsync(userId.Value, tenantId.Value, context.RequestAborted);

        if (role is null)
        {
            await ApiProblems.UnauthenticatedProblem(context).ExecuteAsync(context);
            return;
        }

        tenantContext.Set(tenantId.Value, userId.Value);
        currentUser.Set(userId.Value, tenantId.Value, role.Value);

        await using var scope = await DatabaseScope.EnterTenantAsync(
            db, tenantId.Value, userId.Value, context.RequestAborted);

        if (requiredPermission is not null && !RolePermissions.Grants(role.Value, requiredPermission.Permission))
        {
            await ApiProblems.ForbiddenProblem(context, requiredPermission.Permission).ExecuteAsync(context);
            return;
        }

        await next(context);

        // Commit only a request that succeeded. Anything else rolls back on dispose, so a handler
        // that returns 4xx after a partial write leaves nothing behind.
        if (context.Response.StatusCode < StatusCodes.Status400BadRequest)
        {
            await scope.CompleteAsync(context.RequestAborted);
        }
    }
}
