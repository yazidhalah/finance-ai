using System.Net.Http.Json;
using System.Text.Json;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// Doc 03 §9, verbatim, as T-20 requires: "the six worked examples are implemented as fixture tests
/// and must pass before the slice is accepted". E1's 5% is the placeholder rate doc 00 marks
/// [Q-01]; the rate is data the code never computes, so any answer to Q-01 changes only these
/// numbers, not the code.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WorkedExampleTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task E1_WithholdingTax()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E1", 10_000.000m);

        // The customer withholds 5% and pays 9,500.000.
        await s.Client.PostAsync("/api/v1/payments", new
        {
            customerId = s.CustomerId,
            amount = M(9_500.000m),
            method = "BankTransfer",
            receivedDate = "2026-09-10",
            allocations = new[] { new { invoiceId = invoice, amount = M(9_500.000m) } },
        });

        var afterPayment = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("500.000", afterPayment.Open());
        Assert.Equal("PartiallyPaid", afterPayment.Settlement());

        // Forbidden: leaving 500.000 open and dunning it. Correct: a withholding deduction.
        await s.Client.PostAsync($"/api/v1/invoices/{invoice}/withholding", new
        {
            baseAmount = M(10_000.000m),
            ratePct = "5",
            withheldAmount = M(500.000m),
            certificateReference = (string?)null,
        });

        var settled = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("0.000", settled.Open());
        Assert.Equal("Settled", settled.Status());
        Assert.Equal("Paid", settled.Settlement());

        // The certificate is pending and chaseable (FIN-29/30).
        var wht = settled.GetProperty("withholding")[0];
        Assert.False(wht.GetProperty("certificateReceived").GetBoolean());
        Assert.Equal("5", wht.GetProperty("ratePct").GetString());

        // The history explains the balance: 10,000 − 9,500 (allocation) − 500 (withholding) = 0.
        var kinds = settled.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["allocation", "withholding"], kinds);
    }

    [Fact]
    public async Task E2_PostDatedCheque_Clears()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E2", 4_000.000m, dueDate: "2026-09-01");

        // 2026-09-05: a cheque dated 2026-10-15 is handed over. A promise, not a payment.
        var cheque = await s.Client.PostAsync("/api/v1/cheques", new
        {
            customerId = s.CustomerId,
            chequeNumber = "CHQ-1001",
            bankName = "Bank of Jordan",
            amount = M(4_000.000m),
            chequeDate = "2026-10-15",
            receivedDate = "2026-09-05",
        });
        Assert.True(cheque.GetProperty("isPostDated").GetBoolean());
        Assert.Equal("Received", cheque.GetProperty("status").GetString());

        var unchanged = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("4000.000", unchanged.Open());   // balance unchanged, still overdue in aging
        Assert.Equal("Unpaid", unchanged.Settlement());

        var chequeId = cheque.GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "deposit" });

        // 2026-10-16: cleared → payment → allocation → Settled.
        var cleared = await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new
        {
            @event = "clear",
            allocations = new[] { new { invoiceId = invoice, amount = M(4_000.000m) } },
        });
        Assert.Equal("Cleared", cleared.GetProperty("cheque").GetProperty("status").GetString());
        Assert.Equal("Cheque", cleared.GetProperty("payment").GetProperty("method").GetString());

        var settled = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("0.000", settled.Open());
        Assert.Equal("Settled", settled.Status());

        // The promise half of E2 (an Active PTP dated 2026-10-15, then Kept) is slice 6.
    }

    [Fact]
    public async Task E2_PostDatedCheque_Bounces()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E2b", 4_000.000m);
        var chequeId = (await s.Client.PostAsync("/api/v1/cheques", new
        {
            customerId = s.CustomerId,
            chequeNumber = "CHQ-1002",
            amount = M(4_000.000m),
            chequeDate = "2026-10-15",
            receivedDate = "2026-09-05",
        })).GetProperty("id").GetGuid();

        await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "deposit" });

        // A bounce needs a reason (doc 06 §6.5).
        var (status, _) = await s.Client.TryPostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "bounce" });
        Assert.Equal(422, status);

        var bounced = await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "bounce", reason = "insufficient_funds" });
        Assert.Equal("Bounced", bounced.GetProperty("cheque").GetProperty("status").GetString());

        // No allocation ever happened; the invoice is untouched; the customer carries the mark.
        Assert.Equal("4000.000", (await s.Client.InvoiceAsync(invoice)).Open());
        var bouncedCount = await fixture.Database.ScalarAsync<int>("SELECT bounced_cheque_count_12m FROM customers WHERE id = @c", ("c", s.CustomerId));
        Assert.Equal(1, bouncedCount);
    }

    [Fact]
    public async Task E2_ClearedCheque_ThenBounces_Reopens()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E2c", 4_000.000m);
        var chequeId = (await s.Client.PostAsync("/api/v1/cheques", new
        {
            customerId = s.CustomerId,
            chequeNumber = "CHQ-1003",
            amount = M(4_000.000m),
            chequeDate = "2026-09-10",
            receivedDate = "2026-09-05",
        })).GetProperty("id").GetGuid();

        await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "deposit" });
        await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "clear", allocations = new[] { new { invoiceId = invoice, amount = M(4_000.000m) } } });
        Assert.Equal("Settled", (await s.Client.InvoiceAsync(invoice)).Status());

        // The bank reverses a cleared cheque: the money comes back out, the invoice reopens (I7).
        await s.Client.PostAsync($"/api/v1/cheques/{chequeId}/transitions", new { @event = "bounce", reason = "stopped_by_drawer" });

        var reopened = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("4000.000", reopened.Open());
        Assert.Equal("Open", reopened.Status());

        // FIN-23: the allocation and its reversal are both on record.
        var history = reopened.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["allocation", "allocation_reversal"], history);

        var reopenAudited = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE entity_id = @i AND event_type = 'invoice.status_changed' AND from_state = 'Settled' AND to_state = 'Open'", ("i", invoice));
        Assert.Equal(1, reopenAudited);
    }

    [Fact]
    public async Task E3_PartialPaymentThenCreditNote()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E3", 6_000.000m);

        await s.Client.PostAsync("/api/v1/payments", new
        {
            customerId = s.CustomerId,
            amount = M(4_000.000m),
            method = "CliQ",
            receivedDate = "2026-09-10",
            allocations = new[] { new { invoiceId = invoice, amount = M(4_000.000m) } },
        });

        var partial = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("2000.000", partial.Open());
        Assert.Equal("PartiallyPaid", partial.Settlement());
        Assert.Equal("Open", partial.Status());

        // The dispute half (case Disputed, dunning blocked, "of which disputed") is slice 7. Its
        // resolution is a credit note, which is here.
        var note = await s.Client.PostAsync("/api/v1/credit-notes", new
        {
            customerId = s.CustomerId,
            amount = M(2_000.000m),
            issueDate = "2026-09-20",
            reasonCode = "dispute_resolution",
            applications = new[] { new { invoiceId = invoice, amount = M(2_000.000m) } },
        });
        Assert.Equal("0.000", note.GetProperty("unapplied").GetProperty("amount").GetString());

        var settled = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("0.000", settled.Open());
        Assert.Equal("Settled", settled.Status());
    }

    [Fact]
    public async Task E4_Overpayment_LeavesUnappliedCash()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E4", 1_000.000m);

        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(1_500.000m), method = "BankTransfer", receivedDate = "2026-09-10" });
        var paymentId = payment.GetProperty("id").GetGuid();

        // Forbidden: a negative balance. Allocating 1,500 to a 1,000 invoice is refused, with the live figure.
        var (status, body) = await s.Client.TryPostAsync($"/api/v1/payments/{paymentId}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(1_500.000m) } } });
        Assert.Equal(422, status);
        var error = body.GetProperty("errors")[0];
        Assert.Equal("exceeds_open_balance", error.GetProperty("code").GetString());
        Assert.Equal("1000.000 JOD", error.GetProperty("meta").GetProperty("openBalance").GetString());

        // Correct: allocate 1,000; 500 stays as unapplied cash, visible on the customer.
        var result = await s.Client.PostAsync($"/api/v1/payments/{paymentId}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(1_000.000m) } } });
        Assert.Equal("500.000", result.GetProperty("unallocated").GetProperty("amount").GetString());
        Assert.Equal("Settled", (await s.Client.InvoiceAsync(invoice)).Status());

        var customer = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{s.CustomerId}", ApiScenario.Json);
        var jod = customer.GetProperty("balances")[0];
        Assert.Equal("0.000", jod.GetProperty("openBalance").GetProperty("amount").GetString());
        Assert.Equal("500.000", jod.GetProperty("unappliedCash").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task E5_RoundingResidual_IsProposedNotAssumed()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E5", 333.335m);

        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(333.330m), method = "Cash", receivedDate = "2026-09-10" });
        var result = await s.Client.PostAsync($"/api/v1/payments/{payment.GetProperty("id").GetGuid()}/allocations",
            new { lines = new[] { new { invoiceId = invoice, amount = M(333.330m) } } });

        // Forbidden: treating 0.005 as zero. The invoice stays PartiallyPaid with an exact residual…
        var residual = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("0.005", residual.Open());
        Assert.Equal("PartiallyPaid", residual.Settlement());
        Assert.Equal("Open", residual.Status());

        // …and the system proposes — proposes — a rounding adjustment, because 0.005 < 0.100
        // (auto_clear_residual_below). Nothing is cleared by the proposal itself.
        var proposed = result.GetProperty("invoices")[0].GetProperty("proposedRoundingAdjustment");
        Assert.Equal("0.005", proposed.GetProperty("amount").GetString());

        // A human approves it as a credit note; only then does the invoice settle.
        await s.Client.PostAsync("/api/v1/credit-notes", new
        {
            customerId = s.CustomerId,
            amount = M(0.005m),
            issueDate = "2026-09-11",
            reasonCode = "rounding_adjustment",
            applications = new[] { new { invoiceId = invoice, amount = M(0.005m) } },
        });

        var settled = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("0.000", settled.Open());
        Assert.Equal("Settled", settled.Status());
    }

    [Fact]
    public async Task E6_MultiCurrency_NeverSummed()
    {
        var s = await fixture.Api.NewCustomerAsync();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E6-JOD", 5_000.000m, "JOD");
        var usd = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "E6-USD", 2_000.000m, "USD");

        var customer = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{s.CustomerId}", ApiScenario.Json);
        var blocks = customer.GetProperty("balances").EnumerateArray().Select(b => (b.GetProperty("currency").GetString(), b.GetProperty("openBalance").GetProperty("amount").GetString())).ToList();
        Assert.Equal([("JOD", "5000.000"), ("USD", "2000.000")], blocks);

        // Nothing in the customer response is a number that could be a sum across the two.
        Assert.DoesNotContain("7000", customer.GetRawText(), StringComparison.Ordinal);

        // A JOD payment cannot settle the USD invoice (FIN-24).
        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(2_000.000m, "JOD"), method = "BankTransfer", receivedDate = "2026-09-10" });
        var (status, body) = await s.Client.TryPostAsync($"/api/v1/payments/{payment.GetProperty("id").GetGuid()}/allocations", new { lines = new[] { new { invoiceId = usd, amount = M(2_000.000m, "JOD") } } });
        Assert.Equal(422, status);
        Assert.Equal("currency_mismatch", body.GetProperty("errors")[0].GetProperty("code").GetString());

        // The FIFO proposal for that JOD payment only ever names the JOD invoice.
        var proposal = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment.GetProperty("id").GetGuid()}/allocation-proposal", ApiScenario.Json);
        Assert.Equal("E6-JOD", Assert.Single(proposal.GetProperty("lines").EnumerateArray()).GetProperty("invoiceNumber").GetString());
    }
}
