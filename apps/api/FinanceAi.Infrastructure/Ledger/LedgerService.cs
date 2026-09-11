using System.Globalization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ledger;

/// <summary>A business-rule violation (doc 05 §0.2: <c>422 business_rule_violated</c>) with the field and the live figure that explains it.</summary>
public sealed class LedgerException(string code, string? field = null, IReadOnlyDictionary<string, string>? meta = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public string? Field { get; } = field;
    public IReadOnlyDictionary<string, string>? Meta { get; } = meta;
}

/// <summary>
/// Every way money moves against an invoice (FIN-20, as amended: allocation, credit application,
/// approved write-off, withholding), and the one function that derives the balance from them.
/// <para>
/// Discipline: each public method is one business action inside the request transaction; each
/// ends by calling <see cref="RecomputeAsync"/> for every invoice it touched, and that is the only
/// code that writes <c>balance_cache</c> or performs I4/I7 (SM-12, SM-50). Nothing here rounds:
/// every input is at scale ≤ 3 or refused, and every derived figure is a sum or a difference.
/// </para>
/// </summary>
public sealed class LedgerService(TenantDbContext db, IAuditWriter audit, TimeProvider time, FinanceAi.Infrastructure.Cases.ICaseHooks cases)
{
    // ---------------------------------------------------------------------------------------
    // The balance
    // ---------------------------------------------------------------------------------------

    /// <summary>The instrument sums behind an invoice's balance. Also what the reconciliation check reads.</summary>
    public sealed record BalanceInputs(decimal AllocatedPayments, decimal AppliedCredits, decimal WrittenOff, decimal Withheld);

    public async Task<BalanceInputs> InputsAsync(Guid invoiceId, CancellationToken ct)
    {
        // Allocations count only while their payment is confirmed (doc 03 §2.1); a reversed payment
        // has already had its allocations reversed, so is_active alone is sufficient — the join is
        // belt and braces against a future path that forgets.
        var allocated = await db.PaymentAllocations
            .Where(a => a.InvoiceId == invoiceId && a.IsActive)
            .Join(db.Payments.Where(p => p.Status == PaymentStatus.Confirmed), a => a.PaymentId, p => p.Id, (a, _) => a.Amount)
            .SumAsync(ct);

        var applied = await db.CreditNoteApplications
            .Where(a => a.InvoiceId == invoiceId && a.IsActive)
            .Join(db.CreditNotes.Where(n => n.Status == CreditNoteStatus.Active), a => a.CreditNoteId, n => n.Id, (a, _) => a.Amount)
            .SumAsync(ct);

        var writtenOff = await db.WriteOffs
            .Where(w => w.InvoiceId == invoiceId && w.Status == WriteOffStatus.Approved)
            .SumAsync(w => w.Amount, ct);

        var withheld = await db.WithholdingDeductions
            .Where(w => w.InvoiceId == invoiceId && w.IsActive)
            .SumAsync(w => w.WithheldAmount, ct);

        return new BalanceInputs(allocated, applied, writtenOff, withheld);
    }

    /// <summary>
    /// FIN-10 / SM-12: derives the balance from the instruments, writes the cache, and performs
    /// I4 (Open → Settled at exactly zero) or I7 (Settled → Open above zero), audited. Idempotent:
    /// running it twice changes nothing the second time.
    /// </summary>
    public async Task<Invoice> RecomputeAsync(Guid invoiceId, Guid? actorUserId, string reasonCode, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstAsync(i => i.Id == invoiceId, ct);
        var inputs = await InputsAsync(invoiceId, ct);

        InvoiceBalance.Recompute(invoice, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff + inputs.Withheld);

        var transition = LedgerRules.TransitionFor(invoice.Status, invoice.BalanceCache);
        if (transition is { } next)
        {
            var from = invoice.Status;
            invoice.Status = next;
            invoice.SettledAt = next == InvoiceStatus.Settled ? time.GetUtcNow() : null;
            await audit.WriteAsync(Transition("invoice", invoice.Id, from.ToString(), next.ToString(), reasonCode, actorUserId), ct);
        }

        invoice.UpdatedAt = time.GetUtcNow();
        invoice.UpdatedBy = actorUserId;
        invoice.RowVersion++;
        await db.SaveChangesAsync(ct);

        // SM-50: the case layer sees every balance change in the same transaction (C10, rescoring).
        await cases.InvoiceChangedAsync(invoice.Id, actorUserId, ct);
        return invoice;
    }

    // ---------------------------------------------------------------------------------------
    // Payments and allocation
    // ---------------------------------------------------------------------------------------

    public sealed record AllocationLine(Guid InvoiceId, decimal Amount);

    public sealed record InvoiceResidual(Guid InvoiceId, decimal OpenBalance, Settlement Settlement, decimal? ProposedRoundingAdjustment);

