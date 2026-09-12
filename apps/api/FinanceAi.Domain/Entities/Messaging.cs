using System.Globalization;
using System.Text.RegularExpressions;
using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

public enum MessageStatus { Draft, PendingApproval, Approved, Queued, Sent, Delivered, Bounced, Failed, Cancelled, PreparedForManualSend }

public enum MessageEvent
{
    RequestApproval,    // Draft → PendingApproval
    Approve,            // Draft | PendingApproval → Approved (human, ai.suggestions.approve)
    Send,               // Approved → Queued (human, messages.send) — or the cadence when path C holds
    Dispatched,         // Queued → Sent (dispatcher)
    DispatchFailed,     // Queued → Failed (dispatcher, after retries)
    Delivered,          // Sent → Delivered (MTA event; deferred)
    Bounced,            // Sent → Bounced (MTA event; deferred)
    Cancel,             // Draft | PendingApproval | Queued → Cancelled
    PrepareManualSend,  // Draft | Approved → PreparedForManualSend (WhatsApp link)
    ConfirmManualSend,  // PreparedForManualSend → Sent (the user says they sent it)
}

/// <summary>The message machine as one table (T-10). Terminal: Sent, Delivered, Bounced, Failed, Cancelled.</summary>
public static class MessageMachine
{
    private static readonly IReadOnlyDictionary<(MessageStatus, MessageEvent), MessageStatus> Table = new Dictionary<(MessageStatus, MessageEvent), MessageStatus>
    {
        [(MessageStatus.Draft, MessageEvent.RequestApproval)] = MessageStatus.PendingApproval,
        [(MessageStatus.Draft, MessageEvent.Approve)] = MessageStatus.Approved,
        [(MessageStatus.PendingApproval, MessageEvent.Approve)] = MessageStatus.Approved,
        [(MessageStatus.Approved, MessageEvent.Send)] = MessageStatus.Queued,
        [(MessageStatus.Queued, MessageEvent.Dispatched)] = MessageStatus.Sent,
        [(MessageStatus.Queued, MessageEvent.DispatchFailed)] = MessageStatus.Failed,
        [(MessageStatus.Sent, MessageEvent.Delivered)] = MessageStatus.Delivered,
        [(MessageStatus.Sent, MessageEvent.Bounced)] = MessageStatus.Bounced,
        [(MessageStatus.Draft, MessageEvent.Cancel)] = MessageStatus.Cancelled,
        [(MessageStatus.PendingApproval, MessageEvent.Cancel)] = MessageStatus.Cancelled,
        [(MessageStatus.Queued, MessageEvent.Cancel)] = MessageStatus.Cancelled,
        [(MessageStatus.Draft, MessageEvent.PrepareManualSend)] = MessageStatus.PreparedForManualSend,
        [(MessageStatus.Approved, MessageEvent.PrepareManualSend)] = MessageStatus.PreparedForManualSend,
        [(MessageStatus.PreparedForManualSend, MessageEvent.ConfirmManualSend)] = MessageStatus.Sent,
        [(MessageStatus.PreparedForManualSend, MessageEvent.Cancel)] = MessageStatus.Cancelled,
    };

    public static IReadOnlyDictionary<(MessageStatus From, MessageEvent Event), MessageStatus> Transitions => Table;

    /// <summary>Sent is not terminal: the MTA may still report Delivered or Bounced (deferred, slice 8 D-6).</summary>
    public static bool IsTerminal(MessageStatus s) => s is MessageStatus.Delivered or MessageStatus.Bounced or MessageStatus.Failed or MessageStatus.Cancelled;

    public static MessageStatus? Peek(MessageStatus from, MessageEvent e) => Table.TryGetValue((from, e), out var to) ? to : null;

    public static MessageStatus Next(MessageStatus from, MessageEvent e) =>
        Peek(from, e) ?? throw new InvalidTransitionException("message", from.ToString(), e.ToString());

