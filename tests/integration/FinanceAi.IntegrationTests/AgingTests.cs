using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 4 AC-03 … AC-16 against the real database and API.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgingTests(ApiTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private static JsonElement Section(JsonElement report, string currency) =>
        report.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == currency);

    private static string Bucket(JsonElement section, string key) =>
        section.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("bucket").GetString() == key).GetProperty("amount").GetProperty("amount").GetString()!;

    private static async Task<JsonElement> ReportAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/api/v1/reports/aging" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
    }

    private static async Task PayAsync(HttpClient client, Guid customerId, Guid invoice, decimal amount, string receivedDate) =>
        await client.PostAsync("/api/v1/payments", new
        {
            customerId,
            amount = M(amount),
            method = "BankTransfer",
            receivedDate,
            allocations = new[] { new { invoiceId = invoice, amount = M(amount) } },
        });

    /// <summary>AC-04 / FIN-53 / FIN-54, and AC-03's integration half: bucket sums equal the ledger's open balances.</summary>
    [Fact]
    public async Task Aging_UsesOpenBalance_AndDropsSettled()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var big = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "A-1", 10_000m, dueDate: "2026-08-20", issueDate: "2026-07-20");
        var settled = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "A-2", 500m, dueDate: "2026-08-20", issueDate: "2026-07-20");
        var current = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "A-3", 250m, dueDate: "2026-10-01", issueDate: "2026-09-01");
        await PayAsync(s.Client, s.CustomerId, big, 9_000m, "2026-09-01");
        await PayAsync(s.Client, s.CustomerId, settled, 500m, "2026-09-01");

        var report = await ReportAsync(s.Client, "?asOf=2026-09-11");
        var jod = Section(report, "JOD");
        Assert.Equal("1000.000", Bucket(jod, "Days1To30"));          // 10,000 − 9,000; dpd 22
        Assert.Equal("250.000", Bucket(jod, "Current"));
        Assert.Equal("1250.000", jod.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal(2, jod.GetProperty("invoiceCount").GetInt32());   // the settled one is absent, not "fully settled"

        var detail = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging/customers/{s.CustomerId}?asOf=2026-09-11", ApiScenario.Json);
        var numbers = detail.GetProperty("invoices").EnumerateArray().Select(i => i.GetProperty("invoiceNumber").GetString()).ToList();
        Assert.Equal(["A-1", "A-3"], numbers);
        Assert.DoesNotContain("A-2", numbers);

        // INV-08 against the ledger: the sum over buckets equals what the invoice list says is open.
        var invoices = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices?customerId={s.CustomerId}", ApiScenario.Json);
        var open = invoices.GetProperty("items").EnumerateArray().Sum(i => decimal.Parse(i.GetProperty("openBalance").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        var buckets = jod.GetProperty("buckets").EnumerateArray().Sum(b => decimal.Parse(b.GetProperty("amount").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(open, buckets);
    }

    /// <summary>AC-05 / FIN-58 / T-25: the default as-of is the tenant's calendar day, DST or not.</summary>
    [Theory]
    [InlineData("Asia/Amman", "2026-09-10T20:59:00Z", "2026-09-10T21:01:00Z")]        // UTC+3, no DST
    [InlineData("Europe/London", "2026-09-10T22:59:00Z", "2026-09-10T23:01:00Z")]     // BST, UTC+1
    [InlineData("Europe/London", "2026-12-10T23:59:00Z", "2026-12-11T00:01:00Z")]     // GMT, UTC+0
    public async Task Aging_DayBoundaryIsTenantLocal(string timezone, string beforeMidnight, string afterMidnight)
    {
        var s = await fixture.Api.NewCustomerAsync();
        await fixture.Database.ExecuteAsync("UPDATE tenants SET timezone = @tz WHERE id = @t", ("tz", timezone), ("t", s.Organization.TenantId));
        await fixture.Database.WaiveMfaGraceAsync(s.Organization.TenantId);   // the clock is pinned months ahead below
        var due = DateOnly.FromDateTime(DateTime.Parse(beforeMidnight, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal));
        // For the UTC+ zones the local date before midnight equals the UTC date; the invoice is due on that local day.
        var localDue = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(beforeMidnight, System.Globalization.CultureInfo.InvariantCulture), TimeZoneInfo.FindSystemTimeZoneById(timezone)).Date;
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "TZ", 100m, dueDate: localDue.ToString("yyyy-MM-dd"), issueDate: localDue.AddDays(-30).ToString("yyyy-MM-dd"));
        _ = due;

        try
        {
            fixture.Api.Clock.Override = DateTimeOffset.Parse(beforeMidnight, System.Globalization.CultureInfo.InvariantCulture);
            var before = await ReportAsync(s.Client);
            Assert.Equal(localDue.ToString("yyyy-MM-dd"), before.GetProperty("asOf").GetString());
            Assert.Equal("100.000", Bucket(Section(before, "JOD"), "Current"));

            fixture.Api.Clock.Override = DateTimeOffset.Parse(afterMidnight, System.Globalization.CultureInfo.InvariantCulture);
            var after = await ReportAsync(s.Client);
            Assert.Equal(localDue.AddDays(1).ToString("yyyy-MM-dd"), after.GetProperty("asOf").GetString());
            Assert.Equal("100.000", Bucket(Section(after, "JOD"), "Days1To30"));
            Assert.Equal("0.000", Bucket(Section(after, "JOD"), "Current"));
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-06 / FIN-57 / T-26: later activity does not change the past.</summary>
    [Fact]
    public async Task Aging_AsOf_IsReproducible()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "R-1", 1_000m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        var later = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "R-2", 300m, dueDate: "2026-09-30", issueDate: "2026-09-01");
        _ = later;   // issued after the as-of date: must not appear in the past report
        await PayAsync(s.Client, s.CustomerId, invoice, 400m, "2026-08-15");

        var snapshot10 = (await ReportAsync(s.Client, "?asOf=2026-08-10&groupBy=customer")).GetRawText();
        var snapshot20 = (await ReportAsync(s.Client, "?asOf=2026-08-20&groupBy=customer")).GetRawText();
        Assert.Equal("1000.000", Bucket(Section(JsonDocument.Parse(snapshot10).RootElement, "JOD"), "Days1To30"));   // dpd 9
        Assert.Equal("600.000", Bucket(Section(JsonDocument.Parse(snapshot20).RootElement, "JOD"), "Days1To30"));    // dpd 19
        Assert.Equal(1, Section(JsonDocument.Parse(snapshot10).RootElement, "JOD").GetProperty("invoiceCount").GetInt32());

        // Later activity: a new payment, a reversal of the first allocation, a write-off of the rest.
        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(100m), method = "Cash", receivedDate = "2026-09-01", allocations = new[] { new { invoiceId = invoice, amount = M(100m) } } });
        var first = (await s.Client.InvoiceAsync(invoice)).GetProperty("history").EnumerateArray().First(h => h.GetProperty("amount").GetProperty("amount").GetString() == "400.000").GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/allocations/{first}/reverse", new { reason = "test" });
        var proposal = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectible" });
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        await s.Client.PostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        Assert.Equal("WrittenOff", (await s.Client.InvoiceAsync(invoice)).Status());
        _ = payment;

        Assert.Equal(snapshot10, (await ReportAsync(s.Client, "?asOf=2026-08-10&groupBy=customer")).GetRawText());
        Assert.Equal(snapshot20, (await ReportAsync(s.Client, "?asOf=2026-08-20&groupBy=customer")).GetRawText());
        // And today the invoice is written off: it owes nothing and is absent.
        Assert.DoesNotContain("R-1", (await s.Client.GetStringAsync($"/api/v1/reports/aging/customers/{s.CustomerId}")));
    }

    /// <summary>AC-07: the history path for today agrees with balance_cache for every invoice, after mixed activity.</summary>
    [Fact]
    public async Task Aging_HistoryMatchesCache_Today()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var a = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "H-1", 1_000m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        var b = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "H-2", 2_000m, dueDate: "2026-08-15", issueDate: "2026-07-15");
        var c = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "H-3", 333.335m, dueDate: "2026-08-15", issueDate: "2026-07-15");
        await PayAsync(s.Client, s.CustomerId, a, 400m, "2026-08-10");
        await PayAsync(s.Client, s.CustomerId, b, 2_000m, "2026-08-20");
        var note = await s.Client.PostAsync("/api/v1/credit-notes", new { customerId = s.CustomerId, amount = M(100m), issueDate = "2026-08-21", reasonCode = "agreed_discount" });
        await s.Client.PostAsync($"/api/v1/credit-notes/{note.GetProperty("id").GetGuid()}/applications", new { lines = new[] { new { invoiceId = a, amount = M(100m) } } });
        await s.Client.PostAsync($"/api/v1/invoices/{c}/withholding", new { baseAmount = M(333.335m), ratePct = "5", withheldAmount = M(16.667m) });
        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(200m), method = "Cash", receivedDate = "2026-08-22", allocations = new[] { new { invoiceId = a, amount = M(200m) } } });
        await s.Client.PostAsync($"/api/v1/payments/{payment.GetProperty("id").GetGuid()}/reverse", new { reason = "bounced" });

        var tz = "Asia/Amman";
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(tz)).DateTime);
        var mismatches = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM invoices i
            LEFT JOIN fn_aging(@t, @d, 'due_date', @tz) f ON f.invoice_id = i.id
            WHERE i.tenant_id = @t AND i.status <> 'Void'
              AND coalesce(f.open_balance, 0) <> i.balance_cache
            """, ("t", s.Organization.TenantId), ("d", today), ("tz", tz));
        Assert.Equal(0L, mismatches);

        var reconciliation = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/reconciliation", ApiScenario.Json);
        Assert.Equal(0, reconciliation.GetProperty("mismatches").GetArrayLength());
        Assert.Equal(3, reconciliation.GetProperty("invoicesChecked").GetInt32());
    }

    /// <summary>AC-08 / FIN-59.</summary>
    [Fact]
    public async Task Aging_UnappliedLinesAreSeparate()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var paid = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "U-1", 1_000m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "U-2", 500m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(1_500m), method = "BankTransfer", receivedDate = "2026-08-05", allocations = new[] { new { invoiceId = paid, amount = M(1_000m) } } });
        await s.Client.PostAsync("/api/v1/credit-notes", new { customerId = s.CustomerId, amount = M(200m), issueDate = "2026-08-06", reasonCode = "service_credit" });

        var jod = Section(await ReportAsync(s.Client, "?asOf=2026-09-11"), "JOD");
        Assert.Equal("500.000", jod.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("500.000", jod.GetProperty("unappliedCash").GetProperty("amount").GetString());
        Assert.Equal("200.000", jod.GetProperty("unappliedCredit").GetProperty("amount").GetString());
        // As of a date before the payment, there was no unapplied cash and the paid invoice was still open.
        var earlier = Section(await ReportAsync(s.Client, "?asOf=2026-08-04"), "JOD");
        Assert.Equal("1500.000", earlier.GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("0.000", earlier.GetProperty("unappliedCash").GetProperty("amount").GetString());
        Assert.Equal("0.000", earlier.GetProperty("unappliedCredit").GetProperty("amount").GetString());
    }

    /// <summary>AC-09 / FIN-04 / FIN-55 / E6.</summary>
    [Fact]
    public async Task Aging_PerCurrency_WithIndicativeBase()
    {
        var s = await fixture.Api.NewCustomerAsync();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "C-JOD", 1_000m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "C-USD", 2_000m, currency: "USD", dueDate: "2026-08-01", issueDate: "2026-07-01", fxRateToBase: 0.709m);
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "C-USD2", 0.001m, currency: "USD", dueDate: "2026-08-01", issueDate: "2026-07-01", fxRateToBase: 0.709m);

        var report = await ReportAsync(s.Client, "?asOf=2026-09-11");
        Assert.Equal(["JOD", "USD"], report.GetProperty("currencies").EnumerateArray().Select(c => c.GetProperty("currency").GetString()));
        Assert.Equal("1000.000", Section(report, "JOD").GetProperty("total").GetProperty("amount").GetString());
        Assert.Equal("2000.001", Section(report, "USD").GetProperty("total").GetProperty("amount").GetString());
        var baseTotal = report.GetProperty("baseCurrencyTotal");
        Assert.True(baseTotal.GetProperty("indicative").GetBoolean());
        // 1000 + round(2000 × 0.709) + round(0.001 × 0.709 = 0.000709 → 0.001): per-invoice rounding, then the sum.
        Assert.Equal("2418.001", baseTotal.GetProperty("amount").GetString());
        Assert.Equal("JOD", baseTotal.GetProperty("currency").GetString());
        Assert.DoesNotContain("3000", report.GetRawText(), StringComparison.Ordinal);   // no naive cross-currency sum anywhere

        var usdOnly = await ReportAsync(s.Client, "?asOf=2026-09-11&currency=USD");
        Assert.Single(usdOnly.GetProperty("currencies").EnumerateArray());
    }

    /// <summary>AC-10 / UI-44: by customer, and the invoices behind a cell.</summary>
    [Fact]
    public async Task Aging_ByCustomer_DrillsThrough()
    {
        var s = await fixture.Api.NewCustomerAsync("Alpha");
        var beta = (await s.Client.PostAsync("/api/v1/customers", new { nameEn = "Beta", defaultCurrency = "JOD" })).GetProperty("id").GetGuid();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "D-A1", 100m, dueDate: "2026-09-01", issueDate: "2026-08-01");
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "D-A2", 200m, dueDate: "2026-07-01", issueDate: "2026-06-01");
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, beta, "D-B1", 300m, dueDate: "2026-09-01", issueDate: "2026-08-01");

        var report = await ReportAsync(s.Client, "?asOf=2026-09-11&groupBy=customer");
        var jod = Section(report, "JOD");
        var rows = jod.GetProperty("customers").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        foreach (var key in jod.GetProperty("buckets").EnumerateArray().Select(b => b.GetProperty("bucket").GetString()!))
        {
            var fromRows = rows.Sum(r => decimal.Parse(Bucket(r, key), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(decimal.Parse(Bucket(jod, key), System.Globalization.CultureInfo.InvariantCulture), fromRows);
        }

        Assert.Equal("400.000", Bucket(jod, "Days1To30"));
        Assert.Equal("200.000", Bucket(jod, "Days61To90"));

        var cell = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging/customers/{s.CustomerId}?asOf=2026-09-11&bucket=Days1To30", ApiScenario.Json);
        Assert.Equal(["D-A1"], cell.GetProperty("invoices").EnumerateArray().Select(i => i.GetProperty("invoiceNumber").GetString()));
        Assert.Equal(10, cell.GetProperty("invoices")[0].GetProperty("daysPastDue").GetInt32());
        var all = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging/customers/{s.CustomerId}?asOf=2026-09-11", ApiScenario.Json);
        Assert.Equal(["D-A2", "D-A1"], all.GetProperty("invoices").EnumerateArray().Select(i => i.GetProperty("invoiceNumber").GetString()));

        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/reports/aging/customers/{Guid.CreateVersion7()}")).StatusCode);
    }

    /// <summary>AC-11 / AC-02: basis and boundaries come from tenant settings; the query may override the basis only.</summary>
    [Fact]
    public async Task Aging_BasisAndBucketsFromSettings()
    {
        var s = await fixture.Api.NewCustomerAsync();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "S-1", 100m, dueDate: "2026-09-05", issueDate: "2026-07-20");
        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET aging_basis = 'issue_date', aging_bucket_days = '{15,45}' WHERE tenant_id = @t", ("t", s.Organization.TenantId));

        var report = await ReportAsync(s.Client, "?asOf=2026-09-11");
        Assert.Equal("issue_date", report.GetProperty("basis").GetString());
        Assert.Equal(["Current", "Days1To15", "Days16To45", "Days45Plus"], report.GetProperty("bucketKeys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal([15, 45], report.GetProperty("bucketBoundaries").EnumerateArray().Select(k => k.GetInt32()));
        Assert.Equal("100.000", Bucket(Section(report, "JOD"), "Days45Plus"));   // 53 days from issue

        var byDue = await ReportAsync(s.Client, "?asOf=2026-09-11&basis=due_date");
        Assert.Equal("due_date", byDue.GetProperty("basis").GetString());
        Assert.Equal("100.000", Bucket(Section(byDue, "JOD"), "Days1To15"));     // 6 days from due

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync("/api/v1/reports/aging?basis=invoice_date")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync("/api/v1/reports/aging?asOf=11/09/2026")).StatusCode);
    }

    /// <summary>AC-12 / FIN-60.</summary>
    [Fact]
    public async Task Dso_IsGatedAndComputed()
    {
        var s = await fixture.Api.NewCustomerAsync();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "DSO-B", 900m, dueDate: "2026-08-31", issueDate: "2026-08-01");

        var young = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/dso?asOf=2026-09-11", ApiScenario.Json);
        var jod = young.GetProperty("currencies")[0];
        Assert.True(jod.GetProperty("insufficientHistory").GetBoolean());
        Assert.Equal(JsonValueKind.Null, jod.GetProperty("dso").ValueKind);
        Assert.Equal("reports.dso.disclaimer", young.GetProperty("disclaimerKey").GetString());

        // Older history: an invoice issued long before the period, still open. AR 1,000; sales in the 90 days 900.
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "DSO-A", 100m, dueDate: "2026-01-31", issueDate: "2026-01-01");
        var mature = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/dso?asOf=2026-09-11", ApiScenario.Json);
        jod = mature.GetProperty("currencies")[0];
        Assert.False(jod.GetProperty("insufficientHistory").GetBoolean());
        Assert.Equal("100.0", jod.GetProperty("dso").GetString());               // 1000 / 900 × 90
        Assert.Equal("1000.000", jod.GetProperty("arAtPeriodEnd").GetProperty("amount").GetString());
        Assert.Equal("900.000", jod.GetProperty("creditSalesInPeriod").GetProperty("amount").GetString());
        Assert.Equal("2026-06-14", jod.GetProperty("periodStart").GetString());
    }

    /// <summary>AC-13 / FIN-61.</summary>
    [Fact]
    public async Task AverageDaysToPay_StatesSampleSize()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var empty = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging/customers/{s.CustomerId}?asOf=2026-09-11", ApiScenario.Json);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("averageDaysToPay").ValueKind);
        Assert.Equal(0, empty.GetProperty("averageDaysToPaySampleSize").GetInt32());

        var one = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "P-1", 100m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        var two = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "P-2", 100m, dueDate: "2026-08-10", issueDate: "2026-07-10");
        await PayAsync(s.Client, s.CustomerId, one, 100m, "2026-08-06");   // 5 days late
        await PayAsync(s.Client, s.CustomerId, two, 100m, "2026-08-25");   // 15 days late

        var detail = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging/customers/{s.CustomerId}?asOf=2026-09-11", ApiScenario.Json);
        Assert.Equal("10.0", detail.GetProperty("averageDaysToPay").GetString());
        Assert.Equal(2, detail.GetProperty("averageDaysToPaySampleSize").GetInt32());
        Assert.Empty(detail.GetProperty("invoices").EnumerateArray());
    }

    /// <summary>AC-14 / SEC-45 / T-80.</summary>
    [Fact]
    public async Task Export_EscapesFormulas_AndIsRtlSafe()
    {
        const string payload = "=cmd|' /C calc'!A0";
        var s = await fixture.Api.NewCustomerAsync(payload);
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "X-1", 1_250.5m, dueDate: "2026-08-01", issueDate: "2026-07-01");

        var csv = await s.Client.GetAsync("/api/v1/reports/aging/export?format=csv&locale=en-JO&asOf=2026-09-11");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.StartsWith("text/csv", csv.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Equal("aging-2026-09-11.csv", csv.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"'" + payload + "\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\"" + payload, text, StringComparison.Ordinal);
        Assert.Contains("\"Customer\"", text, StringComparison.Ordinal);
        Assert.Contains(",1250.500,", text, StringComparison.Ordinal);

        var xlsx = await s.Client.GetAsync("/api/v1/reports/aging/export?format=xlsx&locale=ar-JO&asOf=2026-09-11");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
        var sheet = await reader.ReadToEndAsync();
        Assert.Contains("rightToLeft=\"1\"", sheet, StringComparison.Ordinal);
        Assert.Contains("العميل", sheet, StringComparison.Ordinal);
        Assert.Contains("s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" + payload, sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheet, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync("/api/v1/reports/aging/export?format=pdf")).StatusCode);
    }

    /// <summary>AC-15 / INV-09.</summary>
    [Fact]
    public async Task Reconciliation_ReportsMismatch()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "REC-1", 100m);
        var clean = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/reconciliation", ApiScenario.Json);
        Assert.Equal(0, clean.GetProperty("mismatches").GetArrayLength());

        await fixture.Database.ExecuteAsync("UPDATE invoices SET balance_cache = 99 WHERE id = @i", ("i", invoice));
        var dirty = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/reconciliation", ApiScenario.Json);
        var mismatch = Assert.Single(dirty.GetProperty("mismatches").EnumerateArray());
        Assert.Equal("REC-1", mismatch.GetProperty("invoiceNumber").GetString());
        Assert.Equal("INV-09", mismatch.GetProperty("rule").GetString());
        Assert.Equal("99.000", mismatch.GetProperty("cached").GetProperty("amount").GetString());
        Assert.Equal("100.000", mismatch.GetProperty("derived").GetProperty("amount").GetString());
    }

    /// <summary>The T-140 dataset: the measured tenant with 50k invoices and 100k allocation rows, plus two neighbours × 50k.</summary>
    private async Task<Setup> SeedThreeTenantsAsync()
    {
        var s = await fixture.Api.NewCustomerAsync("Big Co.");
        var tenant = s.Organization.TenantId;
        // Seeded as the superuser with the constraint triggers off: the point is the read path, not the writers.
        // The neighbours get invoices only; they are never read, they make the measured tenant a third of the table.
        const string seedInvoices = """
            INSERT INTO customers (id, tenant_id, code, name_en, payment_terms_days)
            SELECT gen_random_uuid(), @t, 'PERF-' || g, 'Perf customer ' || g, 30 FROM generate_series(1, 200) g;
            INSERT INTO invoices (id, tenant_id, customer_id, invoice_number, status, issue_date, due_date, currency, net_amount, tax_amount, total_amount, balance_cache, base_currency)
            SELECT gen_random_uuid(), @t, c.id, 'PERF-' || c.code || '-' || g, 'Open',
                   date '2026-09-11' - (g % 400), date '2026-09-11' - (g % 400) + 30, 'JOD', 1000, 160, 1160, 1160, 'JOD'
            FROM customers c CROSS JOIN generate_series(1, 250) g WHERE c.tenant_id = @t AND c.code LIKE 'PERF-%';
            """;
        foreach (var neighbour in new[] { await fixture.Api.CreateOrganizationAsync("Neighbour A"), await fixture.Api.CreateOrganizationAsync("Neighbour B") })
        {
            await fixture.Database.ExecuteAsync(seedInvoices, ("t", neighbour.TenantId));
        }

        await fixture.Database.ExecuteAsync(
            "ALTER TABLE payment_allocations DISABLE TRIGGER ALL;\n" + seedInvoices +
            """
            INSERT INTO payments (id, tenant_id, customer_id, amount, currency, method, received_date, effective_date, status)
            SELECT gen_random_uuid(), @t, c.id, 100000000, 'JOD', 'BankTransfer', date '2026-01-01', date '2026-01-01', 'Confirmed'
            FROM customers c WHERE c.tenant_id = @t AND c.code LIKE 'PERF-%';
            INSERT INTO payment_allocations (id, tenant_id, payment_id, invoice_id, amount, currency, effective_date, is_active)
            SELECT gen_random_uuid(), @t, p.id, i.id, 100, 'JOD', i.issue_date + 10, true
            FROM invoices i JOIN payments p ON p.tenant_id = i.tenant_id AND p.customer_id = i.customer_id
            CROSS JOIN generate_series(1, 2) g
            WHERE i.tenant_id = @t AND i.invoice_number LIKE 'PERF-%';
            UPDATE invoices SET balance_cache = 960 WHERE tenant_id = @t AND invoice_number LIKE 'PERF-%';
            ALTER TABLE payment_allocations ENABLE TRIGGER ALL;
            ANALYZE invoices; ANALYZE payment_allocations;
            """, ("t", tenant));
        Assert.Equal(50_000L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE tenant_id = @t", ("t", tenant)));
        Assert.True(await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM invoices") >= 150_000L, "T-140 seeds three tenants × 50k invoices");

        return s;
    }

    /// <summary>
    /// AC-16 / T-140 / PRD-22: 3 tenants × 50k invoices (the measured tenant also carries 100k allocation rows),
    /// aging P95 under 800 ms for the measured tenant. The two neighbour tenants exist to give the planner a
    /// table where no tenant is the majority — the shape T-140 names and T-141's index assertion assumes.
    /// </summary>
    [Fact]
    [Trait("Category", "Performance")]
    public async Task Aging_P95_Under800ms_At50k()
    {
        var s = await SeedThreeTenantsAsync();
        var tenant = s.Organization.TenantId;

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await s.Client.GetAsync("/api/v1/reports/aging?groupBy=customer");
            watch.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var p95 = timings.Order().ElementAt((int)Math.Ceiling(timings.Count * 0.95) - 1);
        output.WriteLine($"aging P95 {p95:F0} ms, min {timings.Min():F0} ms, max {timings.Max():F0} ms over {timings.Count} calls at 50k invoices");
        Assert.True(p95 < 800, $"aging P95 was {p95:F0} ms over {timings.Count} calls (min {timings.Min():F0}, max {timings.Max():F0})");

        // T-141 (slice 16): the plans behind the two hot reads at 50k rows. fn_aging is LANGUAGE sql and inlinable,
        // so EXPLAIN shows the real plan, not a function scan. The sweep's candidate query, asked for the old tail of
        // the ledger (a selective cutoff — the seed makes every invoice overdue), must hit the partial index. The
        // aging read touches a tenant's whole ledger: when that tenant is most of the table a sequential scan
        // filtered on tenant_id is the planner's correct choice; when the tenant is not the majority (the
        // three-tenant seed puts it at a third) the scan must be keyed on tenant_id through an index — at a third
        // the planner chooses a bitmap heap scan on the tenant key.
        var share = await fixture.Database.ScalarAsync<double>("SELECT (SELECT count(*) FROM invoices WHERE tenant_id = @t)::float / greatest((SELECT count(*) FROM invoices), 1)", ("t", tenant));
        var agingPlan = await fixture.Database.ScalarAsync<string>(
            "EXPLAIN (FORMAT JSON) SELECT * FROM fn_aging(@t, date '2026-09-11', 'due_date', 'Asia/Amman')", ("t", tenant));
        var agingScans = AssertInvoiceScanIsTenantScoped(agingPlan!, "fn_aging", requireIndex: share < 0.5);
        var sweepPlan = await fixture.Database.ScalarAsync<string>(
            "EXPLAIN (FORMAT JSON) SELECT DISTINCT customer_id FROM invoices WHERE tenant_id = @t AND status = 'Open' AND balance_cache > 0 AND due_date < date '2025-09-20'", ("t", tenant));
        var sweepScans = AssertInvoiceScanIsTenantScoped(sweepPlan!, "the sweep's overdue candidates", requireIndex: true);
        output.WriteLine($"T-141: invoice scans are tenant-scoped (tenant share of the table {share:P0}; index required for the sweep, {(share < 0.5 ? "and" : "not")} for aging)");
        output.WriteLine($"T-141 plans: aging → {agingScans}; sweep → {sweepScans}");
    }

    /// <summary>T-140 / PRD-22: a single-entity read (one customer, one invoice) at 50k invoices — P95 under 500 ms.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public async Task SingleEntityRead_P95_Under500ms_At50k()
    {
        var s = await SeedThreeTenantsAsync();
        var tenant = s.Organization.TenantId;
        var customerId = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM customers WHERE tenant_id = @t AND code = 'PERF-137'", ("t", tenant));
        var invoiceId = await fixture.Database.ScalarAsync<Guid>("SELECT id FROM invoices WHERE tenant_id = @t AND customer_id = @c ORDER BY invoice_number LIMIT 1", ("t", tenant), ("c", customerId));

        var timings = new List<double>();
        foreach (var path in new[] { $"/api/v1/customers/{customerId}", $"/api/v1/invoices/{invoiceId}" })
        {
            for (var i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew();
                var response = await s.Client.GetAsync(path);
                watch.Stop();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        var p95 = timings.Order().ElementAt((int)Math.Ceiling(timings.Count * 0.95) - 1);
        output.WriteLine($"single API P95 {p95:F0} ms, min {timings.Min():F0} ms, max {timings.Max():F0} ms over {timings.Count} single-entity reads at 50k invoices");
        Assert.True(p95 < 500, $"single API P95 was {p95:F0} ms over {timings.Count} calls (min {timings.Min():F0}, max {timings.Max():F0})");
    }

    /// <summary>
    /// T-141: walks an EXPLAIN (FORMAT JSON) tree. Every scan of <c>invoices</c> must be keyed on the tenant through
    /// an index (an index scan, or a bitmap heap scan whose recheck condition names <c>tenant_id</c>), or — only
    /// when allowed — a sequential scan whose filter names <c>tenant_id</c>. An unfiltered scan, or a sequential
    /// scan where an index was required, fails.
    /// </summary>
    private static string AssertInvoiceScanIsTenantScoped(string planJson, string what, bool requireIndex)
    {
        using var doc = JsonDocument.Parse(planJson);
        var scans = new List<(string Type, string Detail)>();
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) { foreach (var n in node.EnumerateArray()) Walk(n); return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            if (node.TryGetProperty("Relation Name", out var rel) && rel.GetString() == "invoices" && node.TryGetProperty("Node Type", out var type))
            {
                var cond = node.TryGetProperty("Index Cond", out var ic) ? ic.GetString() ?? string.Empty
                    : node.TryGetProperty("Recheck Cond", out var rc) ? rc.GetString() ?? string.Empty : string.Empty;
                var filter = node.TryGetProperty("Filter", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                var index = node.TryGetProperty("Index Name", out var iname) ? iname.GetString() ?? string.Empty : string.Empty;
                scans.Add((type.GetString()!, $"index={index} cond=[{cond}] filter=[{filter}]"));
            }

            foreach (var property in node.EnumerateObject()) Walk(property.Value);
        }

        Walk(doc.RootElement);
        Assert.True(scans.Count > 0, $"{what}: the plan never reads invoices? {planJson}");
        foreach (var (type, detail) in scans)
        {
            // A bitmap heap scan is index-driven: its Bitmap Index Scan child carries the index and its recheck condition the key.
            var isIndex = (type.Contains("Index", StringComparison.Ordinal) || type == "Bitmap Heap Scan")
                && detail[..detail.IndexOf(" filter=", StringComparison.Ordinal)].Contains("tenant_id", StringComparison.Ordinal);
            var isFilteredSeq = type == "Seq Scan" && detail.Contains("tenant_id", StringComparison.Ordinal);
            Assert.True(isIndex || (!requireIndex && isFilteredSeq),
                $"{what}: {type} on invoices is not tenant-scoped the way T-141 requires ({detail}); index required: {requireIndex}. Plan: {planJson}");
        }

        return string.Join(" | ", scans.Select(scan => $"{scan.Type} ({scan.Detail})"));
    }
}
