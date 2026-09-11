using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.TestSupport;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 23 AC-01 … AC-05: holidays, the manual invoice, invoice edits, the invoice trail, the customer merge.</summary>
[Collection(ApiCollection.Name)]
public sealed class ContractCompletionTests(ApiTestFixture fixture)
{
    private static string D(int daysFromToday) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysFromToday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static object Invoice(Guid customerId, string number, string? net = "1000.000", string? tax = "160.000", string? total = "1160.000", string currency = "JOD", string? due = null, string? fx = null) =>
        new { customerId, invoiceNumber = number, issueDate = D(-10), dueDate = due, currency, netAmount = net, taxAmount = tax, totalAmount = total, fxRateToBase = fx };

    /// <summary>AC-01: the calendar, and the promise deadline that honours it.</summary>
    [Fact]
    public async Task Holidays_AreManaged_AndMoveThePromiseDeadline()
    {
        var s = await fixture.Api.NewCustomerAsync("Holiday Co.");
        var created = await s.Client.PostAsync("/api/v1/organization/holidays", new { date = "2026-12-25", name = "Christmas" });
        Assert.Equal("2026-12-25", created.GetProperty("date").GetString());
        var (dup, dupBody) = await s.Client.TryPostAsync("/api/v1/organization/holidays", new { date = "2026-12-25", name = "Again" });
        Assert.Equal(409, dup);
        Assert.Equal("holiday_exists", dupBody.GetProperty("code").GetString());
        var (bad, _) = await s.Client.TryPostAsync("/api/v1/organization/holidays", new { date = "25/12/2026", name = "x" });
        Assert.Equal(400, bad);
        await s.Client.PostAsync("/api/v1/organization/holidays", new { date = "2026-11-02", name = "Announced" });
        var list = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/holidays", ApiScenario.Json);
        Assert.Equal(new[] { "2026-11-02", "2026-12-25" }, list.GetProperty("items").EnumerateArray().Select(h => h.GetProperty("date").GetString()!).ToArray());

        // The deadline arithmetic the promise service already runs now sees the calendar: a holiday inside the grace window pushes the deadline.
        var settings = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization", ApiScenario.Json);
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "HOL-1", 500m, dueDate: D(-20), issueDate: D(-50));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        var promised = new DateOnly(2026, 12, 24);   // Thursday; grace crosses the 25th
        await s.Client.PostAsync("/api/v1/organization/holidays", new { date = "2026-12-27", name = "Bridge" });   // Sunday: a working day here (weekend is Fri+Sat)
        var promise = await s.Client.PostAsync($"/api/v1/cases/{caseId}/promises", new { invoiceIds = new[] { invoice }, promisedAmount = M(500m), promisedDate = promised.ToString("yyyy-MM-dd"), source = "call" });
        var holidays = new HashSet<DateOnly> { new(2026, 12, 25), new(2026, 12, 27), new(2026, 11, 2) };
        var grace = (await fixture.Database.ScalarAsync<int>("SELECT ptp_grace_business_days FROM tenant_settings WHERE tenant_id = @t", ("t", s.Organization.TenantId)));
        Assert.Equal(BusinessDays.Add(promised, grace, holidays).ToString("yyyy-MM-dd"), promise.GetProperty("deadlineDate").GetString());
        Assert.NotEqual(BusinessDays.Add(promised, grace, new HashSet<DateOnly>()).ToString("yyyy-MM-dd"), promise.GetProperty("deadlineDate").GetString());

        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync($"/api/v1/organization/holidays/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.DeleteAsync($"/api/v1/organization/holidays/{id}")).StatusCode);
        var collector = await fixture.Database.AddMemberAsync(s.Organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Collector);
        using var c = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(collector.Email, collector.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/organization/holidays")).StatusCode);
    }

    /// <summary>AC-02: the manual invoice runs the import's rules and enters AR the import's way.</summary>
    [Fact]
    public async Task ManualInvoice_ReconcilesLikeAnImport_AndEntersAr()
    {
        var s = await fixture.Api.NewCustomerAsync("Manual Co.");
        var created = await s.Client.PostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-1"));
        var inv = created.GetProperty("invoice");
        Assert.Equal("Open", inv.GetProperty("status").GetString());
        Assert.Equal("manual", inv.GetProperty("source").GetString());
        Assert.Equal("1160.000", created.Open());
        Assert.Equal(D(-10 + 30), inv.GetProperty("dueDate").GetString());   // FIN-72: issue + the customer's 30-day terms

        // Net only → total = net + tax; total only → net = total − tax; a mismatch is refused, not repaired.
        var netOnly = await s.Client.PostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-2", net: "100.000", tax: "16.000", total: null));
        Assert.Equal("116.000", netOnly.Open());
        var totalOnly = await s.Client.PostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-3", net: null, tax: "16.000", total: "116.000"));
        Assert.Equal("100.000", totalOnly.GetProperty("invoice").GetProperty("netAmount").GetProperty("amount").GetString());
        var (mismatch, mismatchBody) = await s.Client.TryPostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-4", net: "100.000", tax: "16.000", total: "120.000"));
        Assert.Equal(422, mismatch);
        Assert.Equal("totals_do_not_reconcile", mismatchBody.GetProperty("errors")[0].GetProperty("code").GetString());
        var (dueBefore, _) = await s.Client.TryPostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-5", due: D(-11)));
        Assert.Equal(422, dueBefore);
        var (noFx, noFxBody) = await s.Client.TryPostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-6", currency: "USD"));
        Assert.Equal(422, noFx);
        Assert.Equal("missing_fx_rate", noFxBody.GetProperty("errors")[0].GetProperty("code").GetString());
        var usd = await s.Client.PostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-6", currency: "USD", fx: "0.709"));
        Assert.Equal("USD", usd.GetProperty("invoice").GetProperty("currency").GetString());
        var (dupNumber, dupBody) = await s.Client.TryPostAsync("/api/v1/invoices", Invoice(s.CustomerId, "man-1"));
        Assert.Equal(422, dupNumber);
        Assert.Equal("duplicate_invoice_number", dupBody.GetProperty("errors")[0].GetProperty("code").GetString());
        var (strangerCustomer, _) = await s.Client.TryPostAsync("/api/v1/invoices", Invoice(Guid.CreateVersion7(), "MAN-7"));
        Assert.Equal(404, strangerCustomer);
        var (badMoney, _) = await s.Client.TryPostAsync("/api/v1/invoices", Invoice(s.CustomerId, "MAN-8", net: "1.2345"));
        Assert.Equal(400, badMoney);

        // Audited as the import's I2, and visible where an imported invoice is visible.
        var trail = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{inv.GetProperty("id").GetGuid()}/audit", ApiScenario.Json);
        var opened = trail.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("eventType").GetString() == "invoice.status_changed");
        Assert.Equal("manual_entry", opened.GetProperty("reasonCode").GetString());
        Assert.Equal("MAN-1", opened.GetProperty("changes").GetProperty("invoiceNumber").GetString());
        var position = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{s.CustomerId}", ApiScenario.Json);
        Assert.Contains(position.GetProperty("balances").EnumerateArray(), b => b.GetProperty("currency").GetString() == "JOD" && b.GetProperty("openBalance").GetProperty("amount").GetString() == "1392.000");
    }

