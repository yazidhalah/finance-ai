using System.Globalization;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Ai;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Ai;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 9. Inbound replies, one AI operation, and the human gates around it. No route here lets the model's
/// output reach an invoice, a payment or a case status: <c>approve</c> confirms a Proposed promise with the human's id,
/// <c>edit-and-approve</c> records the human's own values, <c>reject</c> withdraws what the model proposed.
/// </summary>
public static class AiEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapAiEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var inbound = api.MapGroup("/inbound-messages");
        inbound.MapGet("/", ListInboundAsync).Produces<InboundMessageListResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("ListInboundMessages");
        inbound.MapPost("/", CreateInboundAsync).Produces<InboundMessageResponse>(201).RequiresPermission(Permissions.CasesWrite).WithName("CreateInboundMessage");
        inbound.MapGet("/{id:guid}", GetInboundAsync).Produces<InboundMessageResponse>(200).RequiresPermission(Permissions.CasesRead).WithName("GetInboundMessage");
        inbound.MapPost("/{id:guid}/classify", ClassifyAsync).Produces<AiSuggestionResponse>(200).RequiresPermission(Permissions.CasesWrite).WithName("ClassifyInboundMessage");
        inbound.MapPost("/{id:guid}/match-customer", MatchAsync).Produces<InboundMessageResponse>(200).RequiresPermission(Permissions.CasesWrite).WithName("MatchInboundCustomer");
        inbound.MapPost("/{id:guid}/classify-manually", ClassifyManuallyAsync).Produces<InboundMessageResponse>(200).RequiresPermission(Permissions.CasesWrite).WithName("ClassifyInboundManually");

        var ai = api.MapGroup("/ai");
        ai.MapGet("/suggestions", ListSuggestionsAsync).Produces<AiSuggestionListResponse>(200).RequiresPermission(Permissions.AiSuggestionsRead).WithName("ListAiSuggestions");
        ai.MapGet("/suggestions/{id:guid}", GetSuggestionAsync).Produces<AiSuggestionResponse>(200).RequiresPermission(Permissions.AiSuggestionsRead).WithName("GetAiSuggestion");
        ai.MapPost("/suggestions/{id:guid}/approve", ApproveAsync).Produces<AiSuggestionResponse>(200).RequiresPermission(Permissions.AiSuggestionsApprove).WithName("ApproveAiSuggestion");
        ai.MapPost("/suggestions/{id:guid}/edit-and-approve", EditAndApproveAsync).Produces<AiSuggestionResponse>(200).RequiresPermission(Permissions.AiSuggestionsApprove).WithName("EditAndApproveAiSuggestion");
        ai.MapPost("/suggestions/{id:guid}/reject", RejectAsync).Produces<AiSuggestionResponse>(200).RequiresPermission(Permissions.AiSuggestionsApprove).WithName("RejectAiSuggestion");
        ai.MapGet("/health", HealthAsync).Produces<AiHealthResponse>(200).RequiresPermission(Permissions.TenantRead).WithName("AiHealth");

        api.MapGet("/organization/ai-settings", AiSettingsAsync).Produces<AiSettingsResponse>(200).RequiresPermission(Permissions.TenantRead).WithName("AiSettings");
        api.MapPatch("/organization/ai-settings", UpdateAiSettingsAsync).Produces<AiSettingsResponse>(200).RequiresPermission(Permissions.AiSettingsWrite).WithName("UpdateAiSettings");

        return api;
    }

    // ---------------------------------------------------------------------------------------
    // Inbound messages
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> ListInboundAsync(HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var q = context.Request.Query;
        var query = db.InboundMessages.AsQueryable();
        if (q["status"].ToString() is { Length: > 0 } statusText)
        {
            if (!Enum.TryParse<InboundStatus>(statusText, out var status)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("status", "invalid", "errors.validation.status.invalid")]);
            query = query.Where(m => m.ClassificationStatus == status);
        }

        if (q["unmatched"].ToString() == "true") query = query.Where(m => m.CustomerId == null);
        if (Guid.TryParse(q["customerId"], out var customerId)) query = query.Where(m => m.CustomerId == customerId);
        if (Guid.TryParse(q["caseId"], out var caseId)) query = query.Where(m => m.CaseId == caseId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.ReceivedAt).Take(200).ToListAsync(ct);
        var shaped = new List<InboundMessageResponse>();
        foreach (var m in items) shaped.Add(await MessageAsync(m, db, includeSuggestion: true, ct));
        return TypedResults.Ok(new InboundMessageListResponse(shaped, total));
    }

    private static async Task<IResult> CreateInboundAsync(CreateInboundMessageRequest request, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        var validation = new Validation().Require("channel", request.Channel).Require("body", request.Body).MaxLength("body", request.Body, 100_000)
            .MaxLength("subject", request.Subject, 500).MaxLength("fromAddress", request.FromAddress, 320).Email("fromAddress", request.FromAddress);
        if (request.Channel is not null && !InboundChannels.All.Contains(request.Channel, StringComparer.Ordinal)) validation.Require("channel", null, "invalid");
        DateTimeOffset? receivedAt = null;
        if (request.ReceivedAt is { Length: > 0 } r)
        {
            if (!DateTimeOffset.TryParse(r, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) validation.Require("receivedAt", null, "invalid_date");
            else receivedAt = parsed;
        }

        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        try
        {
            var m = await inbound.IngestAsync(request.Channel!, request.FromAddress, request.Subject, request.Body!, receivedAt, request.CustomerId, request.InReplyToMessageId, user.UserId, ct);
            return TypedResults.Created($"/api/v1/inbound-messages/{m.Id}", await MessageAsync(m, db, includeSuggestion: false, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { ConstraintName: "fk_inbound_reply_to" })
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("inReplyToMessageId", "not_found", "errors.validation.inReplyToMessageId.not_found")]);
        }
    }

    private static async Task<IResult> GetInboundAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var m = await db.InboundMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return m is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await MessageAsync(m, db, includeSuggestion: true, ct));
    }

    private static async Task<IResult> ClassifyAsync(Guid id, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        if (!await db.InboundMessages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        try
        {
            var r = await inbound.ClassifyAsync(id, user.UserId, ct);
            return TypedResults.Ok(await SuggestionAsync(r.Suggestion, db, includeMessage: true, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> MatchAsync(Guid id, MatchCustomerRequest request, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        if (!await db.InboundMessages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (request.CustomerId is not { } customerId) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("customerId", "required", "errors.validation.customerId.required")]);
        try
        {
            var m = await inbound.MatchAsync(id, customerId, user.UserId, ct);
            return TypedResults.Ok(await MessageAsync(m, db, includeSuggestion: true, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> ClassifyManuallyAsync(Guid id, ManualClassificationRequest request, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        if (!await db.InboundMessages.AnyAsync(m => m.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var validation = new Validation().Require("classification", request.Classification).MaxLength("note", request.Note, 2000);
        if (request.Classification is not null && !AiClassifications.All.Contains(request.Classification, StringComparer.Ordinal)) validation.Require("classification", null, "invalid");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        try
        {
            var m = await inbound.ClassifyManuallyAsync(id, request.Classification!, request.Note, user.UserId, ct);
            return TypedResults.Ok(await MessageAsync(m, db, includeSuggestion: true, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    // ---------------------------------------------------------------------------------------
    // Suggestions and the human gates
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> ListSuggestionsAsync(HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var q = context.Request.Query;
        var query = db.AiSuggestions.AsQueryable();
        if (q["decision"].ToString() is { Length: > 0 } decision) query = query.Where(s => s.HumanDecision == decision);
        if (q["classification"].ToString() is { Length: > 0 } classification) query = query.Where(s => s.Classification == classification);
        if (q["review"].ToString() == "true") query = query.Where(s => s.RequiresHumanReview);
        if (Guid.TryParse(q["subjectId"], out var subjectId)) query = query.Where(s => s.SubjectId == subjectId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(s => s.CreatedAt).Take(200).ToListAsync(ct);
        var shaped = new List<AiSuggestionResponse>();
        foreach (var s in items) shaped.Add(await SuggestionAsync(s, db, includeMessage: true, ct));
        return TypedResults.Ok(new AiSuggestionListResponse(shaped, total));
    }

    private static async Task<IResult> GetSuggestionAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var s = await db.AiSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await SuggestionAsync(s, db, includeMessage: true, ct));
    }

    private static async Task<IResult> ApproveAsync(Guid id, ApproveSuggestionRequest request, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        if (!await db.AiSuggestions.AnyAsync(s => s.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (request.Note is { Length: > 2000 }) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("note", "too_long", "errors.validation.note.too_long")]);
        try
        {
            var s = await inbound.ApproveAsync(id, request.Note, user.UserId, ct);
            return TypedResults.Ok(await SuggestionAsync(s, db, includeMessage: true, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    private static async Task<IResult> EditAndApproveAsync(Guid id, EditAndApproveSuggestionRequest request, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        if (!await db.AiSuggestions.AnyAsync(s => s.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var validation = new Validation().MaxLength("note", request.Note, 2000).Currency("amount.currency", request.Amount?.Currency);
        if (request.Classification is not null && !AiClassifications.All.Contains(request.Classification, StringComparer.Ordinal)) validation.Require("classification", null, "invalid");
        decimal? amount = null;
        if (request.Amount is not null)
        {
            if (!request.Amount.TryParse(out var a) || a <= 0m) validation.Require("amount", null, "invalid_amount");
            else amount = a;
        }

        DateOnly? date = null;
        if (request.PromisedDate is { Length: > 0 })
        {
            if (!DateOnly.TryParseExact(request.PromisedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) validation.Require("promisedDate", null, "invalid_date");
            else date = d;
        }

        if (request.DisputeReasonCode is not null && !DisputeReasons.All.Contains(request.DisputeReasonCode, StringComparer.Ordinal)) validation.Require("disputeReasonCode", null, "invalid");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        try
        {
            var s = await inbound.EditAndApproveAsync(id, new InboundService.HumanValues(request.Classification, request.InvoiceId, request.InvoiceIds, amount, date, request.DisputeReasonCode, request.Note), user.UserId, ct);
            return TypedResults.Ok(await SuggestionAsync(s, db, includeMessage: true, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { ConstraintName: "one_active_ptp_per_invoice" })
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "overlapping_promise", "An active promise already covers one of these invoices.", "errors.overlapping_promise");
        }
    }

    private static async Task<IResult> RejectAsync(Guid id, RejectSuggestionRequest request, HttpContext context, CurrentUser user, TenantDbContext db, InboundService inbound, CancellationToken ct)
    {
        if (!await db.AiSuggestions.AnyAsync(s => s.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        var validation = new Validation().Require("reason", request.Reason).MaxLength("reason", request.Reason, 2000);
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        try
        {
            var s = await inbound.RejectAsync(id, request.Reason!, user.UserId, ct);
            return TypedResults.Ok(await SuggestionAsync(s, db, includeMessage: true, ct));
        }
        catch (CaseException ex) { return Rule(context, ex); }
    }

    // ---------------------------------------------------------------------------------------
    // Health and settings
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> HealthAsync(TenantDbContext db, IAiClient ai, CancellationToken ct)
    {
        var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        var configured = HttpAiClient.Token is not null;
        var h = configured ? await ai.HealthAsync(ct) : new AiHealth(false, false, null, null, null, "ai_not_configured");
        return TypedResults.Ok(new AiHealthResponse(configured, h.Reachable, h.Ready, h.ModelName, h.Digest, h.PromptVersion, h.Error, settings.AiEnabled));
    }

    private static async Task<IResult> AiSettingsAsync(TenantDbContext db, CancellationToken ct)
    {
        var s = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        return TypedResults.Ok(Settings(s));
    }

    private static async Task<IResult> UpdateAiSettingsAsync(AiSettingsRequest request, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Audit.IAuditWriter audit, CancellationToken ct)
    {
        decimal? threshold = null;
        if (request.AiMinConfidence is { Length: > 0 } t)
        {
            if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed < 0.5m || parsed > 1m || decimal.Round(parsed, 3) != parsed)
            {
                return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("aiMinConfidence", "invalid", "errors.validation.aiMinConfidence.invalid")]);
            }

            threshold = parsed;
        }

        var s = await db.TenantSettings.FirstAsync(ct);
        var before = new { s.AiEnabled, aiMinConfidence = F3(s.AiMinConfidence) };
        if (request.AiEnabled is { } enabled) s.AiEnabled = enabled;
        if (threshold is { } th) s.AiMinConfidence = th;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "tenant.ai_settings_changed",
            EntityType = "tenant",
            EntityId = db.CurrentTenantId,
            Changes = JsonSerializer.Serialize(new { before, after = new { s.AiEnabled, aiMinConfidence = F3(s.AiMinConfidence) } }, Json),
        }, ct);
        return TypedResults.Ok(Settings(s));
    }

    private static AiSettingsResponse Settings(TenantSettings s) =>
        new(s.AiEnabled, F3(s.AiMinConfidence), Uri.TryCreate(HttpAiClient.BaseUrl, UriKind.Absolute, out var u) ? u.Host : "unknown");

    // ---------------------------------------------------------------------------------------
    // Shaping
    // ---------------------------------------------------------------------------------------

    private static async Task<InboundMessageResponse> MessageAsync(InboundMessage m, TenantDbContext db, bool includeSuggestion, CancellationToken ct)
    {
        var customerName = m.CustomerId is { } cid ? await db.Customers.Where(c => c.Id == cid).Select(c => c.NameEn ?? c.NameAr ?? c.LegalName ?? c.Code).FirstOrDefaultAsync(ct) : null;
        var caseNumber = m.CaseId is { } caseId ? await db.Cases.Where(c => c.Id == caseId).Select(c => (long?)c.CaseNumber).FirstOrDefaultAsync(ct) : null;
        AiSuggestionResponse? last = null;
        if (includeSuggestion && m.LastSuggestionId is { } sid && await db.AiSuggestions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct) is { } s) last = await SuggestionAsync(s, db, includeMessage: false, ct);
        return new InboundMessageResponse(
            m.Id, m.CustomerId, customerName, m.CaseId, caseNumber, m.Channel, m.FromAddress, m.Subject, m.BodyRaw, m.DetectedLanguage, Iso(m.ReceivedAt), m.InReplyToMessageId,
            m.MatchMethod, m.MatchConfidence is { } mc ? F3(mc) : null, m.ClassificationStatus.ToString(), m.Classification, m.HumanClassification, m.HumanClassifiedBy,
            m.HumanClassifiedAt is { } h ? Iso(h) : null, m.LastSuggestionId, m.TruncatedForAi, Iso(m.CreatedAt), m.RowVersion, last);
    }

    private static async Task<AiSuggestionResponse> SuggestionAsync(AiSuggestion s, TenantDbContext db, bool includeMessage, CancellationToken ct)
    {
        string? language = null, sentiment = null, rationale = null;
        AiExtractedDto? extracted = null;
        var secondary = new List<AiSecondaryDto>();
        try
        {
            using var doc = JsonDocument.Parse(s.OutputJson);
            if (AiResponseValidator.TryParse(doc.RootElement, out var r, out _) && r is not null)
            {
                language = r.DetectedLanguage;
                sentiment = r.Sentiment;
                rationale = r.Rationale;
                var e = r.Extracted;
                extracted = new AiExtractedDto(e.MentionedAmountText, e.MentionedAmountNumeric, e.MentionedCurrency, e.MentionedDateText, e.MentionedDateIso, e.DateIsRelative, e.ReferencedInvoiceNumbers, e.PaymentMethodMentioned, e.PaymentReferenceText);
                secondary = r.Secondary.Select(x => new AiSecondaryDto(x.Classification, F3(x.Confidence))).ToList();
            }
        }
        catch (JsonException)
        {
            // An invalid output is shown as nothing: the row still carries the provenance and the human decides from the text.
        }

        InboundMessageResponse? message = null;
        if (includeMessage && s.SubjectType == "inbound_message" && await db.InboundMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == s.SubjectId, ct) is { } m) message = await MessageAsync(m, db, includeSuggestion: false, ct);
        return new AiSuggestionResponse(
            s.Id, s.Operation, s.SubjectType, s.SubjectId, s.ModelName, s.ModelDigest, s.PromptVersion, s.SchemaVersion, s.InputHash, F3(s.Confidence), s.Classification, s.ReasonCode,
            s.ValidationStatus, s.RequiresHumanReview, s.Suspicious, s.LatencyMs, s.OutcomeType, s.OutcomeId, s.GuardReason, language, sentiment, rationale, extracted, secondary,
            Iso(s.CreatedAt), s.HumanDecision, s.DecidedBy, s.DecidedAt is { } d ? Iso(d) : null, s.DecisionReason, message);
    }

    private static IResult Rule(HttpContext context, CaseException ex) => ex.Code switch
    {
        "message_not_found" or "suggestion_not_found" or "customer_not_found" or "case_not_found" or "invoice_not_found" => ApiProblems.NotFoundProblem(context),
        "ai_unavailable" or "ai_busy" or "ai_not_configured" or "ai_bad_response" => ApiProblems.Create(context, StatusCodes.Status503ServiceUnavailable, ex.Code, "The AI service is not available; the message stays in the human queue.", $"errors.rule.{ex.Code}"),
        "ai_disabled" or "already_classified" or "already_decided" => ApiProblems.Create(context, StatusCodes.Status409Conflict, ex.Code, $"Refused: {ex.Code}.", $"errors.rule.{ex.Code}"),
        "duplicate" => ApiProblems.Create(context, StatusCodes.Status409Conflict, "duplicate", "An open dispute already exists on this invoice.", "errors.duplicate_dispute"),
        _ => ApiProblems.BusinessRuleProblem(context, ex.Code, ex.Field, ex.Meta),
    };

    private static string F3(decimal d) => d.ToString("F3", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset d) => d.ToString("O", CultureInfo.InvariantCulture);
}
