using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Messaging;
using FinanceAi.Infrastructure.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Slice 24: the tenant's SMTP (doc 05 /organization/email-settings — write-only secret, SEC-09 re-authentication to
/// change, SEC-66 host policy) and the signed MTA webhook (doc 05 /webhooks/email-events).
/// </summary>
public static class EmailEndpoints
{
    public static RouteGroupBuilder MapEmailEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/organization/email-settings", GetSettingsAsync).RequiresPermission(Permissions.TenantSettingsWrite).WithName("EmailSettings");
        api.MapPut("/organization/email-settings", PutSettingsAsync).RequiresPermission(Permissions.TenantSettingsWrite).RequiresReauth().WithName("UpdateEmailSettings");
        api.MapPost("/organization/email-settings/test", TestSettingsAsync).RequiresPermission(Permissions.TenantSettingsWrite).WithName("TestEmailSettings");
        // Anonymous and signed: the MTA has no session. The signature is checked before anything is read.
        api.MapPost("/webhooks/email-events", EmailEventsAsync).AllowAnonymousEndpoint().WithName("EmailEvents");
        return api;
    }

    private static async Task<Results<Ok<EmailSettingsResponse>, ProblemHttpResult>> GetSettingsAsync(TenantDbContext db, CancellationToken ct)
    {
        var s = await db.TenantEmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return TypedResults.Ok(Shape(s));
    }

    private static async Task<Results<Ok<EmailSettingsResponse>, ProblemHttpResult>> PutSettingsAsync(EmailSettingsRequest request, HttpContext context, CurrentUser user, TenantDbContext db, ISecretBox secrets, IAuditWriter audit, TimeProvider time, CancellationToken ct)
    {
        var validation = new Validation().Require("smtpHost", request.SmtpHost).MaxLength("smtpHost", request.SmtpHost, 253)
            .Require("fromAddress", request.FromAddress).Email("fromAddress", request.FromAddress).MaxLength("smtpUsername", request.SmtpUsername, 200);
        if (request.SmtpPort is null or < 1 or > 65535) validation.Require("smtpPort", null, "invalid");
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        var host = request.SmtpHost!.Trim();
        if (!await SmtpHostPolicy.IsAllowedResolvedAsync(host, ct))
        {
            return ApiProblems.BusinessRuleProblem(context, "smtp_host_not_allowed", "smtpHost", null);   // SEC-66
        }

        var settings = await db.TenantEmailSettings.FirstOrDefaultAsync(ct);
        var created = settings is null;
        settings ??= new TenantEmailSettings { TenantId = db.CurrentTenantId, SmtpHost = host, FromAddress = request.FromAddress!.Trim() };
        var before = new { settings.SmtpHost, settings.SmtpPort, settings.SmtpTls, settings.SmtpUsername, settings.FromAddress, hasPassword = settings.SmtpPasswordEnc is not null };
        settings.SmtpHost = host;
        settings.SmtpPort = request.SmtpPort!.Value;
        settings.SmtpTls = request.SmtpTls ?? true;
        settings.SmtpUsername = string.IsNullOrWhiteSpace(request.SmtpUsername) ? null : request.SmtpUsername.Trim();
        settings.FromAddress = request.FromAddress!.Trim();
        if (request.SmtpPassword is not null)
        {
            if (!secrets.Available) return ApiProblems.BusinessRuleProblem(context, "secret_store_unavailable", "smtpPassword", null);
            settings.SmtpPasswordEnc = request.SmtpPassword.Length == 0 ? null : secrets.Seal(Encoding.UTF8.GetBytes(request.SmtpPassword));   // SEC-67: sealed, write-only
        }

        settings.UpdatedAt = time.GetUtcNow();
        settings.UpdatedBy = user.UserId;
        if (created) db.TenantEmailSettings.Add(settings); else settings.RowVersion++;
        await db.SaveChangesAsync(ct);
        // The audit row never carries the password, sealed or clear — only whether one is set (SEC-41, SEC-67).
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = user.UserId,
            EventType = "tenant.email_settings_changed",
            EntityType = "tenant",
            EntityId = db.CurrentTenantId,
            Changes = JsonSerializer.Serialize(new { old = before, @new = new { settings.SmtpHost, settings.SmtpPort, settings.SmtpTls, settings.SmtpUsername, settings.FromAddress, hasPassword = settings.SmtpPasswordEnc is not null } }),
        }, ct);
        return TypedResults.Ok(Shape(settings));
    }

    /// <summary>Opens an SMTP session with the stored settings (no mail is sent) and reports. Errors are the server's words, never the credentials.</summary>
    private static async Task<Results<Ok<EmailSettingsTestResponse>, ProblemHttpResult>> TestSettingsAsync(HttpContext context, TenantDbContext db, ISecretBox secrets, CancellationToken ct)
    {
        var settings = await db.TenantEmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is null) return ApiProblems.NotFoundProblem(context);
        var via = TenantSmtpResolver.From(settings, secrets)!;
        if (!await SmtpHostPolicy.IsAllowedResolvedAsync(via.Host, ct)) return TypedResults.Ok(new EmailSettingsTestResponse(false, "smtp_host_not_allowed"));
        try
        {
            using var client = new SmtpClient(via.Host, via.Port) { EnableSsl = via.Tls, Timeout = 8_000, Credentials = via.Username is null ? null : new System.Net.NetworkCredential(via.Username, via.Password ?? string.Empty) };
            // System.Net.Mail has no bare NOOP; a message to the From address itself is the smallest real check.
            using var probe = new MailMessage(via.From, via.From) { Subject = "finance-ai SMTP test", Body = "This message confirms the SMTP settings work. No customer mail was sent." };
            await client.SendMailAsync(probe, ct);
            return TypedResults.Ok(new EmailSettingsTestResponse(true, null));
        }
        catch (Exception ex) when (ex is SmtpException or System.Net.Sockets.SocketException or InvalidOperationException or System.IO.IOException)
        {
            return TypedResults.Ok(new EmailSettingsTestResponse(false, ex.GetType().Name + ": " + ex.Message));
        }
    }

    /// <summary>The MTA's events, HMAC-signed with <c>EMAIL_WEBHOOK_SECRET</c>. Applies Delivered/Bounced to Sent messages; counts the rest.</summary>
    private static async Task<Results<Ok<EmailEventsResponse>, ProblemHttpResult>> EmailEventsAsync(HttpContext context, DbContextOptions<TenantDbContext> options, CancellationToken ct)
    {
        var secret = Environment.GetEnvironmentVariable("EMAIL_WEBHOOK_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            return ApiProblems.Create(context, StatusCodes.Status503ServiceUnavailable, "webhook_not_configured", "EMAIL_WEBHOOK_SECRET is not set.", "errors.webhook_not_configured");
        }

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        context.Request.Body.Position = 0;
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        var given = context.Request.Headers["X-Signature"].ToString();
        if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected)))
        {
            return ApiProblems.Create(context, StatusCodes.Status401Unauthorized, "invalid_signature", "The request signature does not match.", "errors.invalid_signature");
        }

        EmailEventsRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EmailEventsRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("events", "invalid", "errors.validation.events.invalid")]);
        }

        var applied = 0;
        var skipped = 0;
        foreach (var e in request?.Events ?? [])
        {
            // <messageId>.<tenantId>, as the dispatcher set the header. Anything else is skipped, not trusted.
            var parts = (e.MessageId ?? string.Empty).Split('.', 2);
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var messageId) || !Guid.TryParseExact(parts[1], "N", out var tenantId)
                || e.Event is not ("delivered" or "bounced"))
            {
                skipped++;
                continue;
            }

            // One context per event, bound to the tenant the tag names — the same way a background job enters a tenant (SEC-22).
            var tenantContext = new TenantContext();
            tenantContext.Set(tenantId, null);
            await using var db = new TenantDbContext(options, tenantContext);
            await using var scope = await DatabaseScope.EnterTenantAsync(db, tenantId, null, ct);
            var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
            if (message is null || message.Status != MessageStatus.Sent)
            {
                await scope.CompleteAsync(ct);
                skipped++;
                continue;
            }

            var ev = e.Event == "delivered" ? MessageEvent.Delivered : MessageEvent.Bounced;
            var to = MessageMachine.Next(MessageStatus.Sent, ev);
            var at = e.OccurredAt ?? DateTimeOffset.UtcNow;
            message.Status = to;
            if (ev == MessageEvent.Delivered) message.DeliveredAt = at;
            else message.BounceReason = (e.Reason ?? "bounced").Length <= 500 ? e.Reason ?? "bounced" : e.Reason![..500];
            message.RowVersion++;
            if (ev == MessageEvent.Bounced && message.ContactId is { } contactId)
            {
                // Slice 25: the address is now known-dead; the send guard refuses it until the contact is edited.
                var contact = await db.CustomerContacts.FirstOrDefaultAsync(c => c.Id == contactId, ct);
                if (contact is not null && contact.BouncedAt is null)
                {
                    contact.BouncedAt = at;
                    contact.BounceReason = message.BounceReason;
                    contact.UpdatedAt = at;
                }
            }

            await db.SaveChangesAsync(ct);
            await new AuditWriter(db).WriteAsync(new AuditEvent
            {
                TenantId = tenantId,
                ActorKind = ActorKinds.System,
                EventType = ev == MessageEvent.Delivered ? "message.delivered" : "message.bounced",
                EntityType = "message",
                EntityId = message.Id,
                FromState = nameof(MessageStatus.Sent),
                ToState = to.ToString(),
                ReasonCode = ev == MessageEvent.Bounced ? "mta_bounce" : "mta_delivery",
                Note = ev == MessageEvent.Bounced ? message.BounceReason : null,
            }, ct);
            await scope.CompleteAsync(ct);
            applied++;
        }

        return TypedResults.Ok(new EmailEventsResponse(applied, skipped));
    }

    private static EmailSettingsResponse Shape(TenantEmailSettings? s) => s is null
        ? new EmailSettingsResponse(false, null, null, null, null, false, null, null)
        : new EmailSettingsResponse(true, s.SmtpHost, s.SmtpPort, s.SmtpTls, s.SmtpUsername, s.SmtpPasswordEnc is not null, s.FromAddress, s.UpdatedAt);
}
