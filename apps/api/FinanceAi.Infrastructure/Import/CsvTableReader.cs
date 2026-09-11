using System.Text;

namespace FinanceAi.Infrastructure.Import;

/// <summary>
/// RFC 4180 with the accommodations real exports need: a BOM, CRLF or LF, and a delimiter that is
/// a comma, a semicolon or a tab (European locales export semicolons). Quoted fields may contain
/// the delimiter, newlines and doubled quotes. No other interpretation: every cell is text.
/// </summary>
public static class CsvTableReader
{
    private static readonly char[] CandidateDelimiters = [',', ';', '\t'];

    public static TabularFile Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string text;
        try
        {
            // Strict decoding: invalid UTF-8 is a sign this is not the text file it claims to be.
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new ImportFileException("invalid_encoding", "The file is not valid UTF-8.");
        }

        if (text.Contains('\0'))
        {
            throw new ImportFileException("invalid_content", "The file contains NUL bytes and is not a CSV.");
        }

        text = text.TrimStart('﻿');

        var records = Parse(text, DetectDelimiter(text));
        if (records.Count == 0 || records[0].Count == 0)
        {
            throw new ImportFileException("empty_file", "The file has no header row.");
        }

        var headers = records[0].Select(h => h.Trim()).ToList();
        if (headers.Count > ImportLimits.MaxColumns)
        {
            throw new ImportFileException("too_many_columns", $"More than {ImportLimits.MaxColumns} columns.");
        }

        var rows = new List<IReadOnlyList<string>>();
        foreach (var record in records.Skip(1))
        {
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (rows.Count >= ImportLimits.MaxRows)
            {
                throw new ImportFileException("too_many_rows", $"More than {ImportLimits.MaxRows} rows.");
            }

            // Pad or truncate to the header width so every row addresses the same columns.
            var cells = new string[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                var value = i < record.Count ? record[i] : string.Empty;
                cells[i] = value.Length > ImportLimits.MaxCellChars ? value[..ImportLimits.MaxCellChars] : value;
            }

            rows.Add(cells);
        }

        return new TabularFile(headers, rows);
    }

    private static char DetectDelimiter(string text)
    {
        var firstLineEnd = text.IndexOfAny(['\r', '\n']);
        var firstLine = firstLineEnd < 0 ? text : text[..firstLineEnd];

        return CandidateDelimiters
            .Select(d => (Delimiter: d, Count: firstLine.Count(c => c == d)))
            .OrderByDescending(x => x.Count)
            .First().Delimiter;
    }

    private static List<List<string>> Parse(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    break;
                default:
                    if (c == delimiter)
                    {
                        record.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(c);
                    }

                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }
}
