using System.IO.Compression;
using System.Text;

namespace FinanceAi.TestSupport;

/// <summary>
/// Builds minimal .xlsx files for tests: a single sheet, shared strings, a date style, and —
/// when asked — formulas and hostile XML, so the reader's SEC-46 promises can be exercised.
/// </summary>
public static class XlsxBuilder
{
    public sealed record Cell(string? Text = null, double? Number = null, bool IsDate = false, string? Formula = null);

    public static byte[] Build(IReadOnlyList<IReadOnlyList<Cell>> rows, string? extraXmlInSheet = null, int extraEntries = 0)
    {
        var shared = new List<string>();
        int SharedIndex(string s)
        {
            var i = shared.IndexOf(s);
            if (i < 0) { shared.Add(s); i = shared.Count - 1; }
            return i;
        }

        var sheet = new StringBuilder();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var r = 0; r < rows.Count; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c];
                var reference = $"{(char)('A' + c)}{r + 1}";
                if (cell.Formula is not null)
                {
                    sheet.Append($"<c r=\"{reference}\"><f>{cell.Formula}</f><v>{cell.Number ?? 0}</v></c>");
                }
                else if (cell.Number is { } n)
                {
                    sheet.Append(cell.IsDate ? $"<c r=\"{reference}\" s=\"1\"><v>{n}</v></c>" : $"<c r=\"{reference}\"><v>{n}</v></c>");
                }
                else if (cell.Text is not null)
                {
                    sheet.Append($"<c r=\"{reference}\" t=\"s\"><v>{SharedIndex(cell.Text)}</v></c>");
                }
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData>").Append(extraXmlInSheet ?? string.Empty).Append("</worksheet>");

        var sharedXml = new StringBuilder("""<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        foreach (var s in shared)
        {
            sharedXml.Append("<si><t>").Append(System.Security.SecurityElement.Escape(s)).Append("</t></si>");
        }

        sharedXml.Append("</sst>");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""");
            Add(zip, "xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>""");
            Add(zip, "xl/sharedStrings.xml", sharedXml.ToString());
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());

            for (var i = 0; i < extraEntries; i++)
            {
                Add(zip, $"junk/{i}.txt", "x");
            }
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
