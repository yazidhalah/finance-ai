using System.Text.RegularExpressions;

namespace FinanceAi.Api.Logging;

/// <summary>
/// The redaction layer SEC-41 requires: secrets and personal financial data never reach logs.
/// <para>
/// Two mechanisms, because either alone is insufficient. <b>Field names</b> catch structured values
/// whose key says what they are (<c>password</c>, <c>token</c>, <c>authorization</c>). <b>Patterns</b>
/// catch values that leak without a helpful key — an IBAN pasted into a note, a card number in a
/// customer reply, a national ID in an imported row.
/// </para>
/// <para>
/// Redaction is the last line, not the first: the application does not log request bodies,
/// credentials or prompts in the first place. AC-42 feeds a payload full of secrets through here
/// and asserts none survive.
/// </para>
/// </summary>
public static partial class SensitiveData
{
    public const string Placeholder = "[redacted]";

    /// <summary>Keys whose value is never safe to log, regardless of what it looks like.</summary>
    private static readonly string[] SensitiveKeys =
    [
        "password", "passwd", "pwd", "secret", "token", "accesstoken", "refreshtoken",
        "authorization", "apikey", "api_key", "cookie", "set-cookie", "passwordhash",
        "password_hash", "mfa", "totp", "otp", "connectionstring", "connection_string",
        "smtppassword", "smtp_password", "privatekey", "private_key", "clientsecret",
    ];

    public static bool IsSensitiveKey(string? key) =>
        key is not null &&
        SensitiveKeys.Any(k => key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Contains(k.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));

    /// <summary>Scrubs a rendered log line.</summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        // Order matters. Bearer tokens go first: the keyed-value rule would otherwise consume the
        // word "Bearer" as the whole value and leave the token itself in the log.
        var result = BearerToken().Replace(message, $"Bearer {Placeholder}");
        result = ArgonHash().Replace(result, Placeholder);
        result = QuotedKeyedValue().Replace(result, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Placeholder}\"");
        result = UnquotedKeyedValue().Replace(result, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Placeholder}");
        result = Iban().Replace(result, Placeholder);
        result = PaymentCard().Replace(result, Placeholder);
        result = JordanianNationalId().Replace(result, Placeholder);

        return result;
    }

    /// <summary>A JSON <c>"key": "value"</c> pair whose key is in the sensitive list.</summary>
    [GeneratedRegex(
        """(?<key>"(?:password|passwd|pwd|secret|token|access_?token|refresh_?token|authorization|api_?key|cookie|password_?hash|mfa_?secret|totp|otp|connection_?string|smtp_?password|private_?key|client_?secret)")(?<sep>\s*[:=]\s*")(?<value>[^"]*)"     """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedKeyedValue();

    /// <summary>A bare <c>key=value</c> or <c>key: value</c> pair, as in a connection string or a log line.</summary>
    [GeneratedRegex(
        """(?<key>\b(?:password|passwd|pwd|secret|token|access_?token|refresh_?token|authorization|api_?key|cookie|password_?hash|mfa_?secret|totp|otp|connection_?string|smtp_?password|private_?key|client_?secret)\b)(?<sep>\s*[:=]\s*)(?<value>[^\s",;}&]+)""",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnquotedKeyedValue();

    /// <summary>IBAN, including the Jordanian JO form.</summary>
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Iban();

    /// <summary>13–19 digit payment card numbers, with or without separators.</summary>
    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PaymentCard();

    /// <summary>Jordanian national ID: 10 digits beginning with 9 or 2.</summary>
    [GeneratedRegex(@"\b[92]\d{9}\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JordanianNationalId();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\$argon2[a-z]*\$[^\s""]+", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ArgonHash();
}
