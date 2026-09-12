using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Ai;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Messaging;
using FinanceAi.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Briefings;

/// <summary>
/// The daily briefing (doc 05 slice 10). <see cref="ComputeAsync"/> assembles the figures from the computations that
/// already own them; <see cref="GenerateAsync"/> stores them and asks the model to narrate them; the numeric-fidelity
/// guard (AI-81) decides whether a person ever sees the prose. The email goes through slice 8's template approval
/// and the one mail transport.
/// </summary>
public sealed class BriefingService(TenantDbContext db, IAuditWriter audit, TimeProvider time, CaseService cases, AgingService aging, IAiClient ai, IMailTransport mail) : IBriefingHooks
{
    private static readonly string[] Languages = ["ar", "en"];

    public sealed record Generated(IReadOnlyList<DailyBriefing> Briefings, bool Created);

    // ---------------------------------------------------------------------------------------
    // Metrics (FIN-62): each figure comes from the code that already computes it elsewhere.
    // ---------------------------------------------------------------------------------------

    public async Task<BriefingMetrics> ComputeAsync(CaseService.Context context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var today = context.Today;
        var yesterday = today.AddDays(-1);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(context.Timezone);
        var (yStart, yEnd) = LocalDay(yesterday, zone);
        var now = context.Now;
        var baseCurrency = await db.Tenants.Select(t => t.BaseCurrency).FirstAsync(ct);

        // Slice 4: the aging rows and the per-invoice indicative conversion.
        var overdue = await aging.OverdueAsync(today, ct);

        // The previous briefing's figure, whatever language it was generated in: "material aging changes" is the delta.
        var previous = await db.DailyBriefings.Where(b => b.BriefingDate < today).OrderByDescending(b => b.BriefingDate).ThenBy(b => b.Language).Select(b => b.MetricsJson).FirstOrDefaultAsync(ct);
        MetricMoney? change = null;
        if (previous is not null && BriefingMetrics.Deserialize(previous) is { } prev && prev.TotalOverdue.Currency == overdue.BaseCurrency
            && decimal.TryParse(prev.TotalOverdue.Amount, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var prevTotal))
        {
            change = MetricMoney.From(overdue.BaseTotal - prevTotal, overdue.BaseCurrency);
        }

        // Slice 3b: confirmed, unreversed payments received yesterday, base currency only (D-7 in the slice doc).
        var collected = await db.Payments.Where(p => p.ReceivedDate == yesterday && p.Status == PaymentStatus.Confirmed && p.ReversedAt == null && p.Currency == baseCurrency).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        // Slice 6: stored promise states.
        var dueToday = await db.Promises.Where(p => p.Status == PtpStatus.Active && p.PromisedDate == today).ToListAsync(ct);
        var dueTodayAmount = dueToday.Where(p => p.Currency == baseCurrency).Sum(p => p.PromisedAmount);
        var brokenYesterday = await db.Promises.CountAsync(p => p.Status == PtpStatus.Broken && p.EvaluatedAt >= yStart && p.EvaluatedAt < yEnd, ct);

        // Slice 7: disputes raised since yesterday began (the morning briefing covers what came in overnight too), and the SLA clock as the entity defines it.
        var newDisputes = await db.Disputes.CountAsync(d => d.RaisedAt >= yStart, ct);
        var openDisputes = await db.Disputes.Where(d => d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer).ToListAsync(ct);
        var breaching = openDisputes.Count(d => d.IsSlaBreached(now));

        // Slice 5: the queue predicate, verbatim.
        var queue = cases.QueueQuery(now);
        var queueSize = await queue.CountAsync(ct);
        var top = await queue.OrderByDescending(c => c.PriorityScore).ThenByDescending(c => c.MaxDaysPastDue).Take(5)
            .Join(db.Customers, c => c.CustomerId, cu => cu.Id, (c, cu) => new { c.Id, c.CaseNumber, c.OverdueBalanceBase, c.MaxDaysPastDue, c.Status, Name = cu.NameEn ?? cu.NameAr ?? cu.LegalName ?? cu.Code ?? "Customer" })
            .ToListAsync(ct);

        // Slices 7 and 9: what waits for a person.
        var unverified = await db.VerificationTasks.CountAsync(t => t.Status == "Open", ct);
        var unmatched = await db.InboundMessages.CountAsync(m => m.CustomerId == null && m.ClassificationStatus != InboundStatus.HumanClassified && m.ClassificationStatus != InboundStatus.Ignored, ct);
        var needingHuman = await db.InboundMessages.CountAsync(m => m.ClassificationStatus == InboundStatus.Unprocessed || m.ClassificationStatus == InboundStatus.Unclassified, ct);
        var pending = await db.AiSuggestions.CountAsync(s => s.HumanDecision == AiHumanDecisions.Pending && s.Operation == AiOperations.ClassifyCustomerReply, ct);

        return new BriefingMetrics(
            MetricMoney.From(overdue.BaseTotal, overdue.BaseCurrency),
            overdue.ByCurrency.Select(c => new CurrencyOverdue(c.Currency, c.Total.ToString("F3", CultureInfo.InvariantCulture), c.InvoiceCount)).ToList(),
            change,
            MetricMoney.From(collected, baseCurrency),
            new MetricCountAmount(dueToday.Count, MetricMoney.From(dueTodayAmount, baseCurrency)),
            new MetricCount(brokenYesterday),
            new MetricCount(newDisputes),
            new MetricCount(breaching),
            queueSize,
            new MetricCount(unverified),
            new MetricCount(unmatched),
            new MetricCount(needingHuman),
            new MetricCount(pending),
            top.Select(c => new TopCaseMetric(c.Id, c.CaseNumber, c.Name, MetricMoney.From(c.OverdueBalanceBase, overdue.BaseCurrency), c.MaxDaysPastDue, c.Status.ToString())).ToList());
    }