    /// <summary>AC-03 / AC-04: the three editable fields, nothing else, audited; a void invoice is immutable.</summary>
    [Fact]
    public async Task InvoiceEdit_ChangesOnlyTheThreeFields()
    {
        var s = await fixture.Api.NewCustomerAsync("Edit Co.");
        var id = (await s.Client.PostAsync("/api/v1/invoices", Invoice(s.CustomerId, "EDIT-1"))).GetProperty("invoice").GetProperty("id").GetGuid();
        var patched = await Patch(s.Client, $"/api/v1/invoices/{id}", new { dueDate = D(40), poReference = "PO-77", notes = "called on Monday" });
        Assert.Equal(200, patched.Status);
        Assert.Equal(D(40), patched.Body.GetProperty("invoice").GetProperty("dueDate").GetString());
        Assert.Equal("PO-77", patched.Body.GetProperty("invoice").GetProperty("poReference").GetString());

        var amount = await Patch(s.Client, $"/api/v1/invoices/{id}", new { totalAmount = "1.000" });
        Assert.Equal(400, amount.Status);
        Assert.Equal("unexpected_field", amount.Body.GetProperty("code").GetString());
        var before = await Patch(s.Client, $"/api/v1/invoices/{id}", new { dueDate = D(-11) });
        Assert.Equal(422, before.Status);
        var cleared = await Patch(s.Client, $"/api/v1/invoices/{id}", new { poReference = "" });
        Assert.Equal(JsonValueKind.Null, cleared.Body.GetProperty("invoice").GetProperty("poReference").ValueKind);

        var trail = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{id}/audit", ApiScenario.Json);
        var updates = trail.GetProperty("items").EnumerateArray().Where(e => e.GetProperty("eventType").GetString() == "invoice.updated").ToList();
        Assert.Equal(2, updates.Count);
        Assert.Equal("PO-77", updates[1].GetProperty("changes").GetProperty("poReference").GetProperty("new").GetString());
        Assert.Equal(JsonValueKind.Null, updates[1].GetProperty("changes").GetProperty("poReference").GetProperty("old").ValueKind);

        await s.Client.PostAsync($"/api/v1/invoices/{id}/void", new { reason = "test" });
        var voided = await Patch(s.Client, $"/api/v1/invoices/{id}", new { notes = "x" });
        Assert.Equal(422, voided.Status);
        Assert.Equal("invoice_void", voided.Body.GetProperty("errors")[0].GetProperty("code").GetString());

        var other = await fixture.Api.NewCustomerAsync("Other Edit Co.");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/invoices/{id}/audit")).StatusCode);
        Assert.Equal(404, (await Patch(other.Client, $"/api/v1/invoices/{id}", new { notes = "x" })).Status);
    }

