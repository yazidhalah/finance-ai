using System.Security.Cryptography;
using FinanceAi.Domain.Entities;

namespace FinanceAi.Infrastructure.Import;

/// <summary>
/// SEC-44: allowlist by extension <b>and</b> by sniffed content, cap the size, and never let the
/// caller's filename near a path.
/// </summary>
public static class UploadInspector
{
    public sealed record Inspected(ImportFileKind Kind, string SafeFileName, string Sha256Hex);

    public static Inspected Inspect(string? fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            throw new ImportFileException("empty_file", "The upload is empty.");
        }

        if (content.Length > ImportLimits.MaxFileBytes)
        {
            throw new ImportFileException("file_too_large", $"The upload exceeds {ImportLimits.MaxFileBytes} bytes.");
        }

        var safeName = SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();

        var kind = extension switch
        {
            ".csv" => ImportFileKind.Csv,
            ".xlsx" => ImportFileKind.Xlsx,
            _ => throw new ImportFileException("unsupported_file_type", "Only .csv and .xlsx files are accepted."),
        };

        // The extension says what the file claims to be; the bytes say what it is. Both must agree.
        var sniffed = Sniff(content);
        if (sniffed != kind)
        {
            throw new ImportFileException("content_mismatch", $"The file is named {extension} but its content is not.");
        }

        return new Inspected(kind, safeName, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static ImportFileKind Sniff(byte[] content)
    {
        // A zip local-file header. Whether it is a workbook is checked when it is opened.
        if (content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B && content[2] == 0x03 && content[3] == 0x04)
        {
            return ImportFileKind.Xlsx;
        }

        // Text: no NUL bytes in the first 8 KB, and valid UTF-8 there.
        var sample = content.AsSpan(0, Math.Min(content.Length, 8192));
        if (sample.IndexOf((byte)0) >= 0)
        {
            throw new ImportFileException("unsupported_file_type", "The file content is neither CSV text nor a workbook.");
        }

        try
        {
            new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content.AsSpan(0, Math.Min(content.Length, 4096)).ToArray().AsSpan(0, Math.Max(0, Math.Min(content.Length, 4096) - 4)));
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new ImportFileException("unsupported_file_type", "The file content is neither CSV text nor a workbook.");
        }

        return ImportFileKind.Csv;
    }

    /// <summary>The stored name is display metadata only. It is never used to locate anything.</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();

        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
        {
            cleaned = "upload";
        }

        return cleaned.Length > 200 ? cleaned[^200..] : cleaned;
    }
}
