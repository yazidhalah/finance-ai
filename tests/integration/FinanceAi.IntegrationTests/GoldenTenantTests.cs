using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// T-52 (doc 09 §3): a golden tenant — 40 customers, 2,000 imported invoices and a scripted year of activity
/// (payments with FIFO and explicit allocations, short payments with withholding, credit notes, reversals, the
/// sweep) — is rebuilt from a fixed seed on every run, and its aging report, per-customer balances, invoice status
/// counts, reconciliation and audit trail are compared against <c>Golden/golden-ledger.json</c>. A change to
/// financial logic that moves a number fails here and is accepted consciously with <c>UPDATE_GOLDEN=1</c>.
/// Everything is written through the product's own endpoints; nothing is inserted directly.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class GoldenTenantTests(ApiTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly TimeZoneInfo Amman = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
    // The script is relative to the real day (the database's own immutability rules read current_date): the seed fixes
    // every offset, so buckets, balances and case counts are the same whichever day the test runs.
    private static readonly DateOnly AsOf = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Amman).DateTime);
    private static readonly DateTimeOffset Now = new(AsOf.ToDateTime(new TimeOnly(9, 0)), Amman.GetUtcOffset(AsOf.ToDateTime(new TimeOnly(9, 0))));
    private const int Customers = 40;
    private const int Invoices = 2_000;
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", "golden-ledger.json");

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string F3(decimal d) => d.ToString("F3", CultureInfo.InvariantCulture);

    private sealed record Inv(Guid Id, string Number, int Customer, string Currency, decimal Total, DateOnly Issue, DateOnly Due);

    [Fact]
    public async Task GoldenLedger_MatchesCheckedInExpectations()
    {
        using var _ = fixture.Api.PinClock(Now);
        var s = await fixture.Api.NewCustomerAsync("Golden Co.");
        var client = s.Client;
        var random = new Random(52);

        // 1. Customers: 34 in JOD, 6 in USD (fixed rate 0.709 to base on every USD invoice).
        var customerIds = new List<Guid>();
        for (var i = 1; i <= Customers; i++)
        {
            var currency = i % 7 == 0 ? "USD" : "JOD";
            var terms = new[] { 15, 30, 30, 45, 60 }[i % 5];
            var created = await client.PostAsync("/api/v1/customers", new { nameEn = $"Golden customer {i:00}", code = $"GOLD-{i:00}", paymentTermsDays = terms, defaultCurrency = currency });
            customerIds.Add(created.GetProperty("id").GetGuid());
        }

        // 2. Invoices: one 2,000-row import, dates spread over the year before AsOf.
        var csv = new StringBuilder("Invoice No,Customer,Issue Date,Due Date,Currency,Net,Tax,Total,Rate\n");
        var invoices = new List<Inv>();
        for (var n = 1; n <= Invoices; n++)
        {
            var customer = 1 + random.Next(Customers);
            var currency = customer % 7 == 0 ? "USD" : "JOD";
            var terms = new[] { 15, 30, 30, 45, 60 }[customer % 5];
            var issue = AsOf.AddDays(-random.Next(1, 366));
            var due = issue.AddDays(terms);
            var net = decimal.Round(50m + (decimal)random.NextDouble() * 4_950m, 3);
            var tax = decimal.Round(net * 0.16m, 3);
            var total = net + tax;
            var number = $"G-{n:0000}";
            csv.Append(number).Append(",Golden customer ").Append(customer.ToString("00", CultureInfo.InvariantCulture)).Append(',').Append(D(issue)).Append(',').Append(D(due)).Append(',')
               .Append(currency).Append(',').Append(F3(net)).Append(',').Append(F3(tax)).Append(',').Append(F3(total)).Append(',').Append(currency == "USD" ? "0.709" : string.Empty).Append('\n');
            invoices.Add(new Inv(Guid.Empty, number, customer, currency, total, issue, due));
        }

        var batchId = await UploadAsync(client, csv.ToString());
        var mapped = await MapAsync(client, batchId);
        Assert.Equal(Invoices, mapped.GetProperty("acceptedCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).StatusCode);
        var byNumber = new Dictionary<string, Guid>();
        string? cursor = null;
        do
        {
            var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices?limit=200{(cursor is null ? string.Empty : "&cursor=" + cursor)}", ApiScenario.Json);
            foreach (var item in list.GetProperty("items").EnumerateArray()) byNumber[item.GetProperty("invoiceNumber").GetString()!] = item.GetProperty("id").GetGuid();
            cursor = list.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        Assert.Equal(Invoices, byNumber.Count);
        invoices = invoices.Select(i => i with { Id = byNumber[i.Number] }).ToList();

        // 3. A year of activity, in issue order so the script reads like a ledger.
        var payments = new List<Guid>();
        var withholdings = 0; var partials = 0; var credits = 0; var reversals = 0; var fifo = 0;
        foreach (var inv in invoices.OrderBy(i => i.Issue).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            if (inv.Due > AsOf.AddDays(-5)) continue;                  // the recent tail stays open
            var roll = random.Next(100);
            var received = inv.Due.AddDays(random.Next(-10, 41));
            if (received > AsOf) received = AsOf;
            var customerId = customerIds[inv.Customer - 1];
            if (roll < 55)
            {
                // Full payment, allocated explicitly.
                payments.Add(await PayAsync(client, customerId, inv.Total, inv.Currency, received, inv.Number, [new { invoiceId = inv.Id, amount = M(inv.Total, inv.Currency) }]));
            }
            else if (roll < 70)
            {
                // Partial payment (a round half), explicit.
                var part = decimal.Round(inv.Total / 2m, 3);
                payments.Add(await PayAsync(client, customerId, part, inv.Currency, received, inv.Number + "/1", [new { invoiceId = inv.Id, amount = M(part, inv.Currency) }]));
                partials++;
            }
            else if (roll < 78)
            {
                // Short payment net of 5 % withholding on the net (E1): the customer pays total − 5 % of net, and the certificate covers the rest.
                var net = decimal.Round(inv.Total / 1.16m, 3);
                var withheld = decimal.Round(net * 0.05m, 3);
                var paid = inv.Total - withheld;
                payments.Add(await PayAsync(client, customerId, paid, inv.Currency, received, inv.Number + "/W", [new { invoiceId = inv.Id, amount = M(paid, inv.Currency) }]));
                await client.PostAsync($"/api/v1/invoices/{inv.Id}/withholding", new { baseAmount = M(net, inv.Currency), ratePct = "5", withheldAmount = M(withheld, inv.Currency), certificateReceived = true });
                withholdings++;
            }
            else if (roll < 84)
            {
                // A payment for the invoice amount with no lines: the FIFO proposal allocates it.
                var id = await PayAsync(client, customerId, inv.Total, inv.Currency, received, inv.Number + "/F", null);
                var proposal = await client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{id}/allocation-proposal", ApiScenario.Json);
                var lines = proposal.GetProperty("lines").EnumerateArray().Select(l => new { invoiceId = l.GetProperty("invoiceId").GetGuid(), amount = new { amount = l.GetProperty("proposed").GetProperty("amount").GetString(), currency = l.GetProperty("proposed").GetProperty("currency").GetString() } }).ToArray();
                if (lines.Length > 0) await client.PostAsync($"/api/v1/payments/{id}/allocations", new { lines });
                payments.Add(id);
                fifo++;
            }
            else if (roll < 88)
            {
                // A credit note for a tenth of the invoice, applied to it.
                var credit = decimal.Round(inv.Total / 10m, 3);
                await client.PostAsync("/api/v1/credit-notes", new { customerId, noteNumber = $"CN-{inv.Number}", amount = M(credit, inv.Currency), issueDate = D(received), reasonCode = "agreed_discount", applications = new[] { new { invoiceId = inv.Id, amount = M(credit, inv.Currency) } } });
                credits++;
            }
            // else: unpaid, overdue.
        }

        // Every 40th payment was a mistake and is reversed.
        for (var i = 39; i < payments.Count; i += 40)
        {
            await client.PostAsync($"/api/v1/payments/{payments[i]}/reverse", new { reason = "golden: recorded against the wrong customer" });
            reversals++;
        }

        await client.PostAsync("/api/v1/cases/sweep", new { });
        output.WriteLine($"golden: {Customers} customers, {Invoices} invoices, {payments.Count} payments ({partials} partial, {withholdings} with withholding, {fifo} FIFO), {credits} credit notes, {reversals} reversals");

        // 4. What the ledger says.
        var aging = await client.GetFromJsonAsync<JsonElement>($"/api/v1/reports/aging?groupBy=customer&asOf={D(AsOf)}", ApiScenario.Json);
        var reconciliation = await client.GetFromJsonAsync<JsonElement>("/api/v1/reports/reconciliation", ApiScenario.Json);
        var statuses = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await foreach (var (status, count) in fixture.Database.RowsAsync<string, long>("SELECT status, count(*) FROM invoices WHERE tenant_id = @t GROUP BY status", ("t", s.Organization.TenantId))) statuses[status] = count;
        var auditCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await foreach (var (type, count) in fixture.Database.RowsAsync<string, long>("SELECT event_type, count(*) FROM audit_events WHERE tenant_id = @t GROUP BY event_type", ("t", s.Organization.TenantId))) auditCounts[type] = count;
        var run = await client.PostAsync("/api/v1/organization/invariants/run", new { });

        var actual = new JsonObject
        {
            ["seed"] = 52,
            ["aging"] = new JsonObject
            {
                ["baseCurrencyTotal"] = aging.GetProperty("baseCurrencyTotal").GetProperty("amount").GetString(),
                ["currencies"] = new JsonArray(aging.GetProperty("currencies").EnumerateArray().Select(c => (JsonNode)new JsonObject
                {
                    ["currency"] = c.GetProperty("currency").GetString(),
                    ["total"] = c.GetProperty("total").GetProperty("amount").GetString(),
                    ["invoiceCount"] = c.GetProperty("invoiceCount").GetInt32(),
                    ["unappliedCash"] = c.GetProperty("unappliedCash").GetProperty("amount").GetString(),
                    ["unappliedCredit"] = c.GetProperty("unappliedCredit").GetProperty("amount").GetString(),
                    ["buckets"] = new JsonArray(c.GetProperty("buckets").EnumerateArray().Select(b => (JsonNode)new JsonObject { ["bucket"] = b.GetProperty("bucket").GetString(), ["amount"] = b.GetProperty("amount").GetProperty("amount").GetString(), ["invoiceCount"] = b.GetProperty("invoiceCount").GetInt32() }).ToArray()),
                    ["customers"] = new JsonArray(c.GetProperty("customers").EnumerateArray().OrderBy(r => r.GetProperty("code").GetString(), StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject { ["code"] = r.GetProperty("code").GetString(), ["total"] = r.GetProperty("total").GetProperty("amount").GetString(), ["invoiceCount"] = r.GetProperty("invoiceCount").GetInt32() }).ToArray()),
                }).ToArray()),
            },
            ["invoiceStatusCounts"] = new JsonObject(statuses.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["reconciliation"] = new JsonObject { ["invoicesChecked"] = reconciliation.GetProperty("invoicesChecked").GetInt32(), ["mismatches"] = reconciliation.GetProperty("mismatches").GetArrayLength() },
            ["invariants"] = run.GetProperty("status").GetString(),
            ["auditEventCounts"] = new JsonObject(auditCounts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        };
        var actualJson = actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

        Assert.Equal("ok", run.GetProperty("status").GetString());
        Assert.Equal(0, reconciliation.GetProperty("mismatches").GetArrayLength());

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(FindRepoRoot(), "tests", "integration", "FinanceAi.IntegrationTests", "Golden", "golden-ledger.json");
            await File.WriteAllTextAsync(source, actualJson);
            output.WriteLine($"golden: wrote {source}");
            return;
        }

        Assert.True(File.Exists(GoldenPath), $"no golden file at {GoldenPath}; run once with UPDATE_GOLDEN=1");
        var expected = await File.ReadAllTextAsync(GoldenPath);
        if (expected != actualJson)
        {
            var e = expected.Split('\n'); var a = actualJson.Split('\n');
            var diff = new StringBuilder("golden ledger differs from tests/integration/FinanceAi.IntegrationTests/Golden/golden-ledger.json — a financial number moved. If intended, rerun with UPDATE_GOLDEN=1 and commit the file.\n");
            for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
            {
                var el = i < e.Length ? e[i] : "<missing>"; var al = i < a.Length ? a[i] : "<missing>";
                if (el != al) diff.Append(CultureInfo.InvariantCulture, $"  line {i + 1}: expected {el.Trim()} | actual {al.Trim()}\n");
                if (diff.Length > 6_000) { diff.Append("  …\n"); break; }
            }

            Assert.Fail(diff.ToString());
        }
    }

    private static async Task<Guid> PayAsync(HttpClient client, Guid customerId, decimal amount, string currency, DateOnly received, string reference, object[]? allocations)
    {
        var response = await client.PostAsync("/api/v1/payments", new { customerId, amount = M(amount, currency), method = "BankTransfer", receivedDate = D(received), reference, allocations });
        return response.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string csv)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "golden.csv");
        var response = await client.PostAsync("/api/v1/imports", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> MapAsync(HttpClient client, Guid batchId)
    {
        var map = new Dictionary<string, string>
        {
            ["Invoice No"] = "invoice_number",
            ["Customer"] = "customer_name",
            ["Issue Date"] = "issue_date",
            ["Due Date"] = "due_date",
            ["Currency"] = "currency",
            ["Net"] = "net_amount",
            ["Tax"] = "tax_amount",
            ["Total"] = "total_amount",
            ["Rate"] = "fx_rate_to_base",
        };
        var response = await client.PostAsJsonAsync($"/api/v1/imports/{batchId}/mapping", new { columnMap = map, dateFormat = "yyyy-MM-dd", decimalSeparator = "." }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