    public sealed record AllocationResult(Payment Payment, decimal Unallocated, IReadOnlyList<InvoiceResidual> Invoices, IReadOnlyList<PaymentAllocation> Lines);

    public async Task<Payment> RecordPaymentAsync(Payment payment, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payment);
        RequireScale3(payment.Amount, "amount");

        if (!await db.Customers.AnyAsync(c => c.Id == payment.CustomerId, ct))
        {
            throw new LedgerException("customer_not_found", "customerId");
        }

        payment.CreatedBy = actorUserId;
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("payment.recorded", "payment", payment.Id, actorUserId, note: Money(payment.Amount, payment.Currency)), ct);
        return payment;
    }

    /// <summary>FIN-25/26: the FIFO proposal. Read-only; nothing is written.</summary>
    public async Task<IReadOnlyList<AllocationLine>> ProposeAsync(Payment payment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var available = payment.Amount - await AllocatedOfPaymentAsync(payment.Id, ct);

        // Slices 5 and 7 add the "skip disputed / escalated" exclusions here (FIN-25).
        var candidates = await db.Invoices
            .Where(i => i.CustomerId == payment.CustomerId && i.Currency == payment.Currency && i.Status == InvoiceStatus.Open && i.BalanceCache > 0m)
            .Select(i => new { i.Id, i.DueDate, i.InvoiceNumber, i.BalanceCache })
            .ToListAsync(ct);

        return LedgerRules
            .ProposeFifo(available, candidates.Select(c => (c.Id, c.DueDate, c.InvoiceNumber, c.BalanceCache)))
            .Select(l => new AllocationLine(l.InvoiceId, l.Amount))
            .ToList();
    }

    /// <summary>
    /// FIN-21, FIN-22, FIN-24: explicit lines, each validated against the live balance under a row
    /// lock, then the whole set written. The database triggers re-check the payment cap and the
    /// currencies; the invoice cap is the one rule only the lock can hold.
    /// </summary>
    public async Task<AllocationResult> AllocateAsync(Payment payment, IReadOnlyList<AllocationLine> lines, AllocationMethod method, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(lines);

        if (payment.Status != PaymentStatus.Confirmed)
        {
            throw new LedgerException("payment_not_confirmed");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Amount <= 0m)
            {
                throw new LedgerException("amount_not_positive", $"lines[{i}].amount");
            }

            RequireScale3(lines[i].Amount, $"lines[{i}].amount");
        }

        if (lines.Select(l => l.InvoiceId).Distinct().Count() != lines.Count)
        {
            throw new LedgerException("duplicate_invoice_in_lines", "lines");
        }

        // Lock the payment, then the invoices in a fixed order (by id), so two concurrent requests
        // cannot deadlock each other and the second always sees the first's writes.
        await LockAsync("payments", payment.Id, ct);
        var alreadyAllocated = await AllocatedOfPaymentAsync(payment.Id, ct);
        var requested = lines.Sum(l => l.Amount);

        if (alreadyAllocated + requested > payment.Amount)
        {
            throw new LedgerException("exceeds_payment", "lines", new Dictionary<string, string>
            {
                ["unallocated"] = Money(payment.Amount - alreadyAllocated, payment.Currency),
            });
        }

        var settings = await db.TenantSettings.FirstAsync(ct);
        var created = new List<PaymentAllocation>();
        var touched = new List<Guid>();

        foreach (var (index, line) in lines.Select((l, i) => (i, l)).OrderBy(x => x.l.InvoiceId))
        {
            await LockAsync("invoices", line.InvoiceId, ct);
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == line.InvoiceId, ct)
                ?? throw new LedgerException("invoice_not_found", $"lines[{index}].invoiceId");

            if (invoice.CustomerId != payment.CustomerId)
            {
                throw new LedgerException("invoice_belongs_to_another_customer", $"lines[{index}].invoiceId");
            }

            if (invoice.Currency != payment.Currency)
            {
                throw new LedgerException("currency_mismatch", $"lines[{index}].currency", new Dictionary<string, string>
                {
                    ["invoiceCurrency"] = invoice.Currency,
                    ["paymentCurrency"] = payment.Currency,
                });
            }

            // SM-13: money arriving on a written-off invoice is recorded, never dropped — reverse the
            // write-off (I8) so the balance reappears, then allocate against it.
            if (invoice.Status == InvoiceStatus.WrittenOff)
            {
                await ReverseWriteOffsForPaymentAsync(invoice, actorUserId, ct);
            }

            if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Settled))
            {
                throw new LedgerException("invoice_not_open", $"lines[{index}].invoiceId", new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
            }

            // The live balance, under the lock, is the cap (FIN-22). Re-derived rather than read from
            // the cache so a stale cache can never let money through.
            var inputs = await InputsAsync(invoice.Id, ct);
            var open = LedgerRules.OpenBalance(invoice.TotalAmount, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff, inputs.Withheld);

            if (line.Amount > open)
            {
                throw new LedgerException("exceeds_open_balance", $"lines[{index}].amount", new Dictionary<string, string>
                {
                    ["openBalance"] = Money(open, invoice.Currency),
                });
            }

            created.Add(new PaymentAllocation
            {
                TenantId = payment.TenantId,
                PaymentId = payment.Id,
                InvoiceId = invoice.Id,
                Amount = line.Amount,
                Currency = payment.Currency,
                EffectiveDate = payment.EffectiveDate,
                AllocatedBy = actorUserId,
                Method = method,
            });
            touched.Add(invoice.Id);
        }

        db.PaymentAllocations.AddRange(created);
        await db.SaveChangesAsync(ct);

        var residuals = new List<InvoiceResidual>();
        foreach (var invoiceId in touched)
        {
            var invoice = await RecomputeAsync(invoiceId, actorUserId, "allocation", ct);
            await audit.WriteAsync(Event("allocation.recorded", "invoice", invoice.Id, actorUserId,
                note: Money(created.First(c => c.InvoiceId == invoiceId).Amount, invoice.Currency)), ct);

            // FIN-14 / E5: a residual below the threshold is proposed for a rounding adjustment.
            // Proposed. A human creates the credit note; nothing here clears anything.
            var proposal = invoice.BalanceCache > 0m && invoice.BalanceCache < settings.AutoClearResidualBelow
                ? invoice.BalanceCache
                : (decimal?)null;

            residuals.Add(new InvoiceResidual(invoice.Id, invoice.BalanceCache, invoice.Settlement, proposal));
        }

        payment.UpdatedAt = time.GetUtcNow();
        payment.RowVersion++;
        await db.SaveChangesAsync(ct);

        return new AllocationResult(payment, payment.Amount - alreadyAllocated - requested, residuals, created);
    }

    /// <summary>FIN-23: a compensating row. Both rows remain, both inactive; the original is marked, not changed.</summary>
    public async Task<PaymentAllocation> ReverseAllocationAsync(PaymentAllocation allocation, string reason, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!allocation.IsActive)
        {
            throw new LedgerException("allocation_not_active");
        }

        await LockAsync("invoices", allocation.InvoiceId, ct);
        await ReopenIfWrittenOffAsync(allocation.InvoiceId, "allocation_reversed", actorUserId, ct);

        var reversal = new PaymentAllocation
        {
            TenantId = allocation.TenantId,
            PaymentId = allocation.PaymentId,
            InvoiceId = allocation.InvoiceId,
            Amount = allocation.Amount,
            Currency = allocation.Currency,
            EffectiveDate = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
            IsActive = false,
            ReversalOfId = allocation.Id,
            ReversalReason = reason,
            AllocatedBy = actorUserId,
            Method = allocation.Method,
        };

        allocation.IsActive = false;
        allocation.ReversalReason = reason;
        db.PaymentAllocations.Add(reversal);
        await db.SaveChangesAsync(ct);

        await RecomputeAsync(allocation.InvoiceId, actorUserId, "allocation_reversed", ct);
        await audit.WriteAsync(Event("allocation.reversed", "invoice", allocation.InvoiceId, actorUserId, reasonCode: reason, note: Money(allocation.Amount, allocation.Currency)), ct);
        return reversal;
    }

    public async Task ReversePaymentAsync(Payment payment, string reason, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (payment.Status == PaymentStatus.Reversed)
        {
            throw new LedgerException("payment_already_reversed");
        }

        await LockAsync("payments", payment.Id, ct);

        var active = await db.PaymentAllocations.Where(a => a.PaymentId == payment.Id && a.IsActive).ToListAsync(ct);
        foreach (var allocation in active)
        {
            await ReverseAllocationAsync(allocation, reason, actorUserId, ct);
        }

        payment.Status = PaymentStatus.Reversed;
        payment.ReversedAt = time.GetUtcNow();
        payment.ReversalReason = reason;
        payment.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition("payment", payment.Id, nameof(PaymentStatus.Confirmed), nameof(PaymentStatus.Reversed), reason, actorUserId), ct);
    }

    // ---------------------------------------------------------------------------------------
    // Cheques (SM-51)
    // ---------------------------------------------------------------------------------------

    public async Task<Cheque> RecordChequeAsync(Cheque cheque, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cheque);
        RequireScale3(cheque.Amount, "amount");

        if (!await db.Customers.AnyAsync(c => c.Id == cheque.CustomerId, ct))
        {
            throw new LedgerException("customer_not_found", "customerId");
        }

        if (await db.Cheques.AnyAsync(c => c.CustomerId == cheque.CustomerId && c.ChequeNumber == cheque.ChequeNumber, ct))
        {
            throw new LedgerException("duplicate_cheque_number", "chequeNumber");
        }

        cheque.CreatedBy = actorUserId;
        db.Cheques.Add(cheque);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("cheque.recorded", "cheque", cheque.Id, actorUserId, note: Money(cheque.Amount, cheque.Currency)), ct);

        // A-05 / E2: a post-dated cheque is a promise; the case layer decides whether there is one to make.
        cheque.PtpId = await cases.ChequeReceivedAsync(cheque, actorUserId, ct);
        if (cheque.PtpId is not null)
        {
            await db.SaveChangesAsync(ct);
        }

        return cheque;
    }

    public sealed record ChequeTransitionResult(Cheque Cheque, Payment? Payment, AllocationResult? Allocation);

    public async Task<ChequeTransitionResult> TransitionChequeAsync(Cheque cheque, string @event, string? reason, IReadOnlyList<AllocationLine>? lines, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cheque);

        var next = Cheque.Next(cheque.Status, @event)
            ?? throw new LedgerException("invalid_transition", "event", new Dictionary<string, string> { ["from"] = cheque.Status.ToString(), ["event"] = @event });

        if (@event == "bounce" && string.IsNullOrWhiteSpace(reason))
        {
            throw new LedgerException("reason_required", "reason");
        }

        var from = cheque.Status;
        Payment? payment = null;
        AllocationResult? allocation = null;

        switch (next)
        {
            case ChequeStatus.Cleared:
                // The only cheque state that creates money (SM-51). Allocation is a separate,
                // confirmed step (FIN-26) unless the lines come with the event.
                payment = await RecordPaymentAsync(new Payment
                {
                    TenantId = cheque.TenantId,
                    CustomerId = cheque.CustomerId,
                    Amount = cheque.Amount,
                    Currency = cheque.Currency,
                    Method = PaymentMethod.Cheque,
                    ReceivedDate = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
                    EffectiveDate = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
                    Reference = $"Cheque {cheque.ChequeNumber}",
                    ChequeId = cheque.Id,
                }, actorUserId, ct);

                cheque.PaymentId = payment.Id;
                cheque.ClearedDate = payment.ReceivedDate;

                if (lines is { Count: > 0 })
                {
                    allocation = await AllocateAsync(payment, lines, AllocationMethod.Manual, actorUserId, ct);
                }

                break;

            case ChequeStatus.Bounced:
                // A cleared cheque that bounces takes its money back: the payment and every
                // allocation reverse, and settled invoices reopen (I7).
                if (cheque.PaymentId is { } paymentId)
                {
                    var cleared = await db.Payments.FirstAsync(p => p.Id == paymentId, ct);
                    if (cleared.Status == PaymentStatus.Confirmed)
                    {
                        await ReversePaymentAsync(cleared, $"cheque_bounced: {reason}", actorUserId, ct);
                    }
                }

                cheque.BouncedReason = reason;
                await db.Customers.Where(c => c.Id == cheque.CustomerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.BouncedChequeCount12m, c => c.BouncedChequeCount12m + 1), ct);
                break;
        }

        cheque.Status = next;
        cheque.UpdatedAt = time.GetUtcNow();
        cheque.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition("cheque", cheque.Id, from.ToString(), next.ToString(), @event, actorUserId, reason), ct);

        if (next == ChequeStatus.Bounced)
        {
            await cases.ChequeBouncedAsync(cheque, actorUserId, ct);   // E2: the promise breaks at once, the case reopens
        }

        return new ChequeTransitionResult(cheque, payment, allocation);
    }

    // ---------------------------------------------------------------------------------------
    // Withholding (FIN-29): the fourth instrument
    // ---------------------------------------------------------------------------------------

    public async Task<WithholdingDeduction> RecordWithholdingAsync(WithholdingDeduction deduction, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deduction);
        RequireScale3(deduction.WithheldAmount, "withheldAmount");
        RequireScale3(deduction.BaseAmount, "baseAmount");

        await LockAsync("invoices", deduction.InvoiceId, ct);
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == deduction.InvoiceId, ct)
            ?? throw new LedgerException("invoice_not_found", "invoiceId");

        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new LedgerException("invoice_not_open", "invoiceId", new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
        }

        if (deduction.Currency != invoice.Currency)
        {
            throw new LedgerException("currency_mismatch", "currency");
        }

        var inputs = await InputsAsync(invoice.Id, ct);
        var open = LedgerRules.OpenBalance(invoice.TotalAmount, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff, inputs.Withheld);
        if (deduction.WithheldAmount > open)
        {
            throw new LedgerException("exceeds_open_balance", "withheldAmount", new Dictionary<string, string> { ["openBalance"] = Money(open, invoice.Currency) });
        }

        deduction.TenantId = invoice.TenantId;
        deduction.CreatedBy = actorUserId;
        db.WithholdingDeductions.Add(deduction);
        await db.SaveChangesAsync(ct);

        await RecomputeAsync(invoice.Id, actorUserId, "withholding", ct);
        await audit.WriteAsync(Event("withholding.recorded", "invoice", invoice.Id, actorUserId, note: Money(deduction.WithheldAmount, deduction.Currency)), ct);
        return deduction;
    }

    // ---------------------------------------------------------------------------------------
    // Credit notes (FIN-40..43)
    // ---------------------------------------------------------------------------------------

    public async Task<CreditNote> CreateCreditNoteAsync(CreditNote note, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        RequireScale3(note.Amount, "amount");

        if (!CreditNoteReasons.All.Contains(note.ReasonCode, StringComparer.Ordinal))
        {
            throw new LedgerException("invalid_reason_code", "reasonCode");
        }

        if (!await db.Customers.AnyAsync(c => c.Id == note.CustomerId, ct))
        {
            throw new LedgerException("customer_not_found", "customerId");
        }

        note.CreatedBy = actorUserId;
        note.ApprovedBy = actorUserId;
        db.CreditNotes.Add(note);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("credit_note.created", "credit_note", note.Id, actorUserId, reasonCode: note.ReasonCode, note: Money(note.Amount, note.Currency)), ct);
        return note;
    }

    public async Task<IReadOnlyList<InvoiceResidual>> ApplyCreditNoteAsync(CreditNote note, IReadOnlyList<AllocationLine> lines, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(lines);

        if (note.Status != CreditNoteStatus.Active)
        {
            throw new LedgerException("credit_note_not_active");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Amount <= 0m)
            {
                throw new LedgerException("amount_not_positive", $"lines[{i}].amount");
            }

            RequireScale3(lines[i].Amount, $"lines[{i}].amount");
        }

        await LockAsync("credit_notes", note.Id, ct);
        var alreadyApplied = await db.CreditNoteApplications.Where(a => a.CreditNoteId == note.Id && a.IsActive).SumAsync(a => a.Amount, ct);
        var requested = lines.Sum(l => l.Amount);

        if (alreadyApplied + requested > note.Amount)
        {
            throw new LedgerException("exceeds_credit_note", "lines", new Dictionary<string, string> { ["unapplied"] = Money(note.Amount - alreadyApplied, note.Currency) });
        }

        var touched = new List<Guid>();
        foreach (var (index, line) in lines.Select((l, i) => (i, l)).OrderBy(x => x.l.InvoiceId))
        {
            await LockAsync("invoices", line.InvoiceId, ct);
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == line.InvoiceId, ct)
                ?? throw new LedgerException("invoice_not_found", $"lines[{index}].invoiceId");

            if (invoice.CustomerId != note.CustomerId)
            {
                throw new LedgerException("invoice_belongs_to_another_customer", $"lines[{index}].invoiceId");
            }

            if (invoice.Currency != note.Currency)
            {
                throw new LedgerException("currency_mismatch", $"lines[{index}].currency");
            }

            if (invoice.Status != InvoiceStatus.Open)
            {
                throw new LedgerException("invoice_not_open", $"lines[{index}].invoiceId", new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
            }

            var inputs = await InputsAsync(invoice.Id, ct);
            var open = LedgerRules.OpenBalance(invoice.TotalAmount, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff, inputs.Withheld);
            if (line.Amount > open)
            {
                throw new LedgerException("exceeds_open_balance", $"lines[{index}].amount", new Dictionary<string, string> { ["openBalance"] = Money(open, invoice.Currency) });
            }

            db.CreditNoteApplications.Add(new CreditNoteApplication
            {
                TenantId = note.TenantId,
                CreditNoteId = note.Id,
                InvoiceId = invoice.Id,
                Amount = line.Amount,
                Currency = note.Currency,
                EffectiveDate = note.IssueDate,
                AppliedBy = actorUserId,
            });
            touched.Add(invoice.Id);
        }

        await db.SaveChangesAsync(ct);

        var residuals = new List<InvoiceResidual>();
        foreach (var invoiceId in touched)
        {
            var invoice = await RecomputeAsync(invoiceId, actorUserId, "credit_applied", ct);
            await audit.WriteAsync(Event("credit_note.applied", "invoice", invoice.Id, actorUserId, note: Money(lines.First(l => l.InvoiceId == invoiceId).Amount, note.Currency)), ct);
            residuals.Add(new InvoiceResidual(invoice.Id, invoice.BalanceCache, invoice.Settlement, null));
        }

        return residuals;
    }

    /// <summary>FIN-43: voiding reverses every application (compensating rows) and may reopen invoices (I7).</summary>
    public async Task VoidCreditNoteAsync(CreditNote note, string reason, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (note.Status == CreditNoteStatus.Void)
        {
            throw new LedgerException("credit_note_already_void");
        }

        await LockAsync("credit_notes", note.Id, ct);
        var active = await db.CreditNoteApplications.Where(a => a.CreditNoteId == note.Id && a.IsActive).ToListAsync(ct);

        foreach (var application in active)
        {
            await LockAsync("invoices", application.InvoiceId, ct);
            await ReopenIfWrittenOffAsync(application.InvoiceId, "credit_note_voided", actorUserId, ct);
            application.IsActive = false;
            application.ReversalReason = reason;
            db.CreditNoteApplications.Add(new CreditNoteApplication
            {
                TenantId = application.TenantId,
                CreditNoteId = application.CreditNoteId,
                InvoiceId = application.InvoiceId,
                Amount = application.Amount,
                Currency = application.Currency,
                EffectiveDate = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
                IsActive = false,
                ReversalOfId = application.Id,
                ReversalReason = reason,
                AppliedBy = actorUserId,
            });
        }

        note.Status = CreditNoteStatus.Void;
        note.VoidedAt = time.GetUtcNow();
        note.VoidReason = reason;
        note.RowVersion++;
        await db.SaveChangesAsync(ct);

        foreach (var invoiceId in active.Select(a => a.InvoiceId).Distinct())
        {
            await RecomputeAsync(invoiceId, actorUserId, "credit_note_voided", ct);
        }

        await audit.WriteAsync(Transition("credit_note", note.Id, nameof(CreditNoteStatus.Active), nameof(CreditNoteStatus.Void), reason, actorUserId), ct);
    }

    // ---------------------------------------------------------------------------------------
    // Write-off (FIN-31..34, I5, I8)
    // ---------------------------------------------------------------------------------------

    /// <summary>FIN-32: the amount is the system's open balance. There is no parameter for it.</summary>
    public async Task<WriteOff> ProposeWriteOffAsync(Guid invoiceId, string reasonCode, string? note, Guid proposerUserId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);

        await LockAsync("invoices", invoiceId, ct);
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new LedgerException("invoice_not_found");

        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new LedgerException("invoice_not_open", null, new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
        }

        var inputs = await InputsAsync(invoice.Id, ct);
        var open = LedgerRules.OpenBalance(invoice.TotalAmount, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff, inputs.Withheld);
        if (open <= 0m)
        {
            throw new LedgerException("nothing_to_write_off");
        }

        if (await db.WriteOffs.AnyAsync(w => w.InvoiceId == invoiceId && w.Status == WriteOffStatus.Proposed, ct))
        {
            throw new LedgerException("proposal_already_open");
        }

        var writeOff = new WriteOff
        {
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            Amount = open,
            Currency = invoice.Currency,
            ReasonCode = reasonCode.Trim(),
            Note = note,
            ProposedBy = proposerUserId,
            ProposedAt = time.GetUtcNow(),
        };

        db.WriteOffs.Add(writeOff);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition("write_off", writeOff.Id, null, nameof(WriteOffStatus.Proposed), reasonCode, proposerUserId, Money(open, invoice.Currency)), ct);
        return writeOff;
    }

    /// <summary>PRD-11 / FIN-31: a different human, or an explicit and recorded self-approval.</summary>
    public async Task<WriteOff> ApproveWriteOffAsync(WriteOff writeOff, bool selfApproved, Guid approverUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writeOff);

        if (writeOff.Status != WriteOffStatus.Proposed)
        {
            throw new LedgerException("write_off_not_proposed", null, new Dictionary<string, string> { ["status"] = writeOff.Status.ToString() });
        }

        if (writeOff.ProposedBy == approverUserId && !selfApproved)
        {
            throw new LedgerException("four_eyes_required");
        }

        if (writeOff.ProposedBy != approverUserId && selfApproved)
        {
            throw new LedgerException("self_approval_not_applicable");
        }

        await LockAsync("invoices", writeOff.InvoiceId, ct);
        var invoice = await db.Invoices.FirstAsync(i => i.Id == writeOff.InvoiceId, ct);
        if (await cases.HasActivePromiseAsync(invoice.Id, ct))
        {
            throw new LedgerException("ptp_active");   // SM-54: a write-off needs quiet
        }

        // Slices 6 and 7 add SM-54 here: blocked while a dispute is open or a PTP is Active.
        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new LedgerException("invoice_not_open", null, new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
        }

        // The balance may have moved since the proposal; the proposal was for what was owed then.
        var inputs = await InputsAsync(invoice.Id, ct);
        var open = LedgerRules.OpenBalance(invoice.TotalAmount, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff, inputs.Withheld);
        if (writeOff.Amount != open)
        {
            throw new LedgerException("balance_changed_since_proposal", null, new Dictionary<string, string>
            {
                ["proposed"] = Money(writeOff.Amount, writeOff.Currency),
                ["openBalance"] = Money(open, writeOff.Currency),
            });
        }

        writeOff.Status = WriteOffStatus.Approved;
        writeOff.ApprovedBy = approverUserId;
        writeOff.SelfApproved = selfApproved;
        writeOff.ApprovedAt = time.GetUtcNow();
        writeOff.RowVersion++;

        // I5, then the recompute confirms a zero balance under the new status.
        invoice.Status = InvoiceStatus.WrittenOff;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition("write_off", writeOff.Id, nameof(WriteOffStatus.Proposed), nameof(WriteOffStatus.Approved), writeOff.ReasonCode, approverUserId,
            selfApproved ? "self_approved" : null), ct);
        await audit.WriteAsync(Transition("invoice", invoice.Id, nameof(InvoiceStatus.Open), nameof(InvoiceStatus.WrittenOff), "writeoff_approved", approverUserId), ct);

        await RecomputeAsync(invoice.Id, approverUserId, "writeoff_approved", ct);
        return writeOff;
    }

    public async Task<WriteOff> RejectWriteOffAsync(WriteOff writeOff, string? reason, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writeOff);

        if (writeOff.Status != WriteOffStatus.Proposed)
        {
            throw new LedgerException("write_off_not_proposed");
        }

        writeOff.Status = WriteOffStatus.Rejected;
        writeOff.RejectedBy = actorUserId;
        writeOff.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition("write_off", writeOff.Id, nameof(WriteOffStatus.Proposed), nameof(WriteOffStatus.Rejected), reason ?? "rejected", actorUserId), ct);
        return writeOff;
    }

    /// <summary>I8: back to aging with the original due date; audited high-severity.</summary>
    public async Task<WriteOff> ReverseWriteOffAsync(WriteOff writeOff, string reason, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writeOff);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (writeOff.Status != WriteOffStatus.Approved)
        {
            throw new LedgerException("write_off_not_approved");
        }

        await LockAsync("invoices", writeOff.InvoiceId, ct);
        var invoice = await db.Invoices.FirstAsync(i => i.Id == writeOff.InvoiceId, ct);

        writeOff.Status = WriteOffStatus.Reversed;
        writeOff.ReversedBy = actorUserId;
        writeOff.ReversedAt = time.GetUtcNow();
        writeOff.ReversalReason = reason;
        writeOff.RowVersion++;
        invoice.Status = InvoiceStatus.Open;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(Transition("write_off", writeOff.Id, nameof(WriteOffStatus.Approved), nameof(WriteOffStatus.Reversed), reason, actorUserId), ct);
        await audit.WriteAsync(Transition("invoice", invoice.Id, nameof(InvoiceStatus.WrittenOff), nameof(InvoiceStatus.Open), "writeoff_reversed", actorUserId, "high_severity"), ct);
        await RecomputeAsync(invoice.Id, actorUserId, "writeoff_reversed", ct);
        return writeOff;
    }

    private async Task ReverseWriteOffsForPaymentAsync(Invoice invoice, Guid actorUserId, CancellationToken ct)
    {
        await ReopenIfWrittenOffAsync(invoice.Id, "payment_received", actorUserId, ct);
        await db.Entry(invoice).ReloadAsync(ct);
    }

    /// <summary>
    /// A write-off approved what remained owed at that moment. If money later moves on the invoice
    /// — in (SM-13) or back out (an allocation reversed, a credit note voided, a cheque bounced) —
    /// that decision no longer describes the balance, so it is reversed (I8, audited) and the
    /// invoice returns to aging for a human to look at again. The alternative, a WrittenOff invoice
    /// carrying a balance, is exactly the inconsistent state INV-10 forbids. (Review F-2.)
    /// </summary>
    private async Task ReopenIfWrittenOffAsync(Guid invoiceId, string reason, Guid actorUserId, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstAsync(i => i.Id == invoiceId, ct);
        if (invoice.Status != InvoiceStatus.WrittenOff)
        {
            return;
        }

        foreach (var writeOff in await db.WriteOffs.Where(w => w.InvoiceId == invoiceId && w.Status == WriteOffStatus.Approved).ToListAsync(ct))
        {
            await ReverseWriteOffAsync(writeOff, reason, actorUserId, ct);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Void (I6, SM-55): the narrowest door
    // ---------------------------------------------------------------------------------------

    public async Task<Invoice> VoidInvoiceAsync(Guid invoiceId, string reason, Guid actorUserId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await LockAsync("invoices", invoiceId, ct);
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new LedgerException("invoice_not_found");

        if (invoice.Status is not (InvoiceStatus.Open or InvoiceStatus.Imported))
        {
            throw new LedgerException("invoice_not_open", null, new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
        }

        // Any row ever — active or reversed. If money touched it, the instrument is a credit note.
        var history =
            await db.PaymentAllocations.AnyAsync(a => a.InvoiceId == invoiceId, ct) ||
            await db.CreditNoteApplications.AnyAsync(a => a.InvoiceId == invoiceId, ct) ||
            await db.WithholdingDeductions.AnyAsync(w => w.InvoiceId == invoiceId, ct) ||
            await db.WriteOffs.AnyAsync(w => w.InvoiceId == invoiceId, ct);

        if (history)
        {
            throw new LedgerException("has_financial_history");
        }

        var from = invoice.Status;
        invoice.Status = InvoiceStatus.Void;
        invoice.UpdatedAt = time.GetUtcNow();
        invoice.UpdatedBy = actorUserId;
        invoice.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition("invoice", invoice.Id, from.ToString(), nameof(InvoiceStatus.Void), reason, actorUserId), ct);
        await cases.InvoiceChangedAsync(invoice.Id, actorUserId, ct);
        return invoice;
    }

    // ---------------------------------------------------------------------------------------
    // Customer position (FIN-15, FIN-42): three figures per currency, never netted
    // ---------------------------------------------------------------------------------------

    public sealed record CustomerPosition(string Currency, decimal OpenBalance, int OpenInvoiceCount, decimal UnappliedCash, decimal UnappliedCredit);

    public async Task<IReadOnlyList<CustomerPosition>> CustomerPositionAsync(Guid customerId, CancellationToken ct)
    {
        var open = await db.Invoices
            .Where(i => i.CustomerId == customerId && i.Status == InvoiceStatus.Open && i.BalanceCache > 0m)
            .GroupBy(i => i.Currency)
            .Select(g => new { Currency = g.Key, Open = g.Sum(i => i.BalanceCache), Count = g.Count() })
            .ToListAsync(ct);

        var payments = await db.Payments
            .Where(p => p.CustomerId == customerId && p.Status == PaymentStatus.Confirmed)
            .Select(p => new { p.Id, p.Currency, p.Amount })
            .ToListAsync(ct);

        var allocatedByPayment = await db.PaymentAllocations
            .Where(a => a.IsActive && payments.Select(p => p.Id).Contains(a.PaymentId))
            .GroupBy(a => a.PaymentId)
            .Select(g => new { PaymentId = g.Key, Sum = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.PaymentId, x => x.Sum, ct);

        var notes = await db.CreditNotes
            .Where(n => n.CustomerId == customerId && n.Status == CreditNoteStatus.Active)
            .Select(n => new { n.Id, n.Currency, n.Amount })
            .ToListAsync(ct);

        var appliedByNote = await db.CreditNoteApplications
            .Where(a => a.IsActive && notes.Select(n => n.Id).Contains(a.CreditNoteId))
            .GroupBy(a => a.CreditNoteId)
            .Select(g => new { NoteId = g.Key, Sum = g.Sum(a => a.Amount) })
            .ToDictionaryAsync(x => x.NoteId, x => x.Sum, ct);

        // Per currency, always. There is no line here that adds two currencies (FIN-04).
        var currencies = open.Select(o => o.Currency).Concat(payments.Select(p => p.Currency)).Concat(notes.Select(n => n.Currency)).Distinct().Order(StringComparer.Ordinal);

        return currencies.Select(currency => new CustomerPosition(
            currency,
            open.Where(o => o.Currency == currency).Sum(o => o.Open),
            open.Where(o => o.Currency == currency).Sum(o => o.Count),
            payments.Where(p => p.Currency == currency).Sum(p => p.Amount - allocatedByPayment.GetValueOrDefault(p.Id)),
            notes.Where(n => n.Currency == currency).Sum(n => n.Amount - appliedByNote.GetValueOrDefault(n.Id)))).ToList();
    }

    // ---------------------------------------------------------------------------------------

    private Task<decimal> AllocatedOfPaymentAsync(Guid paymentId, CancellationToken ct) =>
        db.PaymentAllocations.Where(a => a.PaymentId == paymentId && a.IsActive).SumAsync(a => a.Amount, ct);

    /// <summary>FIN-22: <c>SELECT … FOR UPDATE</c>, under RLS, inside the request transaction.</summary>
    private Task LockAsync(string table, Guid id, CancellationToken ct) => table switch
    {
        "invoices" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM invoices WHERE id = {id} FOR UPDATE", ct),
        "payments" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM payments WHERE id = {id} FOR UPDATE", ct),
        "credit_notes" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM credit_notes WHERE id = {id} FOR UPDATE", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static void RequireScale3(decimal amount, string field)
    {
        if (amount.Scale > 3)
        {
            throw new LedgerException("too_many_decimals", field);
        }
    }

    private static string Money(decimal amount, string currency) => $"{amount.ToString("F3", CultureInfo.InvariantCulture)} {currency}";

    private AuditEvent Transition(string entityType, Guid entityId, string? from, string to, string reasonCode, Guid? actor, string? note = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = $"{entityType}.status_changed",
        EntityType = entityType,
        EntityId = entityId,
        FromState = from,
        ToState = to,
        ReasonCode = reasonCode,
        Note = note,
    };

    private AuditEvent Event(string eventType, string entityType, Guid entityId, Guid? actor, string? reasonCode = null, string? note = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = eventType,
        EntityType = entityType,
        EntityId = entityId,
        ReasonCode = reasonCode,
        Note = note,
    };
}
