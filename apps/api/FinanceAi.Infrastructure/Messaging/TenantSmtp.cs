using System.Net;
using System.Net.Sockets;
using System.Text;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Security;

namespace FinanceAi.Infrastructure.Messaging;

/// <summary>What the transport needs to speak to a tenant's own SMTP host (slice 24). Never logged; never returned.</summary>
public sealed record SmtpEndpoint(string Host, int Port, bool Tls, string? Username, string? Password, string From);

/// <summary>
/// SEC-66: a tenant-supplied SMTP host is validated against loopback, private, link-local (the cloud metadata address
/// among them) and unique-local ranges — at write time and again, after DNS resolution, before every connection.
/// <c>SMTP_ALLOW_PRIVATE_HOSTS=1</c> is the documented dev/test switch for Mailpit.
/// </summary>
public static class SmtpHostPolicy
{
    public static bool AllowPrivate => Environment.GetEnvironmentVariable("SMTP_ALLOW_PRIVATE_HOSTS") is "1" or "true";

    public static bool IsAllowedLiteral(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        var h = host.Trim().TrimEnd('.');
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return AllowPrivate;
        }

        return !IPAddress.TryParse(h, out var ip) || IsPublic(ip) || AllowPrivate;
    }

    /// <summary>Resolves and checks every address; false when any resolved address is private (a rebinding-style host is refused whole).</summary>
    public static async Task<bool> IsAllowedResolvedAsync(string host, CancellationToken ct)
    {
        if (!IsAllowedLiteral(host)) return false;
        if (AllowPrivate) return true;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);   // fc00::/7 unique-local
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)          // link-local, incl. 169.254.169.254
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // CGNAT
            || b[0] == 0 || b[0] >= 224);
    }
}

/// <summary>Turns the stored row into what the transport needs, opening the sealed password with the MFA KEK envelope (SEC-67).</summary>
public static class TenantSmtpResolver
{
    public static SmtpEndpoint? From(TenantEmailSettings? settings, ISecretBox secrets)
    {
        if (settings is null) return null;
        ArgumentNullException.ThrowIfNull(secrets);
        string? password = null;
        if (settings.SmtpPasswordEnc is not null && secrets.Available)
        {
            password = Encoding.UTF8.GetString(secrets.Open(settings.SmtpPasswordEnc));
        }

        return new SmtpEndpoint(settings.SmtpHost, settings.SmtpPort, settings.SmtpTls, settings.SmtpUsername, password, settings.FromAddress);
    }
}
