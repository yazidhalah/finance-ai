using System.IO.Compression;
using System.Reflection;

namespace FinanceAi.Infrastructure.Security;

/// <summary>
/// SEC-01: "checked against a breached-password list" — offline, from a list bundled with the build (slice 25).
/// The list is the ≥ 12-character subset of SecLists' xato-net 1M most-used passwords (MIT), gzipped in the
/// assembly; shorter entries are already refused by the length rule. The check is exact and case-insensitive:
/// a breached password with different capitalisation is still the password an attacker will try.
/// No network call, no third-party service, nothing about the candidate leaves the process (SEC-66, SEC-67).
/// </summary>
public static class BreachedPasswords
{
    private const string Resource = "FinanceAi.Infrastructure.Security.Data.breached-passwords-12plus.txt.gz";

    private static readonly Lazy<HashSet<string>> List = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Count => List.Value.Count;

    public static bool IsBreached(string? password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var trimmed = password.Trim();
        return List.Value.Contains(trimmed) || List.Value.Contains(trimmed.ToLowerInvariant());
    }

    private static HashSet<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Embedded resource {Resource} is missing.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var set = new HashSet<string>(50_000, StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0) set.Add(line);
        }

        return set;
    }
}
