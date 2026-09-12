using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceAi.Domain.Ai;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ai;

/// <summary>
/// Inbound replies and what the AI is allowed to say about them (doc 07 §4, slice 9). The shape of every method is
/// the same: the model's output is data; this class decides; every consequence is a row a human still has to act on.
/// Nothing here writes an invoice, a payment, a credit note, or a case status other than <c>ReplyReceived</c>.
/// </summary>
public sealed class InboundService(TenantDbContext db, IAuditWriter audit, TimeProvider time, CaseService cases, PromiseService promises, DisputeService disputes, IAiClient ai)
{
    public const int MaxAiChars = 4000;
    private const int MaxInvoicesInScope = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record ClassifyResult(InboundMessage Message, AiSuggestion Suggestion, string? OutcomeType, Guid? OutcomeId);

    public sealed record HumanValues(string? Classification, Guid? InvoiceId, IReadOnlyList<Guid>? InvoiceIds, decimal? Amount, DateOnly? PromisedDate, string? DisputeReasonCode, string? Note);

    // ---------------------------------------------------------------------------------------
    // Ingest and match
    // ---------------------------------------------------------------------------------------

    public async Task<InboundMessage> IngestAsync(string channel, string? fromAddress, string? subject, string body, DateTimeOffset? receivedAt, Guid? customerId, Guid? inReplyToMessageId, Guid? actorUserId, CancellationToken ct)
    {
        if (!InboundChannels.All.Contains(channel, StringComparer.Ordinal)) throw new CaseException("invalid_channel", "channel");
        var now = time.GetUtcNow();
        var message = new InboundMessage
        {
            TenantId = db.CurrentTenantId,
            Channel = channel,
            FromAddress = fromAddress?.Trim() is { Length: > 0 } f ? f : null,
            Subject = subject?.Trim() is { Length: > 0 } s ? s : null,
            BodyRaw = body,
            ReceivedAt = receivedAt ?? now,
            InReplyToMessageId = inReplyToMessageId,
            CreatedBy = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (customerId is { } explicitCustomer)
        {
            if (!await db.Customers.AnyAsync(c => c.Id == explicitCustomer, ct)) throw new CaseException("customer_not_found", "customerId");
            await BindAsync(message, explicitCustomer, "manual", 1.000m, actorUserId, ct);
        }
        else if (inReplyToMessageId is { } replyTo && await db.Messages.Where(m => m.Id == replyTo).Select(m => (Guid?)m.CustomerId).FirstOrDefaultAsync(ct) is { } threadCustomer)
        {
            await BindAsync(message, threadCustomer, "reply_to", 1.000m, null, ct);
        }
        else if (message.FromAddress is { } from)
        {
            // Exact, case-insensitive match on a contact email; anything less certain waits for a human (doc 05).
            var lowered = from.ToLowerInvariant();
            var candidates = await db.CustomerContacts.Where(c => c.Email != null && c.Email.ToLower() == lowered).Select(c => c.CustomerId).Distinct().ToListAsync(ct);
            if (candidates.Count == 1) await BindAsync(message, candidates[0], "contact_email", 1.000m, null, ct);
        }

        db.InboundMessages.Add(message);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("inbound_message.received", "inbound_message", message.Id, actorUserId, note: $"{channel}; matched={message.CustomerId is not null}"), ct);
        return message;
    }