    // ---------------------------------------------------------------------------------------
    // Generation and the guard
    // ---------------------------------------------------------------------------------------

    /// <summary>Today's briefing in both languages. Idempotent unless <paramref name="regenerate"/>; a past date is never written.</summary>
    public async Task<Generated> GenerateAsync(bool regenerate, Guid? actorUserId, CancellationToken ct)
    {
        var context = await cases.ContextAsync(ct);
        var today = context.Today;
        var existing = await db.DailyBriefings.Where(b => b.BriefingDate == today).ToListAsync(ct);
        if (existing.Count == Languages.Length && !regenerate)
        {
            return new Generated(existing.OrderBy(b => b.Language, StringComparer.Ordinal).ToList(), false);
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('briefing'), hashtext({db.CurrentTenantId.ToString()}))", ct);
        var metrics = await ComputeAsync(context, ct);
        var companyName = await db.Tenants.Select(t => t.Name).FirstAsync(ct);
        var result = new List<DailyBriefing>();
        foreach (var language in Languages)
        {
            var row = existing.FirstOrDefault(b => b.Language == language);
            if (row is not null && !regenerate)
            {
                result.Add(row);
                continue;
            }

            var (narrative, highlights, status, suggestionId) = await NarrateAsync(metrics, language, today, companyName, context, actorUserId, ct);
            if (row is null)
            {
                row = new DailyBriefing { TenantId = db.CurrentTenantId, BriefingDate = today, Language = language, MetricsJson = metrics.Serialize() };
                db.DailyBriefings.Add(row);
            }
            else
            {
                row.MetricsJson = metrics.Serialize();
                row.RowVersion++;
            }

            row.Narrative = narrative;
            row.HighlightsJson = JsonSerializer.Serialize(highlights);
            row.NarrativeStatusValue = status;
            row.AiSuggestionId = suggestionId;
            row.GeneratedAt = time.GetUtcNow();
            row.GeneratedBy = actorUserId;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(Event(regenerate && existing.Count > 0 ? "briefing.regenerated" : "briefing.generated", row.Id, actorUserId, status, suggestionId), ct);
            result.Add(row);
        }

        return new Generated(result.OrderBy(b => b.Language, StringComparer.Ordinal).ToList(), true);
    }

