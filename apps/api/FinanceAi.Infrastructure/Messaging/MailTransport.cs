using System.Net.Mail;
using System.Text;

namespace FinanceAi.Infrastructure.Messaging;

public sealed record OutgoingMail(string To, string Subject, string Body, string Language, string MessageId);

/// <summary>What the dispatcher needs from a mail host. One implementation in production; tests may wrap it.</summary>
public interface IMailTransport
{
    /// <summary>Returns the provider's message id (or ours) on success; throws on failure.</summary>
    Task<string> SendAsync(OutgoingMail mail, CancellationToken ct);

    /// <summary>Slice 24: through the tenant's own SMTP (doc 05 email-settings); the host is re-checked against SEC-66 before connecting.</summary>
    Task<string> SendAsync(OutgoingMail mail, SmtpEndpoint? via, CancellationToken ct);
}

/// <summary>
/// Plain SMTP through the host in <c>.env</c> (Mailpit locally) — <c>System.Net.Mail</c>, no dependency, no paid API
/// (slice 8 D-4, CLAUDE.md → Cost Rules). Per-tenant SMTP with write-only secrets is deferred (D-1).
/// </summary>
public sealed class SmtpMailTransport : IMailTransport
{
    public static string Host => Environment.GetEnvironmentVariable("SMTP_HOST") is { Length: > 0 } h ? h : "127.0.0.1";

    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) && p > 0 ? p : 1025;

    public static string From => Environment.GetEnvironmentVariable("MAIL_FROM") is { Length: > 0 } f ? f : "collections@finance-ai.local";

    public Task<string> SendAsync(OutgoingMail mail, CancellationToken ct) => this.SendAsync(mail, null, ct);

    public async Task<string> SendAsync(OutgoingMail mail, SmtpEndpoint? via, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        if (via is not null && !await SmtpHostPolicy.IsAllowedResolvedAsync(via.Host, ct))
        {
            throw new InvalidOperationException("smtp_host_not_allowed");   // SEC-66, at the last moment as well as at write time
        }

        using var message = new MailMessage(via?.From ?? From, mail.To)
        {
            Subject = mail.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = mail.Body,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        message.Headers.Add("Content-Language", mail.Language);
        message.Headers.Add("X-FinanceAi-Message", mail.MessageId);

        using var client = via is null
            ? new SmtpClient(Host, Port) { EnableSsl = false, Timeout = 10_000 }
            : new SmtpClient(via.Host, via.Port) { EnableSsl = via.Tls, Timeout = 10_000, Credentials = via.Username is null ? null : new System.Net.NetworkCredential(via.Username, via.Password ?? string.Empty) };
        await client.SendMailAsync(message, ct);
        return mail.MessageId;
    }
}

/// <summary>SEC-103: the global half of the kill switch. The tenant half is a settings column.</summary>
public static class OutboundSwitch
{
    public static bool GloballyEnabled => Environment.GetEnvironmentVariable("OUTBOUND_SENDING_ENABLED") is not { Length: > 0 } v || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
}
