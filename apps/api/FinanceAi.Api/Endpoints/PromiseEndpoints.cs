using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
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
/// Doc 05 slice 6. Three human transitions (confirm, reject, cancel) and reads. There is deliberately no
/// route that sets Kept / PartiallyKept / Broken — <see cref="PromiseService"/> decides those from the ledger.
/// </summary>
public static class PromiseEndpoints
{
    public static RouteGroupBuilder MapPromiseEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/cases/{id:guid}/promises", RecordAsync).RequiresPermission(Permissions.PtpWrite).WithName("RecordPromise");
        var promises = api.MapGroup("/promises");
        promises.MapGet("/", ListAsync).RequiresPermission(Permissions.CasesRead).WithName("ListPromises");
        promises.MapGet("/{id:guid}", GetAsync).RequiresPermission(Permissions.CasesRead).WithName("GetPromise");
        promises.MapPost("/{id:guid}/confirm", ConfirmAsync).RequiresPermission(Permissions.PtpWrite).WithName("ConfirmPromise");
        promises.MapPost("/{id:guid}/reject", RejectAsync).RequiresPermission(Permissions.PtpWrite).WithName("RejectPromise");
        promises.MapPost("/{id:guid}/cancel", CancelAsync).RequiresPermission(Permissions.PtpWrite).WithName("CancelPromise");
        api.MapGet("/customers/{id:guid}/promise-history", HistoryAsync).RequiresPermission(Permissions.CasesRead).WithName("PromiseHistory");

