using System.Globalization;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 5. Handlers parse, authorize and shape; <see cref="CaseService"/> owns every state write.
/// PRD-14 is applied here as a server-side filter on every read for a scoped Collector.
/// </summary>
public static class CaseEndpoints
{
    public static RouteGroupBuilder MapCaseEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/queue", QueueAsync).RequiresPermission(Permissions.CasesRead).WithName("Queue");
        api.MapGet("/queue/summary", SummaryAsync).RequiresPermission(Permissions.CasesRead).WithName("QueueSummary");

        var cases = api.MapGroup("/cases");
        cases.MapGet("/", ListAsync).RequiresPermission(Permissions.CasesRead).WithName("ListCases");
        cases.MapPost("/", CreateAsync).RequiresPermission(Permissions.CasesWrite).WithName("CreateCase");
        cases.MapPost("/sweep", SweepAsync).RequiresPermission(Permissions.CasesWrite).WithName("SweepCases");
        cases.MapGet("/{id:guid}", GetAsync).RequiresPermission(Permissions.CasesRead).WithName("GetCase");
        cases.MapGet("/{id:guid}/timeline", TimelineAsync).RequiresPermission(Permissions.CasesRead).WithName("CaseTimeline");
        cases.MapPost("/{id:guid}/transitions", TransitionAsync).RequiresPermission(Permissions.CasesWrite).WithName("CaseTransition");
        cases.MapPost("/{id:guid}/assign", AssignAsync).RequiresPermission(Permissions.CasesAssign).WithName("AssignCase");
        cases.MapPost("/{id:guid}/activities", ActivityAsync).RequiresPermission(Permissions.CasesWrite).WithName("LogCaseActivity");
        cases.MapPost("/{id:guid}/snooze", SnoozeAsync).RequiresPermission(Permissions.CasesWrite).WithName("SnoozeCase");

