using System.Text;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Import;
using FinanceAi.TestSupport;

namespace FinanceAi.UnitTests.Import;

public sealed class CsvTableReaderTests
{
    [Fact]
    public void Reads_QuotedFieldsDelimitersAndNewlines()
    {
        var csv = "Invoice No,Customer,Total\n\"INV-001\",\"Al Amal, Trading\",\"1,250.500\"\nINV-002,\"Line\nbreak\",10\n";
        var table = CsvTableReader.Read(Encoding.UTF8.GetBytes(csv));

        Assert.Equal(["Invoice No", "Customer", "Total"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Al Amal, Trading", table.Rows[0][1]);
        Assert.Equal("1,250.500", table.Rows[0][2]);
        Assert.Equal("Line\nbreak", table.Rows[1][1]);
    }

    [Fact]
    public void Detects_SemicolonDelimiter_AndStripsBom()
    {
        var csv = "\uFEFFRef;Amount\r\nA;1.250,500\r\n";
        var table = CsvTableReader.Read(Encoding.UTF8.GetBytes(csv));

        Assert.Equal(["Ref", "Amount"], table.Headers);
        Assert.Equal("1.250,500", table.Rows[0][1]);
    }

    [Fact]
    public void Rejects_InvalidUtf8_AndNulBytes()
    {
        Assert.Equal("invalid_encoding", Assert.Throws<ImportFileException>(() => CsvTableReader.Read([0x41, 0xFF, 0xFE, 0x41])).Code);
        Assert.Equal("invalid_content", Assert.Throws<ImportFileException>(() => CsvTableReader.Read(Encoding.UTF8.GetBytes("a,b\n1\0,2\n"))).Code);
    }

    [Fact]
    public void Skips_BlankRows_AndPadsShortOnes()
    {
        var table = CsvTableReader.Read(Encoding.UTF8.GetBytes("a,b,c\n1\n\n,,\n2,3,4,5\n"));

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["1", "", ""], table.Rows[0]);
        Assert.Equal(["2", "3", "4"], table.Rows[1]);
    }
}

/// <summary>AC-02, AC-16 / SEC-46.</summary>
public sealed class XlsxTableReaderTests
{
    [Fact]
    public void Reads_SharedStringsNumbersAndDateSerials()
    {
        var file = XlsxBuilder.Build(
        [
            [new("Invoice No"), new("Issue Date"), new("Total")],
            [new("INV-001"), new(Number: 46275, IsDate: true), new(Number: 1250.5)],
        ]);

        var table = XlsxTableReader.Read(file);

        Assert.Equal(["Invoice No", "Issue Date", "Total"], table.Headers);
        Assert.Equal("INV-001", table.Rows[0][0]);
        Assert.Equal("2026-09-10", table.Rows[0][1]);   // Excel serial 46275
        Assert.Equal("1250.5", table.Rows[0][2]);
    }

    [Fact]
    public void Xlsx_IgnoresFormulasAndEntities()
    {
        // A formula cell yields only its cached value; the formula text never surfaces anywhere.
        var withFormula = XlsxBuilder.Build(
        [
            [new("A")],
            [new(Formula: "CMD(\"calc\")", Number: 7)],
        ]);

        var table = XlsxTableReader.Read(withFormula);
        Assert.Equal("7", table.Rows[0][0]);

        // A DTD / external entity in sheet XML is refused outright.
        var hostile = Encoding.UTF8.GetString(XlsxBuilder.Build([[new("A")], [new("x")]]));
        Assert.NotEmpty(hostile);

        var withEntity = BuildWithSheetXml(
            """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>&xxe;</t></is></c></row></sheetData></worksheet>""");

        Assert.ThrowsAny<Exception>(() => XlsxTableReader.Read(withEntity));
    }

    [Fact]
    public void Rejects_TooManyEntries()
    {
        var bomb = XlsxBuilder.Build([[new("A")], [new("x")]], extraEntries: ImportLimits.MaxZipEntries + 1);
        Assert.Equal("zip_bomb", Assert.Throws<ImportFileException>(() => XlsxTableReader.Read(bomb)).Code);
    }

    [Fact]
    public void Rejects_NonWorkbookZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            w.Write("not a workbook");
        }

