using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 3, the payments block. Handlers parse and authorize; <see cref="LedgerService"/> is
/// where the money moves. A <see cref="LedgerException"/> becomes <c>422 business_rule_violated</c>
/// with the rule code, the field and the live figure (doc 05 §0.2 example).
/// </summary>
public static class LedgerEndpoints
{
    public static RouteGroupBuilder MapLedgerEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var payments = api.MapGroup("/payments");
        payments.MapPost("/", RecordPaymentAsync).RequiresPermission(Permissions.PaymentsWrite).WithName("RecordPayment");
        payments.MapGet("/", ListPaymentsAsync).RequiresPermission(Permissions.PaymentsRead).WithName("ListPayments");
        payments.MapGet("/{id:guid}", GetPaymentAsync).RequiresPermission(Permissions.PaymentsRead).WithName("GetPayment");
        payments.MapGet("/{id:guid}/allocation-proposal", ProposeAsync).RequiresPermission(Permissions.PaymentsAllocate).WithName("AllocationProposal");
        payments.MapPost("/{id:guid}/allocations", AllocateAsync).RequiresPermission(Permissions.PaymentsAllocate).WithName("Allocate");
        payments.MapPost("/{id:guid}/reverse", ReversePaymentAsync).RequiresPermission(Permissions.PaymentsWrite).WithName("ReversePayment");

        api.MapPost("/allocations/{id:guid}/reverse", ReverseAllocationAsync).RequiresPermission(Permissions.PaymentsAllocate).WithName("ReverseAllocation");

        var cheques = api.MapGroup("/cheques");
        cheques.MapPost("/", RecordChequeAsync).RequiresPermission(Permissions.PaymentsWrite).WithName("RecordCheque");
        cheques.MapGet("/", ListChequesAsync).RequiresPermission(Permissions.PaymentsRead).WithName("ListCheques");
        cheques.MapGet("/{id:guid}", GetChequeAsync).RequiresPermission(Permissions.PaymentsRead).WithName("GetCheque");
        cheques.MapPost("/{id:guid}/transitions", TransitionChequeAsync).RequiresPermission(Permissions.PaymentsWrite).WithName("TransitionCheque");

        api.MapPost("/invoices/{id:guid}/withholding", RecordWithholdingAsync).RequiresPermission(Permissions.PaymentsWrite).WithName("RecordWithholding");
        api.MapPost("/invoices/{id:guid}/write-off", ProposeWriteOffAsync).RequiresPermission(Permissions.WriteoffPropose).WithName("ProposeWriteOff");
        api.MapPost("/invoices/{id:guid}/void", VoidInvoiceAsync).RequiresPermission(Permissions.InvoicesVoid).WithName("VoidInvoice");

        var creditNotes = api.MapGroup("/credit-notes");
        creditNotes.MapPost("/", CreateCreditNoteAsync).RequiresPermission(Permissions.CreditNotesWrite).WithName("CreateCreditNote");
        creditNotes.MapGet("/", ListCreditNotesAsync).RequiresPermission(Permissions.PaymentsRead).WithName("ListCreditNotes");
        creditNotes.MapGet("/{id:guid}", GetCreditNoteAsync).RequiresPermission(Permissions.PaymentsRead).WithName("GetCreditNote");
        creditNotes.MapPost("/{id:guid}/applications", ApplyCreditNoteAsync).RequiresPermission(Permissions.CreditNotesWrite).WithName("ApplyCreditNote");
        creditNotes.MapPost("/{id:guid}/void", VoidCreditNoteAsync).RequiresPermission(Permissions.CreditNotesWrite).WithName("VoidCreditNote");

