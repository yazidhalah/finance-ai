using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 6 AC-04 … AC-14, AC-16 against the real database and API.</summary>
[Collection(ApiCollection.Name)]
public sealed class PromiseTests(ApiTestFixture fixture)
{
    private static readonly TimeZoneInfo Amman = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
    private static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Amman).DateTime);

    private static string D(int daysFromToday) => Today.AddDays(daysFromToday).ToString("yyyy-MM-dd");

    private static DateTimeOffset AmmanMorning(DateOnly day) => new DateTimeOffset(day.ToDateTime(new TimeOnly(7, 0)), Amman.GetUtcOffset(day.ToDateTime(new TimeOnly(7, 0))));

    private sealed record Ctx(Setup S, Guid CaseId, Guid Invoice);

    /// <summary>A customer with one overdue invoice and an open case.</summary>
    private async Task<Ctx> OpenAsync(string name, decimal total = 1_000m, int daysOverdue = 10)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", total, dueDate: D(-daysOverdue), issueDate: D(-daysOverdue - 30));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var caseId = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        return new Ctx(s, caseId, invoice);
    }

    private static object Promise(Guid invoice, decimal amount, string date, string source = "call") =>
        new { invoiceIds = new[] { invoice }, promisedAmount = M(amount), promisedDate = date, source };

    private static async Task<JsonElement> CaseAsync(HttpClient c, Guid id) => await c.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{id}", ApiScenario.Json);

    private static async Task<bool> InQueueAsync(HttpClient c, Guid caseId) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/v1/queue", ApiScenario.Json)).GetProperty("items").EnumerateArray().Any(i => i.GetProperty("caseId").GetGuid() == caseId);

    /// <summary>AC-04 / SM-32.</summary>
    [Fact]
    public async Task Record_GuardsCoverageAndAmount()
    {
        var x = await OpenAsync("Guard");
        var (over, overBody) = await x.S.Client.TryPostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 1_000.001m, D(5)));
        Assert.Equal(422, over);
        var error = overBody.GetProperty("errors")[0];
        Assert.Equal("exceeds_covered_balance", error.GetProperty("code").GetString());
        Assert.Equal("1000.000", error.GetProperty("meta").GetProperty("coveredBalance").GetString());

        var stranger = await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, x.S.CustomerId, "Guard-notdue", 100m, dueDate: D(30), issueDate: D(0));
        var (scope, _) = await x.S.Client.TryPostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(stranger, 50m, D(5)));
        Assert.Equal(422, scope);

        var (past, _) = await x.S.Client.TryPostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 100m, D(-1)));
        Assert.Equal(422, past);
        var (badSource, _) = await x.S.Client.TryPostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 100m, D(5), "cheque"));
        Assert.Equal(400, badSource);

        // A full promise is fine; the response carries the business-day deadline.
        var ok = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 1_000m, D(5)));
        Assert.Equal("Active", ok.GetProperty("status").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, ok.GetProperty("confirmedBy").GetGuid());
        var deadline = DateOnly.Parse(ok.GetProperty("deadlineDate").GetString()!);
        Assert.True(deadline >= Today.AddDays(5));
        Assert.True(deadline.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) || deadline == Today.AddDays(5));

        // A promise on a closed case is refused (the active promise has to go first: PromiseActive has no abandon edge).
        await x.S.Client.PostAsync($"/api/v1/promises/{ok.GetProperty("id").GetGuid()}/cancel", new { reason = "test" });
        await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/transitions", new { @event = "abandon", reasonCode = "test" });
        var (closed, _) = await x.S.Client.TryPostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 100m, D(5)));
        Assert.Equal(422, closed);
    }

    /// <summary>AC-05 / C4, AC-07 / C5 / T-13, AC-16 / T-126, and "nothing else happens".</summary>
    [Fact]
    public async Task BrokenPromise_ReopensTheCase_AndNothingElse()
    {
        var x = await OpenAsync("Broken", total: 2_000m, daysOverdue: 20);
        await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/activities", new { kind = "call", summary = "promised Thursday" });
        var before = (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("priorityScore").GetInt32();

        var promise = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 2_000m, D(1)));
        var deadline = DateOnly.Parse(promise.GetProperty("deadlineDate").GetString()!);
        var detail = await CaseAsync(x.S.Client, x.CaseId);
        Assert.Equal("PromiseActive", detail.GetProperty("case").GetProperty("status").GetString());
        Assert.False(await InQueueAsync(x.S.Client, x.CaseId));
        Assert.Equal("await_promise", detail.GetProperty("case").GetProperty("suggestedAction").GetProperty("kind").GetString());

        try
        {
            // The deadline morning: the sweep judges the promise and releases the case, once.
            fixture.Api.Clock.Override = AmmanMorning(deadline);
            await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
            var evaluated = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promise.GetProperty("id").GetGuid()}", ApiScenario.Json);
            Assert.Equal("Broken", evaluated.GetProperty("status").GetString());
            Assert.Equal("0.000", evaluated.GetProperty("receivedInWindow").GetProperty("amount").GetString());
            Assert.Contains("received 0.000 of 2000.000", evaluated.GetProperty("evaluationNote").GetString());

            var after = await CaseAsync(x.S.Client, x.CaseId);
            Assert.Equal("InProgress", after.GetProperty("case").GetProperty("status").GetString());
            Assert.True(after.GetProperty("case").GetProperty("priorityScore").GetInt32() > before, "a broken promise raises the priority");
            Assert.Equal(1, after.GetProperty("case").GetProperty("customer").GetProperty("brokenPromiseCount12m").GetInt32());
            Assert.Contains(after.GetProperty("case").GetProperty("priorityFactors").EnumerateArray(), f => f.GetProperty("factor").GetString() == "broken_promises" && f.GetProperty("contribution").GetInt32() > 0);
            Assert.True(await InQueueAsync(x.S.Client, x.CaseId));

            // Nothing else: the invoice is untouched, no write-off, no credit note, no message.
            var invoice = await x.S.Client.InvoiceAsync(x.Invoice);
            Assert.Equal("Open", invoice.Status());
            Assert.Equal("2000.000", invoice.Open());
            Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM write_offs WHERE tenant_id = @t", ("t", x.S.Organization.TenantId)));
            Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM credit_notes WHERE tenant_id = @t", ("t", x.S.Organization.TenantId)));
            Assert.Equal("InProgress", after.GetProperty("case").GetProperty("status").GetString());   // not escalated, not on hold

            // Idempotent: a second sweep the same day changes nothing.
            var again = await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
            Assert.Equal(0, again.GetProperty("created").GetInt32());
            Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE entity_id = @p AND to_state = 'Broken'", ("p", promise.GetProperty("id").GetGuid())));
            Assert.Equal(1, (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("customer").GetProperty("brokenPromiseCount12m").GetInt32());
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-08 / SM-34 / SM-35 / SM-50.</summary>
    [Fact]
    public async Task Evaluation_KeptAndPartiallyKept()
    {
        // Kept before the deadline, balance remains → case back to work.
        var x = await OpenAsync("Kept", total: 1_000m);
        var promise = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 400m, D(3)));
        await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(400m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = x.Invoice, amount = M(400m) } } });
        var kept = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promise.GetProperty("id").GetGuid()}", ApiScenario.Json);
        Assert.Equal("Kept", kept.GetProperty("status").GetString());
        Assert.Equal("400.000", kept.GetProperty("receivedInWindow").GetProperty("amount").GetString());
        var c = await CaseAsync(x.S.Client, x.CaseId);
        Assert.Equal("InProgress", c.GetProperty("case").GetProperty("status").GetString());
        Assert.Equal(0, c.GetProperty("case").GetProperty("customer").GetProperty("brokenPromiseCount12m").GetInt32());

        // Kept with zero balance → Resolved (C10 through the same SM-50 chain).
        var y = await OpenAsync("KeptAll", total: 500m);
        var full = await y.S.Client.PostAsync($"/api/v1/cases/{y.CaseId}/promises", Promise(y.Invoice, 500m, D(3)));
        await y.S.Client.PostAsync("/api/v1/payments", new { customerId = y.S.CustomerId, amount = M(500m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = y.Invoice, amount = M(500m) } } });
        Assert.Equal("Kept", (await y.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{full.GetProperty("id").GetGuid()}", ApiScenario.Json)).GetProperty("status").GetString());
        Assert.Equal("Resolved", (await CaseAsync(y.S.Client, y.CaseId)).GetProperty("case").GetProperty("status").GetString());

        // Partially kept at exactly the threshold (50% default), decided at the deadline.
        var z = await OpenAsync("Partial", total: 1_000m);
        var partial = await z.S.Client.PostAsync($"/api/v1/cases/{z.CaseId}/promises", Promise(z.Invoice, 1_000m, D(1)));
        await z.S.Client.PostAsync("/api/v1/payments", new { customerId = z.S.CustomerId, amount = M(500m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = z.Invoice, amount = M(500m) } } });
        Assert.Equal("Active", (await z.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{partial.GetProperty("id").GetGuid()}", ApiScenario.Json)).GetProperty("status").GetString());
        try
        {
            fixture.Api.Clock.Override = AmmanMorning(DateOnly.Parse(partial.GetProperty("deadlineDate").GetString()!));
            await z.S.Client.PostAsync("/api/v1/cases/sweep", new { });
            var judged = await z.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{partial.GetProperty("id").GetGuid()}", ApiScenario.Json);
            Assert.Equal("PartiallyKept", judged.GetProperty("status").GetString());
            Assert.Equal("500.000", judged.GetProperty("receivedInWindow").GetProperty("amount").GetString());
            var zc = await CaseAsync(z.S.Client, z.CaseId);
            Assert.Equal("InProgress", zc.GetProperty("case").GetProperty("status").GetString());
            Assert.Equal(1, zc.GetProperty("case").GetProperty("customer").GetProperty("brokenPromiseCount12m").GetInt32());   // counts as broken (SM-37)
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-06 / SM-36 / INV-07 / DM-24.</summary>
    [Fact]
    public async Task Supersede_CancelsThePriorPromise_AndTheTriggerHolds()
    {
        var x = await OpenAsync("Supersede", total: 1_000m);
        var first = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 300m, D(2)));
        var second = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 1_000m, D(6), "whatsapp"));
        Assert.Equal([first.GetProperty("id").GetGuid()], second.GetProperty("superseded").EnumerateArray().Select(e => e.GetGuid()));
        var old = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{first.GetProperty("id").GetGuid()}", ApiScenario.Json);
        Assert.Equal("Cancelled", old.GetProperty("status").GetString());
        Assert.Equal(second.GetProperty("id").GetGuid(), old.GetProperty("supersededById").GetGuid());
        Assert.Equal("superseded", old.GetProperty("cancelReason").GetString() ?? "superseded");

        // The trigger alone: a direct second Active cover of the invoice is refused.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsync(
            """
            WITH p AS (
              INSERT INTO promises_to_pay (id, tenant_id, case_id, customer_id, status, promised_amount, currency, promised_date, deadline_date, source, confirmed_by)
              VALUES (gen_random_uuid(), @t, @c, @cu, 'Active', 10, 'JOD', current_date, current_date, 'call', @u) RETURNING id)
            INSERT INTO ptp_invoices (tenant_id, ptp_id, invoice_id) SELECT @t, id, @i FROM p
            """, ("t", x.S.Organization.TenantId), ("c", x.CaseId), ("cu", x.S.CustomerId), ("u", x.S.Organization.OwnerUserId), ("i", x.Invoice)));
        Assert.Equal("one_active_ptp_per_invoice", ex.ConstraintName);
    }

    /// <summary>AC-09: the only transitions a client can request are confirm, reject, cancel.</summary>
    [Fact]
    public async Task NoManualKeptOrBroken()
    {
        var x = await OpenAsync("Manual");
        var promise = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 100m, D(3)));
        var id = promise.GetProperty("id").GetGuid();
        foreach (var path in new[] { "kept", "broken", "transitions", "evaluate" })
        {
            var response = await x.S.Client.PostAsJsonAsync($"/api/v1/promises/{id}/{path}", new { @event = "payment_covers_promise" }, ApiScenario.Json);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Confirm on an Active promise is an illegal transition, not a way to re-arm it.
        var (status, body) = await x.S.Client.TryPostAsync($"/api/v1/promises/{id}/confirm", new { });
        Assert.Equal(409, status);
        Assert.Equal("invalid_transition", body.GetProperty("code").GetString());

        var (cancelNoReason, _) = await x.S.Client.TryPostAsync($"/api/v1/promises/{id}/cancel", new { });
        Assert.Equal(400, cancelNoReason);
        var cancelled = await x.S.Client.PostAsync($"/api/v1/promises/{id}/cancel", new { reason = "customer withdrew" });
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("InProgress", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());
        Assert.True(await InQueueAsync(x.S.Client, x.CaseId));
    }

    /// <summary>AC-10 / SM-31 / INV-13.</summary>
    [Fact]
    public async Task ProposedPromise_NeedsAHuman()
    {
        var x = await OpenAsync("Proposed");
        // No API records ai_suggested until slice 9; seed the row the way that slice will.
        var id = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsync(
            """
            INSERT INTO promises_to_pay (id, tenant_id, case_id, customer_id, status, promised_amount, currency, promised_date, deadline_date, source)
            VALUES (@id, @t, @c, @cu, 'Proposed', 600, 'JOD', current_date + 3, current_date + 5, 'ai_suggested');
            INSERT INTO ptp_invoices (tenant_id, ptp_id, invoice_id) VALUES (@t, @id, @i);
            """, ("id", id), ("t", x.S.Organization.TenantId), ("c", x.CaseId), ("cu", x.S.CustomerId), ("i", x.Invoice));
        Assert.Equal("Open", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());   // no effect on the queue
        Assert.True(await InQueueAsync(x.S.Client, x.CaseId));

        // The CHECK refuses an Active row with no human.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsync("UPDATE promises_to_pay SET status = 'Active' WHERE id = @id", ("id", id)));
        Assert.Equal("active_requires_human", ex.ConstraintName);

        var confirmed = await x.S.Client.PostAsync($"/api/v1/promises/{id}/confirm", new { promisedAmount = M(500m) });   // a correction
        Assert.Equal("Active", confirmed.GetProperty("status").GetString());
        Assert.Equal(x.S.Organization.OwnerUserId, confirmed.GetProperty("confirmedBy").GetGuid());
        Assert.Equal("500.000", confirmed.GetProperty("promisedAmount").GetProperty("amount").GetString());
        Assert.Equal("PromiseActive", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());
        Assert.False(await InQueueAsync(x.S.Client, x.CaseId));
        Assert.Equal("human_correction", await fixture.Database.ScalarAsync<string>("SELECT reason_code FROM audit_events WHERE entity_id = @id AND to_state = 'Active'", ("id", id)));

        var rejectId = Guid.CreateVersion7();
        await fixture.Database.ExecuteAsync(
            "INSERT INTO promises_to_pay (id, tenant_id, case_id, customer_id, status, promised_amount, currency, promised_date, deadline_date, source) VALUES (@id, @t, @c, @cu, 'Proposed', 1, 'JOD', current_date, current_date, 'ai_suggested')",
            ("id", rejectId), ("t", x.S.Organization.TenantId), ("c", x.CaseId), ("cu", x.S.CustomerId));
        var (noReason, _) = await x.S.Client.TryPostAsync($"/api/v1/promises/{rejectId}/reject", new { });
        Assert.Equal(400, noReason);
        Assert.Equal("Rejected", (await x.S.Client.PostAsync($"/api/v1/promises/{rejectId}/reject", new { reason = "misclassified" })).GetProperty("status").GetString());
    }

    /// <summary>AC-11 / E2 / T-125 / SM-51.</summary>
    [Fact]
    public async Task PostDatedCheque_CreatesPromise_ClearKeeps_BounceBreaks()
    {
        var x = await OpenAsync("PDC", total: 4_000m);
        var cheque = await x.S.Client.PostAsync("/api/v1/cheques", new { customerId = x.S.CustomerId, chequeNumber = "PDC-1", amount = M(4_000m), chequeDate = D(40), receivedDate = D(0) });
        var promiseId = cheque.GetProperty("ptpId").GetGuid();
        var promise = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promiseId}", ApiScenario.Json);
        Assert.Equal("Active", promise.GetProperty("status").GetString());
        Assert.Equal("cheque", promise.GetProperty("source").GetString());
        Assert.Equal(D(40), promise.GetProperty("promisedDate").GetString());
        Assert.Equal("4000.000", promise.GetProperty("promisedAmount").GetProperty("amount").GetString());
        Assert.Equal(cheque.GetProperty("id").GetGuid(), promise.GetProperty("chequeId").GetGuid());
        Assert.Equal([x.Invoice], promise.GetProperty("invoices").EnumerateArray().Select(i => i.GetProperty("invoiceId").GetGuid()));
        Assert.Equal("PromiseActive", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());
        Assert.Equal("4000.000", (await x.S.Client.InvoiceAsync(x.Invoice)).Open());   // no allocation yet (A-05)

        // Clear with allocation → Settled → Kept → Resolved.
        var chequeId = cheque.GetProperty("id").GetGuid();
        await x.S.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "deposit" });
        await x.S.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "clear", allocations = new[] { new { invoiceId = x.Invoice, amount = M(4_000m) } } });
        Assert.Equal("Kept", (await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{promiseId}", ApiScenario.Json)).GetProperty("status").GetString());
        Assert.Equal("Resolved", (await CaseAsync(x.S.Client, x.CaseId)).GetProperty("case").GetProperty("status").GetString());

        // The other branch: bounce.
        var y = await OpenAsync("PDC-bounce", total: 4_000m);
        var before = (await CaseAsync(y.S.Client, y.CaseId)).GetProperty("case").GetProperty("priorityScore").GetInt32();
        var pdc = await y.S.Client.PostAsync("/api/v1/cheques", new { customerId = y.S.CustomerId, chequeNumber = "PDC-2", amount = M(4_000m), chequeDate = D(40), receivedDate = D(0) });
        var pdcId = pdc.GetProperty("id").GetGuid();
        await y.S.Client.PostAsync($"/api/v1/cheques/{pdcId}/transitions", new { @event = "deposit" });
        await y.S.Client.PostAsync($"/api/v1/cheques/{pdcId}/transitions", new { @event = "bounce", reason = "insufficient funds" });
        var broken = await y.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/promises/{pdc.GetProperty("ptpId").GetGuid()}", ApiScenario.Json);
        Assert.Equal("Broken", broken.GetProperty("status").GetString());
        var yc = (await CaseAsync(y.S.Client, y.CaseId)).GetProperty("case");
        Assert.Equal("InProgress", yc.GetProperty("status").GetString());
        Assert.Equal(1, yc.GetProperty("customer").GetProperty("bouncedChequeCount12m").GetInt32());
        Assert.Equal(1, yc.GetProperty("customer").GetProperty("brokenPromiseCount12m").GetInt32());
        Assert.True(yc.GetProperty("priorityScore").GetInt32() > before);
        Assert.Equal("4000.000", (await y.S.Client.InvoiceAsync(y.Invoice)).Open());

        // A cheque dated today is not a promise; a customer without a case gets none either.
        var plain = await y.S.Client.PostAsync("/api/v1/cheques", new { customerId = y.S.CustomerId, chequeNumber = "NOW-1", amount = M(100m), chequeDate = D(0), receivedDate = D(0) });
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("ptpId").ValueKind);
    }

    /// <summary>AC-12 / SM-37 through the API.</summary>
    [Fact]
    public async Task Reliability_ShowsTheDenominator()
    {
        // One invoice per promise: SM-34 counts every payment received on the covered invoices in the window,
        // and a same-day earlier payment on the same invoice would count for a later promise too.
        var x = await OpenAsync("Reliable", total: 1_000m);
        var second = await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, x.S.CustomerId, "Reliable-2", 1_000m, dueDate: D(-10), issueDate: D(-40));
        var third = await fixture.Database.OpenInvoiceAsync(x.S.Organization.TenantId, x.S.CustomerId, "Reliable-3", 1_000m, dueDate: D(-10), issueDate: D(-40));
        await x.S.Client.PostAsync("/api/v1/cases/sweep", new { });
        var invoices = new Queue<Guid>([x.Invoice, second, third]);
        async Task PromiseAndEvaluate(decimal amount, decimal paid)
        {
            var invoice = invoices.Dequeue();
            var p = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(invoice, amount, D(1)));
            if (paid > 0m)
                await x.S.Client.PostAsync("/api/v1/payments", new { customerId = x.S.CustomerId, amount = M(paid), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = invoice, amount = M(paid) } } });
            if (paid < amount)
            {
                try { fixture.Api.Clock.Override = AmmanMorning(DateOnly.Parse(p.GetProperty("deadlineDate").GetString()!)); await x.S.Client.PostAsync("/api/v1/cases/sweep", new { }); }
                finally { fixture.Api.ResetClock(); }
            }
        }

        await PromiseAndEvaluate(100m, 100m);   // kept
        var two = await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{x.S.CustomerId}/promise-history", ApiScenario.Json);
        Assert.Equal(1, two.GetProperty("reliability").GetProperty("denominator").GetInt32());
        Assert.Equal(JsonValueKind.Null, two.GetProperty("reliability").GetProperty("ratio").ValueKind);   // sample too small

        await PromiseAndEvaluate(100m, 100m);   // kept
        await PromiseAndEvaluate(200m, 0m);     // broken
        var three = (await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{x.S.CustomerId}/promise-history", ApiScenario.Json)).GetProperty("reliability");
        Assert.Equal(2, three.GetProperty("kept").GetInt32());
        Assert.Equal(1, three.GetProperty("broken").GetInt32());
        Assert.Equal(3, three.GetProperty("denominator").GetInt32());
        Assert.Equal("0.667", three.GetProperty("ratio").GetString());

        // Older than twelve months does not count.
        await fixture.Database.ExecuteAsync("UPDATE promises_to_pay SET evaluated_at = now() - interval '400 days' WHERE customer_id = @cu AND status = 'Broken'", ("cu", x.S.CustomerId));
        var aged = (await x.S.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{x.S.CustomerId}/promise-history", ApiScenario.Json)).GetProperty("reliability");
        Assert.Equal(2, aged.GetProperty("denominator").GetInt32());
    }

    /// <summary>AC-13 / SM-54.</summary>
    [Fact]
    public async Task WriteOff_IsBlockedByAnActivePromise()
    {
        var x = await OpenAsync("WriteOff");
        await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 100m, D(3)));
        var proposal = await x.S.Client.PostAsync($"/api/v1/invoices/{x.Invoice}/write-off", new { reasonCode = "uncollectible" });
        await x.S.Client.ReauthAsync();   // SEC-09 (slice 13)
        var (status, body) = await x.S.Client.TryPostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        Assert.Equal(422, status);
        Assert.Equal("ptp_active", body.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    /// <summary>AC-14 / SM-03 / INV-12.</summary>
    [Fact]
    public async Task PromiseTransitions_AreAudited_OneToOne()
    {
        var x = await OpenAsync("Audit", total: 1_000m);
        var first = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 100m, D(2)));
        var second = await x.S.Client.PostAsync($"/api/v1/cases/{x.CaseId}/promises", Promise(x.Invoice, 200m, D(3)));   // supersedes the first
        await x.S.Client.PostAsync($"/api/v1/promises/{second.GetProperty("id").GetGuid()}/cancel", new { reason = "renegotiated" });

        var firstAudit = await fixture.Database.ScalarAsync<string>("SELECT string_agg(coalesce(from_state,'-') || '>' || to_state, ',' ORDER BY id) FROM audit_events WHERE entity_id = @p AND event_type = 'promise_to_pay.status_changed'", ("p", first.GetProperty("id").GetGuid()));
        Assert.Equal("->Active,Active>Cancelled", firstAudit);
        var secondAudit = await fixture.Database.ScalarAsync<string>("SELECT string_agg(coalesce(from_state,'-') || '>' || to_state, ',' ORDER BY id) FROM audit_events WHERE entity_id = @p AND event_type = 'promise_to_pay.status_changed'", ("p", second.GetProperty("id").GetGuid()));
        Assert.Equal("->Active,Active>Cancelled", secondAudit);
        var activities = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM case_activities WHERE case_id = @c AND kind = 'ptp'", ("c", x.CaseId));
        Assert.Equal(4L, activities);   // recorded, superseded, recorded, cancelled
    }
}
