using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FinanceAi.Infrastructure.Reports;

/// <summary>
/// CSV and XLSX writers for the aging report. Both are in-house (slice 4 D-4) so SEC-45 can be tested rather
/// than trusted. A text cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or CR is a formula to
/// Excel: in CSV it is prefixed with <c>'</c>; in XLSX it is written as text under a <c>quotePrefix</c> style,
/// which is how Excel itself stores a leading apostrophe without displaying it.
/// </summary>
public static class AgingExport
{
    public sealed record Cell(string Text, bool IsNumber = false);

    public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<Cell>> Rows, bool RightToLeft, string SheetName);

    public static bool LooksLikeFormula(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>SEC-45. Applied to every text cell; numbers are emitted as numbers and never carry a sign here.</summary>
    public static string EscapeForCsv(string text) => LooksLikeFormula(text) ? "'" + text : text;

    public static byte[] ToCsv(Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sb = new StringBuilder();
        sb.Append('﻿');   // BOM: Excel otherwise guesses the code page and mangles Arabic.
        sb.AppendLine(string.Join(',', table.Headers.Select(h => Quote(EscapeForCsv(h)))));
        foreach (var row in table.Rows)
        {
            sb.AppendLine(string.Join(',', row.Select(c => c.IsNumber ? c.Text : Quote(EscapeForCsv(c.Text)))));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>A minimal SpreadsheetML package: one sheet, inline strings, two cell styles (plain, quotePrefix).</summary>
    public static byte[] ToXlsx(Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{Xml(table.SheetName)}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            // cellXfs 0: default; 1: quotePrefix (text that must stay text); 2: money, three decimals.
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.000"/></numFmts>
                  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="3">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" quotePrefix="1" applyAlignment="1"><alignment horizontal="left"/></xf>
                    <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
                  </cellXfs>
                </styleSheet>
                """);

            var sheet = new StringBuilder();
            sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""").Append('\n');
            sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
            sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(table.RightToLeft ? " rightToLeft=\"1\"" : "").Append("/></sheetViews>");
            sheet.Append("<sheetData>");
            var rowIndex = 1;
            AppendRow(sheet, rowIndex++, table.Headers.Select(h => new Cell(h)).ToList());
            foreach (var row in table.Rows)
            {
                AppendRow(sheet, rowIndex++, row);
            }

            sheet.Append("</sheetData></worksheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return stream.ToArray();
    }

    private static void AppendRow(StringBuilder sheet, int rowIndex, IReadOnlyList<Cell> cells)
    {
        sheet.Append("<row r=\"").Append(rowIndex).Append("\">");
        for (var i = 0; i < cells.Count; i++)
        {
            var reference = ColumnName(i) + rowIndex.ToString(CultureInfo.InvariantCulture);
            var cell = cells[i];
            if (cell.IsNumber)
            {
                sheet.Append("<c r=\"").Append(reference).Append("\" s=\"2\"><v>").Append(cell.Text).Append("</v></c>");
            }
            else
            {
                var style = LooksLikeFormula(cell.Text) ? " s=\"1\"" : "";
                sheet.Append("<c r=\"").Append(reference).Append('"').Append(style).Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                    .Append(Xml(cell.Text)).Append("</t></is></c>");
            }
        }

        sheet.Append("</row>");
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            name = (char)('A' + rem) + name;
            index = (index - 1) / 26;
        }

        return name;
    }

    private static string Xml(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Control characters are not representable in XML 1.0; drop them rather than emit a corrupt file.
            if (ch < 0x20 && ch is not '\t' and not '\n' and not '\r')
            {
                continue;
            }

            sb.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => ch.ToString(),
            });
        }

        return sb.ToString();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.TrimStart());
    }
}