        var writeOffs = api.MapGroup("/write-offs");
        writeOffs.MapGet("/", ListWriteOffsAsync).RequiresPermission(Permissions.PaymentsRead).WithName("ListWriteOffs");
        writeOffs.MapGet("/{id:guid}", GetWriteOffAsync).RequiresPermission(Permissions.PaymentsRead).WithName("GetWriteOff");
        writeOffs.MapPost("/{id:guid}/approve", ApproveWriteOffAsync).RequiresPermission(Permissions.WriteoffApprove).RequiresReauth().WithName("ApproveWriteOff");   // SEC-09, slice 13 closes slice 3b D-6
        writeOffs.MapPost("/{id:guid}/reject", RejectWriteOffAsync).RequiresPermission(Permissions.WriteoffApprove).WithName("RejectWriteOff");
        writeOffs.MapPost("/{id:guid}/reverse", ReverseWriteOffAsync).RequiresPermission(Permissions.WriteoffApprove).WithName("ReverseWriteOff");

        return api;
    }

    // ---------------------------------------------------------------------------------------
    // Payments
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Created<PaymentResponse>, Ok<PaymentResponse>, ProblemHttpResult>> RecordPaymentAsync(PaymentRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        // API-08: every POST that creates money requires an Idempotency-Key.
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is 0 or > 200)
        {
            return ApiProblems.Create(context, StatusCodes.Status428PreconditionRequired, "idempotency_key_required",
                "POST /payments requires an Idempotency-Key header.", "errors.idempotency_key_required");
        }

        var requestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
        var existing = await db.Payments.FirstOrDefaultAsync(p => p.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            // A replay returns the original; the same key with a different body is a client bug.
            return existing.RequestHash == requestHash
                ? TypedResults.Ok(await ToResponseAsync(existing, db, ct))
                : ApiProblems.Create(context, StatusCodes.Status409Conflict, "idempotency_key_reused",
                    "This Idempotency-Key was already used with a different request.", "errors.idempotency_key_reused");
        }

        var validation = new Validation().Currency("amount.currency", request.Amount?.Currency);
        if (request.CustomerId is null) validation.Require("customerId", null);
        var amount = 0m;
        if (request.Amount is null || !request.Amount.TryParse(out amount) || amount <= 0m) validation.Require("amount", null, "invalid_amount");
        if (request.Method is null || !Enum.TryParse<PaymentMethod>(request.Method, out var method)) { validation.Require("method", null, "invalid"); method = PaymentMethod.Other; }
        if (!TryDate(request.ReceivedDate, out var receivedDate)) validation.Require("receivedDate", null, "invalid_date");
        if (request.EffectiveDate is not null && !TryDate(request.EffectiveDate, out _)) validation.Require("effectiveDate", null, "invalid_date");
        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var lines = ParseLines(request.Allocations, context, out var lineProblem);
        if (lineProblem is not null)
        {
            return lineProblem;
        }

        try
        {
            var payment = await ledger.RecordPaymentAsync(new Payment
            {
                TenantId = user.TenantId,
                CustomerId = request.CustomerId!.Value,
                Amount = amount,
                Currency = request.Amount!.Currency!,
                Method = method,
                ReceivedDate = receivedDate,
                EffectiveDate = request.EffectiveDate is null ? receivedDate : DateOnly.ParseExact(request.EffectiveDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Reference = request.Reference,
                Notes = request.Notes,
                IdempotencyKey = key,
                RequestHash = requestHash,
            }, user.UserId, ct);

            if (lines.Count > 0)
            {
                await ledger.AllocateAsync(payment, lines, AllocationMethod.Manual, user.UserId, ct);
            }

            return TypedResults.Created($"/api/v1/payments/{payment.Id}", await ToResponseAsync(payment, db, ct));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<PaymentListResponse>, ProblemHttpResult>> ListPaymentsAsync(TenantDbContext db, CancellationToken ct, Guid? customerId = null, int limit = 50, Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        var query = db.Payments.AsQueryable();
        if (customerId is not null) query = query.Where(p => p.CustomerId == customerId);
        var total = await query.CountAsync(ct);
        if (cursor is not null) query = query.Where(p => p.Id.CompareTo(cursor.Value) < 0);
        var page = await query.OrderByDescending(p => p.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var items = new List<PaymentResponse>();
        foreach (var p in hasMore ? page[..pageSize] : page) items.Add(await ToResponseAsync(p, db, ct));
        return TypedResults.Ok(new PaymentListResponse(items, hasMore ? items[^1].Id.ToString() : null, total));
    }

    private static async Task<Results<Ok<PaymentResponse>, ProblemHttpResult>> GetPaymentAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);
        return payment is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await ToResponseAsync(payment, db, ct));
    }

    private static async Task<Results<Ok<AllocationProposalResponse>, ProblemHttpResult>> ProposeAsync(Guid id, HttpContext context, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null) return ApiProblems.NotFoundProblem(context);

        var lines = await ledger.ProposeAsync(payment, ct);
        var invoices = await db.Invoices.Where(i => lines.Select(l => l.InvoiceId).Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var allocated = await db.PaymentAllocations.Where(a => a.PaymentId == id && a.IsActive).SumAsync(a => a.Amount, ct);
        var available = payment.Amount - allocated;

        return TypedResults.Ok(new AllocationProposalResponse(
            MoneyDto.From(available, payment.Currency),
            lines.Select(l => new ProposalLineDto(l.InvoiceId, invoices[l.InvoiceId].InvoiceNumber, Iso(invoices[l.InvoiceId].DueDate),
                MoneyDto.From(invoices[l.InvoiceId].BalanceCache, payment.Currency), MoneyDto.From(l.Amount, payment.Currency))).ToList(),
            MoneyDto.From(available - lines.Sum(l => l.Amount), payment.Currency)));
    }

    private static async Task<Results<Ok<AllocationResultResponse>, ProblemHttpResult>> AllocateAsync(Guid id, AllocationRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null) return ApiProblems.NotFoundProblem(context);

        var lines = ParseLines(request.Lines, context, out var lineProblem);
        if (lineProblem is not null) return lineProblem;
        if (lines.Count == 0) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("lines", "required", "errors.lines.required")]);

        try
        {
            var result = await ledger.AllocateAsync(payment, lines, AllocationMethod.Manual, user.UserId, ct);
            return TypedResults.Ok(await ToResponseAsync(result, db, ct));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<PaymentResponse>, ProblemHttpResult>> ReversePaymentAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.reason.required")]);

        try
        {
            await ledger.ReversePaymentAsync(payment, request.Reason.Trim(), user.UserId, ct);
            return TypedResults.Ok(await ToResponseAsync(payment, db, ct));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<AllocationDto>, ProblemHttpResult>> ReverseAllocationAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var allocation = await db.PaymentAllocations.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (allocation is null) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.reason.required")]);

        try
        {
            var reversal = await ledger.ReverseAllocationAsync(allocation, request.Reason.Trim(), user.UserId, ct);
            return TypedResults.Ok(ToDto(reversal));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Cheques
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Created<ChequeResponse>, ProblemHttpResult>> RecordChequeAsync(ChequeRequest request, HttpContext context, CurrentUser user, LedgerService ledger, CancellationToken ct)
    {
        var validation = new Validation().Require("chequeNumber", request.ChequeNumber).MaxLength("chequeNumber", request.ChequeNumber, 50).Currency("amount.currency", request.Amount?.Currency);
        if (request.CustomerId is null) validation.Require("customerId", null);
        var amount = 0m;
        if (request.Amount is null || !request.Amount.TryParse(out amount) || amount <= 0m) validation.Require("amount", null, "invalid_amount");
        if (!TryDate(request.ChequeDate, out var chequeDate)) validation.Require("chequeDate", null, "invalid_date");
        if (!TryDate(request.ReceivedDate, out var receivedDate)) validation.Require("receivedDate", null, "invalid_date");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        try
        {
            var cheque = await ledger.RecordChequeAsync(new Cheque
            {
                TenantId = user.TenantId,
                CustomerId = request.CustomerId!.Value,
                ChequeNumber = request.ChequeNumber!.Trim(),
                BankName = request.BankName,
                Amount = amount,
                Currency = request.Amount!.Currency!,
                ChequeDate = chequeDate,
                ReceivedDate = receivedDate,
                Notes = request.Notes,
            }, user.UserId, ct);
            return TypedResults.Created($"/api/v1/cheques/{cheque.Id}", ToDto(cheque));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<ChequeListResponse>, ProblemHttpResult>> ListChequesAsync(TenantDbContext db, CancellationToken ct, Guid? customerId = null, string? status = null, int limit = 50, Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        var query = db.Cheques.AsQueryable();
        if (customerId is not null) query = query.Where(c => c.CustomerId == customerId);
        if (status is not null && Enum.TryParse<ChequeStatus>(status, true, out var s)) query = query.Where(c => c.Status == s);
        var total = await query.CountAsync(ct);
        if (cursor is not null) query = query.Where(c => c.Id.CompareTo(cursor.Value) < 0);
        var page = await query.OrderByDescending(c => c.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page[..pageSize] : page).Select(ToDto).ToList();
        return TypedResults.Ok(new ChequeListResponse(items, hasMore ? items[^1].Id.ToString() : null, total));
    }

    private static async Task<Results<Ok<ChequeResponse>, ProblemHttpResult>> GetChequeAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var cheque = await db.Cheques.FirstOrDefaultAsync(c => c.Id == id, ct);
        return cheque is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(ToDto(cheque));
    }

    private static async Task<Results<Ok<ChequeTransitionResponse>, ProblemHttpResult>> TransitionChequeAsync(Guid id, ChequeTransitionRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var cheque = await db.Cheques.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cheque is null) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Event)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("event", "required", "errors.event.required")]);

        var lines = ParseLines(request.Allocations, context, out var lineProblem);
        if (lineProblem is not null) return lineProblem;

        try
        {
            var result = await ledger.TransitionChequeAsync(cheque, request.Event.Trim().ToLowerInvariant(), request.Reason?.Trim(), lines, user.UserId, ct);
            return TypedResults.Ok(new ChequeTransitionResponse(
                ToDto(result.Cheque),
                result.Payment is null ? null : await ToResponseAsync(result.Payment, db, ct),
                result.Allocation is null ? null : await ToResponseAsync(result.Allocation, db, ct)));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Withholding, write-off, void (on an invoice)
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Created<WithholdingDto>, ProblemHttpResult>> RecordWithholdingAsync(Guid id, WithholdingRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return ApiProblems.NotFoundProblem(context);

        var validation = new Validation();
        var baseAmount = 0m;
        var withheld = 0m;
        if (request.BaseAmount is null || !request.BaseAmount.TryParse(out baseAmount) || baseAmount <= 0m) validation.Require("baseAmount", null, "invalid_amount");
        if (request.WithheldAmount is null || !request.WithheldAmount.TryParse(out withheld) || withheld <= 0m) validation.Require("withheldAmount", null, "invalid_amount");
        if (!decimal.TryParse(request.RatePct, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) || rate <= 0m || rate >= 100m || rate.Scale > 2) validation.Require("ratePct", null, "invalid_rate");
        if (request.WithheldAmount?.Currency is not null && request.WithheldAmount.Currency != invoice.Currency) validation.Require("withheldAmount.currency", null, "currency_mismatch");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        try
        {
            var deduction = await ledger.RecordWithholdingAsync(new WithholdingDeduction
            {
                InvoiceId = invoice.Id,
                PaymentId = request.PaymentId,
                BaseAmount = baseAmount,
                RatePct = rate,
                WithheldAmount = withheld,
                Currency = invoice.Currency,
                CertificateReference = request.CertificateReference,
                CertificateReceived = request.CertificateReceived ?? false,
            }, user.UserId, ct);
            return TypedResults.Created($"/api/v1/invoices/{id}", ToDto(deduction));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Created<WriteOffResponse>, ProblemHttpResult>> ProposeWriteOffAsync(Guid id, WriteOffProposeRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        if (!await db.Invoices.AnyAsync(i => i.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.ReasonCode)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reasonCode", "required", "errors.reasonCode.required")]);

        try
        {
            var writeOff = await ledger.ProposeWriteOffAsync(id, request.ReasonCode, request.Note, user.UserId, ct);
            return TypedResults.Created($"/api/v1/write-offs/{writeOff.Id}", ToDto(writeOff));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<InvoiceResponse>, ProblemHttpResult>> VoidInvoiceAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        if (!await db.Invoices.AnyAsync(i => i.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.reason.required")]);

        try
        {
            var invoice = await ledger.VoidInvoiceAsync(id, request.Reason.Trim(), user.UserId, ct);
            return TypedResults.Ok(ImportEndpoints.ToResponse(invoice));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Credit notes
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Created<CreditNoteResponse>, ProblemHttpResult>> CreateCreditNoteAsync(CreditNoteRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var validation = new Validation().Currency("amount.currency", request.Amount?.Currency).Require("reasonCode", request.ReasonCode);
        if (request.CustomerId is null) validation.Require("customerId", null);
        var amount = 0m;
        if (request.Amount is null || !request.Amount.TryParse(out amount) || amount <= 0m) validation.Require("amount", null, "invalid_amount");
        if (!TryDate(request.IssueDate, out var issueDate)) validation.Require("issueDate", null, "invalid_date");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        var lines = ParseLines(request.Applications, context, out var lineProblem);
        if (lineProblem is not null) return lineProblem;

        try
        {
            var note = await ledger.CreateCreditNoteAsync(new CreditNote
            {
                TenantId = user.TenantId,
                CustomerId = request.CustomerId!.Value,
                NoteNumber = request.NoteNumber,
                Amount = amount,
                Currency = request.Amount!.Currency!,
                IssueDate = issueDate,
                ReasonCode = request.ReasonCode!.Trim(),
                Notes = request.Notes,
            }, user.UserId, ct);

            if (lines.Count > 0)
            {
                await ledger.ApplyCreditNoteAsync(note, lines, user.UserId, ct);
            }

            return TypedResults.Created($"/api/v1/credit-notes/{note.Id}", await ToResponseAsync(note, db, ct));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<CreditNoteListResponse>, ProblemHttpResult>> ListCreditNotesAsync(TenantDbContext db, CancellationToken ct, Guid? customerId = null, int limit = 50, Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        var query = db.CreditNotes.AsQueryable();
        if (customerId is not null) query = query.Where(n => n.CustomerId == customerId);
        var total = await query.CountAsync(ct);
        if (cursor is not null) query = query.Where(n => n.Id.CompareTo(cursor.Value) < 0);
        var page = await query.OrderByDescending(n => n.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var items = new List<CreditNoteResponse>();
        foreach (var n in hasMore ? page[..pageSize] : page) items.Add(await ToResponseAsync(n, db, ct));
        return TypedResults.Ok(new CreditNoteListResponse(items, hasMore ? items[^1].Id.ToString() : null, total));
    }

    private static async Task<Results<Ok<CreditNoteResponse>, ProblemHttpResult>> GetCreditNoteAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var note = await db.CreditNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
        return note is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await ToResponseAsync(note, db, ct));
    }

    private static async Task<Results<Ok<CreditNoteResponse>, ProblemHttpResult>> ApplyCreditNoteAsync(Guid id, AllocationRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var note = await db.CreditNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null) return ApiProblems.NotFoundProblem(context);

        var lines = ParseLines(request.Lines, context, out var lineProblem);
        if (lineProblem is not null) return lineProblem;
        if (lines.Count == 0) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("lines", "required", "errors.lines.required")]);

        try
        {
            await ledger.ApplyCreditNoteAsync(note, lines, user.UserId, ct);
            return TypedResults.Ok(await ToResponseAsync(note, db, ct));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<CreditNoteResponse>, ProblemHttpResult>> VoidCreditNoteAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var note = await db.CreditNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.reason.required")]);

        try
        {
            await ledger.VoidCreditNoteAsync(note, request.Reason.Trim(), user.UserId, ct);
            return TypedResults.Ok(await ToResponseAsync(note, db, ct));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Write-offs
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Ok<WriteOffListResponse>, ProblemHttpResult>> ListWriteOffsAsync(TenantDbContext db, CancellationToken ct, string? status = null, int limit = 50, Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        var query = db.WriteOffs.AsQueryable();
        if (status is not null && Enum.TryParse<WriteOffStatus>(status, true, out var s)) query = query.Where(w => w.Status == s);
        var total = await query.CountAsync(ct);
        if (cursor is not null) query = query.Where(w => w.Id.CompareTo(cursor.Value) < 0);
        var page = await query.OrderByDescending(w => w.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page[..pageSize] : page).Select(ToDto).ToList();
        return TypedResults.Ok(new WriteOffListResponse(items, hasMore ? items[^1].Id.ToString() : null, total));
    }

    private static async Task<Results<Ok<WriteOffResponse>, ProblemHttpResult>> GetWriteOffAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var writeOff = await db.WriteOffs.FirstOrDefaultAsync(w => w.Id == id, ct);
        return writeOff is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(ToDto(writeOff));
    }

    private static async Task<Results<Ok<WriteOffResponse>, ProblemHttpResult>> ApproveWriteOffAsync(Guid id, WriteOffApproveRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var writeOff = await db.WriteOffs.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (writeOff is null) return ApiProblems.NotFoundProblem(context);

        try
        {
            // SEC-09 re-authentication is deferred with MFA (slice 1b, D-6).
            return TypedResults.Ok(ToDto(await ledger.ApproveWriteOffAsync(writeOff, request.SelfApproved ?? false, user.UserId, ct)));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<WriteOffResponse>, ProblemHttpResult>> RejectWriteOffAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var writeOff = await db.WriteOffs.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (writeOff is null) return ApiProblems.NotFoundProblem(context);

        try
        {
            return TypedResults.Ok(ToDto(await ledger.RejectWriteOffAsync(writeOff, request.Reason, user.UserId, ct)));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    private static async Task<Results<Ok<WriteOffResponse>, ProblemHttpResult>> ReverseWriteOffAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, LedgerService ledger, CancellationToken ct)
    {
        var writeOff = await db.WriteOffs.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (writeOff is null) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Reason)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("reason", "required", "errors.reason.required")]);

        try
        {
            return TypedResults.Ok(ToDto(await ledger.ReverseWriteOffAsync(writeOff, request.Reason.Trim(), user.UserId, ct)));
        }
        catch (LedgerException ex)
        {
            return Rule(context, ex);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Invoice detail: settlement facet and money history
    // ---------------------------------------------------------------------------------------

    public static async Task<InvoiceDetailResponse> InvoiceDetailAsync(Invoice invoice, TenantDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(db);

        var allocations = await db.PaymentAllocations.Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        var payments = await db.Payments.Where(p => allocations.Select(a => a.PaymentId).Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var applications = await db.CreditNoteApplications.Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        var notes = await db.CreditNotes.Where(n => applications.Select(a => a.CreditNoteId).Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct);
        var withholding = await db.WithholdingDeductions.Where(w => w.InvoiceId == invoice.Id).ToListAsync(ct);
        var writeOffs = await db.WriteOffs.Where(w => w.InvoiceId == invoice.Id).ToListAsync(ct);

        var history = new List<MoneyHistoryEntry>();
        history.AddRange(allocations.Select(a => new MoneyHistoryEntry(
            a.ReversalOfId is null ? "allocation" : "allocation_reversal", a.Id, Iso(a.EffectiveDate), MoneyDto.From(a.Amount, a.Currency),
            a.ReversalOfId is null ? "reduces" : "restores", a.IsActive, payments.GetValueOrDefault(a.PaymentId)?.Reference, a.ReversalReason, a.AllocatedBy, a.CreatedAt)));
        history.AddRange(applications.Select(a => new MoneyHistoryEntry(
            a.ReversalOfId is null ? "credit_application" : "credit_application_reversal", a.Id, Iso(a.EffectiveDate), MoneyDto.From(a.Amount, a.Currency),
            a.ReversalOfId is null ? "reduces" : "restores", a.IsActive, notes.GetValueOrDefault(a.CreditNoteId)?.NoteNumber, notes.GetValueOrDefault(a.CreditNoteId)?.ReasonCode ?? a.ReversalReason, a.AppliedBy, a.CreatedAt)));
        history.AddRange(withholding.Select(w => new MoneyHistoryEntry(
            "withholding", w.Id, Iso(DateOnly.FromDateTime(w.CreatedAt.UtcDateTime)), MoneyDto.From(w.WithheldAmount, w.Currency), "reduces", w.IsActive, w.CertificateReference, w.RatePct.ToString("0.##", CultureInfo.InvariantCulture) + "%", w.CreatedBy, w.CreatedAt)));
        history.AddRange(writeOffs.Where(w => w.Status is WriteOffStatus.Approved or WriteOffStatus.Reversed).Select(w => new MoneyHistoryEntry(
            "write_off", w.Id, Iso(DateOnly.FromDateTime((w.ApprovedAt ?? w.ProposedAt).UtcDateTime)), MoneyDto.From(w.Amount, w.Currency),
            w.Status == WriteOffStatus.Approved ? "reduces" : "restores", w.Status == WriteOffStatus.Approved, null, w.ReasonCode, w.ApprovedBy, w.ApprovedAt ?? w.ProposedAt)));

        return new InvoiceDetailResponse(
            ImportEndpoints.ToResponse(invoice),
            invoice.Settlement.ToString(),
            history.OrderBy(h => h.RecordedAt).ToList(),
            withholding.Select(ToDto).ToList(),
            writeOffs.Select(ToDto).ToList());
    }

    // ---------------------------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyList<LedgerService.AllocationLine> ParseLines(IReadOnlyList<AllocationLineInput>? inputs, HttpContext context, out ProblemHttpResult? problem)
    {
        problem = null;
        var lines = new List<LedgerService.AllocationLine>();
        if (inputs is null) return lines;

        var errors = new List<ApiProblems.FieldError>();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].InvoiceId is null) errors.Add(new ApiProblems.FieldError($"lines[{i}].invoiceId", "required", "errors.lines.invoiceId.required"));
            if (inputs[i].Amount is null || !inputs[i].Amount!.TryParse(out var amount))
            {
                errors.Add(new ApiProblems.FieldError($"lines[{i}].amount", "invalid_amount", "errors.lines.amount.invalid_amount"));
                continue;
            }

            if (inputs[i].InvoiceId is { } invoiceId) lines.Add(new LedgerService.AllocationLine(invoiceId, amount));
        }

        if (errors.Count > 0) problem = ApiProblems.ValidationProblem(context, errors);
        return lines;
    }

    private static ProblemHttpResult Rule(HttpContext context, LedgerException ex) => ex.Code switch
    {
        "invoice_not_found" or "customer_not_found" => ApiProblems.NotFoundProblem(context),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };

    private static bool TryDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static async Task<PaymentResponse> ToResponseAsync(Payment p, TenantDbContext db, CancellationToken ct)
    {
        var allocations = await db.PaymentAllocations.Where(a => a.PaymentId == p.Id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var allocated = allocations.Where(a => a.IsActive).Sum(a => a.Amount);
        return new PaymentResponse(p.Id, p.CustomerId, MoneyDto.From(p.Amount, p.Currency), p.Currency, p.Method.ToString(), Iso(p.ReceivedDate), Iso(p.EffectiveDate),
            p.Reference, p.Status.ToString(), p.ChequeId, p.Notes, MoneyDto.From(p.Status == PaymentStatus.Reversed ? 0m : p.Amount - allocated, p.Currency),
            allocations.Select(ToDto).ToList(), p.CreatedAt, p.RowVersion.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<AllocationResultResponse> ToResponseAsync(LedgerService.AllocationResult r, TenantDbContext db, CancellationToken ct) =>
        new(await ToResponseAsync(r.Payment, db, ct), MoneyDto.From(r.Unallocated, r.Payment.Currency),
            r.Invoices.Select(i => new InvoiceResidualDto(i.InvoiceId, MoneyDto.From(i.OpenBalance, r.Payment.Currency), i.Settlement.ToString(),
                i.ProposedRoundingAdjustment is { } p ? MoneyDto.From(p, r.Payment.Currency) : null)).ToList());

    private static async Task<CreditNoteResponse> ToResponseAsync(CreditNote n, TenantDbContext db, CancellationToken ct)
    {
        var applications = await db.CreditNoteApplications.Where(a => a.CreditNoteId == n.Id).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var applied = applications.Where(a => a.IsActive).Sum(a => a.Amount);
        return new CreditNoteResponse(n.Id, n.CustomerId, n.NoteNumber, MoneyDto.From(n.Amount, n.Currency), n.Currency, Iso(n.IssueDate), n.ReasonCode, n.Status.ToString(),
            MoneyDto.From(n.Status == CreditNoteStatus.Void ? 0m : n.Amount - applied, n.Currency),
            applications.Select(a => new CreditApplicationDto(a.Id, a.CreditNoteId, a.InvoiceId, MoneyDto.From(a.Amount, a.Currency), Iso(a.EffectiveDate), a.IsActive, a.ReversalOfId, a.ReversalReason, a.CreatedAt, a.AppliedBy)).ToList(),
            n.Notes, n.CreatedAt, n.RowVersion.ToString(CultureInfo.InvariantCulture));
    }

    private static AllocationDto ToDto(PaymentAllocation a) => new(a.Id, a.PaymentId, a.InvoiceId, MoneyDto.From(a.Amount, a.Currency), Iso(a.EffectiveDate), a.IsActive, a.ReversalOfId, a.ReversalReason, a.Method.ToString(), a.CreatedAt, a.AllocatedBy);

    private static ChequeResponse ToDto(Cheque c) => new(c.Id, c.CustomerId, c.ChequeNumber, c.BankName, MoneyDto.From(c.Amount, c.Currency), Iso(c.ChequeDate), Iso(c.ReceivedDate), c.IsPostDated, c.Status.ToString(), c.BouncedReason, c.ClearedDate is { } d ? Iso(d) : null, c.PaymentId, c.PtpId, c.Notes, c.RowVersion.ToString(CultureInfo.InvariantCulture));

    private static WithholdingDto ToDto(WithholdingDeduction w) => new(w.Id, w.InvoiceId, w.PaymentId, MoneyDto.From(w.BaseAmount, w.Currency), w.RatePct.ToString("0.##", CultureInfo.InvariantCulture), MoneyDto.From(w.WithheldAmount, w.Currency), w.CertificateReference, w.CertificateReceived, w.IsActive, w.CreatedAt, w.CreatedBy);

    private static WriteOffResponse ToDto(WriteOff w) => new(w.Id, w.InvoiceId, MoneyDto.From(w.Amount, w.Currency), w.ReasonCode, w.Note, w.Status.ToString(), w.ProposedBy, w.ProposedAt, w.ApprovedBy, w.SelfApproved, w.ApprovedAt, w.RejectedBy, w.ReversedBy, w.ReversalReason, w.RowVersion.ToString(CultureInfo.InvariantCulture));
}
