using System.Globalization;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Messaging;
using FinanceAi.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 8. Templates behind <c>templates.write</c>, drafting behind <c>messages.draft</c>, the human gate
/// behind <c>ai.suggestions.approve</c>, the click that queues behind <c>messages.send</c>. The kill switch and
/// the cap live under the organization's settings. No route sends without a human somewhere on the row.
/// </summary>
public static class MessagingEndpoints
{
    public static RouteGroupBuilder MapMessagingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var templates = api.MapGroup("/templates");
        templates.MapGet("/", ListTemplatesAsync).Produces<TemplateListResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("ListTemplates");
        templates.MapGet("/placeholders", PlaceholdersAsync).Produces<PlaceholderListResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("Placeholders");
        templates.MapGet("/{id:guid}", GetTemplateAsync).Produces<TemplateResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("GetTemplate");
        templates.MapPost("/", CreateTemplateAsync).Produces<TemplateResponse>(201).RequiresPermission(Permissions.TemplatesWrite).WithName("CreateTemplate");
        templates.MapPost("/{id:guid}", NewVersionAsync).Produces<TemplateResponse>(201).RequiresPermission(Permissions.TemplatesWrite).WithName("NewTemplateVersion");
        templates.MapPost("/{id:guid}/approve", ApproveTemplateAsync).Produces<TemplateResponse>(200).RequiresPermission(Permissions.TemplatesWrite).WithName("ApproveTemplate");
        templates.MapPost("/{id:guid}/preview", PreviewAsync).Produces<TemplatePreviewResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("PreviewTemplate");

        api.MapPost("/cases/{id:guid}/messages", ComposeAsync).Produces<MessageResponse>(201).RequiresPermission(Permissions.MessagesDraft).WithName("ComposeMessage");
        var messages = api.MapGroup("/messages");
        messages.MapGet("/", ListMessagesAsync).Produces<MessageListResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("ListMessages");
        messages.MapPost("/dispatch", DispatchAsync).Produces<DispatchResponse>(200).RequiresPermission(Permissions.MessagesSend).WithName("DispatchMessages");
        messages.MapGet("/{id:guid}", GetMessageAsync).Produces<MessageResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("GetMessage");
        messages.MapPost("/{id:guid}/approve", ApproveMessageAsync).Produces<MessageResponse>(200).RequiresPermission(Permissions.AiSuggestionsApprove).WithName("ApproveMessage");
        messages.MapPost("/{id:guid}/send", SendAsync).Produces<MessageResponse>(200).RequiresPermission(Permissions.MessagesSend).WithName("SendMessage");
        messages.MapPost("/{id:guid}/cancel", CancelAsync).Produces<MessageResponse>(200).RequiresPermission(Permissions.MessagesDraft).WithName("CancelMessage");
        messages.MapGet("/{id:guid}/whatsapp-link", WhatsAppLinkAsync).Produces<WhatsAppLinkResponse>(200).RequiresPermission(Permissions.MessagesDraft).WithName("WhatsAppLink");
        messages.MapPost("/{id:guid}/confirm-manual-send", ConfirmManualSendAsync).Produces<MessageResponse>(200).RequiresPermission(Permissions.MessagesDraft).WithName("ConfirmManualSend");

        api.MapGet("/organization/outbound", OutboundSettingsAsync).Produces<OutboundSettingsResponse>(200).RequiresPermission(Permissions.TenantRead).WithName("OutboundSettings");
        api.MapPut("/organization/outbound", UpdateOutboundSettingsAsync).Produces<OutboundSettingsResponse>(200).RequiresPermission(Permissions.TenantSettingsWrite).WithName("UpdateOutboundSettings");
        api.MapGet("/customers/{id:guid}/statement", StatementAsync).Produces<StatementResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("CustomerStatement");