    /// <summary>AC-05: preview, refusals, confirm — every child table moves, the source stays, the merge is audited and irreversible.</summary>
    [Fact]
    public async Task CustomerMerge_MovesEverything_Once()
    {
        var s = await fixture.Api.NewCustomerAsync("Keep Co.");
        var target = s.CustomerId;
        var source = (await s.Client.PostAsync("/api/v1/customers", new { nameEn = "Keep Co (dup)", defaultCurrency = "JOD" })).GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/customers/{source}/contacts", new { name = "Dup contact", email = $"dup-{Guid.NewGuid():N}@example.test", isPrimary = true });
        await s.Client.PostAsync($"/api/v1/customers/{target}/contacts", new { name = "Main contact", email = $"main-{Guid.NewGuid():N}@example.test", isPrimary = true });
        var srcInvoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, source, "DUP-1", 300m, dueDate: D(-15), issueDate: D(-45));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, target, "KEEP-1", 700m, dueDate: D(-15), issueDate: D(-45));
        var payment = (await s.Client.PostAsync("/api/v1/payments", new { customerId = source, amount = M(100m), method = "Cash", receivedDate = D(0) })).GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/payments/{payment}/allocations", new { lines = new[] { new { invoiceId = srcInvoice, amount = M(100m) } } });
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });   // both now have an open case → refused until one is resolved

        var (same, sameBody) = await s.Client.TryPostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = target });
        Assert.Equal(422, same);
        Assert.Equal("same_customer", sameBody.GetProperty("errors")[0].GetProperty("code").GetString());

        var preview = await s.Client.PostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source });
        Assert.False(preview.GetProperty("merged").GetBoolean());
        Assert.True(preview.GetProperty("bothHaveOpenCases").GetBoolean());
        var token = preview.GetProperty("confirmToken").GetString()!;
        var (blocked, blockedBody) = await s.Client.TryPostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source, confirmToken = token });
        Assert.Equal(422, blocked);
        Assert.Equal("both_have_open_cases", blockedBody.GetProperty("errors")[0].GetProperty("code").GetString());

        // Resolve the duplicate's case by paying it off, then the conflict rule: a shared invoice number.
        var settle = (await s.Client.PostAsync("/api/v1/payments", new { customerId = source, amount = M(200m), method = "Cash", receivedDate = D(0) })).GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/payments/{settle}/allocations", new { lines = new[] { new { invoiceId = srcInvoice, amount = M(200m) } } });
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var clash = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, source, "KEEP-1", 10m, dueDate: D(30), issueDate: D(0));
        var (conflict, conflictBody) = await s.Client.TryPostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source, confirmToken = (await s.Client.PostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source })).GetProperty("confirmToken").GetString() });
        Assert.Equal(422, conflict);
        Assert.Equal("invoice_number_conflict", conflictBody.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Contains("KEEP-1", conflictBody.GetProperty("errors")[0].GetProperty("meta").GetProperty("invoiceNumbers").GetString());
        await s.Client.PostAsync($"/api/v1/invoices/{clash}/void", new { reason = "duplicate" });

        // A stale token (counts changed since the preview) is refused; a fresh preview's token is accepted.
        var stale = await s.Client.TryPostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source, confirmToken = token });
        Assert.Equal(422, stale.Status);
        Assert.Equal("confirm_token_stale", stale.Body.GetProperty("errors")[0].GetProperty("code").GetString());
        var fresh = await s.Client.PostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source });
        var moves = fresh.GetProperty("moves").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("rows").GetInt32());
        Assert.Equal(1, moves["customer_contacts"]);
        Assert.Equal(2, moves["invoices"]);
        Assert.Equal(2, moves["payments"]);
        Assert.Equal(1, moves["collection_cases"]);

        var merged = await s.Client.PostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source, confirmToken = fresh.GetProperty("confirmToken").GetString() });
        Assert.True(merged.GetProperty("merged").GetBoolean());
        foreach (var (table, expected) in moves)
        {
            Assert.Equal(0L, await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id = @t AND customer_id = @c", ("t", s.Organization.TenantId), ("c", source)));
            Assert.True(await fixture.Database.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id = @t AND customer_id = @c", ("t", s.Organization.TenantId), ("c", target)) >= expected, table);
        }

        var sourceRow = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{source}", ApiScenario.Json);
        Assert.Equal(target, sourceRow.GetProperty("mergedIntoId").GetGuid());
        Assert.Equal("Inactive", sourceRow.GetProperty("status").GetString());
        var targetRow = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{target}", ApiScenario.Json);
        Assert.Equal("700.000", targetRow.GetProperty("balances").EnumerateArray().Single(b => b.GetProperty("currency").GetString() == "JOD").GetProperty("openBalance").GetProperty("amount").GetString());
        Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM customer_contacts WHERE tenant_id = @t AND customer_id = @c AND is_primary", ("t", s.Organization.TenantId), ("c", target)));

        var audit = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?eventType=customer.merged", ApiScenario.Json);
        Assert.Equal(2, audit.GetProperty("items")[0].GetProperty("changes").GetProperty("moved").GetProperty("invoices").GetInt32());
        var (again, againBody) = await s.Client.TryPostAsync($"/api/v1/customers/{target}/merge", new { sourceCustomerId = source });
        Assert.Equal(422, again);
        Assert.Equal("already_merged", againBody.GetProperty("errors")[0].GetProperty("code").GetString());

        var other = await fixture.Api.NewCustomerAsync("Other Merge Co.");
        var (cross, _) = await other.Client.TryPostAsync($"/api/v1/customers/{other.CustomerId}/merge", new { sourceCustomerId = target });
        Assert.Equal(404, cross);
    }

    private static async Task<(int Status, JsonElement Body)> Patch(HttpClient client, string path, object body)
    {
        var r = await client.PatchAsJsonAsync(path, body, ApiScenario.Json);
        var text = await r.Content.ReadAsStringAsync();
        return ((int)r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