        return api;
    }

    // ---------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------

    private sealed record Scope(Guid? AssignedTo, bool Scoped);

    /// <summary>PRD-14: a Collector under <c>collector_sees_only_assigned</c> sees their own cases, whatever the query says.</summary>
    private static async Task<Scope> ScopeAsync(HttpContext context, CurrentUser user, TenantDbContext db, CancellationToken ct)
    {
        var scoped = RolePermissions.IsAssignmentScoped(user.Role) && await db.TenantSettings.Select(s => s.CollectorSeesOnlyAssigned).FirstAsync(ct);
        if (scoped)
        {
            return new Scope(user.UserId, true);
        }

        var assignedTo = context.Request.Query["assignedTo"].ToString();
        return assignedTo switch
        {
            "me" => new Scope(user.UserId, false),
            "" or "all" => new Scope(null, false),
            var text when Guid.TryParse(text, out var id) => new Scope(id, false),
            _ => new Scope(null, false),
        };
    }

    private static async Task<IResult> QueueAsync(HttpContext context, CurrentUser user, TenantDbContext db, CaseService cases, TimeProvider time, CancellationToken ct)
    {
        var scope = await ScopeAsync(context, user, db, ct);
        var q = context.Request.Query;
        var bucket = q["bucket"].ToString();
        var minAmount = 0m;
        if (q["minAmount"].ToString() is { Length: > 0 } min && !decimal.TryParse(min, NumberStyles.Number, CultureInfo.InvariantCulture, out minAmount))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("minAmount", "invalid", "errors.validation.minAmount.invalid")]);
        }

        var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 200) : 50;
        var now = time.GetUtcNow();

        var query = cases.QueueQuery(now);
        if (scope.AssignedTo is { } assignee) query = query.Where(c => c.AssignedTo == assignee);
        if (minAmount > 0m) query = query.Where(c => c.OverdueBalanceBase >= minAmount);

        if (bucket.Length > 0)
        {
            // Buckets are ranges of max_days_past_due, so the filter runs in SQL rather than after shaping.
            var ctx = await cases.ContextAsync(ct);
            var range = AgingBuckets.FromBoundaries(ctx.Settings.AgingBucketDays).All.FirstOrDefault(b => b.Key == bucket);
            if (range is null) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("bucket", "invalid", "errors.validation.bucket.invalid")]);
            if (range.FromDays is { } from) query = query.Where(c => c.MaxDaysPastDue >= from);
            if (range.ToDays is { } to) query = query.Where(c => c.MaxDaysPastDue <= to);
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(c => c.PriorityScore).ThenByDescending(c => c.MaxDaysPastDue).Take(limit).ToListAsync(ct);
        return TypedResults.Ok(new QueueResponse(await ShapeAsync(page, db, cases, ct), total, now.ToString("O", CultureInfo.InvariantCulture), scope.Scoped));
    }

    private static async Task<IResult> SummaryAsync(HttpContext context, CurrentUser user, TenantDbContext db, CaseService cases, TimeProvider time, CancellationToken ct)
    {
        var scope = await ScopeAsync(context, user, db, ct);
        var now = time.GetUtcNow();
        var query = db.Cases.AsQueryable();
        if (scope.AssignedTo is { } assignee) query = query.Where(c => c.AssignedTo == assignee);

        var byStatus = await query.GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key.ToString(), x => x.Count, StringComparer.Ordinal, ct);
        var active = await query.Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned).ToListAsync(ct);
        var context5 = await cases.ContextAsync(ct);
        var buckets = AgingBuckets.FromBoundaries(context5.Settings.AgingBucketDays);
        var eligible = active.Where(c => c.Status != CaseStatus.OnHold && c.Status != CaseStatus.Escalated && (c.NextActionAt == null || c.NextActionAt <= now)).ToList();
        var byBucket = buckets.All.ToDictionary(b => b.Key, b => eligible.Count(c => buckets.Classify(c.MaxDaysPastDue).Key == b.Key), StringComparer.Ordinal);

        return TypedResults.Ok(new QueueSummaryResponse(byStatus, byBucket, eligible.Count, active.Count - eligible.Count, scope.Scoped));
    }

    private static async Task<IResult> ListAsync(HttpContext context, CurrentUser user, TenantDbContext db, CaseService cases, CancellationToken ct)
    {
        var scope = await ScopeAsync(context, user, db, ct);
        var q = context.Request.Query;
        var query = db.Cases.AsQueryable();
        if (scope.AssignedTo is { } assignee) query = query.Where(c => c.AssignedTo == assignee);
        if (q["status"].ToString() is { Length: > 0 } statusText)
        {
            if (!Enum.TryParse<CaseStatus>(statusText, out var status)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("status", "invalid", "errors.validation.status.invalid")]);
            query = query.Where(c => c.Status == status);
        }

        if (Guid.TryParse(q["customerId"], out var customerId)) query = query.Where(c => c.CustomerId == customerId);

        var all = await query.OrderByDescending(c => c.OpenedAt).Take(500).ToListAsync(ct);
        return TypedResults.Ok(new CaseListResponse(await ShapeAsync(all, db, cases, ct), all.Count, scope.Scoped));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, CaseService cases, CancellationToken ct)
    {
        var scope = await ScopeAsync(context, user, db, ct);
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || (scope.Scoped && c.AssignedTo != scope.AssignedTo))
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var item = (await ShapeAsync([c], db, cases, ct))[0];
        var ctx = await cases.ContextAsync(ct);
        var scopeRows = await db.CaseInvoices.Where(x => x.CaseId == c.Id).ToListAsync(ct);
        var invoices = await db.Invoices.Where(i => scopeRows.Select(r => r.InvoiceId).Contains(i.Id)).ToListAsync(ct);
        var invoiceDtos = scopeRows
            .Join(invoices, r => r.InvoiceId, i => i.Id, (r, i) => new CaseInvoiceDto(
                i.Id, i.InvoiceNumber, i.Currency, Iso(i.IssueDate), Iso(i.DueDate), MoneyDto.From(i.TotalAmount, i.Currency), MoneyDto.From(i.BalanceCache, i.Currency),
                ctx.Today.DayNumber - i.DueDate.DayNumber, i.Status.ToString(), r.AddedAt.ToString("O", CultureInfo.InvariantCulture),
                r.RemovedAt?.ToString("O", CultureInfo.InvariantCulture), r.RemovedReason))
            .OrderBy(d => d.RemovedAt is not null).ThenBy(d => d.DueDate, StringComparer.Ordinal).ToList();

        return TypedResults.Ok(new CaseDetailResponse(
            item, c.OpenedAt.ToString("O", CultureInfo.InvariantCulture), c.HoldUntil is { } h ? Iso(h) : null, c.HoldReason,
            c.EscalatedAt?.ToString("O", CultureInfo.InvariantCulture), c.EscalatedBy, c.EscalationReason,
            c.ClosedAt?.ToString("O", CultureInfo.InvariantCulture), c.CloseReason, c.NextActionReason, c.RowVersion,
            invoiceDtos, await TimelineEntriesAsync(c, db, ct), [], [], []));
    }

    private static async Task<IResult> TimelineAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, CancellationToken ct)
    {
        var scope = await ScopeAsync(context, user, db, ct);
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || (scope.Scoped && c.AssignedTo != scope.AssignedTo))
        {
            return ApiProblems.NotFoundProblem(context);
        }

        return TypedResults.Ok(new { items = await TimelineEntriesAsync(c, db, ct) });
    }

    /// <summary>Activities plus allocations to in-scope invoices, one chronological stream (doc 05 <c>/timeline</c>).</summary>
    private static async Task<IReadOnlyList<TimelineEntryDto>> TimelineEntriesAsync(CollectionCase c, TenantDbContext db, CancellationToken ct)
    {
        var activities = await db.CaseActivities.Where(a => a.CaseId == c.Id).ToListAsync(ct);
        var entries = activities.Select(a => new TimelineEntryDto(a.Id.ToString(), a.Kind, a.OccurredAt.ToString("O", CultureInfo.InvariantCulture), a.ActorKind, a.ActorUserId, a.Summary,
            a.Detail is null ? null : JsonDocument.Parse(a.Detail).RootElement)).ToList();

        var invoiceIds = await db.CaseInvoices.Where(x => x.CaseId == c.Id).Select(x => x.InvoiceId).ToListAsync(ct);
        var allocations = await db.PaymentAllocations.Where(a => invoiceIds.Contains(a.InvoiceId) && a.CreatedAt >= c.OpenedAt)
            .Join(db.Invoices, a => a.InvoiceId, i => i.Id, (a, i) => new { a.Id, a.Amount, a.Currency, a.CreatedAt, a.AllocatedBy, a.ReversalOfId, i.InvoiceNumber })
            .ToListAsync(ct);
        entries.AddRange(allocations.Select(a => new TimelineEntryDto(a.Id.ToString(), ActivityKinds.Payment, a.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            a.AllocatedBy is null ? ActorKinds.System : ActorKinds.User, a.AllocatedBy,
            a.ReversalOfId is null ? $"Payment allocated to {a.InvoiceNumber}" : $"Allocation reversed on {a.InvoiceNumber}",
            JsonSerializer.SerializeToElement(new { amount = MoneyDto.From(a.Amount, a.Currency), invoiceNumber = a.InvoiceNumber, reversal = a.ReversalOfId is not null }, WebJson))));

        return entries.OrderBy(e => e.OccurredAt, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<QueueItemDto>> ShapeAsync(IReadOnlyList<CollectionCase> list, TenantDbContext db, CaseService cases, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var ctx = await cases.ContextAsync(ct);
        var buckets = AgingBuckets.FromBoundaries(ctx.Settings.AgingBucketDays);
        var customerIds = list.Select(c => c.CustomerId).Distinct().ToList();
        var customers = await db.Customers.Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var caseIds = list.Select(c => c.Id).ToList();
        var scoped = await db.CaseInvoices.Where(x => caseIds.Contains(x.CaseId) && x.RemovedAt == null).Select(x => new { x.CaseId, x.InvoiceId }).ToListAsync(ct);
        var invoiceIds = scoped.Select(s => s.InvoiceId).Distinct().ToList();
        var balances = await db.Invoices.Where(i => invoiceIds.Contains(i.Id) && i.Status == InvoiceStatus.Open)
            .Select(i => new { i.Id, i.Currency, i.BalanceCache }).ToDictionaryAsync(i => i.Id, ct);
        var now = ctx.Today;
        // Slice 7: open disputes per case, and whether any breaches its SLA (SM-48) — visible on every row.
        var utcNow = ctx.Now;
        var disputes = await db.Disputes.Where(d => d.CaseId != null && caseIds.Contains(d.CaseId.Value)
                && (d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer)).ToListAsync(ct);

        return list.Select(c =>
        {
            customers.TryGetValue(c.CustomerId, out var cu);
            // FIN-04: per currency, never summed. The ranking input (base) is not shown as money.
            var overdue = scoped.Where(s => s.CaseId == c.Id).Select(s => balances.GetValueOrDefault(s.InvoiceId)).Where(b => b is not null)
                .GroupBy(b => b!.Currency).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CustomerPositionDto(g.Key, MoneyDto.From(g.Sum(b => b!.BalanceCache), g.Key), g.Count(), MoneyDto.From(0m, g.Key), MoneyDto.From(0m, g.Key)))
                .ToList();
            var sinceContact = c.LastContactAt is { } last ? (int?)Math.Max(0, (int)(ctx.Now - last).TotalDays) : null;
            var language = cu?.PreferredLanguage ?? "ar";
            return new QueueItemDto(
                c.Id, c.CaseNumber,
                new CaseCustomerDto(c.CustomerId, cu?.Code, cu?.NameAr, cu?.NameEn, language, (cu?.RiskFlag ?? RiskFlag.None).ToString(), cu?.BrokenPromiseCount12m ?? 0, cu?.BouncedChequeCount12m ?? 0),
                c.Status.ToString(), c.PriorityScore, c.WeightsVersion,
                CaseService.Factors(c).Select(f => new PriorityFactorDto(f.Factor, f.Contribution, f.Detail)).ToList(),
                overdue, c.MaxDaysPastDue, buckets.Classify(c.MaxDaysPastDue).Key, c.InvoiceCount, c.AssignedTo,
                c.NextActionAt?.ToString("O", CultureInfo.InvariantCulture), c.LastContactAt?.ToString("O", CultureInfo.InvariantCulture),
                c.AutomationDisabled,
                Suggested(SuggestedActions.For(c.Status, c.MaxDaysPastDue, sinceContact, ctx.Settings.DunningCadenceDays, language)),
                disputes.Count(d => d.CaseId == c.Id), disputes.Any(d => d.CaseId == c.Id && d.IsSlaBreached(utcNow)));
        }).ToList();
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static SuggestedActionDto Suggested(SuggestedAction a) => new(a.Kind, a.TemplateKey, a.Language);

    // ---------------------------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(CreateCaseRequest request, HttpContext context, CurrentUser user, CaseService cases, TenantDbContext db, CancellationToken ct)
    {
        if (request.CustomerId is null)
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("customerId", "required", "errors.validation.customerId.required")]);
        }

        try
        {
            var c = await cases.CreateManualAsync(request.CustomerId.Value, user.UserId, ct);
            return TypedResults.Created($"/api/v1/cases/{c.Id}", (await ShapeAsync([c], db, cases, ct))[0]);
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> SweepAsync(CurrentUser user, CaseService cases, CancellationToken ct)
    {
        var r = await cases.SweepAsync(user.UserId, ct);
        return TypedResults.Ok(new SweepResponse(r.Created, r.Resolved, r.Resumed, r.FollowedUp, r.Rescored));
    }

    private static async Task<IResult> TransitionAsync(Guid id, CaseTransitionRequest request, HttpContext context, CurrentUser user, CaseService cases, TenantDbContext db, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(c => c.Id == id, ct))
        {
            return ApiProblems.NotFoundProblem(context);   // existence first, then the body, then who may act (API-03)
        }

        if (request.Event is null || !CaseMachine.UserEvents.TryGetValue(request.Event, out var @event))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("event", "invalid", "errors.validation.event.invalid")]);
        }

        if (CaseMachine.RequiresEscalatePermission(@event) && !user.Can(Permissions.CasesEscalate))
        {
            return ApiProblems.ForbiddenProblem(context, Permissions.CasesEscalate);
        }

        DateOnly? holdUntil = null;
        if (request.HoldUntil is not null)
        {
            if (!DateOnly.TryParseExact(request.HoldUntil, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("holdUntil", "invalid_date", "errors.validation.holdUntil.invalid_date")]);
            }

            holdUntil = d;
        }

        try
        {
            var c = await cases.TransitionAsync(id, @event, user.UserId, request.ReasonCode, request.Note, holdUntil, ct);
            return TypedResults.Ok((await ShapeAsync([c], db, cases, ct))[0]);
        }
        catch (InvalidTransitionException ex)
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "invalid_transition",
                $"No transition from {ex.From} on {ex.Event}.", "errors.invalid_transition",
                [new ApiProblems.FieldError("event", "invalid_transition", "errors.invalid_transition", new Dictionary<string, string> { ["from"] = ex.From, ["event"] = ex.Event })]);
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> AssignAsync(Guid id, AssignCaseRequest request, HttpContext context, CurrentUser user, CaseService cases, TenantDbContext db, CancellationToken ct)
    {
        try
        {
            var c = await cases.AssignAsync(id, request.UserId, user.UserId, ct);
            return TypedResults.Ok((await ShapeAsync([c], db, cases, ct))[0]);
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> ActivityAsync(Guid id, CaseActivityRequest request, HttpContext context, CurrentUser user, CaseService cases, TenantDbContext db, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);   // existence before the body (API-03)
        var validation = new Validation().Require("kind", request.Kind).Require("summary", request.Summary);
        if (request.Summary is { Length: > 2000 }) validation.Require("summary", null, "too_long");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        try
        {
            var a = await cases.LogActivityAsync(id, request.Kind!, request.Summary!, request.Detail?.GetRawText(), user.UserId, ct);
            return TypedResults.Created($"/api/v1/cases/{id}/timeline",
                new TimelineEntryDto(a.Id.ToString(), a.Kind, a.OccurredAt.ToString("O", CultureInfo.InvariantCulture), a.ActorKind, a.ActorUserId, a.Summary,
                    a.Detail is null ? null : JsonDocument.Parse(a.Detail).RootElement));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> SnoozeAsync(Guid id, SnoozeRequest request, HttpContext context, CurrentUser user, CaseService cases, TenantDbContext db, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (!DateOnly.TryParseExact(request.UntilDate ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("untilDate", "invalid_date", "errors.validation.untilDate.invalid_date")]);
        }

        try
        {
            var c = await cases.SnoozeAsync(id, until, request.Reason, user.UserId, ct);
            return TypedResults.Ok((await ShapeAsync([c], db, cases, ct))[0]);
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static IResult Rule(HttpContext context, CaseException ex) => ex.Code switch
    {
        "case_not_found" or "customer_not_found" or "member_not_found" => ApiProblems.NotFoundProblem(context),
        "duplicate" => ApiProblems.Create(context, StatusCodes.Status409Conflict, "duplicate", "An open case already exists for this customer.", "errors.duplicate_case"),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
