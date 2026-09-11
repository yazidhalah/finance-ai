namespace FinanceAi.Infrastructure.Import;

/// <summary>A parsed spreadsheet: headers in file order, then rows of cells as text. Nothing is interpreted here.</summary>
public sealed record TabularFile(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>Limits that bound what a hostile file can cost (SEC-44, SEC-46).</summary>
public static class ImportLimits
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxRows = 50_000;
    public const int MaxColumns = 200;
    public const int MaxCellChars = 4_000;
    public const int MaxZipEntries = 200;
    public const long MaxZipEntryBytes = 60L * 1024 * 1024;
    public const long MaxZipTotalBytes = 100L * 1024 * 1024;
}

public sealed class ImportFileException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}