    private async Task<(string? Narrative, IReadOnlyList<string> Highlights, string Status, Guid? SuggestionId)> NarrateAsync(BriefingMetrics metrics, string language, DateOnly date, string companyName, CaseService.Context context, Guid? actorUserId, CancellationToken ct)
    {
        if (!context.Settings.BriefingActive)
        {
            return (null, [], NarrativeStatus.Disabled, null);
        }

        // AI-80: strings, from the same document that is stored and returned. Names of the top cases only — no ids.
        var payload = new BriefingRequestPayload(Guid.CreateVersion7(), language, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), companyName, new AiBriefingMetrics(
            new AiBriefingMoney(metrics.TotalOverdue.Amount, metrics.TotalOverdue.Currency),
            metrics.OverdueChange is { } ch ? new AiBriefingMoney(ch.Amount, ch.Currency) : null,
            new AiBriefingMoney(metrics.CollectedYesterday.Amount, metrics.CollectedYesterday.Currency),
            new AiBriefingCountAmount(N(metrics.PromisesDueToday.Count), new AiBriefingMoney(metrics.PromisesDueToday.Amount.Amount, metrics.PromisesDueToday.Amount.Currency)),
            N(metrics.PromisesBrokenYesterday.Count), N(metrics.NewDisputes.Count), N(metrics.DisputesBreachingSla.Count), N(metrics.QueueSize),
            N(metrics.UnverifiedPaymentClaims.Count), N(metrics.UnmatchedReplies.Count), N(metrics.RepliesNeedingAHuman.Count), N(metrics.PendingAiSuggestions.Count),
            metrics.TopCases.Select(c => new AiBriefingTopCase(c.CustomerName, new AiBriefingMoney(c.Amount.Amount, c.Amount.Currency), N(c.DaysPastDue), c.Status)).ToList()));

        var started = time.GetUtcNow();
        var call = await ai.BriefingAsync(payload, ct);
        var latency = (int)Math.Max(0, (time.GetUtcNow() - started).TotalMilliseconds);
        if (call.Status == AiCallStatus.Unavailable)
        {
            return (null, [], NarrativeStatus.Unavailable, null);   // PRD-28: the figures are the briefing
        }

        var suggestion = new AiSuggestion
        {
            TenantId = db.CurrentTenantId,
            Operation = "daily_briefing",
            SubjectType = "tenant",
            SubjectId = db.CurrentTenantId,
            ModelName = "unknown",
            ModelDigest = "unknown",
            PromptVersion = "unknown",
            SchemaVersion = BriefingResponseValidator.SchemaVersion,
            InputRef = JsonSerializer.Serialize(new { date = payload.Date, language, requestId = payload.RequestId, metricKeys = metrics.NumeralsByKey().Keys }, BriefingMetrics.Json),
            InputHash = call.InputHash is { Length: 64 } h && h.All(char.IsAsciiHexDigitLower) ? h : Sha256(JsonSerializer.Serialize(payload, BriefingMetrics.Json)),
            OutputJson = call.Body is { } b ? b.GetRawText() : "{}",
            ValidationStatus = AiValidationStatus.SchemaInvalid,
            RequiresHumanReview = false,
            LatencyMs = latency,
            CreatedAt = time.GetUtcNow(),
            HumanDecision = AiHumanDecisions.Pending,
        };

        BriefingNarration? response = null;
        IReadOnlyList<string> errors = [];
        var valid = call.Status == AiCallStatus.Ok && call.Body is { } body && BriefingResponseValidator.TryParse(body, out response, out errors) && call.ServiceValidationStatus != "schema_invalid";
        if (!valid || response is null)
        {
            suggestion.GuardReason = errors.Count > 0 ? string.Join("; ", errors.Take(6)) : call.ErrorCode ?? "schema_invalid";
            suggestion.OutcomeType = AiOutcomeTypes.None;
            db.AiSuggestions.Add(suggestion);
            await db.SaveChangesAsync(ct);
            await Audit(suggestion, language, ct);
            return (null, [], NarrativeStatus.SchemaInvalid, suggestion.Id);
        }