    public static string EventName(MessageEvent e) => e switch
    {
        MessageEvent.RequestApproval => "request_approval",
        MessageEvent.Approve => "approve",
        MessageEvent.Send => "send",
        MessageEvent.Dispatched => "dispatched",
        MessageEvent.DispatchFailed => "dispatch_failed",
        MessageEvent.Delivered => "delivered",
        MessageEvent.Bounced => "bounced",
        MessageEvent.Cancel => "cancel",
        MessageEvent.PrepareManualSend => "prepare_manual_send",
        MessageEvent.ConfirmManualSend => "confirm_manual_send",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}

public static class MessageChannels
{
    public const string Email = "email";
    public const string WhatsappClickToChat = "whatsapp_click_to_chat";
}

public static class TemplateChannels
{
    public const string Email = "email";
    public const string Whatsapp = "whatsapp";
}

public static class TemplateTones
{
    public const string Polite = "polite";
    public const string Neutral = "neutral";
    public const string Firm = "firm";
    /// <summary>Final notices and legal wording: approval always, no exception path (slice 8 §2).</summary>
    public const string Final = "final";

    public static readonly IReadOnlyList<string> All = [Polite, Neutral, Firm, Final];
}

/// <summary>The closed placeholder set (doc 05: unknown → 422). Money placeholders render from decimal as strings.</summary>
public static class Placeholders
{
    public sealed record Definition(string Name, string Type, string Description);

    public static readonly IReadOnlyList<Definition> All =
    [
        new("customer_name", "text", "The customer's name in the message language"),
        new("contact_name", "text", "The recipient contact's name"),
        new("company_name", "text", "Your organization's name"),
        new("invoice_number", "text", "The oldest referenced invoice's number"),
        new("invoice_numbers", "text", "All referenced invoice numbers, comma-separated"),
        new("invoice_count", "number", "How many invoices are referenced"),
        new("amount_due", "money", "Total open balance of the referenced invoices, with currency"),
        new("currency", "text", "The currency code"),
        new("due_date", "date", "The oldest referenced invoice's due date"),
        new("days_past_due", "number", "Days past due of the oldest referenced invoice"),
        new("case_number", "number", "The collection case number"),
        new("promised_amount", "money", "The active promise's amount, with currency"),
        new("promised_date", "date", "The active promise's date"),
        new("dispute_reason", "text", "The open dispute's reason, in the message language"),
        new("payment_amount", "money", "The most recent payment's amount, with currency"),
        new("today", "date", "Today's date in the tenant's calendar"),
    ];

    public static readonly Regex Token = new(@"\{\{\s*([a-z_]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> Used(string text) =>
        Token.Matches(text ?? string.Empty).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Any placeholder outside the set, or null when the text is clean.</summary>
    public static string? FirstUnknown(string text) =>
        Used(text).FirstOrDefault(p => All.All(d => d.Name != p));
}

/// <summary>Everything a render may draw on — assembled by the service from stored rows, never from an AI.</summary>
public sealed record RenderContext(
    string CustomerName, string ContactName, string CompanyName, IReadOnlyList<string> InvoiceNumbers, decimal AmountDue, string Currency,
    DateOnly? DueDate, int DaysPastDue, long? CaseNumber, decimal? PromisedAmount, DateOnly? PromisedDate, string? DisputeReason,
    decimal? PaymentAmount, DateOnly Today);

public static class TemplateRenderer
{
    /// <summary>Substitutes the closed set. Money is formatted from <c>decimal</c> at scale 3 with its currency; nothing is computed here.</summary>
    public static string Render(string text, RenderContext c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Placeholders.Token.Replace(text ?? string.Empty, m => m.Groups[1].Value switch
        {
            "customer_name" => c.CustomerName,
            "contact_name" => c.ContactName,
            "company_name" => c.CompanyName,
            "invoice_number" => c.InvoiceNumbers.FirstOrDefault() ?? string.Empty,
            "invoice_numbers" => string.Join(", ", c.InvoiceNumbers),
            "invoice_count" => c.InvoiceNumbers.Count.ToString(CultureInfo.InvariantCulture),
            "amount_due" => Money(c.AmountDue, c.Currency),
            "currency" => c.Currency,
            "due_date" => c.DueDate is { } d ? Date(d) : string.Empty,
            "days_past_due" => c.DaysPastDue.ToString(CultureInfo.InvariantCulture),
            "case_number" => c.CaseNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "promised_amount" => c.PromisedAmount is { } p ? Money(p, c.Currency) : string.Empty,
            "promised_date" => c.PromisedDate is { } pd ? Date(pd) : string.Empty,
            "dispute_reason" => c.DisputeReason ?? string.Empty,
            "payment_amount" => c.PaymentAmount is { } pa ? Money(pa, c.Currency) : string.Empty,
            "today" => Date(c.Today),
            var other => throw new ArgumentException($"Unknown placeholder '{other}'."),
        });
    }

