using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 7. Updates need <c>disputes.write</c>; the three resolutions have their own route behind
/// <c>disputes.resolve</c> (SM-47). The verification-task routes never mark anything paid (SM-10).
/// </summary>
public static class DisputeEndpoints
{
    public static RouteGroupBuilder MapDisputeEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/invoices/{id:guid}/disputes", RaiseAsync).RequiresPermission(Permissions.DisputesWrite).WithName("RaiseDispute");
        var disputes = api.MapGroup("/disputes");
        disputes.MapGet("/", ListAsync).RequiresPermission(Permissions.CasesRead).WithName("ListDisputes");
        disputes.MapGet("/{id:guid}", GetAsync).RequiresPermission(Permissions.CasesRead).WithName("GetDispute");
        disputes.MapPost("/{id:guid}/transitions", TransitionAsync).RequiresPermission(Permissions.DisputesWrite).WithName("DisputeTransition");
        disputes.MapPost("/{id:guid}/resolve", ResolveAsync).RequiresPermission(Permissions.DisputesResolve).WithName("ResolveDispute");
        disputes.MapPost("/{id:guid}/evidence", EvidenceUploadAsync).RequiresPermission(Permissions.DisputesWrite).DisableAntiforgery().WithName("AttachEvidence");
        disputes.MapGet("/{id:guid}/evidence/{evidenceId:guid}", EvidenceDownloadAsync).Produces(200, contentType: "application/octet-stream").RequiresPermission(Permissions.CasesRead).WithName("DownloadEvidence");
        api.MapGet("/cases/{id:guid}/dunning-eligibility", DunningEligibilityAsync).RequiresPermission(Permissions.CasesRead).WithName("DunningEligibility");
        api.MapGet("/tasks/payment-verification", ListTasksAsync).RequiresPermission(Permissions.PaymentsRead).WithName("ListVerificationTasks");
        api.MapPost("/tasks/payment-verification/{id:guid}/resolve", ResolveTaskAsync).RequiresPermission(Permissions.PaymentsWrite).WithName("ResolveVerificationTask");

