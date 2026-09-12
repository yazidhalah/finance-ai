using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Messaging;

/// <summary>
/// Doc 05 slice 8. Templates are versioned and human-approved; a message's body is rendered once and frozen
/// (DM-25); every path to <c>Sent</c> carries a named human in <c>approved_by</c> (INV-13). The guards run at
/// <c>send</c> and again in the dispatcher, so nothing queued earlier can slip past a later change of state.
/// The slice doc §2 is the contract for which paths send without a click.
/// </summary>
public sealed class MessagingService(TenantDbContext db, IAuditWriter audit, TimeProvider time, CaseService cases, DisputeService disputes, IMailTransport transport, FinanceAi.Infrastructure.Security.ISecretBox secrets) : IMessagingHooks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const int MaxAttempts = 3;

    public sealed record Rendered(string? Subject, string Body, RenderContext Context);

    public sealed record Guard(string Code, IReadOnlyDictionary<string, string>? Meta = null);

    public sealed record DispatchResult(int Sent, int Failed, int Skipped, string? SkipReason);

    public sealed record CadenceResult(int Drafted, int Queued);

    // ---------------------------------------------------------------------------------------
    // Templates
    // ---------------------------------------------------------------------------------------

    /// <summary>D-3: the system templates arrive as Draft; a tenant user approves each before automatic use.</summary>
    public async Task EnsureSystemTemplatesAsync(CancellationToken ct)
    {
        // Seeds every (key, channel, language) the tenant does not have yet, so a template added by a later slice
        // (slice 10's daily_briefing) reaches existing tenants as a Draft too.
        var existing = await db.Templates.Where(t => t.IsSystem).Select(t => new { t.Key, t.Channel, t.Language }).ToListAsync(ct);
        var have = existing.Select(t => $"{t.Key}|{t.Channel}|{t.Language}").ToHashSet(StringComparer.Ordinal);
        var missing = SystemTemplates.All.Where(s => !have.Contains($"{s.Key}|{s.Channel}|{s.Language}")).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var seed in missing)
        {
            db.Templates.Add(new MessageTemplate
            {
                TenantId = db.CurrentTenantId,
                Key = seed.Key,
                Channel = seed.Channel,
                Language = seed.Language,
                Tone = seed.Tone,
                Subject = seed.Subject,
                Body = seed.Body,
                Version = 1,
                Status = "Draft",
                IsSystem = true,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<MessageTemplate> CreateTemplateAsync(string key, string channel, string language, string tone, string? subject, string body, Guid actorUserId, CancellationToken ct)
    {
        ValidateTemplate(key, channel, language, tone, subject, body);
        var latest = await db.Templates.Where(t => t.Key == key && t.Channel == channel && t.Language == language).MaxAsync(t => (int?)t.Version, ct);
        if (latest is not null)
        {
            throw new CaseException("template_exists", "key");   // change it through a new version, never a second lineage
        }

        var template = new MessageTemplate
        {
            TenantId = db.CurrentTenantId,
            Key = key,
            Channel = channel,
            Language = language,
            Tone = tone,
            Subject = channel == TemplateChannels.Email ? subject : null,
            Body = body,
            Version = 1,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = actorUserId,
        };
        db.Templates.Add(template);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("template.created", "message_template", template.Id, actorUserId, note: $"{key}/{channel}/{language} v1"), ct);
        return template;
    }

    /// <summary>A content change is a new version, Draft again; the previous version stays for history (DM-25).</summary>
    public async Task<MessageTemplate> NewVersionAsync(Guid templateId, string? tone, string? subject, string body, Guid actorUserId, CancellationToken ct)
    {
        var previous = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId && t.DeletedAt == null, ct) ?? throw new CaseException("template_not_found");
        var toneValue = tone ?? previous.Tone;
        ValidateTemplate(previous.Key, previous.Channel, previous.Language, toneValue, subject ?? previous.Subject, body);
        var latest = await db.Templates.Where(t => t.Key == previous.Key && t.Channel == previous.Channel && t.Language == previous.Language).MaxAsync(t => t.Version, ct);

        await db.Templates.Where(t => t.Key == previous.Key && t.Channel == previous.Channel && t.Language == previous.Language && t.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false), ct);

        var next = new MessageTemplate
        {
            TenantId = db.CurrentTenantId,
            Key = previous.Key,
            Channel = previous.Channel,
            Language = previous.Language,
            Tone = toneValue,
            Subject = previous.Channel == TemplateChannels.Email ? subject ?? previous.Subject : null,
            Body = body,
            Version = latest + 1,
            IsSystem = previous.IsSystem,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = actorUserId,
        };
        db.Templates.Add(next);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("template.versioned", "message_template", next.Id, actorUserId, note: $"{next.Key}/{next.Channel}/{next.Language} v{next.Version}"), ct);
        return next;
    }

    public async Task<MessageTemplate> ApproveTemplateAsync(Guid templateId, Guid actorUserId, CancellationToken ct)
    {
        var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId && t.DeletedAt == null, ct) ?? throw new CaseException("template_not_found");
        if (template.Status == "Approved")
        {
            return template;
        }

        template.Status = "Approved";
        template.ApprovedBy = actorUserId;
        template.ApprovedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("template.approved", "message_template", template.Id, actorUserId, note: $"{template.Key}/{template.Channel}/{template.Language} v{template.Version}"), ct);
        return template;
    }

    private static void ValidateTemplate(string key, string channel, string language, string tone, string? subject, string body)
    {
        // Slice 10 D-3: the staff briefing template has its own closed placeholder set; everything else is a customer message.
        Func<string, string?> firstUnknown = BriefingPlaceholders.IsBriefingKey(key) ? BriefingPlaceholders.FirstUnknown : Placeholders.FirstUnknown;
        if (channel is not (TemplateChannels.Email or TemplateChannels.Whatsapp)) throw new CaseException("invalid_channel", "channel");
        if (language is not ("ar" or "en")) throw new CaseException("invalid_language", "language");
        if (!TemplateTones.All.Contains(tone)) throw new CaseException("invalid_tone", "tone");
        if (string.IsNullOrWhiteSpace(body)) throw new CaseException("body_required", "body");
        if (channel == TemplateChannels.Email && string.IsNullOrWhiteSpace(subject)) throw new CaseException("subject_required", "subject");
        if (firstUnknown(body) is { } unknown || (subject is not null && firstUnknown(subject) is { } unknownSubject && (unknown = unknownSubject) is not null))
        {
            throw new CaseException("unknown_placeholder", "body", new Dictionary<string, string> { ["placeholder"] = unknown });
        }
    }

    // ---------------------------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------------------------

    public async Task<RenderContext> ContextForAsync(CollectionCase c, IReadOnlyList<Guid> invoiceIds, string language, CustomerContact? contact, CancellationToken ct)
    {
        var ctx = await cases.ContextAsync(ct);
        var customer = await db.Customers.FirstAsync(x => x.Id == c.CustomerId, ct);
        var tenantName = await db.Tenants.Select(t => t.Name).FirstAsync(ct);
        var invoices = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).OrderBy(i => i.DueDate).ThenBy(i => i.InvoiceNumber).ToListAsync(ct);
        var currency = invoices.FirstOrDefault()?.Currency ?? customer.DefaultCurrency;
        var promise = await db.Promises.Where(p => p.CaseId == c.Id && p.Status == PtpStatus.Active).OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        var dispute = await db.Disputes.Where(d => d.CustomerId == c.CustomerId && (d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer)).OrderByDescending(d => d.RaisedAt).FirstOrDefaultAsync(ct);
        var payment = await db.Payments.Where(p => p.CustomerId == c.CustomerId && p.Status == PaymentStatus.Confirmed).OrderByDescending(p => p.ReceivedDate).ThenByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        var name = language == "ar" ? customer.NameAr ?? customer.NameEn : customer.NameEn ?? customer.NameAr;

        return new RenderContext(
            name ?? string.Empty, contact?.Name ?? name ?? string.Empty, tenantName,
            invoices.Select(i => i.InvoiceNumber).ToList(), invoices.Sum(i => i.BalanceCache), currency,
            invoices.FirstOrDefault()?.DueDate, invoices.Count == 0 ? 0 : ctx.Today.DayNumber - invoices[0].DueDate.DayNumber, c.CaseNumber,
            promise?.PromisedAmount, promise?.PromisedDate, dispute?.ReasonCode, payment?.Amount, ctx.Today);
    }

    public static Rendered Render(string? subject, string body, RenderContext context) =>
        new(subject is null ? null : TemplateRenderer.Render(subject, context), TemplateRenderer.Render(body, context), context);

    /// <summary>SEC-81 / T-48: no invoice number of another customer may appear in what we send.</summary>
    public async Task AssertContentBoundAsync(Guid customerId, string? subject, string body, CancellationToken ct)
    {
        var text = (subject ?? string.Empty) + "\n" + body;
        var tenantId = db.CurrentTenantId;
        var leak = await db.Database.SqlQuery<string>(
            $"""
             SELECT invoice_number AS "Value" FROM invoices
             WHERE tenant_id = {tenantId} AND customer_id <> {customerId} AND status <> 'Void'
               AND position(invoice_number IN {text}) > 0
             LIMIT 1
             """).FirstOrDefaultAsync(ct);
        if (leak is not null)
        {
            throw new CaseException("content_binding_violation", "body", new Dictionary<string, string> { ["invoiceNumber"] = leak });
        }
    }

    // ---------------------------------------------------------------------------------------
    // Compose, approve, send (the human path)
    // ---------------------------------------------------------------------------------------

    public sealed record ComposeInput(Guid CaseId, string Channel, string? Language, Guid? TemplateId, string? Subject, string? Body, IReadOnlyList<Guid>? InvoiceIds, Guid? ContactId);

    public async Task<OutboundMessage> ComposeAsync(ComposeInput input, Guid? actorUserId, bool aiDrafted, Guid? aiSuggestionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == input.CaseId, ct) ?? throw new CaseException("case_not_found");
        if (CaseMachine.IsTerminal(c.Status)) throw new CaseException("case_closed");
        if (input.Channel is not (MessageChannels.Email or MessageChannels.WhatsappClickToChat)) throw new CaseException("invalid_channel", "channel");

        var customer = await db.Customers.FirstAsync(x => x.Id == c.CustomerId, ct);
        var language = input.Language ?? customer.PreferredLanguage;   // UI-14: the customer's, overridable
        if (language is not ("ar" or "en")) throw new CaseException("invalid_language", "language");

        var scope = await db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId).ToListAsync(ct);
        var invoiceIds = (input.InvoiceIds is { Count: > 0 } ? input.InvoiceIds.Distinct().ToList() : scope);
        if (invoiceIds.Any(id => !scope.Contains(id))) throw new CaseException("invoice_not_in_scope", "invoiceIds");

        var contact = await PickContactAsync(c.CustomerId, input.ContactId, input.Channel, ct);

        MessageTemplate? template = null;
        string? subject;
        string body;
        var reasons = new List<string>();
        var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        if (settings.RequireApprovalBeforeSend) reasons.Add("tenant_setting");
        if (aiDrafted) reasons.Add("ai_drafted");
        if (!await db.Messages.AnyAsync(m => m.CustomerId == c.CustomerId && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered), ct)) reasons.Add("first_message");

        if (input.TemplateId is { } templateId)
        {
            template = await db.Templates.FirstOrDefaultAsync(t => t.Id == templateId && t.DeletedAt == null, ct) ?? throw new CaseException("template_not_found", "templateId");
            var expectedChannel = input.Channel == MessageChannels.Email ? TemplateChannels.Email : TemplateChannels.Whatsapp;
            if (template.Channel != expectedChannel) throw new CaseException("template_channel_mismatch", "templateId");
            if (template.Language != language) throw new CaseException("template_language_mismatch", "templateId");
            if (!template.IsApproved) reasons.Add("template_not_approved");
            if (template.Tone == TemplateTones.Final) reasons.Add("final_tone");
            subject = template.Subject;
            body = template.Body;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(input.Body)) throw new CaseException("body_required", "body");
            if (Placeholders.FirstUnknown(input.Body) is { } unknown) throw new CaseException("unknown_placeholder", "body", new Dictionary<string, string> { ["placeholder"] = unknown });
            reasons.Add("free_text");
            subject = input.Subject;
            body = input.Body;
        }

        var rendered = Render(subject, body, await ContextForAsync(c, invoiceIds, language, contact, ct));
        await AssertContentBoundAsync(c.CustomerId, rendered.Subject, rendered.Body, ct);

        var now = time.GetUtcNow();
        var message = new OutboundMessage
        {
            TenantId = db.CurrentTenantId,
            CaseId = c.Id,
            CustomerId = c.CustomerId,
            ContactId = contact?.Id,
            Channel = input.Channel,
            Language = language,
            TemplateId = template?.Id,
            TemplateKey = template?.Key,
            TemplateVersion = template?.Version,
            ToAddress = input.Channel == MessageChannels.Email ? contact?.Email : contact?.PhoneE164,
            Subject = rendered.Subject,
            Body = rendered.Body,
            InvoiceIds = invoiceIds.ToArray(),
            Status = reasons.Count > 0 ? MessageStatus.PendingApproval : MessageStatus.Draft,
            ApprovalRequired = reasons.Count > 0,
            ApprovalReasons = reasons.ToArray(),
            AiDrafted = aiDrafted,
            AiSuggestionId = aiSuggestionId,
            DraftedBy = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition(message, null, message.Status, reasons.Count > 0 ? "composed_pending_approval" : "composed", actorUserId, null), ct);
        await cases.AddActivityAsync(c, ActivityKinds.Note, actorUserId, $"Message drafted ({message.Channel}, {language}{(template is null ? ", free text" : $", {template.Key} v{template.Version}")})",
            new { messageId = message.Id, status = message.Status.ToString(), approvalReasons = reasons }, ct);
        return message;
    }

    private async Task<CustomerContact?> PickContactAsync(Guid customerId, Guid? contactId, string channel, CancellationToken ct)
    {
        var contacts = await db.Set<CustomerContact>().Where(x => x.CustomerId == customerId).ToListAsync(ct);
        if (contactId is { } id)
        {
            return contacts.FirstOrDefault(x => x.Id == id) ?? throw new CaseException("contact_not_found", "contactId");
        }

        bool Reachable(CustomerContact x) => channel == MessageChannels.Email ? !string.IsNullOrWhiteSpace(x.Email) : !string.IsNullOrWhiteSpace(x.PhoneE164);
        return contacts.Where(Reachable).OrderByDescending(x => x.IsBilling).ThenByDescending(x => x.IsPrimary).ThenBy(x => x.CreatedAt).FirstOrDefault();
    }

    /// <summary>PRD-15: a named human, recorded. Freezes the body (it was rendered at compose and never changes).</summary>
    public async Task<OutboundMessage> ApproveAsync(Guid messageId, Guid actorUserId, CancellationToken ct)
    {
        var m = await LockAsync(messageId, ct);
        m.ApprovedBy = actorUserId;
        m.ApprovedAt = time.GetUtcNow();
        m.ApprovalKind = "message";
        await ApplyAsync(m, MessageEvent.Approve, actorUserId, "approved", null, ct);
        return m;
    }

    /// <summary>The guards of doc 05 slice 8, in order. Null means "may send now".</summary>
    public async Task<Guard?> SendGuardAsync(OutboundMessage m, CancellationToken ct)
    {
        var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        var ctx = await cases.ContextAsync(ct);

        if (!OutboundSwitch.GloballyEnabled || !settings.OutboundSendingEnabled) return new Guard("outbound_disabled");
        if (m.Status is MessageStatus.Draft or MessageStatus.PendingApproval) return new Guard("approval_required", new Dictionary<string, string> { ["reasons"] = string.Join(",", m.ApprovalReasons) });
        if (m.Channel == MessageChannels.Email && string.IsNullOrWhiteSpace(m.ToAddress)) return new Guard("no_contact_email");
        // Slice 25: an address the MTA bounced is refused until someone edits it — a known-dead address is not a channel.
        if (m.Channel == MessageChannels.Email && m.ContactId is { } contactId && await db.CustomerContacts.AnyAsync(x => x.Id == contactId && x.BouncedAt != null, ct)) return new Guard("contact_email_bounced");

        if (m.CaseId is { } caseId)
        {
            var c = await db.Cases.FirstAsync(x => x.Id == caseId, ct);
            if (c.AutomationDisabled || c.Status == CaseStatus.Escalated) return new Guard("case_escalated");   // SM-26 / SM-53
            if (c.Status == CaseStatus.OnHold) return new Guard("customer_on_hold");
            var eligibility = await disputes.DunningEligibilityAsync(caseId, ct);   // SM-25, slice 7's guard
            if (m.InvoiceIds.Any(id => eligibility.Any(e => e.InvoiceId == id && !e.Allowed))) return new Guard(DisputeService.DisputeBlocksSend);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(ctx.Timezone);
        var localNow = TimeZoneInfo.ConvertTime(ctx.Now, zone).DateTime;
        if (QuietHours.IsQuiet(TimeOnly.FromDateTime(localNow), settings.QuietHoursStart, settings.QuietHoursEnd))
        {
            var next = QuietHours.NextWindow(localNow, settings.QuietHoursStart, settings.QuietHoursEnd);
            return new Guard("quiet_hours", new Dictionary<string, string> { ["nextWindowAt"] = new DateTimeOffset(next, zone.GetUtcOffset(next)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) });
        }

        if (m.TemplateKey is { } key)
        {
            var window = DuplicateWindowDays(settings.DunningCadenceDays);
            var since = ctx.Now.AddDays(-window);
            if (await db.Messages.AnyAsync(x => x.CustomerId == m.CustomerId && x.TemplateKey == key && x.Id != m.Id && x.SentAt != null && x.SentAt >= since, ct))
            {
                return new Guard("duplicate_send_window", new Dictionary<string, string> { ["windowDays"] = window.ToString(CultureInfo.InvariantCulture) });
            }
        }

        var dayStart = new DateTimeOffset(ctx.Today.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(ctx.Today.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var sentToday = await db.Messages.CountAsync(x => x.SentAt != null && x.SentAt >= dayStart && x.Channel == MessageChannels.Email, ct);
        if (sentToday >= settings.DailySendCap) return new Guard("send_cap_exceeded", new Dictionary<string, string> { ["cap"] = settings.DailySendCap.ToString(CultureInfo.InvariantCulture) });

        return null;
    }

    /// <summary>D-5: "not more often than configured" — the smallest gap between cadence steps.</summary>
    public static int DuplicateWindowDays(IReadOnlyList<int> cadence)
    {
        var steps = cadence.Where(d => d >= 0).Distinct().Order().ToList();
        var gaps = steps.Zip(steps.Skip(1), (a, b) => b - a).Where(g => g > 0).ToList();
        return gaps.Count == 0 ? 7 : gaps.Min();
    }

    /// <summary>Path A/B of the slice doc: the human click. Under path B the click is the approval and is recorded as such.</summary>
    public async Task<OutboundMessage> SendAsync(Guid messageId, Guid actorUserId, string idempotencyKey, CancellationToken ct)
    {
        var m = await LockAsync(messageId, ct);
        if (m.Channel != MessageChannels.Email) throw new CaseException("not_an_email");   // WhatsApp goes through the link path

        if (m.Status == MessageStatus.Draft && !m.ApprovalRequired)
        {
            m.ApprovedBy = actorUserId;
            m.ApprovedAt = time.GetUtcNow();
            m.ApprovalKind = "sender";
            await ApplyAsync(m, MessageEvent.Approve, actorUserId, "approved_by_sender", null, ct);
        }

        if (await SendGuardAsync(m, ct) is { } guard)
        {
            throw new CaseException(guard.Code, null, guard.Meta);
        }

        m.IdempotencyKey = idempotencyKey;
        m.SentBy = actorUserId;
        m.QueuedAt = time.GetUtcNow();
        m.NextAttemptAt = m.QueuedAt;
        await ApplyAsync(m, MessageEvent.Send, actorUserId, "queued", null, ct);
        return m;
    }

    public async Task<OutboundMessage> CancelAsync(Guid messageId, string reason, Guid actorUserId, CancellationToken ct)
    {
        var m = await LockAsync(messageId, ct);
        m.CancelReason = reason;
        await ApplyAsync(m, MessageEvent.Cancel, actorUserId, reason, null, ct);
        return m;
    }

    // ---------------------------------------------------------------------------------------
    // WhatsApp click-to-chat (ADR-0004)
    // ---------------------------------------------------------------------------------------

    public async Task<(OutboundMessage Message, string Link)> WhatsAppLinkAsync(Guid messageId, Guid actorUserId, CancellationToken ct)
    {
        var m = await LockAsync(messageId, ct);
        if (m.Channel != MessageChannels.WhatsappClickToChat) throw new CaseException("not_whatsapp");
        if (string.IsNullOrWhiteSpace(m.ToAddress)) throw new CaseException("no_contact_phone");
        if (m.Status == MessageStatus.PendingApproval) throw new CaseException("approval_required", null, new Dictionary<string, string> { ["reasons"] = string.Join(",", m.ApprovalReasons) });
        // The same guards apply to what a person is about to paste into their phone (SM-25, SM-26, hold).
        if (m.CaseId is { } caseId)
        {
            var c = await db.Cases.FirstAsync(x => x.Id == caseId, ct);
            if (c.AutomationDisabled || c.Status == CaseStatus.Escalated) throw new CaseException("case_escalated");
            if (c.Status == CaseStatus.OnHold) throw new CaseException("customer_on_hold");
            var eligibility = await disputes.DunningEligibilityAsync(caseId, ct);
            if (m.InvoiceIds.Any(id => eligibility.Any(e => e.InvoiceId == id && !e.Allowed))) throw new CaseException(DisputeService.DisputeBlocksSend);
        }

        var link = WhatsAppClickToChat.Link(m.ToAddress, m.Body);
        if (m.Status != MessageStatus.PreparedForManualSend)
        {
            m.WhatsappLinkAt = time.GetUtcNow();
            await ApplyAsync(m, MessageEvent.PrepareManualSend, actorUserId, "whatsapp_link_generated", null, ct);
        }

        return (m, link);
    }

    /// <summary>The user says they sent it from their phone; the timeline records that person and moment.</summary>
    public async Task<OutboundMessage> ConfirmManualSendAsync(Guid messageId, Guid actorUserId, CancellationToken ct)
    {
        var m = await LockAsync(messageId, ct);
        m.SentBy = actorUserId;
        m.SentAt = time.GetUtcNow();
        m.ApprovedBy ??= actorUserId;
        m.ApprovalKind ??= "sender";
        await ApplyAsync(m, MessageEvent.ConfirmManualSend, actorUserId, "manual_send_confirmed", null, ct);
        await AfterSentAsync(m, actorUserId, ct);
        return m;
    }

    // ---------------------------------------------------------------------------------------
    // Dispatch (the outbound worker) — idempotent, guarded again
    // ---------------------------------------------------------------------------------------

    public async Task<DispatchResult> DispatchAsync(CancellationToken ct)
    {
        var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        if (!OutboundSwitch.GloballyEnabled || !settings.OutboundSendingEnabled)
        {
            return new DispatchResult(0, 0, await db.Messages.CountAsync(m => m.Status == MessageStatus.Queued, ct), "outbound_disabled");
        }

        var now = time.GetUtcNow();
        // Slice 24: the tenant's own SMTP when it has one (doc 05 email-settings); the .env host otherwise.
        var via = TenantSmtpResolver.From(await db.TenantEmailSettings.AsNoTracking().FirstOrDefaultAsync(ct), secrets);
        var due = await db.Messages.Where(m => m.Status == MessageStatus.Queued && (m.NextAttemptAt == null || m.NextAttemptAt <= now)).OrderBy(m => m.QueuedAt).Select(m => m.Id).ToListAsync(ct);
        var sent = 0;
        var failed = 0;
        var skipped = 0;
        string? skipReason = null;

        foreach (var id in due)
        {
            var m = await LockAsync(id, ct);
            if (m.Status != MessageStatus.Queued)
            {
                continue;
            }

            // Everything is re-checked: the world may have changed since the click (a dispute, a hold, the cap).
            if (await SendGuardAsync(m, ct) is { } guard)
            {
                skipped++;
                skipReason ??= guard.Code;
                if (guard.Code == "quiet_hours" && guard.Meta?["nextWindowAt"] is { } next)
                {
                    m.NextAttemptAt = DateTimeOffset.Parse(next, CultureInfo.InvariantCulture);
                    await db.SaveChangesAsync(ct);
                }

                continue;
            }

            try
            {
                // The header value the MTA echoes back in its events (slice 24 webhook): the message and its tenant.
                var providerId = await transport.SendAsync(new OutgoingMail(m.ToAddress!, m.Subject ?? string.Empty, m.Body, m.Language, $"{m.Id:N}.{db.CurrentTenantId:N}"), via, ct);
                m.ProviderMessageId = providerId;
                m.SentAt = time.GetUtcNow();
                m.Attempts++;
                await ApplyAsync(m, MessageEvent.Dispatched, null, "sent", null, ct);
                await AfterSentAsync(m, null, ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.Attempts++;
                m.FailureReason = ex.GetType().Name + ": " + ex.Message;
                if (m.Attempts >= MaxAttempts)
                {
                    await ApplyAsync(m, MessageEvent.DispatchFailed, null, "dispatch_failed", m.FailureReason, ct);
                    failed++;
                }
                else
                {
                    m.NextAttemptAt = time.GetUtcNow().AddMinutes(5 * m.Attempts);
                    await db.SaveChangesAsync(ct);
                    skipped++;
                }
            }
        }

        return new DispatchResult(sent, failed, skipped, skipReason);
    }

    /// <summary>C3: a sent message puts the case on a follow-up; the next cadence step is the default follow-up.</summary>
    private async Task AfterSentAsync(OutboundMessage m, Guid? actorUserId, CancellationToken ct)
    {
        if (m.CaseId is not { } caseId)
        {
            return;
        }

        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct);
        if (c is null || CaseMachine.IsTerminal(c.Status))
        {
            return;
        }

        await cases.AddActivityAsync(c, m.Channel == MessageChannels.Email ? ActivityKinds.EmailSent : ActivityKinds.WhatsappPrepared, actorUserId,
            $"Sent: {m.Subject ?? m.Body[..Math.Min(80, m.Body.Length)]}", new { messageId = m.Id, channel = m.Channel, templateKey = m.TemplateKey, to = m.ToAddress }, ct);
        c.LastContactAt = m.SentAt ?? time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // A first message to a fresh case is also its first contact (C2), then C3.
        if (CaseMachine.Peek(c.Status, CaseEvent.MessageSent) is null && CaseMachine.Peek(c.Status, CaseEvent.ContactLogged) is not null)
        {
            await cases.FireAsync(c, CaseEvent.ContactLogged, actorUserId, "message_sent", null, ct);
        }

        if (CaseMachine.Peek(c.Status, CaseEvent.MessageSent) is not null)
        {
            var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
            var followUp = DuplicateWindowDays(settings.DunningCadenceDays);
            await cases.FireAsync(c, CaseEvent.MessageSent, actorUserId, "message_sent", null, ct);
            await cases.SuppressAsync(c, time.GetUtcNow().AddDays(followUp), "follow_up", ct);
        }
        else
        {
            await cases.RescoreOnlyAsync(c, ct);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Cadence (path C of the slice doc) — run by the daily sweep
    // ---------------------------------------------------------------------------------------

    public async Task<CadenceResult> RunCadenceAsync(CancellationToken ct)
    {
        await EnsureSystemTemplatesAsync(ct);
        var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        var ctx = await cases.ContextAsync(ct);
        var steps = settings.DunningCadenceDays.Where(d => d >= 0).Distinct().ToHashSet();
        var drafted = 0;
        var queued = 0;

        var eligible = await db.Cases.Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned
                && c.Status != CaseStatus.OnHold && c.Status != CaseStatus.Escalated && c.Status != CaseStatus.Disputed && c.Status != CaseStatus.PromiseActive
                && (c.NextActionAt == null || c.NextActionAt <= ctx.Now))
            .ToListAsync(ct);

        foreach (var c in eligible)
        {
            if (!steps.Contains(c.MaxDaysPastDue))
            {
                continue;
            }

            var key = SystemTemplates.KeyForCadenceStep(c.MaxDaysPastDue);
            var customer = await db.Customers.FirstAsync(x => x.Id == c.CustomerId, ct);
            var template = await db.Templates.Where(t => t.Key == key && t.Channel == TemplateChannels.Email && t.Language == customer.PreferredLanguage && t.IsActive && t.DeletedAt == null)
                .OrderByDescending(t => t.Version).FirstOrDefaultAsync(ct);
            if (template is null)
            {
                continue;
            }

            // Idempotent per day and per window: never a second draft for the same step, never inside the duplicate window.
            var window = ctx.Now.AddDays(-DuplicateWindowDays(settings.DunningCadenceDays));
            if (await db.Messages.AnyAsync(m => m.CustomerId == c.CustomerId && m.TemplateKey == key && m.CreatedAt >= window && m.Status != MessageStatus.Cancelled && m.Status != MessageStatus.Failed, ct))
            {
                continue;
            }

            OutboundMessage message;
            try
            {
                message = await ComposeAsync(new ComposeInput(c.Id, MessageChannels.Email, customer.PreferredLanguage, template.Id, null, null, null, null), null, false, null, ct);
            }
            catch (CaseException)
            {
                continue;   // no scope, no reachable contact, a leak guard: the cadence never forces a message
            }

            drafted++;
            if (!message.ApprovalRequired && template.IsApproved)
            {
                // Path C: the human who approved this exact wording stands behind the send.
                message.ApprovedBy = template.ApprovedBy;
                message.ApprovedAt = time.GetUtcNow();
                message.ApprovalKind = "template";
                await ApplyAsync(message, MessageEvent.Approve, null, "approved_by_template", null, ct);
                if (await SendGuardAsync(message, ct) is null)
                {
                    message.QueuedAt = time.GetUtcNow();
                    message.NextAttemptAt = message.QueuedAt;
                    await ApplyAsync(message, MessageEvent.Send, null, "cadence", null, ct);
                    queued++;
                }
            }
        }

        return new CadenceResult(drafted, queued);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<OutboundMessage> LockAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM messages WHERE id = {id} FOR UPDATE", ct);
        return await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new CaseException("message_not_found");
    }

    private async Task ApplyAsync(OutboundMessage m, MessageEvent e, Guid? actorUserId, string reasonCode, string? note, CancellationToken ct)
    {
        var from = m.Status;
        var to = MessageMachine.Next(from, e);
        m.Status = to;
        m.UpdatedAt = time.GetUtcNow();
        m.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition(m, from, to, MessageMachine.EventName(e), actorUserId, note, reasonCode), ct);
    }

    private AuditEvent Transition(OutboundMessage m, MessageStatus? from, MessageStatus to, string @event, Guid? actor, string? note, string? reasonCode = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = "message.status_changed",
        EntityType = "message",
        EntityId = m.Id,
        FromState = from?.ToString(),
        ToState = to.ToString(),
        ReasonCode = reasonCode ?? @event,
        Note = note,
        AiSuggestionId = m.AiSuggestionId,
        Changes = JsonSerializer.Serialize(new { @event, approvalKind = m.ApprovalKind, to = m.ToAddress is null ? null : "[redacted]" }, Json),
    };

    private AuditEvent Event(string eventType, string entityType, Guid entityId, Guid? actor, string? reasonCode = null, string? note = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = eventType,
        EntityType = entityType,
        EntityId = entityId,
        ReasonCode = reasonCode,
        Note = note,
    };
}

/// <summary>What the daily sweep needs from messaging (resolved at call time, like the other hooks).</summary>
public interface IMessagingHooks
{
    Task<MessagingService.CadenceResult> RunCadenceAsync(CancellationToken ct);

    Task<MessagingService.DispatchResult> DispatchAsync(CancellationToken ct);
}