        Assert.Equal("invalid_xlsx", Assert.Throws<ImportFileException>(() => XlsxTableReader.Read(stream.ToArray())).Code);
    }

    private static byte[] BuildWithSheetXml(string sheetXml)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add("xl/workbook.xml", """<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="S" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="w" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add("xl/worksheets/sheet1.xml", sheetXml);
        }

        return stream.ToArray();
    }
}

/// <summary>AC-08, AC-09, AC-10 at the parser level; AC-15's filename half.</summary>
public sealed class ImportValueParserTests
{
    [Theory]
    [InlineData("1,250.500", '.', "1250.500")]
    [InlineData("1.250,500", ',', "1250.500")]
    [InlineData("1250.5", '.', "1250.500")]
    [InlineData(" 1 250,5 ", ',', "1250.500")]
    [InlineData("0", '.', "0.000")]
    public void ParsesMoney_WithEitherSeparator(string text, char separator, string expected)
    {
        Assert.Equal(ImportValueParser.MoneyError.None, ImportValueParser.TryParseMoney(text, separator, out var value));
        Assert.Equal(expected, value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1.2345", '.', ImportValueParser.MoneyError.TooManyDecimals)]
    [InlineData("-5", '.', ImportValueParser.MoneyError.Negative)]
    [InlineData("(5.000)", '.', ImportValueParser.MoneyError.Negative)]
    [InlineData("abc", '.', ImportValueParser.MoneyError.Invalid)]
    [InlineData("", '.', ImportValueParser.MoneyError.Invalid)]
    public void RejectsMoney_ThatIsNotAnExactNonNegativeScale3Decimal(string text, char separator, ImportValueParser.MoneyError expected) =>
        Assert.Equal(expected, ImportValueParser.TryParseMoney(text, separator, out _));

    [Theory]
    [InlineData("10/09/2026", "dd/MM/yyyy", 2026, 9, 10)]
    [InlineData("2026-09-10", "dd/MM/yyyy", 2026, 9, 10)]   // ISO always accepted (XLSX serials arrive as ISO)
    [InlineData("2026-09-10 00:00:00", "yyyy-MM-dd", 2026, 9, 10)]
    public void ParsesDates_InTheConfiguredFormatOrIso(string text, string format, int y, int m, int d)
    {
        Assert.True(ImportValueParser.TryParseDate(text, format, out var value));
        Assert.Equal(new DateOnly(y, m, d), value);
    }

    [Fact]
    public void RejectsDates_ThatDoNotMatch() =>
        Assert.False(ImportValueParser.TryParseDate("09/10/2026", "yyyy-MM-dd", out _));

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\x\\invoices.csv", "invoices.csv")]
    [InlineData("", "upload")]
    [InlineData("..", "upload")]
    [InlineData("in\u0000voices.csv", "invoices.csv")]
    public void SanitizesFileNames(string input, string expected) =>
        Assert.Equal(expected, UploadInspector.SanitizeFileName(input));

    [Fact]
    public void Inspect_RequiresExtensionAndContentToAgree()
    {
        var csvBytes = Encoding.UTF8.GetBytes("a,b\n1,2\n");
        var xlsxBytes = XlsxBuilder.Build([[new("a")], [new("1")]]);

        Assert.Equal(ImportFileKind.Csv, UploadInspector.Inspect("x.csv", csvBytes).Kind);
        Assert.Equal(ImportFileKind.Xlsx, UploadInspector.Inspect("x.xlsx", xlsxBytes).Kind);

        Assert.Equal("content_mismatch", Assert.Throws<ImportFileException>(() => UploadInspector.Inspect("x.xlsx", csvBytes)).Code);
        Assert.Equal("content_mismatch", Assert.Throws<ImportFileException>(() => UploadInspector.Inspect("x.csv", xlsxBytes)).Code);
        Assert.Equal("unsupported_file_type", Assert.Throws<ImportFileException>(() => UploadInspector.Inspect("x.exe", csvBytes)).Code);
        Assert.Equal("file_too_large", Assert.Throws<ImportFileException>(() => UploadInspector.Inspect("x.csv", new byte[ImportLimits.MaxFileBytes + 1])).Code);
    }
}

/// <summary>AC-13 / FIN-04, and FIN-10's single balance function.</summary>
public sealed class MoneyRuleTests
{
    [Fact]
    public void MoneyTotals_RefuseMixedCurrencies()
    {
        var totals = new MoneyTotals();
        totals.Add("JOD", 1250.500m);
        totals.Add("JOD", 0.500m);
        totals.Add("USD", 100.000m);

        Assert.Equal([("JOD", 1251.000m, 2), ("USD", 100.000m, 1)], totals.PerCurrency);

        var ex = Assert.Throws<InvalidOperationException>(() => totals.Single());
        Assert.Contains("FIN-04", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoneyTotals_SingleCurrency_IsExact()
    {
        var totals = new MoneyTotals();
        totals.Add("JOD", 0.001m);
        totals.Add("JOD", 0.002m);
        Assert.Equal(0.003m, totals.Single());
    }

    [Fact]
    public void InvoiceBalance_DerivesExactly_AndRefusesOutOfRange()
    {
        Assert.Equal(1250.500m, InvoiceBalance.Derive(1250.500m));
        Assert.Equal(250.500m, InvoiceBalance.Derive(1250.500m, allocatedPayments: 1000.000m));
        Assert.Equal(0m, InvoiceBalance.Derive(10m, 5m, 3m, 2m));

        Assert.Throws<InvalidOperationException>(() => InvoiceBalance.Derive(10m, allocatedPayments: 11m));
    }
}
