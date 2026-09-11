using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 3b AC-01 … AC-11, AC-14, AC-15.</summary>
[Collection(ApiCollection.Name)]
public sealed class LedgerRuleTests(ApiTestFixture fixture)
{
    /// <summary>
    /// AC-01 / T-21 against the database. A seeded random walk over the real endpoints; after every
    /// step the reconciliation check (INV-09, INV-10) passes and the invariants INV-01..04 hold on
    /// what the API reports. The seed is in the failure message so a run can be replayed.
    /// </summary>
    [Fact]
    public async Task RandomizedLedger_AgainstTheDatabase()
    {
        var seed = Environment.TickCount;
        var random = new Random(seed);
        var s = await fixture.Api.NewCustomerAsync();

        var invoices = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            invoices.Add(await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"R-{i}", random.Next(100, 5000) + (random.Next(0, 1000) / 1000m)));
        }

        var payments = new List<Guid>();
        var notes = new List<Guid>();
        var allocations = new List<Guid>();

        for (var step = 0; step < 120; step++)
        {
            var invoice = invoices[random.Next(invoices.Count)];
            var detail = await s.Client.InvoiceAsync(invoice);
            var open = decimal.Parse(detail.Open(), System.Globalization.CultureInfo.InvariantCulture);

            switch (random.Next(9))
            {
                case 7: // propose and self-approve a write-off of whatever is open
                    {
                        if (open <= 0m || detail.Status() != "Open") break;
                        var (ps, pb) = await s.Client.TryPostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "random_walk" });
                        if (ps != 201) break;
                        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
                        var (aps, _) = await s.Client.TryPostAsync($"/api/v1/write-offs/{pb.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
                        Assert.True(aps is 200 or 422, $"approve returned {aps} (seed {seed})");
                        break;
                    }

                case 8: // reverse a whole payment (the SM-13 / F-2 path when it touched a written-off invoice)
                    {
                        if (payments.Count == 0) break;
                        var paymentId = payments[random.Next(payments.Count)];
                        var (status, _) = await s.Client.TryPostAsync($"/api/v1/payments/{paymentId}/reverse", new { reason = "random_walk" });
                        Assert.True(status is 200 or 422, $"payment reverse returned {status} (seed {seed})");
                        break;
                    }

                case 0: // record a payment, sometimes allocate part of it
                    {
                        var amount = random.Next(1, 3000) + (random.Next(0, 1000) / 1000m);
                        var p = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(amount), method = "BankTransfer", receivedDate = "2026-09-10" });
                        payments.Add(p.GetProperty("id").GetGuid());
                        break;
                    }

                case 1 when payments.Count > 0: // allocate within both caps
                    {
                        var paymentId = payments[random.Next(payments.Count)];
                        var payment = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{paymentId}", ApiScenario.Json);
                        if (payment.GetProperty("status").GetString() != "Confirmed") break;
                        var unallocated = decimal.Parse(payment.GetProperty("unallocated").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        var cap = Math.Min(unallocated, open);
                        if (cap <= 0m || detail.Status() != "Open") break;
                        var amount = Math.Round(cap * (random.Next(1, 101) / 100m), 3);
                        if (amount <= 0m) break;
                        var r = await s.Client.PostAsync($"/api/v1/payments/{paymentId}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(amount) } } });
                        allocations.AddRange(r.GetProperty("payment").GetProperty("allocations").EnumerateArray().Where(a => a.GetProperty("isActive").GetBoolean()).Select(a => a.GetProperty("id").GetGuid()));
                        break;
                    }

                case 2 when allocations.Count > 0: // reverse an allocation
                    {
                        var id = allocations[random.Next(allocations.Count)];
                        var (status, _) = await s.Client.TryPostAsync($"/api/v1/allocations/{id}/reverse", new { reason = "random_walk" });
                        Assert.True(status is 200 or 422, $"reverse returned {status} (seed {seed})");
                        break;
                    }

                case 3: // credit note, applied within caps
                    {
                        if (open <= 0m || detail.Status() != "Open") break;
                        var amount = Math.Round(open * (random.Next(1, 101) / 100m), 3);
                        if (amount <= 0m) break;
                        var n = await s.Client.PostAsync("/api/v1/credit-notes", new
                        {
                            customerId = s.CustomerId,
                            amount = M(amount),
                            issueDate = "2026-09-10",
                            reasonCode = "agreed_discount",
                            applications = new[] { new { invoiceId = invoice, amount = M(amount) } },
                        });
                        notes.Add(n.GetProperty("id").GetGuid());
                        break;
                    }

                case 4 when notes.Count > 0: // void a credit note
                    {
                        var id = notes[random.Next(notes.Count)];
                        var (status, _) = await s.Client.TryPostAsync($"/api/v1/credit-notes/{id}/void", new { reason = "random_walk" });
                        Assert.True(status is 200 or 422, $"void returned {status} (seed {seed})");
                        break;
                    }

                case 5: // withholding within the balance
                    {
                        if (open <= 0m || detail.Status() != "Open") break;
                        var amount = Math.Round(open * (random.Next(1, 51) / 100m), 3);
                        if (amount <= 0m) break;
                        await s.Client.PostAsync($"/api/v1/invoices/{invoice}/withholding", new { baseAmount = M(amount * 20m), ratePct = "5", withheldAmount = M(amount) });
                        break;
                    }

                case 6: // over-allocate on purpose: must be refused, never partially applied
                    {
                        if (payments.Count == 0) break;
                        var paymentId = payments[random.Next(payments.Count)];
                        // Beyond the invoice total, so no state (including a written-off invoice whose
                        // balance SM-13 would restore) can absorb it.
                        var total = decimal.Parse(detail.GetProperty("invoice").GetProperty("totalAmount").GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        var (status, _) = await s.Client.TryPostAsync($"/api/v1/payments/{paymentId}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(total + 1000m) } } });
                        Assert.True(status is 422, $"over-allocation returned {status} (seed {seed})");
                        break;
                    }
            }

            await AssertInvariantsAsync(s.Organization.TenantId, seed, step);
        }
    }

    /// <summary>INV-01, INV-04, INV-09, INV-10 straight from the database, plus INV-02/03 from the caps.</summary>
    private async Task AssertInvariantsAsync(Guid tenantId, int seed, int step)
    {
        // INV-09 / INV-01 / INV-10: cache == derived, within range, status agrees with balance.
        var mismatches = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM invoices i
            WHERE i.tenant_id = @t AND i.status <> 'Void' AND (
              i.balance_cache <> i.total_amount
                - coalesce((SELECT sum(a.amount) FROM payment_allocations a JOIN payments p ON p.id = a.payment_id WHERE a.invoice_id = i.id AND a.is_active AND p.status = 'Confirmed'), 0)
                - coalesce((SELECT sum(c.amount) FROM credit_note_applications c JOIN credit_notes n ON n.id = c.credit_note_id WHERE c.invoice_id = i.id AND c.is_active AND n.status = 'Active'), 0)
                - coalesce((SELECT sum(w.amount) FROM write_offs w WHERE w.invoice_id = i.id AND w.status = 'Approved'), 0)
                - coalesce((SELECT sum(h.withheld_amount) FROM withholding_deductions h WHERE h.invoice_id = i.id AND h.is_active), 0)
              OR i.balance_cache < 0 OR i.balance_cache > i.total_amount
              OR (i.status = 'Settled' AND i.balance_cache > 0)
              OR (i.status = 'Open' AND i.balance_cache = 0))
            """, ("t", tenantId));
        Assert.True(mismatches == 0, $"INV-01/09/10 violated after step {step} (seed {seed})");

        // INV-02: Σ allocations ≤ payment, same currency.
        var overAllocated = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM payments p WHERE p.tenant_id = @t AND p.amount <
              coalesce((SELECT sum(a.amount) FROM payment_allocations a WHERE a.payment_id = p.id AND a.is_active), 0)
            """, ("t", tenantId));
        Assert.True(overAllocated == 0, $"INV-02 violated after step {step} (seed {seed})");

        // INV-03: Σ applications ≤ credit note.
        var overApplied = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM credit_notes n WHERE n.tenant_id = @t AND n.amount <
              coalesce((SELECT sum(a.amount) FROM credit_note_applications a WHERE a.credit_note_id = n.id AND a.is_active), 0)
            """, ("t", tenantId));
        Assert.True(overApplied == 0, $"INV-03 violated after step {step} (seed {seed})");
    }

    /// <summary>AC-02 / T-42 / FIN-22: two full-balance allocations at once; exactly one wins.</summary>
    [Fact]
    public async Task ConcurrentAllocations_ExactlyOneWins()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "RACE", 1_000.000m);
        var a = (await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(1_000.000m), method = "Cash", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();
        var b = (await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(1_000.000m), method = "Cash", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();

        using var clientA = fixture.Api.AuthenticatedClient(s.Organization.OwnerSession);
        using var clientB = fixture.Api.AuthenticatedClient(s.Organization.OwnerSession);

        var results = await Task.WhenAll(
            clientA.TryPostAsync($"/api/v1/payments/{a}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(1_000.000m) } } }),
            clientB.TryPostAsync($"/api/v1/payments/{b}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(1_000.000m) } } }));

        Assert.Equal(1, results.Count(r => r.Status == 200));
        Assert.Equal(1, results.Count(r => r.Status == 422));

        var detail = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("0.000", detail.Open());
        Assert.Equal("Settled", detail.Status());

        var active = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM payment_allocations WHERE invoice_id = @i AND is_active", ("i", invoice));
        Assert.Equal(1, active);
    }

    /// <summary>AC-03 / T-43 / API-08.</summary>
    [Fact]
    public async Task Payment_IsIdempotent()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var body = new { customerId = s.CustomerId, amount = M(250.000m), method = "CliQ", receivedDate = "2026-09-10" };
        var key = Guid.NewGuid().ToString();

        var first = await s.Client.PostAsync("/api/v1/payments", body, key);
        var second = await s.Client.PostAsync("/api/v1/payments", body, key);
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());

        var count = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM payments WHERE customer_id = @c", ("c", s.CustomerId));
        Assert.Equal(1, count);

        // The same key with a different body is a client bug, not a second payment.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments") { Content = JsonContent.Create(new { customerId = s.CustomerId, amount = M(999.000m), method = "CliQ", receivedDate = "2026-09-10" }, options: ApiScenario.Json) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.SendAsync(request)).StatusCode);

        // No key at all is refused up front.
        var bare = await s.Client.PostAsJsonAsync("/api/v1/payments", body, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.PreconditionRequired, bare.StatusCode);
    }

    /// <summary>AC-04 / FIN-23 / I7.</summary>
    [Fact]
    public async Task Reversal_IsACompensatingRow_AndReopens()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "REV", 500.000m);
        var payment = await s.Client.PostAsync("/api/v1/payments", new
        {
            customerId = s.CustomerId,
            amount = M(500.000m),
            method = "Cash",
            receivedDate = "2026-09-10",
            allocations = new[] { new { invoiceId = invoice, amount = M(500.000m) } },
        });
        Assert.Equal("Settled", (await s.Client.InvoiceAsync(invoice)).Status());

        var allocationId = payment.GetProperty("allocations")[0].GetProperty("id").GetGuid();
        var reversal = await s.Client.PostAsync($"/api/v1/allocations/{allocationId}/reverse", new { reason = "posted_to_wrong_invoice" });
        Assert.Equal(allocationId, reversal.GetProperty("reversalOfId").GetGuid());
        Assert.False(reversal.GetProperty("isActive").GetBoolean());

        var rows = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM payment_allocations WHERE invoice_id = @i", ("i", invoice));
        Assert.Equal(2, rows);   // never a delete
        var active = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM payment_allocations WHERE invoice_id = @i AND is_active", ("i", invoice));
        Assert.Equal(0, active);

        var reopened = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("500.000", reopened.Open());
        Assert.Equal("Open", reopened.Status());

        // The cash is back on the customer's account.
        var p = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment.GetProperty("id").GetGuid()}", ApiScenario.Json);
        Assert.Equal("500.000", p.GetProperty("unallocated").GetProperty("amount").GetString());
    }

    /// <summary>AC-05 / FIN-22, FIN-24, T-28.</summary>
    [Fact]
    public async Task Allocation_RejectsBadLines()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "BAD", 100.000m);
        // A 200 payment against a 100 invoice, so the invoice cap is the one that trips first.
        var payment = (await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(200.000m), method = "Cash", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();

        async Task<string> Code(object lines)
        {
            var (status, body) = await s.Client.TryPostAsync($"/api/v1/payments/{payment}/allocations", new { lines });
            Assert.True(status is 422 or 400, $"got {status}");
            return body.GetProperty("errors")[0].GetProperty("code").GetString()!;
        }

        Assert.Equal("exceeds_open_balance", await Code(new[] { new { invoiceId = invoice, amount = M(100.001m) } }));
        Assert.Equal("invalid_amount", await Code(new[] { new { invoiceId = invoice, amount = M(-1m) } }));
        Assert.Equal("amount_not_positive", await Code(new[] { new { invoiceId = invoice, amount = M(0m) } }));
        Assert.Equal("invalid_amount", await Code(new[] { new { invoiceId = invoice, amount = new { amount = "1.2345", currency = "JOD" } } }));
        Assert.Equal("exceeds_payment", await Code(new[] { new { invoiceId = invoice, amount = M(100m) }, new { invoiceId = Guid.CreateVersion7(), amount = M(101m) } }));

        // A line for another customer's invoice is refused, and a foreign tenant's invoice looks missing.
        var otherCustomer = (await (await s.Client.PostAsJsonAsync("/api/v1/customers", new { nameEn = "Other" }, ApiScenario.Json)).Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();
        var theirs = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, otherCustomer, "THEIRS", 10m);
        Assert.Equal("invoice_belongs_to_another_customer", await Code(new[] { new { invoiceId = theirs, amount = M(10m) } }));

        // Nothing was written by any of the refusals.
        Assert.Equal("100.000", (await s.Client.InvoiceAsync(invoice)).Open());
    }

    /// <summary>AC-06 / FIN-25, FIN-26.</summary>
    [Fact]
    public async Task Proposal_IsFifoAndNotApplied()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var newest = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "N-3", 300.000m, dueDate: "2026-09-30", issueDate: "2026-06-01");
        var oldest = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "N-1", 100.000m, dueDate: "2026-07-01", issueDate: "2026-06-01");
        var middle = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "N-2", 200.000m, dueDate: "2026-08-01", issueDate: "2026-06-01");
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "N-USD", 999.000m, "USD", dueDate: "2026-01-01", issueDate: "2025-12-01");

        var payment = (await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(250.000m), method = "Cash", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();
        var proposal = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/payments/{payment}/allocation-proposal", ApiScenario.Json);

        var lines = proposal.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("invoiceId").GetGuid(), l.GetProperty("proposed").GetProperty("amount").GetString())).ToList();
        Assert.Equal([(oldest, "100.000"), (middle, "150.000")], lines);   // oldest first, exact remainder, USD skipped, newest untouched
        Assert.Equal("0.000", proposal.GetProperty("remainingAfterProposal").GetProperty("amount").GetString());

        // Proposal only: nothing moved.
        Assert.Equal("100.000", (await s.Client.InvoiceAsync(oldest)).Open());
        Assert.Equal("300.000", (await s.Client.InvoiceAsync(newest)).Open());
    }

    /// <summary>AC-07 / FIN-31, FIN-32, T-29, I5, I8.</summary>
    [Fact]
    public async Task WriteOff_FourEyes_UsesComputedBalance_AndReverses()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "WO", 800.000m);
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(300.000m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = invoice, amount = M(300.000m) } } });

        // FIN-32: the request carries no amount; the proposal is the computed open balance.
        var proposed = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectible", note = "Company closed" });
        Assert.Equal("500.000", proposed.GetProperty("amount").GetProperty("amount").GetString());
        var writeOffId = proposed.GetProperty("id").GetGuid();

        // The proposer cannot approve their own proposal.
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        var (selfStatus, selfBody) = await s.Client.TryPostAsync($"/api/v1/write-offs/{writeOffId}/approve", new { });
        Assert.Equal(422, selfStatus);
        Assert.Equal("four_eyes_required", selfBody.GetProperty("errors")[0].GetProperty("code").GetString());

        // A second human can. Admin holds writeoff.approve; Accountant does not.
        var admin = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Admin);
        using var adminClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(admin.Email, admin.Password));
        var accountant = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Accountant);
        using var accountantClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));

        await accountantClient.ReauthAsync();   // SEC-09 (slice 13)
        Assert.Equal(403, (await accountantClient.TryPostAsync($"/api/v1/write-offs/{writeOffId}/approve", new { })).Status);

        await adminClient.ReauthAsync();   // SEC-09 (slice 13)
        var approved = await adminClient.PostAsync($"/api/v1/write-offs/{writeOffId}/approve", new { });
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.False(approved.GetProperty("selfApproved").GetBoolean());
        Assert.Equal(admin.UserId, approved.GetProperty("approvedBy").GetGuid());

        var written = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("WrittenOff", written.Status());
        Assert.Equal("0.000", written.Open());

        // I8: reversal returns the balance to aging, audited high-severity.
        await adminClient.PostAsync($"/api/v1/write-offs/{writeOffId}/reverse", new { reason = "customer_resumed_trading" });
        var back = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("Open", back.Status());
        Assert.Equal("500.000", back.Open());
        var highSeverity = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE entity_id = @i AND from_state = 'WrittenOff' AND to_state = 'Open' AND note = 'high_severity'", ("i", invoice));
        Assert.Equal(1, highSeverity);

        // A one-person tenant may self-approve, but only by saying so, and it is recorded.
        var second = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectible" });
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        var selfApproved = await s.Client.PostAsync($"/api/v1/write-offs/{second.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        Assert.True(selfApproved.GetProperty("selfApproved").GetBoolean());
        Assert.Equal("WrittenOff", (await s.Client.InvoiceAsync(invoice)).Status());
    }

    /// <summary>
    /// Review F-2 (found by the property test): money moving back out of a written-off invoice —
    /// an allocation reversed, a credit note voided — reverses the write-off (I8) rather than leaving
    /// a WrittenOff invoice carrying a balance.
    /// </summary>
    [Fact]
    public async Task MoneyMovingOutOfAWrittenOffInvoice_ReversesTheWriteOff()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "WO3", 100.000m);
        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(60m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = invoice, amount = M(60m) } } });
        var proposal = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectible" });
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        await s.Client.PostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        Assert.Equal("WrittenOff", (await s.Client.InvoiceAsync(invoice)).Status());

        // The 60 turns out to have been posted to the wrong invoice.
        await s.Client.PostAsync($"/api/v1/allocations/{payment.GetProperty("allocations")[0].GetProperty("id").GetGuid()}/reverse", new { reason = "wrong_invoice" });

        var detail = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("Open", detail.Status());
        Assert.Equal("100.000", detail.Open());   // the full amount is owed again, for a human to re-decide
        Assert.Equal("Reversed", detail.GetProperty("writeOffs")[0].GetProperty("status").GetString());
    }

    /// <summary>SM-13: a payment on a written-off invoice is recorded, never dropped — I8 then I4.</summary>
    [Fact]
    public async Task Payment_OnWrittenOffInvoice_ReversesTheWriteOff()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "WO2", 100.000m);
        var proposal = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectible" });
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        await s.Client.PostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        Assert.Equal("WrittenOff", (await s.Client.InvoiceAsync(invoice)).Status());

        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(100.000m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = invoice, amount = M(100.000m) } } });

        var detail = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("Settled", detail.Status());
        Assert.Equal("Reversed", detail.GetProperty("writeOffs")[0].GetProperty("status").GetString());
    }

    /// <summary>AC-08 / I6, SM-55.</summary>
    [Fact]
    public async Task Void_RequiresZeroFinancialHistory()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var clean = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "CLEAN", 10m);
        var touched = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "TOUCHED", 10m);

        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(10m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = touched, amount = M(10m) } } });
        await s.Client.PostAsync($"/api/v1/allocations/{payment.GetProperty("allocations")[0].GetProperty("id").GetGuid()}/reverse", new { reason = "mistake" });

        // Even fully reversed history closes the door: the instrument is a credit note.
        var (status, body) = await s.Client.TryPostAsync($"/api/v1/invoices/{touched}/void", new { reason = "duplicate" });
        Assert.Equal(422, status);
        Assert.Equal("has_financial_history", body.GetProperty("errors")[0].GetProperty("code").GetString());

        var voided = await s.Client.PostAsync($"/api/v1/invoices/{clean}/void", new { reason = "duplicate_import" });
        Assert.Equal("Void", voided.GetProperty("status").GetString());
    }

    /// <summary>AC-09 / FIN-21, INV-02, INV-03: the triggers hold with the application bypassed entirely.</summary>
    [Fact]
    public async Task DatabaseTriggers_HoldWithoutTheApplication()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "TRG", 100m);
        var usd = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "TRG-USD", 100m, "USD");
        var payment = (await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(50m), method = "Cash", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();
        var note = (await s.Client.PostAsync("/api/v1/credit-notes", new { customerId = s.CustomerId, amount = M(50m), issueDate = "2026-09-10", reasonCode = "other" })).GetProperty("id").GetGuid();

        await using var connection = fixture.Database.OpenAdmin();

        async Task<string?> Insert(string sql, params (string, object)[] args)
        {
            await using var cmd = new Npgsql.NpgsqlCommand(sql, connection);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            try { await cmd.ExecuteNonQueryAsync(); return null; }
            catch (Npgsql.PostgresException ex) { return ex.MessageText; }
        }

        var over = await Insert("INSERT INTO payment_allocations (id, tenant_id, payment_id, invoice_id, amount, currency, effective_date) VALUES (@id, @t, @p, @i, 60, 'JOD', '2026-09-10')",
            ("id", Guid.CreateVersion7()), ("t", s.Organization.TenantId), ("p", payment), ("i", invoice));
        Assert.Contains("exceeds_payment", over);

        var mismatch = await Insert("INSERT INTO payment_allocations (id, tenant_id, payment_id, invoice_id, amount, currency, effective_date) VALUES (@id, @t, @p, @i, 10, 'JOD', '2026-09-10')",
            ("id", Guid.CreateVersion7()), ("t", s.Organization.TenantId), ("p", payment), ("i", usd));
        Assert.Contains("currency_mismatch", mismatch);

        var overApplied = await Insert("INSERT INTO credit_note_applications (id, tenant_id, credit_note_id, invoice_id, amount, currency, effective_date) VALUES (@id, @t, @n, @i, 60, 'JOD', '2026-09-10')",
            ("id", Guid.CreateVersion7()), ("t", s.Organization.TenantId), ("n", note), ("i", invoice));
        Assert.Contains("exceeds_credit_note", overApplied);

        // Four eyes in the schema: an approval by the proposer without self_approved is a CHECK failure.
        var proposer = s.Organization.OwnerUserId;
        var fourEyes = await Insert("INSERT INTO write_offs (id, tenant_id, invoice_id, amount, currency, reason_code, status, proposed_by, approved_by, self_approved) VALUES (@id, @t, @i, 1, 'JOD', 'x', 'Approved', @u, @u, false)",
            ("id", Guid.CreateVersion7()), ("t", s.Organization.TenantId), ("i", invoice), ("u", proposer));
        Assert.Contains("four_eyes", fourEyes);
    }

    /// <summary>AC-10 / INV-09, T-45.</summary>
    [Fact]
    public async Task BalanceCache_MatchesDerived_AndCorruptionIsDetected()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "CACHE", 100m);
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(40m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = invoice, amount = M(40m) } } });

        Assert.Empty(await ReconcileAsync(s.Organization.TenantId));

        // Someone with database access edits the cache. The check names the invoice.
        await using (var connection = fixture.Database.OpenAdmin())
        {
            await using var corrupt = new Npgsql.NpgsqlCommand("UPDATE invoices SET balance_cache = 59 WHERE id = @i", connection);
            corrupt.Parameters.AddWithValue("i", invoice);
            await corrupt.ExecuteNonQueryAsync();
        }

        var mismatch = Assert.Single(await ReconcileAsync(s.Organization.TenantId));
        Assert.Equal(invoice, mismatch.InvoiceId);
        Assert.Equal(59m, mismatch.Cached);
        Assert.Equal(60m, mismatch.Derived);
    }

    private async Task<IReadOnlyList<FinanceAi.Infrastructure.Ledger.BalanceReconciliation.Mismatch>> ReconcileAsync(Guid tenantId)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<FinanceAi.Infrastructure.Database.TenantDbContext>()
            .UseNpgsql(fixture.Database.AppConnectionString).Options;
        var tenant = new FinanceAi.Infrastructure.Database.TenantContext();
        tenant.Set(tenantId, null);
        await using var db = new FinanceAi.Infrastructure.Database.TenantDbContext(options, tenant);
        await using var scope = await FinanceAi.Infrastructure.Database.DatabaseScope.EnterTenantAsync(db, tenantId, null);
        var result = await new FinanceAi.Infrastructure.Ledger.BalanceReconciliation(db).RunAsync();
        await scope.CompleteAsync();
        return result;
    }

    /// <summary>AC-11 / INV-12: one audit row per transition, for a scripted scenario.</summary>
    [Fact]
    public async Task Transitions_AreAudited_OneToOne()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "AUD", 100m);

        // Script: allocate 100 (I4) → reverse (I7) → cheque received/deposited/cleared+allocate (3 cheque + I4)
        //         → bounce (cheque + I7) → propose/approve write-off (2 write-off + I5) → reverse (write-off + I8).
        var payment = await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(100m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = invoice, amount = M(100m) } } });
        await s.Client.PostAsync($"/api/v1/allocations/{payment.GetProperty("allocations")[0].GetProperty("id").GetGuid()}/reverse", new { reason = "x" });
        var cheque = (await s.Client.PostAsync("/api/v1/cheques", new { customerId = s.CustomerId, chequeNumber = "A1", amount = M(100m), chequeDate = "2026-09-10", receivedDate = "2026-09-10" })).GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/cheques/{cheque}/transitions", new { @event = "deposit" });
        await s.Client.PostAsync($"/api/v1/cheques/{cheque}/transitions", new { @event = "clear", allocations = new[] { new { invoiceId = invoice, amount = M(100m) } } });
        await s.Client.PostAsync($"/api/v1/cheques/{cheque}/transitions", new { @event = "bounce", reason = "nsf" });
        var wo = await s.Client.PostAsync($"/api/v1/invoices/{invoice}/write-off", new { reasonCode = "uncollectible" });
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        await s.Client.PostAsync($"/api/v1/write-offs/{wo.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });
        await s.Client.PostAsync($"/api/v1/write-offs/{wo.GetProperty("id").GetGuid()}/reverse", new { reason = "y" });

        var invoiceTransitions = await fixture.Database.ScalarAsync<string>(
            "SELECT string_agg(from_state || '>' || to_state, ',' ORDER BY id) FROM audit_events WHERE entity_id = @i AND event_type = 'invoice.status_changed'", ("i", invoice));
        Assert.Equal("Open>Settled,Settled>Open,Open>Settled,Settled>Open,Open>WrittenOff,WrittenOff>Open", invoiceTransitions);

        var chequeTransitions = await fixture.Database.ScalarAsync<string>(
            "SELECT string_agg(from_state || '>' || to_state, ',' ORDER BY id) FROM audit_events WHERE entity_id = @c AND event_type = 'cheque.status_changed'", ("c", cheque));
        Assert.Equal("Received>Deposited,Deposited>Cleared,Cleared>Bounced", chequeTransitions);

        var writeOffTransitions = await fixture.Database.ScalarAsync<string>(
            "SELECT string_agg(coalesce(from_state, '-') || '>' || to_state, ',' ORDER BY id) FROM audit_events WHERE entity_id = @w AND event_type = 'write_off.status_changed'", ("w", wo.GetProperty("id").GetGuid()));
        Assert.Equal("->Proposed,Proposed>Approved,Approved>Reversed", writeOffTransitions);

        // The payment reversal that the bounce caused is on record too.
        var paymentReversed = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'payment.status_changed' AND to_state = 'Reversed'", ("t", s.Organization.TenantId));
        Assert.Equal(1, paymentReversed);
    }

    /// <summary>AC-14 / FIN-15, FIN-42 and AC-15 / FIN-12, doc 06 §6.5.</summary>
    [Fact]
    public async Task CustomerAndInvoiceDetail_ShowTheDerivedFigures()
    {
        var s = await fixture.Api.NewCustomerAsync();
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "DET", 1_000m);
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(700m), method = "Cash", receivedDate = "2026-09-10", allocations = new[] { new { invoiceId = invoice, amount = M(400m) } } });
        await s.Client.PostAsync("/api/v1/credit-notes", new { customerId = s.CustomerId, amount = M(250m), issueDate = "2026-09-10", reasonCode = "service_credit", applications = new[] { new { invoiceId = invoice, amount = M(100m) } } });

        // Three figures: 500 open, 300 unapplied cash, 150 unapplied credit. Never netted to 50.
        var customer = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{s.CustomerId}", ApiScenario.Json);
        var jod = customer.GetProperty("balances")[0];
        Assert.Equal("500.000", jod.GetProperty("openBalance").GetProperty("amount").GetString());
        Assert.Equal("300.000", jod.GetProperty("unappliedCash").GetProperty("amount").GetString());
        Assert.Equal("150.000", jod.GetProperty("unappliedCredit").GetProperty("amount").GetString());
        Assert.DoesNotContain("\"50.000\"", customer.GetRawText(), StringComparison.Ordinal);

        var detail = await s.Client.InvoiceAsync(invoice);
        Assert.Equal("PartiallyPaid", detail.Settlement());
        var history = detail.GetProperty("history").EnumerateArray().Select(h => (h.GetProperty("kind").GetString(), h.GetProperty("amount").GetProperty("amount").GetString(), h.GetProperty("effect").GetString())).ToList();
        Assert.Equal([("allocation", "400.000", "reduces"), ("credit_application", "100.000", "reduces")], history);
        Assert.Equal("500.000", detail.Open());
    }
}