        suggestion.ModelName = response.Model.Name;
        suggestion.ModelDigest = response.Model.Digest;
        suggestion.PromptVersion = response.Model.PromptVersion;
        suggestion.Confidence = response.Confidence;
        suggestion.ReasonCode = response.ReasonCode;

        // AI-81: the guard, in C#, on the figures the model was given — not on what it claims it used.
        var guard = NumericFidelityGuard.Check(response.Narrative, response.Highlights, response.NumbersUsed, metrics, date);
        if (!guard.Accepted || response.Language != language)
        {
            suggestion.ValidationStatus = AiValidationStatus.RejectedByGuard;
            suggestion.GuardReason = response.Language != language ? $"language: {response.Language}" : "untraceable: " + string.Join(", ", guard.Untraceable.Take(8)) + (guard.UnknownKeys.Count > 0 ? "; unknown keys: " + string.Join(", ", guard.UnknownKeys) : string.Empty);
            suggestion.OutcomeType = AiOutcomeTypes.None;
            db.AiSuggestions.Add(suggestion);
            await db.SaveChangesAsync(ct);
            await Audit(suggestion, language, ct);
            return (null, [], NarrativeStatus.RejectedByGuard, suggestion.Id);
        }

        suggestion.ValidationStatus = AiValidationStatus.Valid;
        suggestion.OutcomeType = AiOutcomeTypes.Activity;
        db.AiSuggestions.Add(suggestion);
        await db.SaveChangesAsync(ct);
        await Audit(suggestion, language, ct);
        return (response.Narrative, response.Highlights, NarrativeStatus.Available, suggestion.Id);
    }

    // ---------------------------------------------------------------------------------------
    // Schedule (the sweep) and delivery (slice 8's transport behind slice 8's approval)
    // ---------------------------------------------------------------------------------------

    /// <summary>Called by the sweep: once tenant-local time has passed <c>briefing_send_at</c>, generate today's briefing and deliver it, once.</summary>
    public async Task<bool> RunScheduledAsync(CancellationToken ct)
    {
        var context = await cases.ContextAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(context.Timezone);
        var localNow = TimeZoneInfo.ConvertTime(context.Now, zone);
        if (TimeOnly.FromDateTime(localNow.DateTime) < context.Settings.BriefingSendAt)
        {
            return false;
        }

        var generated = await GenerateAsync(regenerate: false, actorUserId: null, ct);
        var deliverable = generated.Briefings.FirstOrDefault(b => b.Language == context.Settings.BriefingLanguage) ?? generated.Briefings[0];
        if (deliverable.SentAt is null)
        {
            await DeliverAsync(deliverable, context, ct);
        }

        return generated.Created;
    }

    public async Task<string> DeliverAsync(DailyBriefing briefing, CaseService.Context context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(context);
        var status = await TryDeliverAsync(briefing, context, ct);
        briefing.DeliveryStatus = status;
        briefing.RowVersion++;
        await db.SaveChangesAsync(ct);
        return status;
    }

    private async Task<string> TryDeliverAsync(DailyBriefing briefing, CaseService.Context context, CancellationToken ct)
    {
        var settings = context.Settings;
        if (!settings.BriefingEmailEnabled) return BriefingDeliveryStatus.NotEnabled;
        var recipients = await db.TenantMemberships.Where(m => m.Status == MembershipStatus.Active && settings.BriefingRecipientUserIds.Contains(m.UserId))
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Email).Distinct().ToListAsync(ct);
        if (recipients.Count == 0) return BriefingDeliveryStatus.NoRecipients;
        // Slice 8 path C: the exact wording was approved by a human; the transport is the same one.
        var template = await db.Templates.Where(t => t.Key == BriefingPlaceholders.TemplateKey && t.Channel == TemplateChannels.Email && t.Language == briefing.Language && t.IsActive && t.DeletedAt == null && t.Status == "Approved")
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync(ct);
        if (template is null) return BriefingDeliveryStatus.TemplateNotApproved;
        if (!OutboundSwitch.GloballyEnabled || !settings.OutboundSendingEnabled) return BriefingDeliveryStatus.OutboundDisabled;

        var metrics = BriefingMetrics.Deserialize(briefing.MetricsJson) ?? throw new InvalidOperationException("briefing metrics unreadable");
        var companyName = await db.Tenants.Select(t => t.Name).FirstAsync(ct);
        var subject = BriefingPlaceholders.Render(template.Subject ?? $"{companyName} — {briefing.BriefingDate:yyyy-MM-dd}", companyName, metrics, briefing.BriefingDate, briefing.Narrative);
        var body = BriefingPlaceholders.Render(template.Body, companyName, metrics, briefing.BriefingDate, briefing.Narrative);
        var sent = 0;
        foreach (var to in recipients)
        {
            try
            {
                await mail.SendAsync(new OutgoingMail(to, subject, body, briefing.Language, $"briefing-{briefing.Id}-{sent}"), ct);
                sent++;
            }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or IOException or InvalidOperationException)
            {
                // Recorded as failed; the next sweep retries the whole briefing (the status is not Sent).
            }
        }

        briefing.SentToCount = sent;
        briefing.TemplateId = template.Id;
        briefing.TemplateVersion = template.Version;
        if (sent == 0) return BriefingDeliveryStatus.Failed;
        briefing.SentAt = time.GetUtcNow();
        await audit.WriteAsync(Event("briefing.sent", briefing.Id, null, $"{sent} recipient(s), template v{template.Version}", briefing.AiSuggestionId), ct);
        return BriefingDeliveryStatus.Sent;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static (DateTimeOffset Start, DateTimeOffset End) LocalDay(DateOnly day, TimeZoneInfo zone)
    {
        var startLocal = day.ToDateTime(TimeOnly.MinValue);
        var endLocal = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal)).ToUniversalTime(), new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)).ToUniversalTime());
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private async Task Audit(AiSuggestion s, string language, CancellationToken ct) =>
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorKind = ActorKinds.AiAssisted,
            EventType = "ai.briefing_narrated",
            EntityType = "tenant",
            EntityId = db.CurrentTenantId,
            ReasonCode = s.ValidationStatus,
            AiSuggestionId = s.Id,
            Changes = JsonSerializer.Serialize(new { model = s.ModelName, digest = s.ModelDigest, promptVersion = s.PromptVersion, schemaVersion = s.SchemaVersion, language, confidence = s.Confidence.ToString("0.000", CultureInfo.InvariantCulture), validationStatus = s.ValidationStatus, guard = s.GuardReason, latencyMs = s.LatencyMs, inputHash = s.InputHash }, BriefingMetrics.Json),
        }, ct);

    private AuditEvent Event(string eventType, Guid briefingId, Guid? actor, string? note, Guid? suggestionId) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = eventType,
        EntityType = "daily_briefing",
        EntityId = briefingId,
        Note = note,
        AiSuggestionId = suggestionId,
    };
}

