using System.Globalization;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Briefings;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 10. Reads are <c>cases.read</c>; regenerating today's narrative is <c>ai.settings.write</c>;
/// the delivery settings are <c>tenant.settings.write</c>. No route accepts a number from the client.
/// </summary>
public static class BriefingEndpoints
{
    public static RouteGroupBuilder MapBriefingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var briefings = api.MapGroup("/briefings");
        briefings.MapGet("/today", TodayAsync).Produces<BriefingResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("BriefingToday");
        briefings.MapGet("/{date}", ByDateAsync).Produces<BriefingResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("BriefingByDate");
        briefings.MapPost("/regenerate", RegenerateAsync).Produces<BriefingResponse>(200).RequiresPermission(Permissions.AiSettingsWrite).WithName("BriefingRegenerate");
        api.MapGet("/organization/briefing-settings", SettingsAsync).Produces<BriefingSettingsResponse>(200).RequiresPermission(Permissions.TenantRead).WithName("BriefingSettings");
        api.MapPatch("/organization/briefing-settings", UpdateSettingsAsync).Produces<BriefingSettingsResponse>(200).RequiresPermission(Permissions.TenantSettingsWrite).WithName("UpdateBriefingSettings");

        return api;
    }

    /// <summary>Precomputed by the sweep (PRD-22); generated on first read only when nothing exists yet, so the screen never waits on a schedule.</summary>
    private static async Task<IResult> TodayAsync(HttpContext context, TenantDbContext db, BriefingService briefings, CaseService cases, CancellationToken ct)
    {
        var language = Language(context);
        if (language is null) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("language", "invalid", "errors.validation.language.invalid")]);
        var generated = await briefings.GenerateAsync(regenerate: false, actorUserId: null, ct);
        var row = generated.Briefings.First(b => b.Language == language);
        return TypedResults.Ok(await ShapeAsync(row, db, cases, ct));
    }

    private static async Task<IResult> ByDateAsync(string date, HttpContext context, TenantDbContext db, BriefingService briefings, CaseService cases, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return ApiProblems.NotFoundProblem(context);
        var language = Language(context);
        if (language is null) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("language", "invalid", "errors.validation.language.invalid")]);
        var ctx = await cases.ContextAsync(ct);
        if (day == ctx.Today) return await TodayAsync(context, db, briefings, cases, ct);
        var row = await db.DailyBriefings.AsNoTracking().FirstOrDefaultAsync(b => b.BriefingDate == day && b.Language == language, ct);
        return row is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await ShapeAsync(row, db, cases, ct));
    }

    private static async Task<IResult> RegenerateAsync(HttpContext context, CurrentUser user, TenantDbContext db, BriefingService briefings, CaseService cases, CancellationToken ct)
    {
        var q = context.Request.Query["date"].ToString();
        var ctx = await cases.ContextAsync(ct);
        if (q.Length > 0 && (!DateOnly.TryParseExact(q, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requested) || requested != ctx.Today))
        {
            // Doc 10 acceptance 5: a past briefing is what it was.
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "briefing_immutable", "Only today's briefing can be regenerated.", "errors.rule.briefing_immutable");
        }

        var generated = await briefings.GenerateAsync(regenerate: true, actorUserId: user.UserId, ct);
        var language = Language(context) ?? ctx.Settings.BriefingLanguage;
        return TypedResults.Ok(await ShapeAsync(generated.Briefings.First(b => b.Language == language), db, cases, ct));
    }

    private static async Task<IResult> SettingsAsync(TenantDbContext db, CancellationToken ct)
    {
        var s = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        return TypedResults.Ok(await ShapeSettingsAsync(s, db, ct));
    }

    private static async Task<IResult> UpdateSettingsAsync(BriefingSettingsRequest request, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Audit.IAuditWriter audit, CancellationToken ct)
    {
        var validation = new Validation();
        TimeOnly? sendAt = null;
        if (request.BriefingSendAt is { Length: > 0 } t)
        {
            if (!TimeOnly.TryParseExact(t, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) validation.Require("briefingSendAt", null, "invalid_time");
            else sendAt = parsed;
        }

        if (request.BriefingLanguage is not null and not ("ar" or "en")) validation.Require("briefingLanguage", null, "invalid");
        if (request.RecipientUserIds is { } ids)
        {
            var members = await db.TenantMemberships.Where(m => m.Status == MembershipStatus.Active).Select(m => m.UserId).ToListAsync(ct);
            if (ids.Any(id => !members.Contains(id))) validation.Require("recipientUserIds", null, "not_a_member");
        }

        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        var s = await db.TenantSettings.FirstAsync(ct);
        var before = new { sendAt = s.BriefingSendAt.ToString("HH:mm", CultureInfo.InvariantCulture), s.BriefingLanguage, s.BriefingEmailEnabled, recipients = s.BriefingRecipientUserIds.Length };
        if (sendAt is { } at) s.BriefingSendAt = at;
        if (request.BriefingLanguage is { } lang) s.BriefingLanguage = lang;
        if (request.BriefingEmailEnabled is { } enabled) s.BriefingEmailEnabled = enabled;
        if (request.RecipientUserIds is { } recipients) s.BriefingRecipientUserIds = recipients.Distinct().ToArray();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "tenant.briefing_settings_changed",
            EntityType = "tenant",
            EntityId = db.CurrentTenantId,
            Changes = JsonSerializer.Serialize(new { before, after = new { sendAt = s.BriefingSendAt.ToString("HH:mm", CultureInfo.InvariantCulture), s.BriefingLanguage, s.BriefingEmailEnabled, recipients = s.BriefingRecipientUserIds.Length } }),
        }, ct);
        return TypedResults.Ok(await ShapeSettingsAsync(s, db, ct));
    }

    private static string? Language(HttpContext context)
    {
        var q = context.Request.Query["language"].ToString();
        if (q.Length == 0)
        {
            // API-11: Accept-Language selects localized content. Default Arabic (PRD-03).
            var accept = context.Request.Headers.AcceptLanguage.ToString();
            return accept.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        }

        return q is "ar" or "en" ? q : null;
    }

    private static async Task<BriefingResponse> ShapeAsync(DailyBriefing b, TenantDbContext db, CaseService cases, CancellationToken ct)
    {
        var ctx = await cases.ContextAsync(ct);
        var metrics = BriefingMetrics.Deserialize(b.MetricsJson) ?? throw new InvalidOperationException("briefing metrics unreadable");
        var highlights = JsonSerializer.Deserialize<List<string>>(b.HighlightsJson) ?? [];
        var suggestion = b.AiSuggestionId is { } sid ? await db.AiSuggestions.AsNoTracking().Where(s => s.Id == sid).Select(s => new { s.ModelName, s.PromptVersion, s.Confidence }).FirstOrDefaultAsync(ct) : null;
        var dates = await db.DailyBriefings.AsNoTracking().Where(x => x.Language == b.Language).OrderByDescending(x => x.BriefingDate).Select(x => x.BriefingDate).Take(30).ToListAsync(ct);
        return new BriefingResponse(
            b.Id, b.BriefingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), b.Language, metrics, b.Narrative, highlights, b.NarrativeStatusValue == NarrativeStatus.Available,
            b.NarrativeStatusValue, b.AiSuggestionId, suggestion?.ModelName, suggestion?.PromptVersion, suggestion is null ? null : suggestion.Confidence.ToString("F3", CultureInfo.InvariantCulture),
            b.GeneratedAt.ToString("O", CultureInfo.InvariantCulture), b.GeneratedBy, b.SentAt?.ToString("O", CultureInfo.InvariantCulture), b.SentToCount, b.DeliveryStatus,
            b.BriefingDate == ctx.Today, dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList());
    }

    private static async Task<BriefingSettingsResponse> ShapeSettingsAsync(TenantSettings s, TenantDbContext db, CancellationToken ct)
    {
        var rows = await db.TenantMemberships.Where(m => m.Status == MembershipStatus.Active)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.Id, UserId = u.Id, u.Email, u.FullName, m.Role, m.Status, m.CreatedAt }).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var members = rows.Select(r => new MemberDto(r.Id, r.UserId, r.Email, r.FullName, r.Role.ToString(), r.Status.ToString(), r.CreatedAt)).ToList();
        var approved = await db.Templates.AnyAsync(t => t.Key == BriefingPlaceholders.TemplateKey && t.Language == s.BriefingLanguage && t.Status == "Approved" && t.IsActive && t.DeletedAt == null, ct);
        return new BriefingSettingsResponse(s.BriefingSendAt.ToString("HH:mm", CultureInfo.InvariantCulture), s.BriefingLanguage, s.BriefingEmailEnabled, s.BriefingRecipientUserIds, members, approved);
    }
}