    /// <summary>UI-17: Western digits in both languages; the amount is the exact stored string plus the code.</summary>
    private static string Money(decimal amount, string currency) => amount.ToString("F3", CultureInfo.InvariantCulture) + " " + currency;

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>The quiet-hours window in tenant time; it may cross midnight (20:00 → 08:00).</summary>
public static class QuietHours
{
    public static bool IsQuiet(TimeOnly now, TimeOnly start, TimeOnly end) =>
        start == end ? false : start < end ? now >= start && now < end : now >= start || now < end;

    /// <summary>The next moment sending is allowed, in local time.</summary>
    public static DateTime NextWindow(DateTime localNow, TimeOnly start, TimeOnly end)
    {
        var t = TimeOnly.FromDateTime(localNow);
        if (!IsQuiet(t, start, end)) return localNow;
        var endToday = localNow.Date.Add(end.ToTimeSpan());
        return endToday > localNow ? endToday : endToday.AddDays(1);
    }
}

/// <summary>ADR-0004: the product renders text and a link; the user sends it.</summary>
public static class WhatsAppClickToChat
{
    public static string Link(string e164, string text)
    {
        var digits = new string((e164 ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 8) throw new ArgumentException("A phone number in E.164 form is required.", nameof(e164));
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(text ?? string.Empty)}";
    }
}

public sealed class MessageTemplate : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Key { get; set; }
    public required string Channel { get; set; }
    public required string Language { get; set; }
    public string Tone { get; set; } = TemplateTones.Polite;
    public string? Subject { get; set; }
    public required string Body { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "Draft";
    public bool IsActive { get; set; } = true;
    public bool IsSystem { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsApproved => this.Status == "Approved" && this.ApprovedBy is not null;
}

public sealed class OutboundMessage : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid? CaseId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? ContactId { get; set; }
    public required string Channel { get; set; }
    public string Direction { get; set; } = "outbound";
    public required string Language { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateKey { get; set; }
    public int? TemplateVersion { get; set; }
    public string? ToAddress { get; set; }
    public string? Subject { get; set; }
    public required string Body { get; set; }
    public Guid[] InvoiceIds { get; set; } = [];
    public MessageStatus Status { get; set; } = MessageStatus.Draft;
    public bool ApprovalRequired { get; set; } = true;
    public string[] ApprovalReasons { get; set; } = [];
    public string? ApprovalKind { get; set; }
    public bool AiDrafted { get; set; }
    public Guid? AiSuggestionId { get; set; }
    public Guid? DraftedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public Guid? SentBy { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? BounceReason { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? FailureReason { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? WhatsappLinkAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

/// <summary>
/// The eleven system templates, per language, written independently (UI-13) — not translated from one
/// another. Seeded as Draft (slice 8 D-3): a tenant user reads and approves each before any automatic use.
/// </summary>
public static class SystemTemplates
{
    public sealed record Seed(string Key, string Channel, string Language, string Tone, string? Subject, string Body);

    /// <summary>Cadence step (days past due) → template key. Matched against the tenant's dunning_cadence_days.</summary>
    public static string KeyForCadenceStep(int daysPastDue) => daysPastDue <= 0 ? "due_today" : $"dunning_{daysPastDue}";

    public static readonly IReadOnlyList<Seed> All =
    [
        new("reminder_before_due", "email", "en", "polite", "A friendly reminder: invoice {{invoice_number}} is due on {{due_date}}",
            "Dear {{contact_name}},\n\nThis is a courtesy note that invoice {{invoice_number}} for {{amount_due}} falls due on {{due_date}}.\n\nIf payment is already on its way, please disregard this message. Otherwise we would be grateful if you could arrange it by the due date.\n\nKind regards,\n{{company_name}}"),
        new("reminder_before_due", "email", "ar", "polite", "تذكير ودّي: الفاتورة {{invoice_number}} تستحق بتاريخ {{due_date}}",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nنودّ تذكيركم بأن الفاتورة رقم {{invoice_number}} بمبلغ {{amount_due}} تستحق السداد بتاريخ {{due_date}}.\n\nإن كانت الدفعة في طريقها إلينا فنرجو تجاهل هذه الرسالة، وإلا فنقدّر لكم ترتيب السداد قبل الموعد.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("due_today", "email", "en", "polite", "Invoice {{invoice_number}} is due today",
            "Dear {{contact_name}},\n\nInvoice {{invoice_number}} for {{amount_due}} is due today, {{due_date}}.\n\nPlease let us know if you need a copy of the invoice or our bank details.\n\nKind regards,\n{{company_name}}"),
        new("due_today", "email", "ar", "polite", "الفاتورة {{invoice_number}} تستحق اليوم",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nالفاتورة رقم {{invoice_number}} بمبلغ {{amount_due}} تستحق السداد اليوم {{due_date}}.\n\nيسعدنا تزويدكم بنسخة من الفاتورة أو بتفاصيل حسابنا البنكي عند الحاجة.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("dunning_7", "email", "en", "polite", "Invoice {{invoice_number}} — {{days_past_due}} days past due",
            "Dear {{contact_name}},\n\nOur records show invoice {{invoice_number}} for {{amount_due}} was due on {{due_date}} and remains open.\n\nCould you let us know when we may expect payment? If there is a problem with the invoice, please tell us and we will look into it straight away.\n\nKind regards,\n{{company_name}}"),
        new("dunning_7", "email", "ar", "polite", "الفاتورة {{invoice_number}} — متأخرة {{days_past_due}} أيام",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nتشير سجلاتنا إلى أن الفاتورة رقم {{invoice_number}} بمبلغ {{amount_due}} كانت مستحقة بتاريخ {{due_date}} ولم تُسدَّد بعد.\n\nنرجو إعلامنا بموعد السداد المتوقع. وإن كان هناك أي ملاحظة على الفاتورة فنرجو إخبارنا لنعالجها فورًا.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("dunning_14", "email", "en", "neutral", "Second reminder: {{invoice_count}} invoice(s) totalling {{amount_due}}",
            "Dear {{contact_name}},\n\nThe following invoices are now past due: {{invoice_numbers}}, totalling {{amount_due}}.\n\nWe have not yet received payment or a reply to our earlier reminder. Please arrange payment or contact us to agree a date.\n\nRegards,\n{{company_name}}"),
        new("dunning_14", "email", "ar", "neutral", "تذكير ثانٍ: {{invoice_count}} فاتورة بإجمالي {{amount_due}}",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nالفواتير التالية تجاوزت موعد استحقاقها: {{invoice_numbers}}، بإجمالي {{amount_due}}.\n\nلم نتلقَّ حتى الآن السداد أو ردًّا على تذكيرنا السابق. نرجو ترتيب السداد أو التواصل معنا للاتفاق على موعد.\n\nمع التقدير،\n{{company_name}}"),
        new("dunning_30", "email", "en", "firm", "Overdue account: {{amount_due}} outstanding for {{days_past_due}} days",
            "Dear {{contact_name}},\n\nYour account with {{company_name}} has {{amount_due}} outstanding, the oldest item ({{invoice_number}}) now {{days_past_due}} days past due.\n\nWe would like to resolve this with you directly. Please contact us within five business days to arrange payment or a payment plan.\n\nRegards,\n{{company_name}}"),
        new("dunning_30", "email", "ar", "firm", "حساب متأخر: {{amount_due}} مستحقة منذ {{days_past_due}} يومًا",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nيبلغ الرصيد المستحق على حسابكم لدى {{company_name}} {{amount_due}}، وأقدم بند فيه (الفاتورة {{invoice_number}}) متأخر منذ {{days_past_due}} يومًا.\n\nنودّ تسوية الأمر معكم مباشرة. نرجو التواصل معنا خلال خمسة أيام عمل لترتيب السداد أو الاتفاق على جدول دفع.\n\nمع التقدير،\n{{company_name}}"),
        new("dunning_60", "email", "en", "firm", "Urgent: {{amount_due}} outstanding for {{days_past_due}} days",
            "Dear {{contact_name}},\n\nDespite our previous reminders, {{amount_due}} remains outstanding on invoices {{invoice_numbers}}.\n\nUnless we receive payment or hear from you within five business days, we will have to review the terms on which we continue to supply your account.\n\nRegards,\n{{company_name}}"),
        new("dunning_60", "email", "ar", "firm", "عاجل: {{amount_due}} مستحقة منذ {{days_past_due}} يومًا",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nرغم تذكيراتنا السابقة، لا يزال مبلغ {{amount_due}} مستحقًا على الفواتير {{invoice_numbers}}.\n\nما لم نتلقَّ السداد أو ردًّا منكم خلال خمسة أيام عمل، سنضطر إلى مراجعة شروط استمرار التعامل مع حسابكم.\n\nمع التقدير،\n{{company_name}}"),
        new("dunning_90", "email", "en", "final", "Final notice before further action: {{amount_due}}",
            "Dear {{contact_name}},\n\nThis is a final notice regarding {{amount_due}} outstanding on invoices {{invoice_numbers}}, the oldest now {{days_past_due}} days past due.\n\nIf full payment is not received within seven days of this notice, {{company_name}} reserves the right to refer the account for further recovery action without further notice.\n\n{{company_name}}"),
        new("dunning_90", "email", "ar", "final", "إشعار نهائي قبل اتخاذ إجراء: {{amount_due}}",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nهذا إشعار نهائي بشأن مبلغ {{amount_due}} المستحق على الفواتير {{invoice_numbers}}، وأقدمها متأخر منذ {{days_past_due}} يومًا.\n\nما لم يُسدَّد المبلغ كاملًا خلال سبعة أيام من تاريخ هذا الإشعار، تحتفظ {{company_name}} بحقها في إحالة الحساب لاتخاذ إجراءات التحصيل دون إشعار آخر.\n\n{{company_name}}"),
        new("ptp_confirm", "email", "en", "polite", "Thank you — payment of {{promised_amount}} expected by {{promised_date}}",
            "Dear {{contact_name}},\n\nThank you for confirming that {{promised_amount}} will be paid by {{promised_date}} against {{invoice_numbers}}.\n\nWe have noted this and will not send further reminders before that date.\n\nKind regards,\n{{company_name}}"),
        new("ptp_confirm", "email", "ar", "polite", "شكرًا لكم — ننتظر سداد {{promised_amount}} بحلول {{promised_date}}",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nنشكركم على تأكيد سداد مبلغ {{promised_amount}} بحلول {{promised_date}} عن الفواتير {{invoice_numbers}}.\n\nسجّلنا ذلك ولن نرسل تذكيرات أخرى قبل هذا الموعد.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("ptp_reminder", "email", "en", "polite", "Reminder of your payment due {{promised_date}}",
            "Dear {{contact_name}},\n\nA gentle reminder that {{promised_amount}} was promised for {{promised_date}} against {{invoice_numbers}}.\n\nKind regards,\n{{company_name}}"),
        new("ptp_reminder", "email", "ar", "polite", "تذكير بموعد سدادكم في {{promised_date}}",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nتذكير لطيف بأن مبلغ {{promised_amount}} كان موعودًا للسداد في {{promised_date}} عن الفواتير {{invoice_numbers}}.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("dispute_ack", "email", "en", "polite", "We have received your query on invoice {{invoice_number}}",
            "Dear {{contact_name}},\n\nThank you for raising your concern about invoice {{invoice_number}} ({{dispute_reason}}). We have logged it and will come back to you within two business days.\n\nNo reminders will be sent on this invoice while we look into it.\n\nKind regards,\n{{company_name}}"),
        new("dispute_ack", "email", "ar", "polite", "استلمنا استفساركم بشأن الفاتورة {{invoice_number}}",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nنشكركم على إبلاغنا بملاحظتكم على الفاتورة رقم {{invoice_number}} ({{dispute_reason}}). سجّلناها وسنعود إليكم خلال يومي عمل.\n\nلن تُرسل أي تذكيرات على هذه الفاتورة ريثما نراجعها.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("payment_thanks", "email", "en", "polite", "Payment received — thank you",
            "Dear {{contact_name}},\n\nWe have received your payment of {{payment_amount}}. Thank you.\n\nKind regards,\n{{company_name}}"),
        new("payment_thanks", "email", "ar", "polite", "استلمنا دفعتكم — شكرًا لكم",
            "الأستاذ/ة {{contact_name}} المحترم/ة،\n\nاستلمنا دفعتكم بمبلغ {{payment_amount}}. شكرًا لكم.\n\nمع خالص التقدير،\n{{company_name}}"),
        new("dunning_7", "whatsapp", "en", "polite", null,
            "Hello {{contact_name}}, this is {{company_name}}. Invoice {{invoice_number}} for {{amount_due}} was due on {{due_date}}. Could you let us know when to expect payment? Thank you."),
        new("dunning_7", "whatsapp", "ar", "polite", null,
            "مرحبًا {{contact_name}}، معكم {{company_name}}. الفاتورة {{invoice_number}} بمبلغ {{amount_due}} كانت مستحقة بتاريخ {{due_date}}. نرجو إعلامنا بموعد السداد المتوقع. شكرًا لكم."),
        new("dunning_30", "whatsapp", "ar", "firm", null,
            "مرحبًا {{contact_name}}، معكم {{company_name}}. الرصيد المستحق على حسابكم {{amount_due}} متأخر منذ {{days_past_due}} يومًا. نرجو التواصل معنا لترتيب السداد."),
        new("dunning_30", "whatsapp", "en", "firm", null,
            "Hello {{contact_name}}, this is {{company_name}}. Your account has {{amount_due}} outstanding, {{days_past_due}} days past due. Please contact us to arrange payment."),
        // Slice 10: the staff briefing email. Its own placeholder set (BriefingPlaceholders); approved by a human before any send.
        new("daily_briefing", "email", "en", "neutral", "{{company_name}} — collections briefing for {{briefing_date}}",
            "Good morning,\n\nCollections at a glance for {{briefing_date}}:\n\n- Total overdue: {{total_overdue}}\n- Collected yesterday: {{collected_yesterday}}\n- Promises due today: {{promises_due_today}} ({{promises_due_amount}})\n- Promises broken yesterday: {{promises_broken_yesterday}}\n- New disputes: {{new_disputes}}; breaching SLA: {{disputes_breaching_sla}}\n- Cases in the queue: {{queue_size}}\n- Payment claims to verify: {{unverified_payment_claims}}; replies waiting for a person: {{replies_needing_a_human}}\n\nTop cases:\n{{top_cases}}\n\n{{narrative}}\n\n{{company_name}} — finance-ai"),
        new("daily_briefing", "email", "ar", "neutral", "{{company_name}} — موجز التحصيل ليوم {{briefing_date}}",
            "صباح الخير،\n\nموجز التحصيل ليوم {{briefing_date}}:\n\n- إجمالي المتأخر: {{total_overdue}}\n- المحصَّل أمس: {{collected_yesterday}}\n- وعود الدفع المستحقة اليوم: {{promises_due_today}} ({{promises_due_amount}})\n- وعود أُخلفت أمس: {{promises_broken_yesterday}}\n- نزاعات جديدة: {{new_disputes}}؛ متجاوزة لمهلة الرد: {{disputes_breaching_sla}}\n- الملفات في قائمة التحصيل: {{queue_size}}\n- ادعاءات دفع بانتظار التحقق: {{unverified_payment_claims}}؛ ردود بانتظار شخص: {{replies_needing_a_human}}\n\nأهم الملفات:\n{{top_cases}}\n\n{{narrative}}\n\n{{company_name}} — finance-ai"),
    ];
}
