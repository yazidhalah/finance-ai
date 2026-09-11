using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinanceAi.Infrastructure.Ops;

/// <summary>The JSON envelope every alert delivery carries (webhook body; the email renders the same fields).</summary>
public sealed record AlertEnvelope(
    Guid AlertId, string Kind, string Severity, Guid TenantId, string TenantName, string Summary,
    JsonElement Details, DateTimeOffset RaisedAt);

/// <summary>The webhook leg of the alert path. One implementation posts to <c>ALERT_WEBHOOK_URL</c>; tests capture.</summary>
public interface IAlertWebhook
{
    /// <summary>True when the operator configured a webhook at all.</summary>
    bool Configured { get; }

    /// <summary>Delivers the envelope; throws on any failure (the caller records it and moves on).</summary>
    Task DeliverAsync(AlertEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// A plain JSON POST with an optional bearer (slice 15 D-4). The URL is operator configuration in <c>.env</c>,
/// never a request value, so this is not the user-supplied outbound request SEC-66 forbids.
/// </summary>
public sealed class HttpAlertWebhook : IAlertWebhook
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(5) };

    public static string? Url => Environment.GetEnvironmentVariable("ALERT_WEBHOOK_URL") is { Length: > 0 } u ? u : null;

    public bool Configured => Url is not null;

    public async Task DeliverAsync(AlertEnvelope envelope, CancellationToken ct)
    {
        var url = Url ?? throw new InvalidOperationException("ALERT_WEBHOOK_URL is not set");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(envelope, options: AlertService.JsonOptions) };
        if (Environment.GetEnvironmentVariable("ALERT_WEBHOOK_TOKEN") is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>
/// The alert path of SEC-102: a row (once per tenant, kind and UTC day), a structured log line, an email to
/// <c>ALERT_EMAIL</c> and the webhook. Delivery outcomes are recorded on the row; nothing here throws past the
/// caller, because an alert that breaks the sweep would silence the next one.
/// </summary>
public sealed class AlertService(TenantDbContext db, IMailTransport mail, IAlertWebhook webhook, TimeProvider time, ILogger<AlertService> log)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Comma-separated operator addresses; empty means the email leg is skipped.</summary>
    public static IReadOnlyList<string> Recipients =>
        (Environment.GetEnvironmentVariable("ALERT_EMAIL") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Raises the alert unless one of the same kind was already raised today. Returns the row either way, and whether it is new.</summary>
    public async Task<(Alert Alert, bool Created)> RaiseAsync(string kind, string severity, string summary, object details, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var key = Alert.DedupeKeyFor(kind, now);
        var existing = await db.Alerts.FirstOrDefaultAsync(a => a.DedupeKey == key, ct);
        if (existing is not null)
        {
            return (existing, false);
        }

        var alert = new Alert
        {
            TenantId = db.CurrentTenantId,
            Kind = kind,
            Severity = severity,
            Summary = summary.Length <= 500 ? summary : summary[..500],
            DetailsJson = JsonSerializer.Serialize(details, JsonOptions),
            DedupeKey = key,
            RaisedAt = now,
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);   // the row exists before any delivery is attempted, so a delivery crash cannot lose it

        var tenantName = await db.Tenants.Select(t => t.Name).FirstAsync(ct);
        var envelope = new AlertEnvelope(alert.Id, kind, severity, alert.TenantId, tenantName, alert.Summary, JsonDocument.Parse(alert.DetailsJson).RootElement, now);

        // SEC-101: the log line is the delivery that always happens. Fields only — the redacting logger applies.
        if (severity == AlertSeverity.Critical)
        {
            log.LogCritical("alert_raised kind={Kind} organization={OrganizationId} alert={AlertId} summary={Summary}", kind, alert.TenantId, alert.Id, alert.Summary);
        }
        else
        {
            log.LogWarning("alert_raised kind={Kind} organization={OrganizationId} alert={AlertId} summary={Summary}", kind, alert.TenantId, alert.Id, alert.Summary);
        }

        alert.EmailDelivery = await DeliverEmailAsync(envelope, ct);
        alert.WebhookDelivery = await DeliverWebhookAsync(envelope, ct);
        alert.RowVersion++;
        await db.SaveChangesAsync(ct);
        return (alert, true);
    }

    private async Task<string> DeliverEmailAsync(AlertEnvelope e, CancellationToken ct)
    {
        var recipients = Recipients;
        if (recipients.Count == 0)
        {
            return AlertDelivery.Skipped;
        }

        var subject = $"[finance-ai {e.Severity.ToUpperInvariant()}] {e.Kind} — {e.TenantName}";
        var body = string.Join('\n',
            $"Alert:      {e.Kind} ({e.Severity})",
            $"Alert id:   {e.AlertId}",
            $"Tenant:     {e.TenantName} ({e.TenantId})",
            $"Raised at:  {e.RaisedAt:O}",
            $"Summary:    {e.Summary}",
            "Details:",
            JsonSerializer.Serialize(e.Details, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }),
            string.Empty,
            "First steps: docs/ops/runbook.md §6.");
        try
        {
            foreach (var to in recipients)
            {
                // Not through the outbound switch: an alert is not customer messaging (slice 15 §2).
                await mail.SendAsync(new OutgoingMail(to, subject, body, "en", $"alert-{e.AlertId:N}@finance-ai"), ct);
            }

            return AlertDelivery.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "alert_email_failed alert={AlertId}", e.AlertId);
            return AlertDelivery.Failed;
        }
    }

    private async Task<string> DeliverWebhookAsync(AlertEnvelope e, CancellationToken ct)
    {
        if (!webhook.Configured)
        {
            return AlertDelivery.Skipped;
        }

        try
        {
            await webhook.DeliverAsync(e, ct);
            return AlertDelivery.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "alert_webhook_failed alert={AlertId}", e.AlertId);
            return AlertDelivery.Failed;
        }
    }

    public async Task<Alert?> AcknowledgeAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return null;
        }

        if (alert.AcknowledgedAt is null)
        {
            alert.AcknowledgedAt = time.GetUtcNow();
            alert.AcknowledgedBy = userId;
            alert.RowVersion++;
            await db.SaveChangesAsync(ct);
        }

        return alert;
    }
}
