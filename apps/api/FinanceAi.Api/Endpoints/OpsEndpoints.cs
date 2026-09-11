using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Ops;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Slice 15: the invariant job's records and the alert path. Reading integrity is <c>audit.read</c> (the same
/// people who read the audit log); running the job and acknowledging an alert change state and are
/// <c>tenant.settings.write</c>. Nothing here writes to a business table.
/// </summary>
public static class OpsEndpoints
{
    public static RouteGroupBuilder MapOpsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/organization/invariants", LatestRunAsync).Produces<LatestInvariantRunResponse>(200).RequiresPermission(Permissions.AuditRead).WithName("LatestInvariantRun");
        api.MapPost("/organization/invariants/run", RunAsync).Produces<InvariantRunResponse>(200).RequiresPermission(Permissions.TenantSettingsWrite).WithName("RunInvariants");
        api.MapGet("/organization/alerts", ListAlertsAsync).Produces<AlertListResponse>(200).RequiresPermission(Permissions.AuditRead).WithName("ListAlerts");
        api.MapPost("/organization/alerts/{id:guid}/acknowledge", AcknowledgeAsync).Produces<AlertDto>(200).RequiresPermission(Permissions.TenantSettingsWrite).WithName("AcknowledgeAlert");

        return api;
    }

    private static async Task<IResult> LatestRunAsync(InvariantService invariants, CancellationToken ct)
    {
        var run = await invariants.LatestAsync(ct);
        return TypedResults.Ok(new LatestInvariantRunResponse(run is null ? null : Shape(run)));
    }

    private static async Task<IResult> RunAsync(CurrentUser user, OpsMonitor ops, CancellationToken ct)
    {
        var run = await ops.RunAsync(InvariantRunTrigger.Manual, user.UserId, ct);
        return TypedResults.Ok(Shape(run));
    }

    private static async Task<IResult> ListAlertsAsync(HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var includeAcknowledged = context.Request.Query["all"].ToString() is "1" or "true";
        var query = db.Alerts.AsNoTracking();
        if (!includeAcknowledged) query = query.Where(a => a.AcknowledgedAt == null);
        var items = await query.OrderByDescending(a => a.RaisedAt).Take(200).ToListAsync(ct);
        var open = await db.Alerts.CountAsync(a => a.AcknowledgedAt == null, ct);
        return TypedResults.Ok(new AlertListResponse(items.Select(Shape).ToList(), open));
    }

    private static async Task<IResult> AcknowledgeAsync(Guid id, HttpContext context, CurrentUser user, AlertService alerts, IAuditWriter audit, TenantDbContext db, CancellationToken ct)
    {
        var alert = await alerts.AcknowledgeAsync(id, user.UserId, ct);
        if (alert is null) return ApiProblems.NotFoundProblem(context);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "alert.acknowledged",
            EntityType = "alert",
            EntityId = alert.Id,
            ToState = "acknowledged",
            ReasonCode = alert.Kind,
        }, ct);
        return TypedResults.Ok(Shape(alert));
    }

    private static InvariantRunResponse Shape(InvariantRun run)
    {
        var checks = JsonSerializer.Deserialize<List<InvariantCheckResult>>(run.ChecksJson, AlertService.JsonOptions) ?? [];
        return new InvariantRunResponse(run.Id, run.RanAt, run.Trigger, run.ActorUserId, run.Status, run.DurationMs,
            checks.Select(c => new InvariantCheckDto(c.Id, c.Description, c.Violations, c.Samples)).ToList());
    }

    private static AlertDto Shape(Alert a) =>
        new(a.Id, a.Kind, a.Severity, a.Summary, JsonDocument.Parse(a.DetailsJson).RootElement.Clone(), a.RaisedAt, a.EmailDelivery, a.WebhookDelivery, a.AcknowledgedAt, a.AcknowledgedBy);
}
