using System.IO.Compression;
using System.Text;
using FinanceAi.Infrastructure.Reports;

namespace FinanceAi.UnitTests.Reports;

/// <summary>SEC-45 / T-80 / AC-14: the export writers, byte for byte.</summary>
public sealed class AgingExportTests
{
    private const string Payload = "=cmd|' /C calc'!A0";

    private static AgingExport.Table Table(bool rtl) => new(
        ["Customer", "Currency", "Total"],
        [
            [new(Payload), new("JOD"), new("1250.500", IsNumber: true)],
            [new("+44 rings"), new("JOD"), new("0.000", IsNumber: true)],
            [new("شركة الأمل"), new("JOD"), new("7.000", IsNumber: true)],
            [new("Quote \"me\", please"), new("JOD"), new("1.000", IsNumber: true)],
        ],
        rtl, "Aging");

    [Fact]
    public void Csv_PrefixesFormulas_QuotesText_LeavesNumbers_AndStartsWithABom()
    {
        var bytes = AgingExport.ToCsv(Table(false));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("\"Customer\",\"Currency\",\"Total\"", lines[0]);
        Assert.Equal("\"'" + Payload + "\",\"JOD\",1250.500", lines[1]);   // the literal, apostrophe-prefixed
        Assert.StartsWith("\"'+44 rings\"", lines[2], StringComparison.Ordinal);
        Assert.Contains("شركة الأمل", lines[3], StringComparison.Ordinal);
        Assert.Equal("\"Quote \"\"me\"\", please\",\"JOD\",1.000", lines[4]);
        Assert.DoesNotContain("\n=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_KeepsFormulaTextAsQuotePrefixedString_AndSetsRightToLeft()
    {
        var bytes = AgingExport.ToXlsx(Table(rtl: true));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sheet = Read(zip, "xl/worksheets/sheet1.xml");
        var styles = Read(zip, "xl/styles.xml");

        Assert.Contains("rightToLeft=\"1\"", sheet, StringComparison.Ordinal);
        // The payload is an inline string (never a <f> formula) under the quotePrefix style.
        Assert.Contains("<c r=\"A2\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + Payload + "</t></is></c>", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheet, StringComparison.Ordinal);
        Assert.Contains("quotePrefix=\"1\"", styles, StringComparison.Ordinal);
        // Numbers are numeric cells with the money format; text is not.
        Assert.Contains("<c r=\"C2\" s=\"2\"><v>1250.500</v></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A4\" t=\"inlineStr\"><is><t xml:space=\"preserve\">شركة الأمل</t></is></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("&quot;me&quot;", sheet, StringComparison.Ordinal);
        Assert.Contains("[Content_Types].xml", zip.Entries.Select(e => e.FullName));

        var ltr = AgingExport.ToXlsx(Table(rtl: false));
        using var ltrZip = new ZipArchive(new MemoryStream(ltr), ZipArchiveMode.Read);
        Assert.DoesNotContain("rightToLeft", Read(ltrZip, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("+1", true)]
    [InlineData("-1", true)]
    [InlineData("@SUM", true)]
    [InlineData("\tx", true)]
    [InlineData("\rx", true)]
    [InlineData("Petra Supplies", false)]
    [InlineData("", false)]
    public void LooksLikeFormula_MatchesTheSec45List(string text, bool expected) =>
        Assert.Equal(expected, AgingExport.LooksLikeFormula(text));

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