        return api;
    }

    private static async Task<Results<Created<DisputeResponse>, ProblemHttpResult>> RaiseAsync(Guid id, RaiseDisputeRequest request, HttpContext context, CurrentUser user, TenantDbContext db, DisputeService disputes, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Invoices.AnyAsync(i => i.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var validation = new Validation().Require("reasonCode", request.ReasonCode).Currency("disputedAmount.currency", request.DisputedAmount?.Currency);
        var amount = 0m;
        if (request.DisputedAmount is null || !request.DisputedAmount.TryParse(out amount) || amount <= 0m) validation.Require("disputedAmount", null, "invalid_amount");
        if (request.ReasonCode is not null && !DisputeReasons.All.Contains(request.ReasonCode, StringComparer.Ordinal)) validation.Require("reasonCode", null, "invalid");
        if (request.CustomerClaim is { Length: > 4000 }) validation.Require("customerClaim", null, "too_long");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        try
        {
            var result = await disputes.RaiseAsync(id, request.ReasonCode!, amount, request.CustomerClaim, "user", user.UserId, null, ct);
            return TypedResults.Created($"/api/v1/disputes/{result.Dispute.Id}", await ToResponseAsync(result.Dispute, db, time, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<Results<Ok<DisputeListResponse>, ProblemHttpResult>> ListAsync(HttpContext context, TenantDbContext db, TimeProvider time, CancellationToken ct)
    {
        var q = context.Request.Query;
        var query = db.Disputes.AsQueryable();
        if (q["status"].ToString() is { Length: > 0 } statusText)
        {
            if (!Enum.TryParse<DisputeStatus>(statusText, out var status)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("status", "invalid", "errors.validation.status.invalid")]);
            query = query.Where(d => d.Status == status);
        }

        if (q["reasonCode"].ToString() is { Length: > 0 } reason) query = query.Where(d => d.ReasonCode == reason);
        if (Guid.TryParse(q["assignedTo"], out var assignee)) query = query.Where(d => d.AssignedTo == assignee);
        if (Guid.TryParse(q["customerId"], out var customerId)) query = query.Where(d => d.CustomerId == customerId);
        if (Guid.TryParse(q["invoiceId"], out var invoiceId)) query = query.Where(d => d.InvoiceId == invoiceId);
        if (Guid.TryParse(q["caseId"], out var caseId)) query = query.Where(d => d.CaseId == caseId);

        var items = await query.OrderBy(d => d.ResolutionDueAt).Take(500).ToListAsync(ct);
        var now = time.GetUtcNow();
        if (q["slaBreached"].ToString() == "true") items = items.Where(d => d.IsSlaBreached(now)).ToList();
        var shaped = new List<DisputeResponse>();
        foreach (var d in items) shaped.Add(await ToResponseAsync(d, db, time, ct));
        return TypedResults.Ok(new DisputeListResponse(shaped, shaped.Count));
    }

    private static async Task<Results<Ok<DisputeResponse>, ProblemHttpResult>> GetAsync(Guid id, HttpContext context, TenantDbContext db, TimeProvider time, CancellationToken ct)
    {
        var d = await db.Disputes.FirstOrDefaultAsync(x => x.Id == id, ct);
        return d is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await ToResponseAsync(d, db, time, ct));
    }

    private static async Task<Results<Ok<DisputeResponse>, ProblemHttpResult>> TransitionAsync(Guid id, DisputeTransitionRequest request, HttpContext context, CurrentUser user, TenantDbContext db, DisputeService disputes, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Disputes.AnyAsync(d => d.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (request.Event is null || !DisputeMachine.UpdateEvents.TryGetValue(request.Event, out var @event))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("event", "invalid", "errors.validation.event.invalid")]);
        }

        try
        {
            return TypedResults.Ok(await ToResponseAsync(await disputes.TransitionAsync(id, @event, user.UserId, request.Reason, request.AssignedTo, ct), db, time, ct));
        }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<Results<Ok<DisputeResponse>, ProblemHttpResult>> ResolveAsync(Guid id, ResolveDisputeRequest request, HttpContext context, CurrentUser user, TenantDbContext db, DisputeService disputes, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Disputes.AnyAsync(d => d.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (request.Outcome is null || !DisputeMachine.ResolutionOutcomes.TryGetValue(request.Outcome, out var outcome))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("outcome", "invalid", "errors.validation.outcome.invalid")]);
        }

        decimal? amount = null;
        if (request.ResolutionAmount is not null)
        {
            if (!request.ResolutionAmount.TryParse(out var a) || a <= 0m) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("resolutionAmount", "invalid_amount", "errors.validation.resolutionAmount.invalid_amount")]);
            amount = a;
        }

        if (outcome == DisputeEvent.Reject && string.IsNullOrWhiteSpace(request.Reason))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.validation.reason.required")]);
        }

        try
        {
            var result = await disputes.ResolveAsync(id, outcome, amount, request.Reason, user.UserId, ct);
            return TypedResults.Ok(await ToResponseAsync(result.Dispute, db, time, ct));
        }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
        catch (LedgerException ex) { return ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta); }   // SM-46 from the ledger unwinds the whole request
    }

    private static async Task<Results<Created<DisputeEvidenceDto>, ProblemHttpResult>> EvidenceUploadAsync(Guid id, IFormFile? file, HttpContext context, CurrentUser user, TenantDbContext db, DisputeService disputes, CancellationToken ct)
    {
        if (!await db.Disputes.AnyAsync(d => d.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (file is null || file.Length == 0) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("file", "required", "errors.validation.file.required")]);
        if (file.Length > EvidenceInspector.MaxBytes) return ApiProblems.BusinessRuleProblem(context, "file_too_large", "file", null);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        try
        {
            var e = await disputes.AttachEvidenceAsync(id, file.FileName, buffer.ToArray(), user.UserId, ct);
            return TypedResults.Created($"/api/v1/disputes/{id}/evidence/{e.Id}", Evidence(e));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    /// <summary>SEC-40: served as an attachment with nosniff; the bytes are never rendered inline and never parsed.</summary>
    private static async Task<Results<FileContentHttpResult, ProblemHttpResult>> EvidenceDownloadAsync(Guid id, Guid evidenceId, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var e = await db.DisputeEvidence.FirstOrDefaultAsync(x => x.Id == evidenceId && x.DisputeId == id, ct);
        if (e is null) return ApiProblems.NotFoundProblem(context);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(e.Content, e.ContentType, e.FileName);
    }

    private static async Task<Results<Ok<DunningEligibilityResponse>, ProblemHttpResult>> DunningEligibilityAsync(Guid id, HttpContext context, TenantDbContext db, DisputeService disputes, CancellationToken ct)
    {
        try
        {
            var items = await disputes.DunningEligibilityAsync(id, ct);
            var split = await db.TenantSettings.Select(s => s.AllowSplitDunningDuringDispute).FirstAsync(ct);
            return TypedResults.Ok(new DunningEligibilityResponse(id, split, items.Select(i => new DunningEligibilityDto(i.InvoiceId, i.Allowed, i.Reason)).ToList()));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<Results<Ok<VerificationTaskListResponse>, ProblemHttpResult>> ListTasksAsync(HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var status = context.Request.Query["status"].ToString();
        var query = db.VerificationTasks.AsQueryable();
        if (status is "Open" or "Resolved") query = query.Where(t => t.Status == status);
        var items = await query.OrderBy(t => t.CreatedAt).Take(500).ToListAsync(ct);
        var invoiceIds = items.Select(t => t.InvoiceId).Distinct().ToList();
        var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return TypedResults.Ok(new VerificationTaskListResponse(items.Select(t =>
        {
            var i = invoices[t.InvoiceId];
            return new VerificationTaskDto(t.Id, t.InvoiceId, i.InvoiceNumber, t.CustomerId, t.DisputeId, t.Source, t.Status, t.Claim,
                MoneyDto.From(i.BalanceCache, i.Currency), i.Status.ToString(), t.Outcome, t.PaymentId, t.Notes,
                t.CreatedAt.ToString("O", CultureInfo.InvariantCulture), t.ResolvedAt?.ToString("O", CultureInfo.InvariantCulture), t.ResolvedBy);
        }).ToList(), items.Count));
    }

    private static async Task<Results<Ok<VerificationTaskDto>, ProblemHttpResult>> ResolveTaskAsync(Guid id, ResolveVerificationRequest request, HttpContext context, CurrentUser user, TenantDbContext db, DisputeService disputes, CancellationToken ct)
    {
        if (!await db.VerificationTasks.AnyAsync(t => t.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var outcome = request.Outcome switch
        {
            "payment_found" => VerificationOutcome.PaymentFound,
            "no_payment_found" => VerificationOutcome.NoPaymentFound,
            "partial" => VerificationOutcome.Partial,
            _ => (VerificationOutcome?)null,
        };
        if (outcome is null) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("outcome", "invalid", "errors.validation.outcome.invalid")]);

        try
        {
            var t = await disputes.ResolveVerificationAsync(id, outcome.Value, request.PaymentId, request.Notes, user.UserId, ct);
            var i = await db.Invoices.FirstAsync(x => x.Id == t.InvoiceId, ct);
            return TypedResults.Ok(new VerificationTaskDto(t.Id, t.InvoiceId, i.InvoiceNumber, t.CustomerId, t.DisputeId, t.Source, t.Status, t.Claim,
                MoneyDto.From(i.BalanceCache, i.Currency), i.Status.ToString(), t.Outcome, t.PaymentId, t.Notes,
                t.CreatedAt.ToString("O", CultureInfo.InvariantCulture), t.ResolvedAt?.ToString("O", CultureInfo.InvariantCulture), t.ResolvedBy));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<DisputeResponse> ToResponseAsync(Dispute d, TenantDbContext db, TimeProvider time, CancellationToken ct)
    {
        var at = time.GetUtcNow();
        var invoice = await db.Invoices.Where(i => i.Id == d.InvoiceId).Select(i => new { i.InvoiceNumber, i.BalanceCache, i.Currency }).FirstAsync(ct);
        var caseNumber = d.CaseId is { } cid ? await db.Cases.Where(c => c.Id == cid).Select(c => (long?)c.CaseNumber).FirstOrDefaultAsync(ct) : null;
        var evidence = await db.DisputeEvidence.Where(e => e.DisputeId == d.Id).OrderBy(e => e.UploadedAt)
            .Select(e => new DisputeEvidenceDto(e.Id, e.FileName, e.ContentType, e.SizeBytes, e.Sha256, e.UploadedBy, e.UploadedAt.ToString("O", CultureInfo.InvariantCulture))).ToListAsync(ct);
        var taskId = await db.VerificationTasks.Where(t => t.DisputeId == d.Id).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        var breached = d.IsSlaBreached(at);
        var slaState = !DisputeMachine.IsOpen(d.Status) ? "closed" : d.PendingSince is not null ? "paused" : breached ? "breached" : at.Date == d.ResolutionDueAt.Date ? "due_today" : "on_track";
        return new DisputeResponse(
            d.Id, d.InvoiceId, invoice.InvoiceNumber, d.CustomerId, d.CaseId, caseNumber, d.Status.ToString(), d.ReasonCode,
            MoneyDto.From(d.DisputedAmount, d.Currency), MoneyDto.From(invoice.BalanceCache, invoice.Currency), d.CustomerClaim,
            d.RaisedAt.ToString("O", CultureInfo.InvariantCulture), d.RaisedBy, d.Source, d.AssignedTo,
            d.FirstResponseDueAt.ToString("O", CultureInfo.InvariantCulture), d.FirstResponseAt?.ToString("O", CultureInfo.InvariantCulture),
            d.ResolutionDueAt.ToString("O", CultureInfo.InvariantCulture), d.PendingSince?.ToString("O", CultureInfo.InvariantCulture), breached, slaState,
            d.ResolvedAt?.ToString("O", CultureInfo.InvariantCulture), d.ResolvedBy, d.ResolutionAmount is { } r ? MoneyDto.From(r, d.Currency) : null, d.ResolutionNote, d.CreditNoteId, d.CloseReason,
            d.RowVersion, evidence, taskId);
    }

    private static DisputeEvidenceDto Evidence(DisputeEvidence e) => new(e.Id, e.FileName, e.ContentType, e.SizeBytes, e.Sha256, e.UploadedBy, e.UploadedAt.ToString("O", CultureInfo.InvariantCulture));

    private static ProblemHttpResult Invalid(HttpContext context, InvalidTransitionException ex) =>
        ApiProblems.Create(context, StatusCodes.Status409Conflict, "invalid_transition", $"No transition from {ex.From} on {ex.Event}.", "errors.invalid_transition",
            [new ApiProblems.FieldError("event", "invalid_transition", "errors.invalid_transition", new Dictionary<string, string> { ["from"] = ex.From, ["event"] = ex.Event })]);

    private static ProblemHttpResult Rule(HttpContext context, CaseException ex) => ex.Code switch
    {
        "invoice_not_found" or "dispute_not_found" or "case_not_found" or "task_not_found" or "member_not_found" or "payment_not_found" => ApiProblems.NotFoundProblem(context),
        "duplicate" => ApiProblems.Create(context, StatusCodes.Status409Conflict, "duplicate", "An open dispute already exists on this invoice.", "errors.duplicate_dispute"),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };
}
