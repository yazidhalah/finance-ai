using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 7 AC-02 … AC-14 against the real database and API.</summary>
[Collection(ApiCollection.Name)]
public sealed class DisputeTests(ApiTestFixture fixture)
{
    private static readonly TimeZoneInfo Amman = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Amman).DateTime);
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private sealed record Ctx(Setup S, Guid CaseId, Guid Invoice);

    private async Task<Ctx> OpenAsync(string name, decimal total = 6_000m)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", total, dueDate: D(-10), issueDate: D(-40));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        return new Ctx(s, caseId, invoice);
    }

    private static object Raise(decimal amount, string reason = "goods_damaged", string? claim = "two pallets arrived crushed") =>
        new { reasonCode = reason, disputedAmount = M(amount), customerClaim = claim };

    private static async Task<JsonElement> CaseAsync(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{id}", ApiScenario.Json);
    private static async Task<JsonElement> DisputeAsync(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/disputes/{id}", ApiScenario.Json);

    /// <summary>AC-02 / SM-40 / SM-43 / SM-48.</summary>
    [Fact]
    public async Task Raise_Guards_AndSlaClocks()
    {
        var x = await OpenAsync("Guard");
        var (bad, _) = await x.S.Client.TryPostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(100m, "vibes"));
        Assert.Equal(400, bad);
        var (over, overBody) = await x.S.Client.TryPostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(6_000.001m));
        Assert.Equal(422, over);
        Assert.Equal("exceeds_open_balance", overBody.GetProperty("errors")[0].GetProperty("code").GetString());

        var raised = await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(2_000m));
        Assert.Equal("Open", raised.GetProperty("status").GetString());
        Assert.Equal("on_track", raised.GetProperty("slaState").GetString());
        var first = DateTimeOffset.Parse(raised.GetProperty("firstResponseDueAt").GetString()!);
        var resolution = DateTimeOffset.Parse(raised.GetProperty("resolutionDueAt").GetString()!);
        Assert.True(first > DateTimeOffset.UtcNow && resolution > first);
        var firstLocal = TimeZoneInfo.ConvertTime(first, Amman);
        Assert.Equal(18, firstLocal.Hour);
        Assert.True(firstLocal.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday));
        Assert.Equal(x.CaseId, raised.GetProperty("caseId").GetGuid());

        var (dup, dupBody) = await x.S.Client.TryPostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(100m));
        Assert.Equal(409, dup);
        Assert.Equal("duplicate", dupBody.GetProperty("code").GetString());

        var settled = await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, x.S.CustomerId, "Guard-settled", 100m, dueDate: D(-5), issueDate: D(-35));
        await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(100m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = settled, amount = M(100m) } } });
        var (closed, _) = await x.S.Client.TryPostAsync($"/api/v1/invoices/{settled}/disputes", Raise(10m));
        Assert.Equal(422, closed);
    }

    /// <summary>AC-03 / C6 / SM-52.</summary>
    [Fact]
    public async Task Raise_MovesTheCaseToDisputed()
    {
        var x = await OpenAsync("Disputed");
        var before = (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("priorityScore").GetInt32();
        // A promise first: SM-52 says the dispute wins and the promise stays Active.
        var promise = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", new { invoiceIds = new[] { x.Invoice }, promisedAmount = M(1_000m), promisedDate = D(3), source = "call" });
        Assert.Equal("PromiseActive", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());

        await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(2_000m));
        var c = (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case");
        Assert.Equal("Disputed", c.GetProperty("status").GetString());
        Assert.Equal(1, c.GetProperty("openDisputes").GetInt32());
        Assert.False(c.GetProperty("disputeSlaBreached").GetBoolean());
        Assert.True(c.GetProperty("priorityScore").GetInt32() < before, "the dispute dampener lowers the priority");
        Assert.Contains(c.GetProperty("priorityFactors").EnumerateArray(), f => f.GetProperty("factor").GetString() == "dispute" && f.GetProperty("contribution").GetInt32() < 0);
        Assert.Equal("resolve_dispute", c.GetProperty("suggestedAction").GetProperty("kind").GetString());
        Assert.Equal("Active", (await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promise.GetProperty("id").GetGuid()}", ApiScenario.Json)).GetProperty("status").GetString());
        // The queue row carries the flag rather than hiding the case.
        var row = (await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("caseId").GetGuid() == x.CaseId);
        Assert.Equal("Disputed", row.GetProperty("status").GetString());
        Assert.Equal(1, row.GetProperty("openDisputes").GetInt32());
    }

    /// <summary>AC-04 / SM-45 / E3: 6,000 invoice, 4,000 paid, 2,000 disputed and accepted → Settled.</summary>
    [Fact]
    public async Task Accept_CreatesAndAppliesTheCreditNote_E3()
    {
        var x = await OpenAsync("E3");
        await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(4_000m), method = "BankTransfer", receivedDate = D(0), allocations = new[] { new { invoiceId = x.Invoice, amount = M(4_000m) } } });
        var raised = await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(2_000m));
        var id = raised.GetProperty("id").GetGuid();
        Assert.Equal("2000.000", raised.GetProperty("invoiceOpenBalance").GetProperty("amount").GetString());

        // Aging shows the disputed slice inside the bucket and as its own column (FIN-56).
        var aging = await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/reports/aging?groupBy=customer", ApiScenario.Json);
        Assert.True(aging.GetProperty("disputedAvailable").GetBoolean());
        var jod = aging.GetProperty("currencies").EnumerateArray().Single(s => s.GetProperty("currency").GetString() == "JOD");
        Assert.Equal("2000.000", jod.GetProperty("disputedTotal").GetProperty("amount").GetString());
        Assert.Equal("2000.000", jod.GetProperty("total").GetProperty("amount").GetString());   // still in the bucket

        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "assign" });
        var (noPerm, _) = await x.S.Client.TryPostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "accept" });
        Assert.Equal(400, noPerm);   // not an update event; resolution has its own door

        var resolved = await x.S.Client.PostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "accepted", reason = "damage confirmed by the driver" });
        Assert.Equal("Accepted", resolved.GetProperty("status").GetString());
        Assert.Equal("2000.000", resolved.GetProperty("resolutionAmount").GetProperty("amount").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, resolved.GetProperty("resolvedBy").GetGuid());
        var noteId = resolved.GetProperty("creditNoteId").GetGuid();

        var note = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/credit-notes/{noteId}", ApiScenario.Json);
        Assert.Equal("dispute_resolution", note.GetProperty("reasonCode").GetString());
        Assert.Equal("2000.000", note.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("0.000", note.GetProperty("unapplied").GetProperty("amount").GetString());
        var invoice = await x.S.Client.InvoiceAsync(x.Invoice);
        Assert.Equal("Settled", invoice.Status());
        Assert.Equal("0.000", invoice.Open());
        Assert.Equal("Resolved", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());   // C10 through SM-50
    }

    /// <summary>AC-05 / SM-45 / SM-46: one transaction, both writes undone.</summary>
    [Fact]
    public async Task Accept_BeyondOpenBalance_RollsBackEverything()
    {
        var x = await OpenAsync("Rollback", total: 3_000m);
        var id = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(2_000m))).GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "assign" });
        // A payment after raising leaves less open than the disputed amount.
        await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(1_500m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = x.Invoice, amount = M(1_500m) } } });

        var (status, body) = await x.S.Client.TryPostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "accepted", reason = "ok" });
        Assert.Equal(422, status);
        Assert.Equal("exceeds_open_balance", body.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("1500.000", body.GetProperty("errors")[0].GetProperty("meta").GetProperty("openBalance").GetString());

        var after = await DisputeAsync(x.S.Client, id);
        Assert.Equal("UnderReview", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("creditNoteId").ValueKind);
        Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM credit_notes WHERE tenant_id = @t", ("t", x.S.Organization.TenantId)));
        Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE entity_id = @d AND to_state = 'Accepted'", ("d", id)));

        // The ledger's own guard, forced past the service: partial for what is open still works.
        var ok = await x.S.Client.PostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "partially_accepted", resolutionAmount = M(1_500m), reason = "half" });
        Assert.Equal("PartiallyAccepted", ok.GetProperty("status").GetString());
        Assert.Equal("Settled", (await x.S.Client.InvoiceAsync(x.Invoice)).Status());
    }

    /// <summary>AC-06.</summary>
    [Fact]
    public async Task PartialAndReject()
    {
        var x = await OpenAsync("Partial");
        var id = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(2_000m))).GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "assign" });
        var (tooMuch, tooMuchBody) = await x.S.Client.TryPostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "partially_accepted", resolutionAmount = M(2_000m), reason = "all" });
        Assert.Equal(422, tooMuch);
        Assert.Equal("partial_must_be_below_disputed", tooMuchBody.GetProperty("errors")[0].GetProperty("code").GetString());

        var partial = await x.S.Client.PostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "partially_accepted", resolutionAmount = M(500m), reason = "one pallet" });
        Assert.Equal("PartiallyAccepted", partial.GetProperty("status").GetString());
        Assert.Equal("5500.000", (await x.S.Client.InvoiceAsync(x.Invoice)).Open());

        var y = await OpenAsync("Reject");
        var rid = (await y.S.Client.PostAsync($"/api/v1/invoices/{y.Invoice}/disputes", Raise(2_000m))).GetProperty("id").GetGuid();
        await y.S.Client.PostAsync($"/api/v1/disputes/{rid}/transitions", new { @event = "assign" });
        var (noReason, _) = await y.S.Client.TryPostAsync($"/api/v1/disputes/{rid}/resolve", new { outcome = "rejected" });
        Assert.Equal(400, noReason);
        var rejected = await y.S.Client.PostAsync($"/api/v1/disputes/{rid}/resolve", new { outcome = "rejected", reason = "signed delivery note on file" });
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rejected.GetProperty("creditNoteId").ValueKind);
        Assert.Equal("6000.000", (await y.S.Client.InvoiceAsync(y.Invoice)).Open());
        Assert.Equal("InProgress", (await CaseAsync(y.S.Client, y.CaseId)).GetProperty("case").GetProperty("status").GetString());   // C7
    }

    /// <summary>AC-07 / SM-47 / T-60.</summary>
    [Fact]
    public async Task Collector_CannotResolve()
    {
        var x = await OpenAsync("Roles");
        var collector = await fixture.Database.AddMemberAsync(x.S.Organization.TenantId, TenantRole.Collector);
        using var collectorClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(collector.Email, collector.Password));
        var (raised, body) = await collectorClient.TryPostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(500m));
        Assert.Equal(201, raised);
        var id = body.GetProperty("id").GetGuid();
        var (assigned, _) = await collectorClient.TryPostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "assign" });
        Assert.Equal(200, assigned);
        var (forbidden, _) = await collectorClient.TryPostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "rejected", reason = "no" });
        Assert.Equal(403, forbidden);

        var accountant = await fixture.Database.AddMemberAsync(x.S.Organization.TenantId, TenantRole.Accountant);
        using var accountantClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));
        var (ok, resolved) = await accountantClient.TryPostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "rejected", reason = "no" });
        Assert.Equal(200, ok);
        Assert.Equal(accountant.UserId, resolved.GetProperty("resolvedBy").GetGuid());
    }

    /// <summary>AC-08 / SM-25 / T-47.</summary>
    [Fact]
    public async Task DunningGuard_FollowsTheSetting()
    {
        var x = await OpenAsync("Dunning");
        var sibling = await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, x.S.CustomerId, "Dunning-2", 500m, dueDate: D(-5), issueDate: D(-35));
        await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
        var id = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(1_000m))).GetProperty("id").GetGuid();

        JsonElement Row(JsonElement r, Guid invoice) => r.GetProperty("invoices").EnumerateArray().Single(i => i.GetProperty("invoiceId").GetGuid() == invoice);
        var off = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{x.CaseId}/dunning-eligibility", ApiScenario.Json);
        Assert.False(off.GetProperty("allowSplitDunningDuringDispute").GetBoolean());
        Assert.Equal("dispute_blocks_send", Row(off, x.Invoice).GetProperty("reason").GetString());
        Assert.Equal("dispute_blocks_send", Row(off, sibling).GetProperty("reason").GetString());   // split dunning off: the whole customer

        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET allow_split_dunning_during_dispute = true WHERE tenant_id = @t", ("t", x.S.Organization.TenantId));
        var on = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{x.CaseId}/dunning-eligibility", ApiScenario.Json);
        Assert.False(Row(on, x.Invoice).GetProperty("allowed").GetBoolean());
        Assert.True(Row(on, sibling).GetProperty("allowed").GetBoolean());

        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "withdraw", reason = "customer accepted the goods" });
        var after = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{x.CaseId}/dunning-eligibility", ApiScenario.Json);
        Assert.All(after.GetProperty("invoices").EnumerateArray(), i => Assert.True(i.GetProperty("allowed").GetBoolean()));
    }

    /// <summary>AC-09 / C7.</summary>
    [Fact]
    public async Task Resolution_ReturnsTheCase()
    {
        var x = await OpenAsync("Two");
        var second = await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, x.S.CustomerId, "Two-2", 500m, dueDate: D(-5), issueDate: D(-35));
        await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
        var a = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(1_000m))).GetProperty("id").GetGuid();
        var b = (await x.S.Client.PostAsync($"/api/v1/invoices/{second}/disputes", Raise(100m, "wrong_amount"))).GetProperty("id").GetGuid();
        Assert.Equal(2, (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("openDisputes").GetInt32());

        await x.S.Client.PostAsync($"/api/v1/disputes/{a}/transitions", new { @event = "cancel", reason = "raised in error" });
        Assert.Equal("Disputed", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());
        await x.S.Client.PostAsync($"/api/v1/disputes/{b}/transitions", new { @event = "assign" });
        await x.S.Client.PostAsync($"/api/v1/disputes/{b}/resolve", new { outcome = "rejected", reason = "amount matches the PO" });
        var c = (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case");
        Assert.Equal("InProgress", c.GetProperty("status").GetString());
        Assert.Equal(0, c.GetProperty("openDisputes").GetInt32());
    }

    /// <summary>AC-10 / SM-48.</summary>
    [Fact]
    public async Task Sla_PausesAndBreaches()
    {
        var x = await OpenAsync("Sla");
        var id = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(1_000m))).GetProperty("id").GetGuid();
        var raised = await DisputeAsync(x.S.Client, id);
        var due = DateTimeOffset.Parse(raised.GetProperty("resolutionDueAt").GetString()!);
        var firstDue = DateTimeOffset.Parse(raised.GetProperty("firstResponseDueAt").GetString()!);
        try
        {
            // No first response by its due moment → breached, visible on the row and the summary is unaffected in count but the row flags it.
            fixture.Api.Clock.Override = firstDue.AddHours(1);
            var late = await DisputeAsync(x.S.Client, id);
            Assert.True(late.GetProperty("slaBreached").GetBoolean());
            Assert.Equal("breached", late.GetProperty("slaState").GetString());
            var row = (await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("caseId").GetGuid() == x.CaseId);
            Assert.True(row.GetProperty("disputeSlaBreached").GetBoolean());
            var breachedList = await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/disputes?slaBreached=true", ApiScenario.Json);
            Assert.Contains(breachedList.GetProperty("items").EnumerateArray(), d => d.GetProperty("id").GetGuid() == id);

            // Assign (the first response) clears that half; then a pause of three days moves the resolution clock.
            await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "assign" });
            Assert.False((await DisputeAsync(x.S.Client, id)).GetProperty("slaBreached").GetBoolean());
            await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "request_info" });
            Assert.Equal("paused", (await DisputeAsync(x.S.Client, id)).GetProperty("slaState").GetString());
            fixture.Api.Clock.Override = firstDue.AddHours(1).AddDays(3);
            await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "info_received" });
            var resumed = await DisputeAsync(x.S.Client, id);
            var shifted = DateTimeOffset.Parse(resumed.GetProperty("resolutionDueAt").GetString()!);
            Assert.Equal(due.AddDays(3), shifted);
            Assert.Equal("UnderReview", resumed.GetProperty("status").GetString());

            // Past the shifted due moment: breached. A pending dispute is timed out by the sweep after five business days.
            fixture.Api.Clock.Override = shifted.AddHours(1);
            Assert.True((await DisputeAsync(x.S.Client, id)).GetProperty("slaBreached").GetBoolean());
            await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "request_info" });
            fixture.Api.Clock.Override = shifted.AddDays(10);
            await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
            var timedOut = await DisputeAsync(x.S.Client, id);
            Assert.Equal("UnderReview", timedOut.GetProperty("status").GetString());
            Assert.Equal("system", await fixture.Database.ScalarAsync<string>("SELECT actor_kind FROM audit_events WHERE entity_id = @d AND reason_code = 'timeout'", ("d", id)));
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-11 / SM-44 / SM-10.</summary>
    [Fact]
    public async Task AlreadyPaid_OpensAVerificationTask()
    {
        var x = await OpenAsync("Paid");
        var raised = await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(6_000m, "already_paid", "we paid by transfer on the 3rd"));
        var taskId = raised.GetProperty("verificationTaskId").GetGuid();
        var tasks = await x.S.Client.GetFromJsonAsync<JsonElement>("/api/v1/tasks/payment-verification?status=Open", ApiScenario.Json);
        var task = tasks.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == taskId);
        Assert.Equal("we paid by transfer on the 3rd", task.GetProperty("claim").GetString());
        Assert.Equal("Open", task.GetProperty("invoiceStatus").GetString());

        var (needsPayment, _) = await x.S.Client.TryPostAsync($"/api/v1/tasks/payment-verification/{taskId}/resolve", new { outcome = "payment_found" });
        Assert.Equal(422, needsPayment);
        var (unknownPayment, _) = await x.S.Client.TryPostAsync($"/api/v1/tasks/payment-verification/{taskId}/resolve", new { outcome = "payment_found", paymentId = Guid.CreateVersion7() });
        Assert.Equal(404, unknownPayment);

        // The payment is found and recorded through the ordinary path; the task only points at it.
        var payment = await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(6_000m), method = "BankTransfer", receivedDate = D(-8), reference = "found in the bank statement", allocations = new[] { new { invoiceId = x.Invoice, amount = M(6_000m) } } });
        var resolved = await x.S.Client.PostAsync($"/api/v1/tasks/payment-verification/{taskId}/resolve", new { outcome = "payment_found", paymentId = payment.GetProperty("id").GetGuid(), notes = "matched" });
        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        Assert.Equal("Settled", resolved.GetProperty("invoiceStatus").GetString());   // settled by the payment, not by the task
        // No route marks an invoice paid.
        foreach (var path in new[] { $"/api/v1/invoices/{x.Invoice}/mark-paid", $"/api/v1/invoices/{x.Invoice}/paid", $"/api/v1/tasks/payment-verification/{taskId}/mark-paid" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await x.S.Client.PostAsJsonAsync(path, new { }, ApiScenario.Json)).StatusCode);
        }
    }

    /// <summary>AC-13 / SEC-40 / SEC-44.</summary>
    [Fact]
    public async Task Evidence_IsAllowlistedAndIsolated()
    {
        var x = await OpenAsync("Evidence");
        var id = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(100m))).GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> Upload(string name, byte[] bytes, string contentType)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", name);
            return await x.S.Client.PostAsync($"/api/v1/disputes/{id}/evidence", form);
        }

        var pdf = await Upload("delivery-note.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< >>\nendobj\n%%EOF"), "application/pdf");
        Assert.Equal(HttpStatusCode.Created, pdf.StatusCode);
        var evidence = await pdf.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json);
        Assert.Equal("application/pdf", evidence.GetProperty("contentType").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Upload("tool.exe", [0x4D, 0x5A, 0x90, 0x00, 0x03], "application/octet-stream")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Upload("innocent.pdf", Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"), "application/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Upload("renamed.png", Encoding.ASCII.GetBytes("%PDF-1.4 really a pdf"), "image/png")).StatusCode);
        var big = new byte[EvidenceInspector_MaxBytes + 1];
        big[0] = (byte)'%'; big[1] = (byte)'P'; big[2] = (byte)'D'; big[3] = (byte)'F'; big[4] = (byte)'-';
        var oversize = await Upload("big.pdf", big, "application/pdf");
        Assert.True(oversize.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, oversize.StatusCode.ToString());

        var download = await x.S.Client.GetAsync($"/api/v1/disputes/{id}/evidence/{evidence.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(await download.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);

        var other = await fixture.Api.NewCustomerAsync("Other Co.");
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/disputes/{id}/evidence/{evidence.GetProperty("id").GetGuid()}")).StatusCode);
    }

    private const int EvidenceInspector_MaxBytes = 10 * 1024 * 1024;

    /// <summary>AC-14 / SM-03 / SM-47.</summary>
    [Fact]
    public async Task DisputeTransitions_AreAudited_OneToOne()
    {
        var x = await OpenAsync("Audit");
        var id = (await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/disputes", Raise(1_000m))).GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "assign" });
        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "request_info" });
        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/transitions", new { @event = "info_received" });
        await x.S.Client.PostAsync($"/api/v1/disputes/{id}/resolve", new { outcome = "partially_accepted", resolutionAmount = M(200m), reason = "one item" });

        var audit = await fixture.Database.ScalarAsync<string>("SELECT string_agg(coalesce(from_state,'-') || '>' || to_state || ':' || actor_kind, ',' ORDER BY id) FROM audit_events WHERE entity_id = @d AND event_type = 'dispute.status_changed'", ("d", id));
        Assert.Equal("->Open:user,Open>UnderReview:user,UnderReview>PendingCustomer:user,PendingCustomer>UnderReview:user,UnderReview>PartiallyAccepted:user", audit);
        Assert.Equal(5L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM case_activities WHERE case_id = @c AND kind = 'dispute'", ("c", x.CaseId)));
        Assert.Equal(x.S.Organization.OwnerUserId, await fixture.Database.ScalarAsync<Guid>("SELECT resolved_by FROM disputes WHERE id = @d", ("d", id)));
    }
}
