using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FinanceAi.Infrastructure.Import;

/// <summary>
/// Reads the first worksheet of an .xlsx file: shared strings, inline strings, numbers, booleans,
/// and — via the cell's number format — dates, which Excel stores as day serials.
/// <para>
/// Written in-house rather than taken from a library so SEC-46 can be <i>asserted</i>: formulas are
/// never evaluated (only a cell's cached <c>&lt;v&gt;</c> is read; <c>&lt;f&gt;</c> is skipped), the
/// XML reader prohibits DTDs and resolves nothing external, and the zip is bounded in entry count,
/// per-entry size and total size before a byte is inflated.
/// </para>
/// </summary>
public static class XlsxTableReader
{
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 64L * 1024 * 1024,
    };

    /// <summary>Built-in Excel number formats that render as dates (ECMA-376 §18.8.30).</summary>
    private static readonly HashSet<int> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47];

    public static TabularFile Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var stream = new MemoryStream(content, writable: false);
        using var zip = OpenBounded(stream);

        var sheetPath = FirstSheetPath(zip);
        var sharedStrings = ReadSharedStrings(zip);
        var dateStyles = ReadDateStyles(zip);

        var entry = zip.GetEntry(sheetPath) ?? throw new ImportFileException("invalid_xlsx", "The workbook's first sheet is missing.");
        var grid = ReadSheet(entry, sharedStrings, dateStyles);

        if (grid.Count == 0)
        {
            throw new ImportFileException("empty_file", "The sheet has no header row.");
        }

        var width = grid.Max(r => r.Count);
        if (width > ImportLimits.MaxColumns)
        {
            throw new ImportFileException("too_many_columns", $"More than {ImportLimits.MaxColumns} columns.");
        }

        var headers = Enumerable.Range(0, width).Select(i => i < grid[0].Count ? grid[0][i].Trim() : string.Empty).ToList();

        var rows = grid.Skip(1)
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(i => i < r.Count ? r[i] : string.Empty).ToList())
            .ToList();

        return new TabularFile(headers, rows);
    }

    private static ZipArchive OpenBounded(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new ImportFileException("invalid_xlsx", "The file is not a valid .xlsx archive.");
        }

        if (zip.Entries.Count > ImportLimits.MaxZipEntries)
        {
            throw new ImportFileException("zip_bomb", $"More than {ImportLimits.MaxZipEntries} archive entries.");
        }

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            // Declared sizes are checked before inflating; the reader below re-checks as it reads,
            // because a declared size is itself untrusted.
            if (entry.Length > ImportLimits.MaxZipEntryBytes)
            {
                throw new ImportFileException("zip_bomb", $"Entry '{entry.FullName}' declares {entry.Length} bytes.");
            }

            total += entry.Length;
            if (total > ImportLimits.MaxZipTotalBytes)
            {
                throw new ImportFileException("zip_bomb", "The archive inflates beyond the permitted total.");
            }
        }

        if (zip.GetEntry("xl/workbook.xml") is null)
        {
            throw new ImportFileException("invalid_xlsx", "The archive is not a workbook.");
        }

        return zip;
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        string? relationshipId = null;

        using (var reader = OpenXml(zip.GetEntry("xl/workbook.xml")!))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                {
                    relationshipId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    break;
                }
            }
        }

        if (relationshipId is null)
        {
            throw new ImportFileException("invalid_xlsx", "The workbook has no sheets.");
        }

        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new ImportFileException("invalid_xlsx", "The workbook has no relationships part.");

        using (var reader = OpenXml(rels))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" && reader.GetAttribute("Id") == relationshipId)
                {
                    var target = reader.GetAttribute("Target") ?? string.Empty;
                    target = target.TrimStart('/');
                    return target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target;
                }
            }
        }

        throw new ImportFileException("invalid_xlsx", "The first sheet's relationship is missing.");
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return strings;
        }

        using var reader = OpenXml(entry);
        var current = new StringBuilder();
        var inItem = false;

        // ReadElementContentAsString advances past the element it reads, so the loop must not
        // Read() again in that branch or it skips the node that follows — often the closing </si>.
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                inItem = true;
                current.Clear();
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                inItem = false;
                strings.Add(Bound(current.ToString()));
            }
            else if (inItem && reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                current.Append(reader.ReadElementContentAsString());
                continue;
            }

            reader.Read();
        }

        return strings;
    }

    /// <summary>Which cell styles (by index) display as dates, from <c>styles.xml</c>.</summary>
    private static HashSet<int> ReadDateStyles(ZipArchive zip)
    {
        var dateStyles = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry is null)
        {
            return dateStyles;
        }

        var customDateFormats = new HashSet<int>();
        var styleIndex = 0;
        var inCellXfs = false;

        using var reader = OpenXml(entry);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                {
                    inCellXfs = false;
                }

                continue;
            }

            switch (reader.LocalName)
            {
                case "numFmt":
                    if (int.TryParse(reader.GetAttribute("numFmtId"), out var id) && LooksLikeDateFormat(reader.GetAttribute("formatCode")))
                    {
                        customDateFormats.Add(id);
                    }

                    break;

                case "cellXfs":
                    inCellXfs = true;
                    break;

                case "xf" when inCellXfs:
                    if (int.TryParse(reader.GetAttribute("numFmtId"), out var fmt) && (BuiltInDateFormats.Contains(fmt) || customDateFormats.Contains(fmt)))
                    {
                        dateStyles.Add(styleIndex);
                    }

                    styleIndex++;
                    break;
            }
        }

        return dateStyles;
    }

    private static bool LooksLikeDateFormat(string? formatCode)
    {
        if (string.IsNullOrEmpty(formatCode))
        {
            return false;
        }

        // Strip quoted literals and colour/condition brackets, then look for day/month/year tokens.
        var stripped = System.Text.RegularExpressions.Regex.Replace(formatCode, "\"[^\"]*\"|\\[[^\\]]*\\]", string.Empty);
        return stripped.Contains('y', StringComparison.OrdinalIgnoreCase) && stripped.Contains('d', StringComparison.OrdinalIgnoreCase)
            || stripped.Contains('m', StringComparison.OrdinalIgnoreCase) && stripped.Contains('d', StringComparison.OrdinalIgnoreCase);
    }

    private static List<List<string>> ReadSheet(ZipArchiveEntry entry, List<string> sharedStrings, HashSet<int> dateStyles)
    {
        var rows = new List<List<string>>();
        List<string>? row = null;

        using var reader = OpenXml(entry);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                if (rows.Count >= ImportLimits.MaxRows + 1)
                {
                    throw new ImportFileException("too_many_rows", $"More than {ImportLimits.MaxRows} rows.");
                }

                row = [];
                rows.Add(row);
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c" && row is not null)
            {
                var column = ColumnIndex(reader.GetAttribute("r"));
                var type = reader.GetAttribute("t");
                var style = int.TryParse(reader.GetAttribute("s"), out var s) ? s : -1;

                var value = ReadCellValue(reader, type, sharedStrings, style, dateStyles);

                while (row.Count <= column)
                {
                    row.Add(string.Empty);
                }

                row[column] = value;
            }
        }

        return rows;
    }

    /// <summary>
    /// Reads a cell's cached value. A <c>&lt;f&gt;</c> (formula) child is skipped outright: the
    /// file's own last-computed <c>&lt;v&gt;</c> is what the user saw, and we never compute anything.
    /// </summary>
    private static string ReadCellValue(XmlReader reader, string? type, List<string> sharedStrings, int style, HashSet<int> dateStyles)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        string? v = null;
        var inlineText = new StringBuilder();

        // A subtree reader leaves the outer reader on this cell's end element when disposed, so
        // sibling cells are never swallowed. ReadElementContentAsString and Skip both advance the
        // subtree reader themselves, hence `continue` rather than a trailing Read().
        using (var cell = reader.ReadSubtree())
        {
            cell.Read();
            while (!cell.EOF)
            {
                if (cell.NodeType == XmlNodeType.Element && cell.Depth > 0)
                {
                    switch (cell.LocalName)
                    {
                        case "v":
                            v = cell.ReadElementContentAsString();
                            continue;
                        case "t" when type == "inlineStr":
                            inlineText.Append(cell.ReadElementContentAsString());
                            continue;
                        case "f":
                            cell.Skip();
                            continue;
                    }
                }

                cell.Read();
            }
        }

        var raw = type switch
        {
            "s" when v is not null && int.TryParse(v, out var index) && index >= 0 && index < sharedStrings.Count => sharedStrings[index],
            "inlineStr" => inlineText.ToString(),
            "b" => v == "1" ? "TRUE" : "FALSE",
            _ => v ?? string.Empty,
        };

        // A numeric cell in a date style is a day serial; render it as ISO so the row parser can
        // treat it like any other date text. Nothing else about the number is touched.
        if ((type is null || type == "n") && dateStyles.Contains(style) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial is >= 1 and < 2958466)
        {
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Math.Floor(serial))).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return Bound(raw);
    }

    private static int ColumnIndex(string? reference)
    {
        var index = 0;
        foreach (var c in reference ?? string.Empty)
        {
            if (!char.IsAsciiLetterUpper(c))
            {
                break;
            }

            index = (index * 26) + (c - 'A' + 1);
        }

        return Math.Max(0, index - 1);
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry)
    {
        // Inflate through a stream that stops at the limit, so a lying central directory cannot
        // turn a 10 MB upload into a multi-gigabyte allocation.
        var bounded = new BoundedStream(entry.Open(), ImportLimits.MaxZipEntryBytes);
        return XmlReader.Create(bounded, SafeXml);
    }

    private static string Bound(string value) =>
        value.Length > ImportLimits.MaxCellChars ? value[..ImportLimits.MaxCellChars] : value;

    private sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        private long read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => this.read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            this.read += n;
            if (this.read > limit)
            {
                throw new ImportFileException("zip_bomb", "An archive entry inflated beyond the permitted size.");
            }

            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
