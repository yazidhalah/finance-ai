namespace FinanceAi.TestSupport;

/// <summary>Locates the repository from a test assembly, so tests can read the specification.</summary>
public static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    public static string Docs => Path.Combine(Root, "docs");

    public static string SourceRoot => Path.Combine(Root, "apps");

    public static string Path_(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "CLAUDE.md")) &&
                Directory.Exists(System.IO.Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test assembly.");
    }
}