    public async Task<InboundMessage> MatchAsync(Guid messageId, Guid customerId, Guid actorUserId, CancellationToken ct)
    {
        var message = await LockAsync(messageId, ct);
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct)) throw new CaseException("customer_not_found", "customerId");
        if (message.ClassificationStatus is InboundStatus.Classified or InboundStatus.HumanClassified) throw new CaseException("already_classified");
        await BindAsync(message, customerId, "manual", 1.000m, actorUserId, ct);
        message.UpdatedAt = time.GetUtcNow();
        message.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("inbound_message.matched", "inbound_message", message.Id, actorUserId, note: "manual"), ct);
        return message;
    }

    private async Task BindAsync(InboundMessage message, Guid customerId, string method, decimal confidence, Guid? actorUserId, CancellationToken ct)
    {
        message.CustomerId = customerId;
        message.MatchMethod = method;
        message.MatchConfidence = confidence;
        message.MatchedBy = actorUserId;
        message.CaseId = await db.Cases.Where(c => c.CustomerId == customerId && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned)
            .OrderByDescending(c => c.OpenedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
    }

    // ---------------------------------------------------------------------------------------
    // Classification (AI-04, AI-05, AI-30, AI-40, AI-41, the safety table)
    // ---------------------------------------------------------------------------------------

    public async Task<ClassifyResult> ClassifyAsync(Guid messageId, Guid? actorUserId, CancellationToken ct)
    {
        var message = await LockAsync(messageId, ct);
        if (message.CustomerId is not { } customerId) throw new CaseException("customer_required");
        if (message.ClassificationStatus is InboundStatus.Classified or InboundStatus.HumanClassified or InboundStatus.Ignored) throw new CaseException("already_classified");

        var context = await cases.ContextAsync(ct);
        if (!context.Settings.ClassificationActive) throw new CaseException("ai_disabled");

        var customer = await db.Customers.FirstAsync(c => c.Id == customerId, ct);
        var c = message.CaseId is { } caseId ? await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct) : null;
        var scope = await ScopeAsync(customerId, c, ct);
        var kept = await db.Promises.CountAsync(p => p.CustomerId == customerId && p.Status == PtpStatus.Kept, ct);
        var broken = await db.Promises.CountAsync(p => p.CustomerId == customerId && p.Status == PtpStatus.Broken, ct);

        // AI-30: an explicit projection. Display name, language, invoice numbers/dates/amounts/days, case status, counts. Nothing else exists on the type.
        var text = message.BodyRaw;
        var truncated = false;
        if (text.Length > MaxAiChars)
        {
            text = text[..MaxAiChars];
            truncated = true;
        }

        var payload = new ClassifyRequestPayload(
            Guid.CreateVersion7(),
            new AiMessagePayload(text, message.Subject is { Length: > 500 } subj ? subj[..500] : message.Subject, message.ReceivedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), truncated, customer.PreferredLanguage is "ar" or "en" ? customer.PreferredLanguage : "unknown"),
            new AiContextPayload(
                DisplayName(customer),
                context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                c?.Status.ToString(),
                scope.Select(s => new AiInvoiceInScope(s.InvoiceNumber, s.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), F3(s.OpenBalance), s.Currency, Math.Max(0, context.Today.DayNumber - s.DueDate.DayNumber))).ToList(),
                new AiPriorPromises(kept, broken)),
            new AiOptionsPayload(context.Settings.AiMinConfidence));

        var started = time.GetUtcNow();
        var call = await ai.ClassifyAsync(payload, ct);
        var latency = (int)Math.Max(0, (time.GetUtcNow() - started).TotalMilliseconds);
        if (call.Status == AiCallStatus.Unavailable)
        {
            // AI-10 / PRD-28: the product keeps working; the message stays in the human queue untouched (the request's
            // transaction rolls back on the 503), nothing is guessed, and the UI shows the degraded banner from /ai/health.
            throw new CaseException(call.ErrorCode ?? "ai_unavailable");
        }

        message.TruncatedForAi = truncated;

        var inputRef = JsonSerializer.Serialize(new { inboundMessageId = message.Id, caseId = c?.Id, customerId, requestId = payload.RequestId, invoiceCount = scope.Count, truncated }, Json);
        var suggestion = new AiSuggestion
        {
            TenantId = db.CurrentTenantId,
            Operation = AiOperations.ClassifyCustomerReply,
            SubjectType = "inbound_message",
            SubjectId = message.Id,
            ModelName = "unknown",
            ModelDigest = "unknown",
            PromptVersion = "unknown",
            SchemaVersion = AiVocabularies.SchemaVersion,
            InputRef = inputRef,
            InputHash = call.InputHash is { Length: 64 } h && h.All(char.IsAsciiHexDigitLower) ? h : Sha256(JsonSerializer.Serialize(payload, Json)),
            OutputJson = call.Body is { } b ? b.GetRawText() : "{}",
            Confidence = 0m,
            ValidationStatus = AiValidationStatus.SchemaInvalid,
            RequiresHumanReview = true,
            LatencyMs = latency,
            CreatedAt = time.GetUtcNow(),
        };

        ClassifyResponse? response = null;
        IReadOnlyList<string> errors = [];
        var valid = call.Status == AiCallStatus.Ok && call.Body is { } body && AiResponseValidator.TryParse(body, out response, out errors);
        if (valid && call.ServiceValidationStatus == "schema_invalid")
        {
            // The service already gave up on the model's output and sent its own placeholder; we do not act on a placeholder.
            valid = false;
            errors = ["service: schema_invalid"];
        }

        if (!valid || response is null)
        {
            suggestion.OutcomeType = AiOutcomeTypes.None;
            suggestion.GuardReason = errors.Count > 0 ? string.Join("; ", errors.Take(6)) : call.ErrorCode ?? "schema_invalid";
            if (call.Body is { } raw && raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                suggestion.ModelName = StrOr(m, "name", "unknown");
                suggestion.ModelDigest = StrOr(m, "digest", "unknown");
                suggestion.PromptVersion = StrOr(m, "prompt_version", "unknown");
            }

            await FinishAsync(message, suggestion, InboundStatus.Unclassified, null, c, actorUserId, "schema_invalid", ct);
            return new ClassifyResult(message, suggestion, null, null);
        }

        suggestion.ModelName = response.Model.Name;
        suggestion.ModelDigest = response.Model.Digest;
        suggestion.PromptVersion = response.Model.PromptVersion;
        suggestion.Confidence = response.Confidence;
        suggestion.Classification = response.Classification;
        suggestion.ReasonCode = response.ReasonCode;
        suggestion.Suspicious = response.ContainsSuspiciousInstructions;
        suggestion.RequiresHumanReview = response.RequiresHumanReview || response.ContainsSuspiciousInstructions || AiClassifications.AlwaysReviewed(response.Classification)
            || response.Extracted.DateIsRelative == true || response.Secondary.Any(s => s.Confidence > 0.5m);
        message.DetectedLanguage = response.DetectedLanguage;

        // AI-05: below the tenant's threshold is not a guess. The service applies it too; the backend does not trust that.
        if (response.Confidence < context.Settings.AiMinConfidence || response.Classification == AiClassifications.Unclassified)
        {
            suggestion.ValidationStatus = response.Classification == AiClassifications.Unclassified && response.Confidence >= context.Settings.AiMinConfidence ? AiValidationStatus.Valid : AiValidationStatus.BelowThreshold;
            suggestion.Classification = AiClassifications.Unclassified;
            suggestion.RequiresHumanReview = true;
            suggestion.OutcomeType = AiOutcomeTypes.None;
            await FinishAsync(message, suggestion, InboundStatus.Unclassified, null, c, actorUserId, suggestion.ValidationStatus, ct);
            return new ClassifyResult(message, suggestion, null, null);
        }

        suggestion.ValidationStatus = AiValidationStatus.Valid;
        db.AiSuggestions.Add(suggestion);
        await db.SaveChangesAsync(ct);

        // The safety table, deterministically. The model's text plays no part from here on.
        var decision = DecideFrom(response, scope, c is not null && !CaseMachine.IsTerminal(c.Status), context.Today);
        var (outcomeType, outcomeId, guard) = await ApplyAsync(decision, response, message, customer, c, suggestion, actorUserId, ct);
        suggestion.OutcomeType = outcomeType;
        suggestion.OutcomeId = outcomeId;
        suggestion.GuardReason = guard;

        await FinishAsync(message, suggestion, InboundStatus.Classified, response.Classification, c, actorUserId, AiValidationStatus.Valid, ct);
        return new ClassifyResult(message, suggestion, outcomeType, outcomeId);
    }

    private async Task<(string OutcomeType, Guid? OutcomeId, string? Guard)> ApplyAsync(AiDecision d, ClassifyResponse r, InboundMessage message, Customer customer, CollectionCase? c, AiSuggestion suggestion, Guid? actorUserId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        try
        {
            switch (d.OutcomeType)
            {
                case AiOutcomeTypes.VerificationTask:
                    {
                        // SM-44 / CLAUDE.md: "they say they paid" opens a task to find the payment. The invoice is not touched.
                        var invoiceId = d.InvoiceId ?? d.InvoiceIds![0];
                        if (await db.VerificationTasks.AnyAsync(t => t.InvoiceId == invoiceId && t.Status == "Open", ct)) return (AiOutcomeTypes.None, null, "verification_task_exists");
                        var task = new PaymentVerificationTask
                        {
                            TenantId = db.CurrentTenantId,
                            InvoiceId = invoiceId,
                            CustomerId = customer.Id,
                            Source = "ai_classification",
                            AiSuggestionId = suggestion.Id,
                            Claim = Claim(r),
                            CreatedAt = now,
                            CreatedBy = actorUserId,
                        };
                        db.VerificationTasks.Add(task);
                        await db.SaveChangesAsync(ct);
                        await audit.WriteAsync(Event("payment_verification.opened", "payment_verification_task", task.Id, actorUserId, "ai_classification", suggestionId: suggestion.Id), ct);
                        return (AiOutcomeTypes.VerificationTask, task.Id, d.GuardReason);
                    }

                case AiOutcomeTypes.PromiseProposed:
                    {
                        // SM-31: Proposed. The slice 6 CHECK makes Active impossible without a human's confirmedBy.
                        var result = await promises.RecordAsync(c!.Id, d.InvoiceIds!, d.Amount!.Value, d.PromisedDate!.Value, PtpSources.AiSuggested, Claim(r), actorUserId, suggestion.Id, ct);
                        return (AiOutcomeTypes.PromiseProposed, result.Promise.Id, null);
                    }

                case AiOutcomeTypes.DisputeOpen:
                    {
                        var result = await disputes.RaiseAsync(d.InvoiceId!.Value, d.DisputeReasonCode!, d.Amount!.Value, Claim(r, message.BodyRaw), "ai_suggested", actorUserId, suggestion.Id, ct);
                        return (AiOutcomeTypes.DisputeOpen, result.Dispute.Id, null);
                    }

                case AiOutcomeTypes.Activity:
                    return (AiOutcomeTypes.Activity, null, null);

                default:
                    return (AiOutcomeTypes.None, null, d.GuardReason);
            }
        }
        catch (CaseException ex)
        {
            // A guard in the slice 6/7 service (duplicate dispute, invoice not open, …) withholds the outcome; the suggestion still reaches a human.
            return (AiOutcomeTypes.None, null, ex.Code);
        }
    }

    private async Task FinishAsync(InboundMessage message, AiSuggestion suggestion, InboundStatus status, string? classification, CollectionCase? c, Guid? actorUserId, string validationStatus, CancellationToken ct)
    {
        if (db.Entry(suggestion).State == EntityState.Detached) db.AiSuggestions.Add(suggestion);
        message.ClassificationStatus = status;
        message.Classification = classification;
        message.LastSuggestionId = suggestion.Id;
        message.UpdatedAt = time.GetUtcNow();
        message.RowVersion++;
        await db.SaveChangesAsync(ct);

        if (c is not null)
        {
            var summary = classification is null ? "Customer reply received; the AI could not classify it — needs a human" : $"Customer reply classified as {classification} (confidence {suggestion.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}, {suggestion.PromptVersion})";
            await cases.AddActivityAsync(c, ActivityKinds.EmailReceived, actorUserId, summary, new { inboundMessageId = message.Id, aiSuggestionId = suggestion.Id, classification, confidence = suggestion.Confidence.ToString("0.000", CultureInfo.InvariantCulture), outcome = suggestion.OutcomeType, guard = suggestion.GuardReason }, ct);
            if (CaseMachine.Peek(c.Status, CaseEvent.ReplyReceived) is not null)
            {
                await cases.FireAsync(c, CaseEvent.ReplyReceived, actorUserId, "reply_received", null, ct);
            }
        }

        // AI-06 / SEC-56: the audit row carries provenance and numbers, never the customer's words.
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = actorUserId,
            ActorKind = ActorKinds.AiAssisted,
            EventType = "ai.classification",
            EntityType = "inbound_message",
            EntityId = message.Id,
            ToState = status.ToString(),
            ReasonCode = validationStatus,
            AiSuggestionId = suggestion.Id,
            Changes = JsonSerializer.Serialize(new
            {
                model = suggestion.ModelName,
                digest = suggestion.ModelDigest,
                promptVersion = suggestion.PromptVersion,
                schemaVersion = suggestion.SchemaVersion,
                confidence = suggestion.Confidence.ToString("0.000", CultureInfo.InvariantCulture),
                classification = suggestion.Classification,
                reasonCode = suggestion.ReasonCode,
                validationStatus,
                suspicious = suggestion.Suspicious,
                requiresHumanReview = suggestion.RequiresHumanReview,
                outcome = suggestion.OutcomeType,
                guard = suggestion.GuardReason,
                latencyMs = suggestion.LatencyMs,
                inputHash = suggestion.InputHash,
            }, Json),
        }, ct);
    }

    // ---------------------------------------------------------------------------------------
    // Human gates (API-20)
    // ---------------------------------------------------------------------------------------

    public async Task<AiSuggestion> ApproveAsync(Guid suggestionId, string? note, Guid actorUserId, CancellationToken ct)
    {
        var s = await LockSuggestionAsync(suggestionId, ct);
        if (s.HumanDecision != AiHumanDecisions.Pending) throw new CaseException("already_decided");
        if (s.Classification is null or AiClassifications.Unclassified) throw new CaseException("nothing_to_approve");
        switch (s.OutcomeType)
        {
            case AiOutcomeTypes.PromiseProposed when s.OutcomeId is { } promiseId:
                await promises.ConfirmAsync(promiseId, null, null, actorUserId, ct);   // Proposed → Active, with the human's id (INV-13)
                break;
            case AiOutcomeTypes.None when s.Classification is AiClassifications.PromiseToPay or AiClassifications.DisputeRaised:
                // The model's values did not pass AI-41/AI-51 or the invoice was ambiguous: a human must type them (edit-and-approve).
                throw new CaseException("values_required", null, new Dictionary<string, string> { ["guard"] = s.GuardReason ?? "unknown" });
        }

        var message = await db.InboundMessages.FirstAsync(m => m.Id == s.SubjectId, ct);
        message.HumanClassification = s.Classification;
        message.HumanClassifiedBy = actorUserId;
        message.HumanClassifiedAt = time.GetUtcNow();
        message.Classification = s.Classification;
        message.ClassificationStatus = InboundStatus.HumanClassified;
        message.UpdatedAt = time.GetUtcNow();
        message.RowVersion++;
        await DecideAsync(s, AiHumanDecisions.Approved, actorUserId, note, null, ct);
        return s;
    }

    public async Task<AiSuggestion> EditAndApproveAsync(Guid suggestionId, HumanValues values, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        var s = await LockSuggestionAsync(suggestionId, ct);
        if (s.HumanDecision != AiHumanDecisions.Pending) throw new CaseException("already_decided");
        var classification = values.Classification ?? s.Classification ?? throw new CaseException("classification_required", "classification");
        if (!AiClassifications.All.Contains(classification, StringComparer.Ordinal) || classification == AiClassifications.Unclassified) throw new CaseException("invalid_classification", "classification");
        var message = await db.InboundMessages.FirstAsync(m => m.Id == s.SubjectId, ct);
        var customerId = message.CustomerId ?? throw new CaseException("customer_required");
        var c = message.CaseId is { } caseId ? await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct) : null;

        Guid? outcomeId = s.OutcomeId;
        var outcomeType = s.OutcomeType;
        switch (classification)
        {
            case AiClassifications.PromiseToPay:
                {
                    if (s.OutcomeType == AiOutcomeTypes.PromiseProposed && s.OutcomeId is { } proposed)
                    {
                        await promises.ConfirmAsync(proposed, values.Amount, values.PromisedDate, actorUserId, ct);
                    }
                    else
                    {
                        if (c is null) throw new CaseException("no_open_case");
                        if (values.Amount is not { } amount) throw new CaseException("amount_required", "promisedAmount");
                        if (values.PromisedDate is not { } date) throw new CaseException("promised_date_required", "promisedDate");
                        var invoiceIds = values.InvoiceIds is { Count: > 0 } ids ? ids : await db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId).ToListAsync(ct);
                        // A human typed every value: this is the human's promise, recorded through the same path a call would use (source email), with the suggestion linked for the audit.
                        var recorded = await promises.RecordAsync(c.Id, invoiceIds, amount, date, message.Channel == InboundChannels.WhatsappPasted ? PtpSources.Whatsapp : PtpSources.Email, values.Note, actorUserId, s.Id, ct);
                        outcomeId = recorded.Promise.Id;
                    }

                    outcomeType = AiOutcomeTypes.PromiseProposed;
                    break;
                }

            case AiClassifications.DisputeRaised:
                {
                    if (s.OutcomeType == AiOutcomeTypes.DisputeOpen && s.OutcomeId is not null) break;   // already Open; the human continues in the dispute itself
                    if (values.InvoiceId is not { } invoiceId) throw new CaseException("invoice_required", "invoiceId");
                    var reason = values.DisputeReasonCode ?? "other";
                    var balance = await db.Invoices.Where(i => i.Id == invoiceId && i.CustomerId == customerId).Select(i => (decimal?)i.BalanceCache).FirstOrDefaultAsync(ct) ?? throw new CaseException("invoice_not_found", "invoiceId");
                    var raised = await disputes.RaiseAsync(invoiceId, reason, values.Amount ?? balance, message.BodyRaw, "customer_email", actorUserId, s.Id, ct);
                    outcomeId = raised.Dispute.Id;
                    outcomeType = AiOutcomeTypes.DisputeOpen;
                    break;
                }

            case AiClassifications.PaymentClaimed:
                {
                    if (s.OutcomeType == AiOutcomeTypes.VerificationTask && s.OutcomeId is not null) break;
                    var invoiceId = values.InvoiceId ?? throw new CaseException("invoice_required", "invoiceId");
                    if (!await db.Invoices.AnyAsync(i => i.Id == invoiceId && i.CustomerId == customerId, ct)) throw new CaseException("invoice_not_found", "invoiceId");
                    if (await db.VerificationTasks.AnyAsync(t => t.InvoiceId == invoiceId && t.Status == "Open", ct)) throw new CaseException("verification_task_exists");
                    var task = new PaymentVerificationTask { TenantId = db.CurrentTenantId, InvoiceId = invoiceId, CustomerId = customerId, Source = "user", AiSuggestionId = s.Id, Claim = values.Note, CreatedAt = time.GetUtcNow(), CreatedBy = actorUserId };
                    db.VerificationTasks.Add(task);
                    await db.SaveChangesAsync(ct);
                    await audit.WriteAsync(Event("payment_verification.opened", "payment_verification_task", task.Id, actorUserId, "user", suggestionId: s.Id), ct);
                    outcomeId = task.Id;
                    outcomeType = AiOutcomeTypes.VerificationTask;
                    break;
                }

            default:
                outcomeType ??= AiOutcomeTypes.Activity;
                break;
        }

        s.OutcomeType = outcomeType;
        s.OutcomeId = outcomeId;
        message.HumanClassification = classification;
        message.HumanClassifiedBy = actorUserId;
        message.HumanClassifiedAt = time.GetUtcNow();
        message.Classification = classification;
        message.ClassificationStatus = InboundStatus.HumanClassified;
        message.UpdatedAt = time.GetUtcNow();
        message.RowVersion++;
        var correction = JsonSerializer.Serialize(new
        {
            classification,
            invoiceId = values.InvoiceId,
            invoiceIds = values.InvoiceIds,
            amount = values.Amount?.ToString("F3", CultureInfo.InvariantCulture),
            promisedDate = values.PromisedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            disputeReasonCode = values.DisputeReasonCode,
            aiClassification = s.Classification,
        }, Json);
        await DecideAsync(s, AiHumanDecisions.Edited, actorUserId, values.Note, correction, ct);
        return s;
    }

    public async Task<AiSuggestion> RejectAsync(Guid suggestionId, string reason, Guid actorUserId, CancellationToken ct)
    {
        var s = await LockSuggestionAsync(suggestionId, ct);
        if (s.HumanDecision != AiHumanDecisions.Pending) throw new CaseException("already_decided");
        switch (s.OutcomeType)
        {
            case AiOutcomeTypes.PromiseProposed when s.OutcomeId is { } promiseId:
                if (await db.Promises.AnyAsync(p => p.Id == promiseId && p.Status == PtpStatus.Proposed, ct)) await promises.RejectAsync(promiseId, "ai_suggestion_rejected", actorUserId, ct);
                break;
            case AiOutcomeTypes.DisputeOpen when s.OutcomeId is { } disputeId:
                if (await db.Disputes.AnyAsync(d => d.Id == disputeId && d.Status == DisputeStatus.Open, ct)) await disputes.TransitionAsync(disputeId, DisputeEvent.Cancel, actorUserId, "ai_suggestion_rejected", null, ct);
                break;
        }

        // A rejected verification task stays open: someone still has to look for the payment or record that none exists (SM-44).
        var message = await db.InboundMessages.FirstAsync(m => m.Id == s.SubjectId, ct);
        if (message.LastSuggestionId == s.Id && message.ClassificationStatus != InboundStatus.HumanClassified)
        {
            message.ClassificationStatus = InboundStatus.Unclassified;
            message.Classification = null;
            message.UpdatedAt = time.GetUtcNow();
            message.RowVersion++;
        }

        await DecideAsync(s, AiHumanDecisions.Rejected, actorUserId, reason, null, ct);
        return s;
    }

    public async Task<InboundMessage> ClassifyManuallyAsync(Guid messageId, string classification, string? note, Guid actorUserId, CancellationToken ct)
    {
        if (!AiClassifications.All.Contains(classification, StringComparer.Ordinal)) throw new CaseException("invalid_classification", "classification");
        var message = await LockAsync(messageId, ct);
        var pending = message.LastSuggestionId is { } sid ? await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == sid && s.HumanDecision == AiHumanDecisions.Pending, ct) : null;
        if (pending is not null)
        {
            // The human's label against the model's: this row is the evaluation set (doc 09 §5.4).
            await DecideAsync(pending, classification == pending.Classification ? AiHumanDecisions.Approved : AiHumanDecisions.Edited, actorUserId, note,
                JsonSerializer.Serialize(new { classification, aiClassification = pending.Classification }, Json), ct);
        }

        message.HumanClassification = classification;
        message.HumanClassifiedBy = actorUserId;
        message.HumanClassifiedAt = time.GetUtcNow();
        message.Classification = classification;
        message.ClassificationStatus = InboundStatus.HumanClassified;
        message.UpdatedAt = time.GetUtcNow();
        message.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("inbound_message.human_classified", "inbound_message", message.Id, actorUserId, classification, note, pending?.Id), ct);
        return message;
    }

    private async Task DecideAsync(AiSuggestion s, string decision, Guid actorUserId, string? reason, string? correction, CancellationToken ct)
    {
        s.HumanDecision = decision;
        s.DecidedBy = actorUserId;
        s.DecidedAt = time.GetUtcNow();
        s.DecisionReason = reason;
        s.HumanCorrection = correction;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event($"ai.suggestion_{decision}", "ai_suggestion", s.Id, actorUserId, s.Classification, reason, s.Id), ct);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private sealed record ScopeRow(Guid InvoiceId, string InvoiceNumber, string Currency, decimal OpenBalance, DateOnly DueDate);

    private async Task<List<ScopeRow>> ScopeAsync(Guid customerId, CollectionCase? c, CancellationToken ct)
    {
        var query = c is not null
            ? db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Join(db.Invoices, x => x.InvoiceId, i => i.Id, (x, i) => i)
            : db.Invoices.Where(i => i.CustomerId == customerId);
        return await query.Where(i => i.Status == InvoiceStatus.Open && i.BalanceCache > 0m).OrderBy(i => i.DueDate).Take(MaxInvoicesInScope)
            .Select(i => new ScopeRow(i.Id, i.InvoiceNumber, i.Currency, i.BalanceCache, i.DueDate)).ToListAsync(ct);
    }

    private static AiDecision DecideFrom(ClassifyResponse r, List<ScopeRow> scope, bool hasCase, DateOnly today) =>
        AiPolicy.Decide(r, scope.Select(x => new ScopedInvoice(x.InvoiceId, x.InvoiceNumber, x.Currency, x.OpenBalance)).ToList(), hasCase, today);

    private async Task<InboundMessage> LockAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM inbound_messages WHERE id = {id} FOR UPDATE", ct);
        return await db.InboundMessages.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new CaseException("message_not_found");
    }

    private async Task<AiSuggestion> LockSuggestionAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ai_suggestions WHERE id = {id} FOR UPDATE", ct);
        return await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new CaseException("suggestion_not_found");
    }

    /// <summary>What a task or a Proposed promise shows a human: the model's short rationale and the customer's own amount/date words — never a system instruction.</summary>
    private static string? Claim(ClassifyResponse r, string? fullText = null)
    {
        if (fullText is not null) return fullText.Length > 4000 ? fullText[..4000] : fullText;
        var parts = new List<string>();
        if (r.Extracted.MentionedAmountText is { Length: > 0 } a) parts.Add($"amount: \"{a}\"");
        if (r.Extracted.MentionedDateText is { Length: > 0 } d) parts.Add($"date: \"{d}\"");
        if (r.Extracted.PaymentReferenceText is { Length: > 0 } p) parts.Add($"reference: \"{p}\"");
        if (r.Rationale is { Length: > 0 } rationale) parts.Add(rationale);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string DisplayName(Customer c) => c.NameEn ?? c.NameAr ?? c.LegalName ?? c.Code ?? "Customer";

    private static string F3(decimal d) => d.ToString("F3", CultureInfo.InvariantCulture);

    private static string StrOr(JsonElement e, string name, string fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s[..Math.Min(s.Length, 120)] : fallback;

    private static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private AuditEvent Event(string eventType, string entityType, Guid entityId, Guid? actor, string? reasonCode = null, string? note = null, Guid? suggestionId = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = eventType,
        EntityType = entityType,
        EntityId = entityId,
        ReasonCode = reasonCode,
        Note = note,
        AiSuggestionId = suggestionId,
    };
}