public sealed record BriefingNarration(string Language, string Narrative, IReadOnlyList<string> Highlights, IReadOnlyList<string> NumbersUsed, decimal Confidence, string ReasonCode, FinanceAi.Domain.Ai.AiModelInfo Model);

/// <summary>The backend's own validator for <c>daily_briefing.v1</c> (AI-04, AI-82): strict, no shared code with the service.</summary>
public static class BriefingResponseValidator
{
    public const string SchemaVersion = "daily_briefing.v1";

    private static readonly HashSet<string> Keys = ["schema_version", "language", "narrative", "highlights", "numbers_used", "confidence", "reason_code", "model"];
    private static readonly HashSet<string> Reasons = ["generated_from_metrics", "quiet_day", "insufficient_data"];
    private static readonly HashSet<string> MetricKeys = ["totalOverdue", "overdueChange", "collectedYesterday", "promisesDueToday", "promisesBrokenYesterday", "newDisputes", "disputesBreachingSla", "queueSize", "unverifiedPaymentClaims", "unmatchedReplies", "repliesNeedingAHuman", "pendingAiSuggestions", "topCases", "overdueByCurrency", "date"];

    public static bool TryParse(JsonElement root, out BriefingNarration? response, out IReadOnlyList<string> errors)
    {
        var errs = new List<string>();
        response = null;
        if (root.ValueKind != JsonValueKind.Object) { errors = ["(root): not an object"]; return false; }
        foreach (var p in root.EnumerateObject()) if (!Keys.Contains(p.Name)) errs.Add($"{p.Name}: additional property");
        foreach (var k in Keys) if (!root.TryGetProperty(k, out _)) errs.Add($"{k}: required");
        if (errs.Count > 0) { errors = errs; return false; }

        if (root.GetProperty("schema_version").GetString() != SchemaVersion) errs.Add("schema_version: const");
        var language = root.GetProperty("language").GetString();
        if (language is not ("ar" or "en")) errs.Add("language: enum");
        var narrative = root.GetProperty("narrative").ValueKind == JsonValueKind.String ? root.GetProperty("narrative").GetString()! : null;
        if (narrative is null || narrative.Length is 0 or > 1200) errs.Add("narrative: length");
        var highlights = new List<string>();
        if (root.GetProperty("highlights").ValueKind != JsonValueKind.Array) errs.Add("highlights: type");
        else
        {
            foreach (var h in root.GetProperty("highlights").EnumerateArray())
            {
                if (h.ValueKind != JsonValueKind.String || h.GetString()!.Length is 0 or > 200) errs.Add("highlights: items");
                else highlights.Add(h.GetString()!);
            }
        }

        if (highlights.Count > 5) errs.Add("highlights: maxItems");
        var used = new List<string>();
        if (root.GetProperty("numbers_used").ValueKind != JsonValueKind.Array) errs.Add("numbers_used: type");
        else
        {
            foreach (var k in root.GetProperty("numbers_used").EnumerateArray())
            {
                if (k.ValueKind != JsonValueKind.String || !MetricKeys.Contains(k.GetString()!)) errs.Add("numbers_used: enum");
                else used.Add(k.GetString()!);
            }
        }

        var conf = root.GetProperty("confidence");
        decimal confidence = 0m;
        if (conf.ValueKind != JsonValueKind.Number || !conf.TryGetDecimal(out confidence) || confidence < 0m || confidence > 1m) errs.Add("confidence: range");
        var reason = root.GetProperty("reason_code").GetString();
        if (reason is null || !Reasons.Contains(reason)) errs.Add("reason_code: enum");
        var m = root.GetProperty("model");
        FinanceAi.Domain.Ai.AiModelInfo? model = null;
        if (m.ValueKind != JsonValueKind.Object) errs.Add("model: type");
        else
        {
            foreach (var p in m.EnumerateObject()) if (p.Name is not ("name" or "digest" or "prompt_version" or "latency_ms")) errs.Add($"model/{p.Name}: additional property");
            var name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var digest = m.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var pv = m.TryGetProperty("prompt_version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (name is null || digest is null || pv is null) errs.Add("model: required");
            else model = new FinanceAi.Domain.Ai.AiModelInfo(name, digest, pv, m.TryGetProperty("latency_ms", out var l) && l.TryGetInt32(out var lat) ? lat : null);
        }

        errors = errs;
        if (errs.Count > 0) return false;
        response = new BriefingNarration(language!, narrative!, highlights, used, confidence, reason!, model!);
        return true;
    }
}