        return api;
    }

    // ---------------------------------------------------------------------------------------
    // Templates
    // ---------------------------------------------------------------------------------------

    private static IResult PlaceholdersAsync() => TypedResults.Ok(new PlaceholderListResponse(Placeholders.All.Select(p => new PlaceholderDto(p.Name, p.Type, p.Description)).ToList()));

    private static async Task<IResult> ListTemplatesAsync(HttpContext context, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        await messaging.EnsureSystemTemplatesAsync(ct);
        var q = context.Request.Query;
        var query = db.Templates.Where(t => t.DeletedAt == null);
        if (q["channel"].ToString() is { Length: > 0 } channel) query = query.Where(t => t.Channel == channel);
        if (q["language"].ToString() is { Length: > 0 } language) query = query.Where(t => t.Language == language);
        if (q["key"].ToString() is { Length: > 0 } key) query = query.Where(t => t.Key == key);
        if (q["all"].ToString() != "true") query = query.Where(t => t.IsActive);
        var items = await query.OrderBy(t => t.Key).ThenBy(t => t.Channel).ThenBy(t => t.Language).ThenByDescending(t => t.Version).ToListAsync(ct);
        return TypedResults.Ok(new TemplateListResponse(items.Select(Template).ToList(), items.Count));
    }

    private static async Task<IResult> GetTemplateAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        return t is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(Template(t));
    }

    private static async Task<IResult> CreateTemplateAsync(TemplateRequest request, HttpContext context, CurrentUser user, MessagingService messaging, CancellationToken ct)
    {
        var validation = new Validation().Require("key", request.Key).Require("channel", request.Channel).Require("language", request.Language).Require("body", request.Body);
        if (request.Key is not null && !System.Text.RegularExpressions.Regex.IsMatch(request.Key, "^[a-z][a-z0-9_]{1,60}$")) validation.Require("key", null, "invalid");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        try
        {
            return TypedResults.Created("/api/v1/templates", Template(await messaging.CreateTemplateAsync(request.Key!, request.Channel!, request.Language!, request.Tone ?? TemplateTones.Polite, request.Subject, request.Body!, user.UserId, ct)));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> NewVersionAsync(Guid id, TemplateVersionRequest request, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Templates.AnyAsync(t => t.Id == id && t.DeletedAt == null, ct)) return ApiProblems.NotFoundProblem(context);
        if (string.IsNullOrWhiteSpace(request.Body)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("body", "required", "errors.validation.body.required")]);
        try
        {
            return TypedResults.Created("/api/v1/templates", Template(await messaging.NewVersionAsync(id, request.Tone, request.Subject, request.Body, user.UserId, ct)));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> ApproveTemplateAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Templates.AnyAsync(t => t.Id == id && t.DeletedAt == null, ct)) return ApiProblems.NotFoundProblem(context);
        try { return TypedResults.Ok(Template(await messaging.ApproveTemplateAsync(id, user.UserId, ct))); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> PreviewAsync(Guid id, TemplatePreviewRequest request, HttpContext context, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        var t = await db.Templates.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (t is null) return ApiProblems.NotFoundProblem(context);
        if (request.CaseId is null) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("caseId", "required", "errors.validation.caseId.required")]);
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == request.CaseId, ct);
        if (c is null) return ApiProblems.NotFoundProblem(context);
        var scope = await db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId).ToListAsync(ct);
        var invoiceIds = request.InvoiceIds is { Count: > 0 } ? request.InvoiceIds.Where(scope.Contains).ToList() : scope;
        var rendered = MessagingService.Render(t.Subject, t.Body, await messaging.ContextForAsync(c, invoiceIds, t.Language, null, ct));
        return TypedResults.Ok(new TemplatePreviewResponse(t.Language, rendered.Subject, rendered.Body, rendered.Context.InvoiceNumbers, MoneyDto.From(rendered.Context.AmountDue, rendered.Context.Currency)));
    }

    // ---------------------------------------------------------------------------------------
    // Messages
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> ComposeAsync(Guid id, ComposeMessageRequest request, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var validation = new Validation().Require("channel", request.Channel);
        if (request.TemplateId is null && string.IsNullOrWhiteSpace(request.Body)) validation.Require("body", null);
        if (request.Body is { Length: > 10000 }) validation.Require("body", null, "too_long");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        try
        {
            var m = await messaging.ComposeAsync(new MessagingService.ComposeInput(id, request.Channel!, request.Language, request.TemplateId, request.Subject, request.Body, request.InvoiceIds, request.ContactId), user.UserId, false, null, ct);
            return TypedResults.Created($"/api/v1/messages/{m.Id}", await MessageAsync(m, db, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> ListMessagesAsync(HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var q = context.Request.Query;
        var query = db.Messages.AsQueryable();
        if (q["status"].ToString() is { Length: > 0 } statusText)
        {
            if (!Enum.TryParse<MessageStatus>(statusText, out var status)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("status", "invalid", "errors.validation.status.invalid")]);
            query = query.Where(m => m.Status == status);
        }

        if (Guid.TryParse(q["customerId"], out var customerId)) query = query.Where(m => m.CustomerId == customerId);
        if (Guid.TryParse(q["caseId"], out var caseId)) query = query.Where(m => m.CaseId == caseId);
        var items = await query.OrderByDescending(m => m.CreatedAt).Take(500).ToListAsync(ct);
        var shaped = new List<MessageResponse>();
        foreach (var m in items) shaped.Add(await MessageAsync(m, db, ct));
        return TypedResults.Ok(new MessageListResponse(shaped, shaped.Count));
    }

    private static async Task<IResult> GetMessageAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Id == id, ct);
        return m is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await MessageAsync(m, db, ct));
    }

    private static async Task<IResult> ApproveMessageAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Messages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try { return TypedResults.Ok(await MessageAsync(await messaging.ApproveAsync(id, user.UserId, ct), db, ct)); }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> SendAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Messages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is 0 or > 200)
        {
            return ApiProblems.Create(context, StatusCodes.Status428PreconditionRequired, "idempotency_key_required", "POST /messages/{id}/send requires an Idempotency-Key header.", "errors.idempotency_key_required");
        }

        var existing = await db.Messages.FirstOrDefaultAsync(m => m.IdempotencyKey == key, ct);
        if (existing is not null)
        {
            return existing.Id == id
                ? TypedResults.Ok(await MessageAsync(existing, db, ct))
                : ApiProblems.Create(context, StatusCodes.Status409Conflict, "idempotency_key_reused", "This Idempotency-Key was already used for another message.", "errors.idempotency_key_reused");
        }

        try { return TypedResults.Ok(await MessageAsync(await messaging.SendAsync(id, user.UserId, key, ct), db, ct)); }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> CancelAsync(Guid id, ReasonRequest request, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Messages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try { return TypedResults.Ok(await MessageAsync(await messaging.CancelAsync(id, request.Reason ?? "cancelled", user.UserId, ct), db, ct)); }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> WhatsAppLinkAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Messages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try
        {
            var (m, link) = await messaging.WhatsAppLinkAsync(id, user.UserId, ct);
            return TypedResults.Ok(new WhatsAppLinkResponse(m.Id, link, m.Body, m.Status.ToString(),
                "The system does not send this message. Open the link, send it from your own WhatsApp, then confirm here."));
        }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> ConfirmManualSendAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, MessagingService messaging, CancellationToken ct)
    {
        if (!await db.Messages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try { return TypedResults.Ok(await MessageAsync(await messaging.ConfirmManualSendAsync(id, user.UserId, ct), db, ct)); }
        catch (InvalidTransitionException ex) { return Invalid(context, ex); }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> DispatchAsync(MessagingService messaging, CancellationToken ct)
    {
        var r = await messaging.DispatchAsync(ct);
        return TypedResults.Ok(new DispatchResponse(r.Sent, r.Failed, r.Skipped, r.SkipReason));
    }

    // ---------------------------------------------------------------------------------------
    // Outbound settings (SEC-103 / SEC-86) and the statement
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> OutboundSettingsAsync(TenantDbContext db, CaseService cases, CancellationToken ct)
    {
        var s = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        var ctx = await cases.ContextAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(ctx.Timezone);
        var dayStart = new DateTimeOffset(ctx.Today.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(ctx.Today.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var sentToday = await db.Messages.CountAsync(m => m.SentAt != null && m.SentAt >= dayStart && m.Channel == MessageChannels.Email, ct);
        return TypedResults.Ok(new OutboundSettingsResponse(s.OutboundSendingEnabled, OutboundSwitch.GloballyEnabled, s.DailySendCap, sentToday, s.RequireApprovalBeforeSend,
            s.QuietHoursStart.ToString("HH:mm", CultureInfo.InvariantCulture), s.QuietHoursEnd.ToString("HH:mm", CultureInfo.InvariantCulture), s.DunningCadenceDays));
    }

    private static async Task<IResult> UpdateOutboundSettingsAsync(OutboundSettingsRequest request, HttpContext context, CurrentUser user, TenantDbContext db, CaseService cases, FinanceAi.Infrastructure.Audit.IAuditWriter audit, CancellationToken ct)
    {
        if (request.DailySendCap is < 0) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("dailySendCap", "invalid", "errors.validation.dailySendCap.invalid")]);
        var s = await db.TenantSettings.FirstAsync(ct);
        var before = new { s.OutboundSendingEnabled, s.DailySendCap };
        if (request.OutboundSendingEnabled is { } enabled) s.OutboundSendingEnabled = enabled;
        if (request.DailySendCap is { } cap) s.DailySendCap = cap;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "tenant.outbound_settings_changed",
            EntityType = "tenant",
            EntityId = db.CurrentTenantId,
            Changes = System.Text.Json.JsonSerializer.Serialize(new { before, after = new { s.OutboundSendingEnabled, s.DailySendCap } }),
        }, ct);
        return await OutboundSettingsAsync(db, cases, ct);
    }

    /// <summary>Doc 06: a real customer statement — positions per currency, open invoices, payments, and what we sent.</summary>
    private static async Task<IResult> StatementAsync(Guid id, HttpContext context, TenantDbContext db, AgingService aging, FinanceAi.Infrastructure.Ledger.LedgerService ledger, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var positions = (await ledger.CustomerPositionAsync(id, ct)).Select(p => new CustomerPositionDto(p.Currency, MoneyDto.From(p.OpenBalance, p.Currency), p.OpenInvoiceCount, MoneyDto.From(p.UnappliedCash, p.Currency), MoneyDto.From(p.UnappliedCredit, p.Currency))).ToList();
        var detail = await aging.CustomerAsync(id, null, null, null, ct);
        var open = detail?.Invoices.Select(d => new AgedInvoiceDto(d.Invoice.InvoiceId, d.Invoice.InvoiceNumber, d.Invoice.Currency, Iso(d.Invoice.IssueDate), Iso(d.Invoice.DueDate),
            MoneyDto.From(d.Invoice.TotalAmount, d.Invoice.Currency), MoneyDto.From(d.Invoice.OpenBalance, d.Invoice.Currency), d.Invoice.DaysPastDue, d.Bucket, MoneyDto.From(d.DisputedAmount, d.Invoice.Currency))).ToList() ?? [];
        var payments = new List<PaymentResponse>();
        foreach (var p in await db.Payments.Where(p => p.CustomerId == id).OrderByDescending(p => p.ReceivedDate).Take(50).ToListAsync(ct)) payments.Add(await LedgerEndpoints.ToResponseAsync(p, db, ct));
        var messages = new List<MessageResponse>();
        foreach (var m in await db.Messages.Where(m => m.CustomerId == id && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered || m.Status == MessageStatus.Bounced)).OrderByDescending(m => m.SentAt).Take(50).ToListAsync(ct)) messages.Add(await MessageAsync(m, db, ct));
        return TypedResults.Ok(new StatementResponse(id, Iso(detail?.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow)), positions, open, payments, messages));
    }

    // ---------------------------------------------------------------------------------------
    // Shaping
    // ---------------------------------------------------------------------------------------

    private static TemplateResponse Template(MessageTemplate t) => new(
        t.Id, t.Key, t.Channel, t.Language, t.Tone, t.Subject, t.Body, t.Version, t.Status, t.IsActive, t.IsSystem, t.ApprovedBy,
        t.ApprovedAt?.ToString("O", CultureInfo.InvariantCulture), Placeholders.Used(t.Body + " " + (t.Subject ?? string.Empty)), t.CreatedAt.ToString("O", CultureInfo.InvariantCulture), t.CreatedBy);

    private static async Task<MessageResponse> MessageAsync(OutboundMessage m, TenantDbContext db, CancellationToken ct)
    {
        var caseNumber = m.CaseId is { } cid ? await db.Cases.Where(c => c.Id == cid).Select(c => (long?)c.CaseNumber).FirstOrDefaultAsync(ct) : null;
        return new MessageResponse(
            m.Id, m.CaseId, caseNumber, m.CustomerId, m.ContactId, m.Channel, m.Language, m.TemplateId, m.TemplateKey, m.TemplateVersion,
            m.ToAddress, m.Subject, m.Body, m.InvoiceIds, m.Status.ToString(), m.ApprovalRequired, m.ApprovalReasons, m.ApprovalKind,
            m.AiDrafted, m.DraftedBy, m.ApprovedBy, m.ApprovedAt?.ToString("O", CultureInfo.InvariantCulture), m.SentBy, m.SentAt?.ToString("O", CultureInfo.InvariantCulture),
            m.Attempts, m.NextAttemptAt?.ToString("O", CultureInfo.InvariantCulture), m.FailureReason, m.CancelReason, m.CreatedAt.ToString("O", CultureInfo.InvariantCulture), m.RowVersion);
    }

    private static IResult Invalid(HttpContext context, InvalidTransitionException ex) =>
        ApiProblems.Create(context, StatusCodes.Status409Conflict, "invalid_transition", $"No transition from {ex.From} on {ex.Event}.", "errors.invalid_transition",
            [new ApiProblems.FieldError("event", "invalid_transition", "errors.invalid_transition", new Dictionary<string, string> { ["from"] = ex.From, ["event"] = ex.Event })]);

    private static IResult Rule(HttpContext context, CaseException ex) => ex.Code switch
    {
        "case_not_found" or "template_not_found" or "message_not_found" or "contact_not_found" => ApiProblems.NotFoundProblem(context),
        "template_exists" => ApiProblems.Create(context, StatusCodes.Status409Conflict, "template_exists", "A template with this key, channel and language exists; post a new version to it.", "errors.template_exists"),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
