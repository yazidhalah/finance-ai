using System.Globalization;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Slice 23 — the tenant holiday calendar (FIN-73): a table of announced dates, never computed. Read by the
/// business-day arithmetic in the promise and dispute services. All three verbs are <c>tenant.settings.write</c> (doc 05).
/// </summary>
public static class HolidayEndpoints
{
    public static RouteGroupBuilder MapHolidayEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var holidays = api.MapGroup("/organization/holidays");
        holidays.MapGet("/", ListAsync).RequiresPermission(Permissions.TenantSettingsWrite).WithName("ListHolidays");
        holidays.MapPost("/", CreateAsync).RequiresPermission(Permissions.TenantSettingsWrite).WithName("CreateHoliday");
        holidays.MapDelete("/{id:guid}", DeleteAsync).RequiresPermission(Permissions.TenantSettingsWrite).WithName("DeleteHoliday");
        return api;
    }

    private static async Task<Results<Ok<HolidayListResponse>, ProblemHttpResult>> ListAsync(TenantDbContext db, CancellationToken ct)
    {
        var items = await db.Holidays.AsNoTracking().OrderBy(h => h.Date).ToListAsync(ct);
        return TypedResults.Ok(new HolidayListResponse(items.Select(Shape).ToList()));
    }

    private static async Task<Results<Created<HolidayDto>, ProblemHttpResult>> CreateAsync(HolidayRequest request, HttpContext context, CurrentUser user, TenantDbContext db, IAuditWriter audit, CancellationToken ct)
    {
        var validation = new Validation().Require("name", request.Name).MaxLength("name", request.Name, 200);
        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) validation.Require("date", null, "invalid_date");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        if (await db.Holidays.AnyAsync(h => h.Date == date, ct))
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "holiday_exists", "A holiday already exists on that date.", "errors.holiday_exists");
        }

        var holiday = new TenantHoliday { TenantId = db.CurrentTenantId, Date = date, Name = request.Name!.Trim() };
        db.Holidays.Add(holiday);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "tenant.holiday_added",
            EntityType = "tenant_holiday",
            EntityId = holiday.Id,
            Changes = JsonSerializer.Serialize(new { date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), name = holiday.Name }),
        }, ct);
        return TypedResults.Created($"/api/v1/organization/holidays/{holiday.Id}", Shape(holiday));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, IAuditWriter audit, CancellationToken ct)
    {
        var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (holiday is null) return ApiProblems.NotFoundProblem(context);
        db.Holidays.Remove(holiday);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "tenant.holiday_removed",
            EntityType = "tenant_holiday",
            EntityId = holiday.Id,
            Changes = JsonSerializer.Serialize(new { date = holiday.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), name = holiday.Name }),
        }, ct);
        return TypedResults.NoContent();
    }

    private static HolidayDto Shape(TenantHoliday h) => new(h.Id, h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), h.Name);
}
