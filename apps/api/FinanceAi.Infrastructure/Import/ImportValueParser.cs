using System.Globalization;

namespace FinanceAi.Infrastructure.Import;

/// <summary>
/// Turns the text in a cell into a typed value, or says exactly why it cannot. Money is parsed to
/// <c>decimal</c> at scale ≤ 3 and never rounded: an amount with four decimals is the customer's
/// mistake to see, not ours to hide (FIN-02, FIN-05).
/// </summary>
public static class ImportValueParser
{
    public enum MoneyError { None, Invalid, TooManyDecimals, Negative }

    public static MoneyError TryParseMoney(string? text, char decimalSeparator, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return MoneyError.Invalid;
        }

        var thousands = decimalSeparator == ',' ? '.' : ',';
        var normalized = text.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)   // NBSP, common in exports
            .Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal)
            .Replace(decimalSeparator, '.');

        // Accounting exports write negatives as (1,250.500).
        if (normalized.StartsWith('(') && normalized.EndsWith(')'))
        {
            normalized = "-" + normalized[1..^1];
        }

        if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return MoneyError.Invalid;
        }

        if (value.Scale > 3)
        {
            return MoneyError.TooManyDecimals;
        }

        if (value < 0m)
        {
            return MoneyError.Negative;
        }

        return MoneyError.None;
    }

    /// <summary>
    /// The configured format first; ISO 8601 second, because XLSX date cells arrive as ISO after
    /// serial conversion whatever format the text dates use.
    /// </summary>
    public static bool TryParseDate(string? text, string dateFormat, out DateOnly value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // A date-time string ("2026-09-10 00:00:00") is a date with noise appended.
        var candidates = new[] { trimmed, trimmed.Split(' ', 'T')[0] };

        foreach (var candidate in candidates)
        {
            if (DateOnly.TryParseExact(candidate, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out value) ||
                DateOnly.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseRate(string? text, char decimalSeparator, out decimal rate)
    {
        rate = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().Replace(decimalSeparator, '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate)
            && rate > 0m && rate.Scale <= 8;
    }

    public static bool IsValidDateFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 20)
        {
            return false;
        }

        try
        {
            _ = new DateOnly(2026, 9, 10).ToString(format, CultureInfo.InvariantCulture);
            return format.Contains('y', StringComparison.Ordinal) && format.Contains('M', StringComparison.Ordinal) && format.Contains('d', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
