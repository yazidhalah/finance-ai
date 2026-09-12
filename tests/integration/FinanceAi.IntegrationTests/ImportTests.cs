using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 3 AC-01 … AC-14, AC-17, AC-18, AC-22 … AC-24. Every test creates its own organization.</summary>
[Collection(ApiCollection.Name)]
public sealed class ImportTests(ApiTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Dictionary<string, string> StandardMap = new()
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

    private const string Header = "Invoice No,Customer,Issue Date,Due Date,Currency,Net,Tax,Total,Rate";

    /// <summary>AC-01 / T-122 (API half): three exceptions, three resolutions, one commit.</summary>
    [Fact]
    public async Task Import_EndToEnd_WithThreeExceptions()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var alAmal = await CreateCustomerAsync(client, new { nameAr = "شركة الأمل التجارية", nameEn = "Al Amal Trading Co.", paymentTermsDays = 45 });
        await CreateCustomerAsync(client, new { nameEn = "Petra Supplies", code = "PET-1" });

        var csv = string.Join('\n',
            Header,
            "INV-001,شركه الامل التجاريه,2026-09-01,2026-10-01,JOD,1000.000,160.000,1160.000,",     // ok: Arabic name, other spelling
            "INV-002,Petra Supplies,2026-09-02,,JOD,500.000,,500.000,",                           // ok: no due date, no tax
            "INV-003,Unknown Traders,2026-09-03,2026-10-03,JOD,100.000,16.000,116.000,",          // exception 1: customer not found
            "INV-004,Petra Supplies,2026-09-04,2026-10-04,JOD,100.000,16.000,120.000,",           // exception 2: totals do not reconcile
            "INV-002,Petra Supplies,2026-09-05,2026-10-05,JOD,10.000,0,10.000,",                  // exception 3: duplicate in batch
            string.Empty);

        var batch = await UploadAsync(client, "september.csv", csv);
        Assert.Equal("Uploaded", batch.GetProperty("status").GetString());
        Assert.Equal(9, batch.GetProperty("headers").GetArrayLength());

        var batchId = batch.GetProperty("id").GetGuid();
        var mapped = await MapAsync(client, batchId, StandardMap);

        Assert.Equal("Preview", mapped.GetProperty("status").GetString());
        Assert.Equal(5, mapped.GetProperty("rowCount").GetInt32());
        Assert.Equal(2, mapped.GetProperty("acceptedCount").GetInt32());
        Assert.Equal(2, mapped.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(1, mapped.GetProperty("duplicateCount").GetInt32());

        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("customer_not_found", rows[2].GetProperty("errorCode").GetString());
        Assert.Equal("totals_do_not_reconcile", rows[3].GetProperty("errorCode").GetString());
        Assert.Equal("duplicate_in_batch", rows[4].GetProperty("errorCode").GetString());

        // Commit is refused while questions are open.
        var premature = await client.PostAsync($"/api/v1/imports/{batchId}/commit", null);
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Equal("unresolved_exceptions", (await premature.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json))!.Code);

        // Resolve: create the unknown customer, skip the arithmetic error, skip the duplicate.
        await ResolveAsync(client, batchId, rows[2].GetProperty("id").GetGuid(), new { action = "create_customer" });
        await ResolveAsync(client, batchId, rows[3].GetProperty("id").GetGuid(), new { action = "skip" });
        await ResolveAsync(client, batchId, rows[4].GetProperty("id").GetGuid(), new { action = "skip" });

        var ready = await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}", ApiScenario.Json);
        Assert.Equal(3, ready.GetProperty("acceptedCount").GetInt32());
        var totals = ready.GetProperty("controlTotals").EnumerateArray().Single();
        Assert.Equal("JOD", totals.GetProperty("currency").GetString());
        Assert.Equal("1776.000", totals.GetProperty("total").GetProperty("amount").GetString());   // 1160 + 500 + 116
        Assert.Equal(3, totals.GetProperty("count").GetInt32());

        var commit = await client.PostAsync($"/api/v1/imports/{batchId}/commit", null);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var committed = await commit.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.Equal(3, committed.GetProperty("invoicesCreated").GetInt32());
        Assert.Equal("Committed", committed.GetProperty("status").GetString());

        // The invoices are Open with balance == total; due date defaulted from the customer's terms.
        var invoices = (await client.GetFromJsonAsync<JsonElement>("/api/v1/invoices", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, invoices.Count);
        Assert.All(invoices, i => Assert.Equal("Open", i.GetProperty("status").GetString()));
        Assert.All(invoices, i => Assert.Equal(i.GetProperty("totalAmount").GetProperty("amount").GetString(), i.GetProperty("openBalance").GetProperty("amount").GetString()));

        var inv001 = invoices.Single(i => i.GetProperty("invoiceNumber").GetString() == "INV-001");
        Assert.Equal(alAmal, inv001.GetProperty("customerId").GetGuid());
        Assert.Equal("1160.000", inv001.GetProperty("totalAmount").GetProperty("amount").GetString());

        var inv002 = invoices.Single(i => i.GetProperty("invoiceNumber").GetString() == "INV-002");
        Assert.Equal("2026-10-02", inv002.GetProperty("dueDate").GetString());   // issue + 30 default terms
        Assert.Equal("0.000", inv002.GetProperty("taxAmount").GetProperty("amount").GetString());

        // Every row of the file is still on record, with what happened to it.
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json);
        var outcomes = history.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("outcome").GetString()).ToList();
        Assert.Equal(["Accepted", "Accepted", "Accepted", "Skipped", "Skipped"], outcomes);
        Assert.All(history.GetProperty("items").EnumerateArray().Where(r => r.GetProperty("outcome").GetString() == "Accepted"),
            r => Assert.Equal(JsonValueKind.String, r.GetProperty("invoiceId").ValueKind));
    }

    /// <summary>AC-02.</summary>
    [Fact]
    public async Task Import_Xlsx_ParsesDatesAndNumbers()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Sheet Co." });

        var xlsx = XlsxBuilder.Build(
        [
            [new("Invoice No"), new("Customer"), new("Issue Date"), new("Total")],
            [new("X-1"), new("Sheet Co."), new(Number: 46275, IsDate: true), new(Number: 1250.5)],
        ]);

        var batch = await UploadAsync(client, "book.xlsx", xlsx);
        var batchId = batch.GetProperty("id").GetGuid();
        var mapped = await MapAsync(client, batchId, new Dictionary<string, string>
        {
            ["Invoice No"] = "invoice_number",
            ["Customer"] = "customer_name",
            ["Issue Date"] = "issue_date",
            ["Total"] = "total_amount",
        });

        Assert.Equal(1, mapped.GetProperty("acceptedCount").GetInt32());
        var row = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json)).GetProperty("items")[0];
        Assert.Equal("2026-09-10", row.GetProperty("parsed").GetProperty("issueDate").GetString());
        Assert.Equal("1250.500", row.GetProperty("parsed").GetProperty("totalAmount").GetString());
    }

    /// <summary>AC-03 / T-41: an invoice sneaks in between preview and commit; the commit rolls back entirely.</summary>
    [Fact]
    public async Task Commit_WhenAnInsertFails_LeavesNoInvoices()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var customerId = await CreateCustomerAsync(client, new { nameEn = "Race Co." });

        var csv = string.Join('\n', Header,
            "R-1,Race Co.,2026-09-01,2026-10-01,JOD,10.000,0,10.000,",
            "R-2,Race Co.,2026-09-01,2026-10-01,JOD,20.000,0,20.000,",
            "R-3,Race Co.,2026-09-01,2026-10-01,JOD,30.000,0,30.000,", string.Empty);

        var batchId = (await UploadAsync(client, "race.csv", csv)).GetProperty("id").GetGuid();
        Assert.Equal(3, (await MapAsync(client, batchId, StandardMap)).GetProperty("acceptedCount").GetInt32());

        // After preview, R-2 appears through another path (a second batch, an API); the unique index
        // will reject the third insert of our commit.
        await using (var connection = fixture.Database.OpenAdmin())
        {
            await using var insert = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO invoices (id, tenant_id, customer_id, invoice_number, status, issue_date, due_date, currency,
                                      net_amount, tax_amount, total_amount, balance_cache, base_currency)
                VALUES (@id, @t, @c, 'r-2', 'Open', '2026-09-01', '2026-10-01', 'JOD', 1, 0, 1, 1, 'JOD')
                """, connection);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("t", org.TenantId);
            insert.Parameters.AddWithValue("c", customerId);
            await insert.ExecuteNonQueryAsync();
        }

        var commit = await client.PostAsync($"/api/v1/imports/{batchId}/commit", null);
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);

        var fromBatch = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM invoices WHERE import_batch_id = @b", ("b", batchId));
        Assert.Equal(0, fromBatch);   // not R-1, not R-3: nothing

        var status = await fixture.Database.ScalarAsync<string>("SELECT status FROM import_batches WHERE id = @b", ("b", batchId));
        Assert.Equal("Preview", status);

        var linked = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM import_rows WHERE batch_id = @b AND invoice_id IS NOT NULL", ("b", batchId));
        Assert.Equal(0, linked);
    }

    /// <summary>AC-04 / DM-23.</summary>
    [Fact]
    public async Task DuplicateFile_IsRefused_ThenOverridable()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var csv = Header + "\nD-1,Nobody,2026-09-01,,JOD,1,0,1,\n";

        var first = await UploadAsync(client, "same.csv", csv);

        var again = await UploadRawAsync(client, "same-renamed.csv", Encoding.UTF8.GetBytes(csv), force: false);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("duplicate_file", (await again.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json))!.Code);

        var forced = await UploadRawAsync(client, "same-renamed.csv", Encoding.UTF8.GetBytes(csv), force: true);
        Assert.Equal(HttpStatusCode.Created, forced.StatusCode);
        var forcedBatch = await forced.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.True(forcedBatch.GetProperty("forced").GetBoolean());

        var audited = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'import.duplicate_file_overridden'", ("t", org.TenantId));
        Assert.Equal(1, audited);

        // A cancelled batch releases the hash: the same file can be uploaded fresh afterwards.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/imports/{first.GetProperty("id").GetGuid()}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/v1/imports/{forcedBatch.GetProperty("id").GetGuid()}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await UploadRawAsync(client, "same.csv", Encoding.UTF8.GetBytes(csv), force: false)).StatusCode);
    }

    /// <summary>AC-05, AC-06.</summary>
    [Fact]
    public async Task DuplicateInvoiceNumber_IsFlagged()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Dup Co." });
        await CreateCustomerAsync(client, new { nameEn = "Other Co." });

        await CommitCsvAsync(client, "first.csv", Header + "\nINV-9,Dup Co.,2026-09-01,,JOD,1,0,1,\n");

        var csv = string.Join('\n', Header,
            "inv-9,Dup Co.,2026-09-02,,JOD,2,0,2,",       // duplicate of the existing one, other case
            "INV-9,Other Co.,2026-09-02,,JOD,2,0,2,",     // same number, different customer: fine
            string.Empty);

        var batchId = (await UploadAsync(client, "second.csv", csv)).GetProperty("id").GetGuid();
        var mapped = await MapAsync(client, batchId, StandardMap);

        Assert.Equal(1, mapped.GetProperty("duplicateCount").GetInt32());
        Assert.Equal(1, mapped.GetProperty("acceptedCount").GetInt32());

        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows?outcome=Duplicate", ApiScenario.Json)).GetProperty("items");
        Assert.Equal("duplicate_invoice_number", rows[0].GetProperty("errorCode").GetString());
    }

    /// <summary>AC-07, AC-08.</summary>
    [Fact]
    public async Task Totals_MustReconcile_AndAmountsAreExact()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Sum Co." });

        var csv = string.Join('\n', Header,
            "S-1,Sum Co.,2026-09-01,,JOD,100.000,16.000,116.000,",   // reconciles
            "S-2,Sum Co.,2026-09-01,,JOD,100.000,16.000,116.001,",   // off by one fils
            "S-3,Sum Co.,2026-09-01,,JOD,100.000,,,",                // net only: total = net
            "S-4,Sum Co.,2026-09-01,,JOD,,,116.000,",                // total only: net = total
            "S-5,Sum Co.,2026-09-01,,JOD,1.2345,0,1.2345,",          // four decimals
            "S-6,Sum Co.,2026-09-01,,JOD,-5,0,-5,",                  // negative
            string.Empty);

        var batchId = (await UploadAsync(client, "sums.csv", csv)).GetProperty("id").GetGuid();
        await MapAsync(client, batchId, StandardMap);

        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal("Accepted", rows[0].GetProperty("outcome").GetString());
        Assert.Equal("totals_do_not_reconcile", rows[1].GetProperty("errorCode").GetString());
        Assert.Equal("100.000", rows[2].GetProperty("parsed").GetProperty("totalAmount").GetString());
        Assert.Equal("116.000", rows[3].GetProperty("parsed").GetProperty("netAmount").GetString());
        Assert.Equal("too_many_decimals", rows[4].GetProperty("errorCode").GetString());
        Assert.Equal("negative_amount", rows[5].GetProperty("errorCode").GetString());
    }

    /// <summary>AC-09, AC-10.</summary>
    [Fact]
    public async Task DecimalSeparatorAndDateFormat_AreHonoured()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Euro Co." });

        var csv = "Invoice No;Customer;Issue Date;Due Date;Total\nE-1;Euro Co.;10/09/2026;01/09/2026;1.250,500\nE-2;Euro Co.;10/09/2026;;1.250,500\n";
        var batchId = (await UploadAsync(client, "euro.csv", csv)).GetProperty("id").GetGuid();

        var mapped = await MapAsync(client, batchId,
            new Dictionary<string, string> { ["Invoice No"] = "invoice_number", ["Customer"] = "customer_name", ["Issue Date"] = "issue_date", ["Due Date"] = "due_date", ["Total"] = "total_amount" },
            dateFormat: "dd/MM/yyyy", decimalSeparator: ",");

        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("due_before_issue", rows[0].GetProperty("errorCode").GetString());
        Assert.Equal("Accepted", rows[1].GetProperty("outcome").GetString());
        Assert.Equal("1250.500", rows[1].GetProperty("parsed").GetProperty("totalAmount").GetString());
        Assert.Equal("2026-09-10", rows[1].GetProperty("parsed").GetProperty("issueDate").GetString());
        Assert.Equal(1, mapped.GetProperty("acceptedCount").GetInt32());
    }

    /// <summary>AC-11 / FIN-06.</summary>
    [Fact]
    public async Task ForeignCurrency_RequiresARate()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "FX Co." });

        var csv = string.Join('\n', Header,
            "F-1,FX Co.,2026-09-01,,USD,100,0,100,",             // no rate
            "F-2,FX Co.,2026-09-01,,USD,100,0,100,0.70900000",   // rate supplied
            "F-3,FX Co.,2026-09-01,,JOD,100,0,100,",             // base currency
            string.Empty);

        var batchId = (await UploadAsync(client, "fx.csv", csv)).GetProperty("id").GetGuid();
        await MapAsync(client, batchId, StandardMap);

        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("missing_fx_rate", rows[0].GetProperty("errorCode").GetString());

        await ResolveAsync(client, batchId, rows[0].GetProperty("id").GetGuid(), new { action = "skip" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).StatusCode);

        var invoices = (await client.GetFromJsonAsync<JsonElement>("/api/v1/invoices", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("0.709", invoices.Single(i => i.GetProperty("invoiceNumber").GetString() == "F-2").GetProperty("fxRateToBase").GetString());
        Assert.Equal("1", invoices.Single(i => i.GetProperty("invoiceNumber").GetString() == "F-3").GetProperty("fxRateToBase").GetString());
        Assert.All(invoices, i => Assert.Equal("JOD", i.GetProperty("baseCurrency").GetString()));
    }

    /// <summary>AC-12.</summary>
    [Fact]
    public async Task Customers_ResolveByCodeThenName_AndCanBeAssigned()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var byCode = await CreateCustomerAsync(client, new { nameEn = "Coded Co.", code = "C-77" });
        var byName = await CreateCustomerAsync(client, new { nameAr = "مؤسسة البتراء" });
        var assignee = await CreateCustomerAsync(client, new { nameEn = "Assignee Co." });

        var csv = "Invoice No,Code,Name,Issue Date,Total\nA-1,c-77,Whatever Name,2026-09-01,1\nA-2,,مؤسسه البتراء,2026-09-01,1\nA-3,,Nobody,2026-09-01,1\n";
        var batchId = (await UploadAsync(client, "match.csv", csv)).GetProperty("id").GetGuid();
        await MapAsync(client, batchId, new Dictionary<string, string>
        {
            ["Invoice No"] = "invoice_number",
            ["Code"] = "customer_code",
            ["Name"] = "customer_name",
            ["Issue Date"] = "issue_date",
            ["Total"] = "total_amount",
        });

        var rows = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(byCode, rows[0].GetProperty("customerId").GetGuid());      // code wins over the (wrong) name
        Assert.Equal(byName, rows[1].GetProperty("customerId").GetGuid());      // normalized Arabic name
        Assert.Equal("customer_not_found", rows[2].GetProperty("errorCode").GetString());

        var resolved = await ResolveAsync(client, batchId, rows[2].GetProperty("id").GetGuid(), new { action = "assign_customer", customerId = assignee });
        Assert.Equal("Accepted", resolved.GetProperty("outcome").GetString());
        Assert.Equal(assignee, resolved.GetProperty("customerId").GetGuid());

        // Assigning a customer of another organization is "not found", not "forbidden".
        var other = await fixture.Api.CreateOrganizationAsync();
        using var otherClient = fixture.Api.AuthenticatedClient(other.OwnerSession);
        var foreign = await CreateCustomerAsync(otherClient, new { nameEn = "Foreign" });
        var response = await client.PostAsJsonAsync($"/api/v1/imports/{batchId}/rows/{rows[2].GetProperty("id").GetGuid()}/resolve",
            new { action = "assign_customer", customerId = foreign }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>AC-13, AC-14.</summary>
    [Fact]
    public async Task ControlTotals_ArePerCurrency_AndCommitIsIdempotent()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Mix Co." });

        var csv = string.Join('\n', Header,
            "M-1,Mix Co.,2026-09-01,,JOD,100.001,0,100.001,",
            "M-2,Mix Co.,2026-09-01,,JOD,0.002,0,0.002,",
            "M-3,Mix Co.,2026-09-01,,USD,50.000,0,50.000,0.709",
            string.Empty);

        var batchId = (await UploadAsync(client, "mix.csv", csv)).GetProperty("id").GetGuid();
        var mapped = await MapAsync(client, batchId, StandardMap);

        var totals = mapped.GetProperty("controlTotals").EnumerateArray().Select(t => (t.GetProperty("currency").GetString(), t.GetProperty("total").GetProperty("amount").GetString(), t.GetProperty("count").GetInt32())).ToList();
        Assert.Equal([("JOD", "100.003", 2), ("USD", "50.000", 1)], totals);

        var first = await (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).Content.ReadAsStringAsync();
        var second = await (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).Content.ReadAsStringAsync();
        Assert.Equal(first, second);

        var count = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE import_batch_id = @b", ("b", batchId));
        Assert.Equal(3, count);
    }

    /// <summary>AC-15 / SEC-44, T-81.</summary>
    [Fact]
    public async Task Upload_RejectsHostileFiles()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var csvBytes = Encoding.UTF8.GetBytes("a,b\n1,2\n");

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(client, "malware.exe", csvBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(client, "lying.xlsx", csvBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(client, "binary.csv", [0x00, 0x01, 0x02, 0xFF])).StatusCode);

        var bomb = XlsxBuilder.Build([[new("a")], [new("1")]], extraEntries: 300);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadRawAsync(client, "bomb.xlsx", bomb)).StatusCode);

        var oversized = await UploadRawAsync(client, "big.csv", new byte[10 * 1024 * 1024 + 1]);
        Assert.True(oversized.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest);

        // A traversal filename is stored sanitized; nothing on disk was ever addressed by it.
        var traversal = await UploadRawAsync(client, "../../etc/passwd.csv", csvBytes);
        Assert.Equal(HttpStatusCode.Created, traversal.StatusCode);
        Assert.Equal("passwd.csv", (await traversal.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("fileName").GetString());
    }

    /// <summary>AC-17 / SEC-48.</summary>
    [Fact]
    public async Task RawRows_AreDataOnly()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Injection Co." });

        const string hostile = "'; DROP TABLE invoices; --";
        var csv = $"Invoice No,Customer,Issue Date,Total,Note\n\"{hostile}\",Injection Co.,2026-09-01,1,\"=CMD()\"\n";
        var batchId = (await UploadAsync(client, "inject.csv", csv)).GetProperty("id").GetGuid();
        await MapAsync(client, batchId, new Dictionary<string, string>
        {
            ["Invoice No"] = "invoice_number",
            ["Customer"] = "customer_name",
            ["Issue Date"] = "issue_date",
            ["Total"] = "total_amount",
            ["Note"] = "notes",
        });

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).StatusCode);

        var stored = await fixture.Database.ScalarAsync<string>("SELECT invoice_number FROM invoices WHERE import_batch_id = @b", ("b", batchId));
        Assert.Equal(hostile, stored);

        var raw = await fixture.Database.ScalarAsync<string>("SELECT raw::text FROM import_rows WHERE batch_id = @b", ("b", batchId));
        Assert.Contains("DROP TABLE", raw, StringComparison.Ordinal);

        Assert.True(await fixture.Database.ScalarAsync<bool>("SELECT to_regclass('public.invoices') IS NOT NULL"));
    }

    /// <summary>AC-18 / INV-12, SEC-50.</summary>
    [Fact]
    public async Task Import_IsAudited()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Audit Co." });

        var batchId = await CommitCsvAsync(client, "audit.csv", Header + "\nAU-1,Audit Co.,2026-09-01,,JOD,1,0,1,\nAU-2,Audit Co.,2026-09-01,,JOD,2,0,2,\n");

        var transitions = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'invoice.status_changed' AND from_state = 'Imported' AND to_state = 'Open'", ("t", org.TenantId));
        Assert.Equal(2, transitions);

        var committed = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'import.committed' AND entity_id = @b", ("t", org.TenantId), ("b", batchId));
        Assert.Equal(1, committed);

        // The batch of audit rows is chained correctly (AuditWriter.WriteManyAsync).
        var broken = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit_events a
            WHERE a.tenant_id = @t AND a.prev_hash IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM audit_events b WHERE b.tenant_id = a.tenant_id AND b.hash = a.prev_hash AND b.id < a.id)
            """, ("t", org.TenantId));
        Assert.Equal(0, broken);
    }

    /// <summary>AC-22, AC-23.</summary>
    [Fact]
    public async Task Customer_WithOpenInvoice_CannotBeDeleted_AndShowsBalance()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        var customerId = await CreateCustomerAsync(client, new { nameEn = "Owing Co." });

        await CommitCsvAsync(client, "owing.csv", Header + "\nO-1,Owing Co.,2026-09-01,,JOD,100.500,0,100.500,\nO-2,Owing Co.,2026-09-01,,USD,7,0,7,0.709\n");

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{customerId}", ApiScenario.Json);
        var balances = detail.GetProperty("balances").EnumerateArray().Select(b => (b.GetProperty("currency").GetString(), b.GetProperty("openBalance").GetProperty("amount").GetString())).ToList();
        Assert.Equal([("JOD", "100.500"), ("USD", "7.000")], balances);

        var delete = await client.DeleteAsync($"/api/v1/customers/{customerId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, delete.StatusCode);
        Assert.Equal("has_open_balance", (await delete.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json))!.Code);
    }

    /// <summary>AC-24.</summary>
    [Fact]
    public async Task Mappings_ArePerTenant_AndReusable()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        var other = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        using var otherClient = fixture.Api.AuthenticatedClient(other.OwnerSession);
        await CreateCustomerAsync(client, new { nameEn = "Reuse Co." });

        var batchId = (await UploadAsync(client, "a.csv", Header + "\nRE-1,Reuse Co.,2026-09-01,,JOD,1,0,1,\n")).GetProperty("id").GetGuid();
        var mapped = await MapAsync(client, batchId, StandardMap, saveAs: "ERP export");
        var mappingId = mapped.GetProperty("mappingId").GetGuid();

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/import-mappings", ApiScenario.Json);
        Assert.Equal("ERP export", list.GetProperty("items")[0].GetProperty("name").GetString());

        // The other organization sees nothing, and cannot use the id.
        Assert.Equal(0, (await otherClient.GetFromJsonAsync<JsonElement>("/api/v1/import-mappings", ApiScenario.Json)).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.DeleteAsync($"/api/v1/import-mappings/{mappingId}")).StatusCode);

        // Reuse by id on a second batch, no column map repeated.
        var second = (await UploadAsync(client, "b.csv", Header + "\nRE-2,Reuse Co.,2026-09-02,,JOD,1,0,1,\n")).GetProperty("id").GetGuid();
        var reused = await client.PostAsJsonAsync($"/api/v1/imports/{second}/mapping", new { mappingId }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.Equal(1, (await reused.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("acceptedCount").GetInt32());
    }

    // ---------------------------------------------------------------------------------------

    private static async Task<Guid> CreateCustomerAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", body, ApiScenario.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();
    }

    /// <summary>T-140 / PRD-22: a 5,000-row CSV — upload, mapping (with its per-row validation and customer matching) and commit — in under 60 s.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public async Task Import_5000Rows_Under60s()
    {
        var org = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(org.OwnerSession);
        await fixture.Database.ExecuteAsync(
            """
            INSERT INTO customers (id, tenant_id, code, name_en, payment_terms_days)
            SELECT gen_random_uuid(), @t, 'IMP-' || g, 'Import customer ' || g, 30 FROM generate_series(1, 50) g;
            """, ("t", org.TenantId));

        var csv = new StringBuilder(Header).Append('\n');
        for (var i = 1; i <= 5_000; i++)
        {
            csv.Append("PERF-").Append(i).Append(",Import customer ").Append(1 + i % 50).Append(",2026-09-01,2026-10-01,JOD,1000.000,160.000,1160.000,\n");
        }

        var watch = Stopwatch.StartNew();
        var batchId = (await UploadAsync(client, "five-thousand.csv", csv.ToString())).GetProperty("id").GetGuid();
        var uploaded = watch.Elapsed;
        var mapped = await MapAsync(client, batchId, StandardMap);
        var mappedAt = watch.Elapsed;
        Assert.Equal(5_000, mapped.GetProperty("acceptedCount").GetInt32());
        var commit = await client.PostAsync($"/api/v1/imports/{batchId}/commit", null);
        watch.Stop();
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        Assert.Equal(5_000L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM invoices WHERE tenant_id = @t AND invoice_number LIKE 'PERF-%'", ("t", org.TenantId)));
        output.WriteLine($"import of 5,000 rows: {watch.Elapsed.TotalSeconds:F1} s total (upload {uploaded.TotalSeconds:F1} s, mapping {(mappedAt - uploaded).TotalSeconds:F1} s, commit {(watch.Elapsed - mappedAt).TotalSeconds:F1} s)");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"import of 5,000 rows took {watch.Elapsed.TotalSeconds:F1} s");
    }

    private static Task<JsonElement> UploadAsync(HttpClient client, string name, string csv) => UploadAsync(client, name, Encoding.UTF8.GetBytes(csv));

    private static async Task<JsonElement> UploadAsync(HttpClient client, string name, byte[] bytes)
    {
        var response = await UploadRawAsync(client, name, bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
    }

    private static Task<HttpResponseMessage> UploadRawAsync(HttpClient client, string name, byte[] bytes, bool force = false)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        return client.PostAsync(force ? "/api/v1/imports?force=true" : "/api/v1/imports", form);
    }

    private static async Task<JsonElement> MapAsync(HttpClient client, Guid batchId, Dictionary<string, string> map, string dateFormat = "yyyy-MM-dd", string decimalSeparator = ".", string? saveAs = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/imports/{batchId}/mapping",
            new { columnMap = map, dateFormat, decimalSeparator, saveAs }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
    }

    private static async Task<JsonElement> ResolveAsync(HttpClient client, Guid batchId, Guid rowId, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/imports/{batchId}/rows/{rowId}/resolve", body, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
    }

    private static async Task<Guid> CommitCsvAsync(HttpClient client, string name, string csv)
    {
        var batchId = (await UploadAsync(client, name, csv)).GetProperty("id").GetGuid();
        await MapAsync(client, batchId, StandardMap);
        var commit = await client.PostAsync($"/api/v1/imports/{batchId}/commit", null);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        return batchId;
    }
}
