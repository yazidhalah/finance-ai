namespace FinanceAi.Infrastructure.Configuration;

/// <summary>
/// Loads <c>.env</c> into the process environment for local development. Existing environment
/// variables always win, so a real deployment injects secrets normally and this is a no-op
/// (SEC-67). <c>.env</c> is git-ignored; nothing here ever writes a value back to disk or logs.
/// </summary>
public static class DotEnv
{
    public static void Load(string? startDirectory = null)
    {
        var file = Find(startDirectory ?? Directory.GetCurrentDirectory());
        if (file is null)
        {
            return;
        }

        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"');

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    /// <summary>Walks up from <paramref name="directory"/> to the repository root.</summary>
    private static string? Find(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