        return api;
    }

    private static async Task<Results<Created<PromiseResponse>, ProblemHttpResult>> RecordAsync(Guid id, RecordPromiseRequest request, HttpContext context, CurrentUser user, TenantDbContext db, PromiseService promises, CaseService cases, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);

        var validation = new Validation().Require("source", request.Source).Currency("promisedAmount.currency", request.PromisedAmount?.Currency);
        if (request.InvoiceIds is null || request.InvoiceIds.Count == 0) validation.Require("invoiceIds", null);
        var amount = 0m;
        if (request.PromisedAmount is null || !request.PromisedAmount.TryParse(out amount) || amount <= 0m) validation.Require("promisedAmount", null, "invalid_amount");
        if (!TryDate(request.PromisedDate, out var promisedDate)) validation.Require("promisedDate", null, "invalid_date");
        if (request.Source is not null && !PtpSources.UserRecordable.Contains(request.Source)) validation.Require("source", null, "invalid");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        try
        {
            var result = await promises.RecordAsync(id, request.InvoiceIds!, amount, promisedDate, request.Source!, request.Notes, user.UserId, null, ct);
            return TypedResults.Created($"/api/v1/promises/{result.Promise.Id}", await ToResponseAsync(result.Promise, db, result.Superseded, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { ConstraintName: "one_active_ptp_per_invoice" })
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "overlapping_promise", "An active promise already covers one of these invoices.", "errors.overlapping_promise");
        }
    }

    private static async Task<Results<Ok<PromiseListResponse>, ProblemHttpResult>> ListAsync(HttpContext context, TenantDbContext db, CaseService cases, CancellationToken ct)
    {
        var q = context.Request.Query;
        var query = db.Promises.AsQueryable();
        if (q["status"].ToString() is { Length: > 0 } statusText)
        {
            if (!Enum.TryParse<PtpStatus>(statusText, out var status)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("status", "invalid", "errors.validation.status.invalid")]);
            query = query.Where(p => p.Status == status);
        }

        if (q["dueBefore"].ToString() is { Length: > 0 } dueText)
        {
            if (!TryDate(dueText, out var due)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("dueBefore", "invalid_date", "errors.validation.dueBefore.invalid_date")]);
            query = query.Where(p => p.DeadlineDate <= due);
        }

        if (Guid.TryParse(q["customerId"], out var customerId)) query = query.Where(p => p.CustomerId == customerId);
        if (Guid.TryParse(q["caseId"], out var caseId)) query = query.Where(p => p.CaseId == caseId);

        var items = await query.OrderBy(p => p.DeadlineDate).ThenByDescending(p => p.CreatedAt).Take(500).ToListAsync(ct);
        var shaped = new List<PromiseResponse>();
        foreach (var p in items) shaped.Add(await ToResponseAsync(p, db, [], ct));
        var today = (await cases.ContextAsync(ct)).Today;
        return TypedResults.Ok(new PromiseListResponse(shaped, shaped.Count, Iso(today)));
    }

    private static async Task<Results<Ok<PromiseResponse>, ProblemHttpResult>> GetAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var p = await db.Promises.FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await ToResponseAsync(p, db, [], ct));
    }

    private static async Task<Results<Ok<PromiseResponse>, ProblemHttpResult>> ConfirmAsync(Guid id, ConfirmPromiseRequest request, HttpContext context, CurrentUser user, TenantDbContext db, PromiseService promises, CancellationToken ct)
    {
        if (!await db.Promises.AnyAsync(p => p.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        decimal? amount = null;
        DateOnly? date = null;
        var validation = new Validation();
        if (request.PromisedAmount is not null)
        {
            if (!request.PromisedAmount.TryParse(out var a) || a <= 0m) validation.Require("promisedAmount", null, "invalid_amount");
            else amount = a;
        }

        if (request.PromisedDate is not null)
        {
            if (!TryDate(request.PromisedDate, out var d)) validation.Require("promisedDate", null, "invalid_date");
            else date = d;
        }

        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        try
        {
            var p = await promises.ConfirmAsync(id, amount, date, user.UserId, ct);
            return TypedResults.Ok(await ToResponseAsync(p, db, [], ct));
        }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<Results<Ok<PromiseResponse>, ProblemHttpResult>> RejectAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, PromiseService promises, CancellationToken ct)
    {
        if (!await db.Promises.AnyAsync(p => p.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.validation.reason.required")]);
        try
        {
            return TypedResults.Ok(await ToResponseAsync(await promises.RejectAsync(id, request.Reason, user.UserId, ct), db, [], ct));
        }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<Results<Ok<PromiseResponse>, ProblemHttpResult>> CancelAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, PromiseService promises, CancellationToken ct)
    {
        if (!await db.Promises.AnyAsync(p => p.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.validation.reason.required")]);
        try
        {
            return TypedResults.Ok(await ToResponseAsync(await promises.CancelAsync(id, request.Reason, user.UserId, ct), db, [], ct));
        }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<Results<Ok<PromiseHistoryResponse>, ProblemHttpResult>> HistoryAsync(Guid id, HttpContext context, TenantDbContext db, PromiseService promises, CaseService cases, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var today = (await cases.ContextAsync(ct)).Today;
        var reliability = await promises.ReliabilityAsync(id, today, ct);
        var items = await db.Promises.Where(p => p.CustomerId == id).OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);
        var shaped = new List<PromiseResponse>();
        foreach (var p in items) shaped.Add(await ToResponseAsync(p, db, [], ct));
        return TypedResults.Ok(new PromiseHistoryResponse(id,
            new ReliabilityDto(reliability.Kept, reliability.PartiallyKept, reliability.Broken, reliability.Denominator, reliability.Ratio?.ToString("0.000", CultureInfo.InvariantCulture)),
            shaped));
    }

    private static async Task<PromiseResponse> ToResponseAsync(PromiseToPay p, TenantDbContext db, IReadOnlyList<Guid> superseded, CancellationToken ct)
    {
        var caseNumber = await db.Cases.Where(c => c.Id == p.CaseId).Select(c => c.CaseNumber).FirstAsync(ct);
        var invoiceIds = await db.PtpInvoices.Where(x => x.PtpId == p.Id).Select(x => x.InvoiceId).ToListAsync(ct);
        var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).OrderBy(i => i.DueDate)
            .Select(i => new PromiseInvoiceDto(i.Id, i.InvoiceNumber, MoneyDto.From(i.BalanceCache, i.Currency), i.Status.ToString())).ToListAsync(ct);
        return new PromiseResponse(
            p.Id, p.CaseId, caseNumber, p.CustomerId, p.Status.ToString(), MoneyDto.From(p.PromisedAmount, p.Currency), Iso(p.PromisedDate), Iso(p.DeadlineDate),
            p.Source, p.CapturedBy, p.ConfirmedBy, p.ChequeId, p.SupersededById, p.CancelReason,
            p.EvaluatedAt?.ToString("O", CultureInfo.InvariantCulture), p.ReceivedInWindow is { } r ? MoneyDto.From(r, p.Currency) : null, p.EvaluationNote, p.Notes,
            p.CreatedAt.ToString("O", CultureInfo.InvariantCulture), p.RowVersion, invoices, superseded);
    }

    private static ProblemHttpResult Invalid(HttpContext context, InvalidTransitionException ex) =>
        ApiProblems.Create(context, StatusCodes.Status409Conflict, "invalid_transition", $"No transition from {ex.From} on {ex.Event}.", "errors.invalid_transition",
            [new ApiProblems.FieldError("event", "invalid_transition", "errors.invalid_transition", new Dictionary<string, string> { ["from"] = ex.From, ["event"] = ex.Event })]);

    private static ProblemHttpResult Rule(HttpContext context, CaseException ex) => ex.Code switch
    {
        "case_not_found" or "promise_not_found" => ApiProblems.NotFoundProblem(context),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };

    private static bool TryDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
