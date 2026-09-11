using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Cases;

/// <summary>What the ledger and the case layer need from promises (SM-50 order, SM-51, SM-54).</summary>
public interface IPromiseHooks
{
    /// <summary>After a balance change: any Active promise covering the invoice may now be Kept.</summary>
    Task InvoiceChangedAsync(Guid invoiceId, Guid? actorUserId, CancellationToken ct);

    /// <summary>A-05: a post-dated cheque is a promise. Returns the promise id, or null when nothing is chased.</summary>
    Task<Guid?> ChequeReceivedAsync(Cheque cheque, Guid? actorUserId, CancellationToken ct);

    /// <summary>E2: a bounced cheque breaks its promise at once.</summary>
    Task ChequeBouncedAsync(Cheque cheque, Guid? actorUserId, CancellationToken ct);

    /// <summary>SM-54: an Active promise on the invoice blocks a write-off approval.</summary>
    Task<bool> HasActivePromiseAsync(Guid invoiceId, CancellationToken ct);

    /// <summary>SM-33: the daily job's half. Returns how many promises were decided.</summary>
    Task<int> EvaluateDueAsync(CancellationToken ct);
}

/// <summary>
/// Doc 02 §3. A promise never moves money; it suppresses chasing and is judged by what the ledger later
/// records. Every transition is one method (SM-02); Kept / PartiallyKept / Broken are decided only by
/// <see cref="PtpRules.Verdict"/> from stored allocations — there is no API path to them.
/// </summary>
public sealed class PromiseService(TenantDbContext db, IAuditWriter audit, TimeProvider time, CaseService cases) : IPromiseHooks
{
    public sealed record RecordResult(PromiseToPay Promise, IReadOnlyList<Guid> InvoiceIds, IReadOnlyList<Guid> Superseded);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------------------------------
    // Recording (SM-30, SM-31, SM-32, SM-36, C4)
    // ---------------------------------------------------------------------------------------

    public async Task<RecordResult> RecordAsync(Guid caseId, IReadOnlyList<Guid> invoiceIds, decimal amount, DateOnly promisedDate, string source, string? notes, Guid actorUserId, Guid? aiSuggestionId, CancellationToken ct)
    {
        if (!PtpSources.All.Contains(source))
        {
            throw new CaseException("invalid_source", "source");
        }

        var context = await cases.ContextAsync(ct);
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct) ?? throw new CaseException("case_not_found");
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM collection_cases WHERE id = {caseId} FOR UPDATE", ct);
        if (CaseMachine.IsTerminal(c.Status))
        {
            throw new CaseException("case_closed");
        }

        if (promisedDate < context.Today)
        {
            throw new CaseException("promised_date_in_past", "promisedDate");
        }

        var distinct = invoiceIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            throw new CaseException("invoices_required", "invoiceIds");
        }

        var inScope = await db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId).ToListAsync(ct);
        var invoices = await db.Invoices.Where(i => distinct.Contains(i.Id)).Select(i => new { i.Id, i.Status, i.Currency, i.BalanceCache }).ToListAsync(ct);
        if (invoices.Count != distinct.Count || invoices.Any(i => !inScope.Contains(i.Id)))
        {
            throw new CaseException("invoice_not_in_scope", "invoiceIds");
        }

        if (invoices.Any(i => i.Status != InvoiceStatus.Open))
        {
            throw new CaseException("invoice_not_open", "invoiceIds");
        }

        if (invoices.Select(i => i.Currency).Distinct().Count() > 1)
        {
            throw new CaseException("currency_mismatch", "invoiceIds");
        }

        var currency = invoices[0].Currency;
        var covered = invoices.Sum(i => i.BalanceCache);
        if (amount <= 0m || amount > covered)
        {
            // SM-32, tolerance zero: a promise above what is owed is a capture error.
            throw new CaseException("exceeds_covered_balance", "promisedAmount", new Dictionary<string, string>
            {
                ["coveredBalance"] = covered.ToString("F3", CultureInfo.InvariantCulture),
                ["currency"] = currency,
            });
        }

        return await CreateAsync(c, distinct, amount, currency, promisedDate, source, notes, actorUserId, aiSuggestionId, null, context, ct);
    }

    private async Task<RecordResult> CreateAsync(CollectionCase c, IReadOnlyList<Guid> invoiceIds, decimal amount, string currency, DateOnly promisedDate, string source, string? notes, Guid? actorUserId, Guid? aiSuggestionId, Guid? chequeId, CaseService.Context context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var holidays = (await db.Holidays.Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var proposed = source == PtpSources.AiSuggested;

        // SM-36: an Active promise on any of these invoices is superseded, never left overlapping.
        var superseded = new List<Guid>();
        if (!proposed)
        {
            var overlapping = await db.PtpInvoices.Where(x => invoiceIds.Contains(x.InvoiceId))
                .Join(db.Promises.Where(p => p.Status == PtpStatus.Active), x => x.PtpId, p => p.Id, (x, p) => p)
                .Distinct().ToListAsync(ct);
            foreach (var old in overlapping)
            {
                await ApplyAsync(old, PtpEvent.SupersededByNewPtp, actorUserId, "superseded", null, ct);
                superseded.Add(old.Id);
            }
        }

        var promise = new PromiseToPay
        {
            TenantId = db.CurrentTenantId,
            CaseId = c.Id,
            CustomerId = c.CustomerId,
            Status = proposed ? PtpStatus.Proposed : PtpStatus.Active,
            PromisedAmount = amount,
            Currency = currency,
            PromisedDate = promisedDate,
            DeadlineDate = PtpRules.Deadline(promisedDate, context.Settings.PtpGraceBusinessDays, holidays),
            Source = source,
            CapturedBy = actorUserId,
            ConfirmedBy = proposed ? null : actorUserId,   // INV-13: a human confirmed by recording it
            AiSuggestionId = aiSuggestionId,
            ChequeId = chequeId,
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Promises.Add(promise);
        await db.SaveChangesAsync(ct);
        foreach (var invoiceId in invoiceIds)
        {
            db.PtpInvoices.Add(new PtpInvoice { TenantId = promise.TenantId, PtpId = promise.Id, InvoiceId = invoiceId });
        }

        await db.SaveChangesAsync(ct);

        foreach (var id in superseded)
        {
            await db.Promises.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.SupersededById, promise.Id), ct);
        }

        await audit.WriteAsync(Transition(promise, null, promise.Status, proposed ? "ai_extracted_promise" : "user_recorded_promise", actorUserId, null), ct);
        await cases.AddActivityAsync(c, ActivityKinds.Ptp, actorUserId,
            $"Promise {(proposed ? "proposed" : "recorded")}: {amount.ToString("F3", CultureInfo.InvariantCulture)} {currency} by {promisedDate:yyyy-MM-dd}",
            new { promiseId = promise.Id, status = promise.Status.ToString(), amount = amount.ToString("F3", CultureInfo.InvariantCulture), currency, promisedDate = promise.PromisedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), deadline = promise.DeadlineDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), superseded }, ct);

        if (!proposed)
        {
            await SuppressCaseAsync(c, promise, actorUserId, context, ct);
        }

        return new RecordResult(promise, invoiceIds, superseded);
    }

    /// <summary>C4: the case waits until the deadline (06:00 tenant time, SM-33) — but only from a state that allows it.</summary>
    private async Task SuppressCaseAsync(CollectionCase c, PromiseToPay promise, Guid? actorUserId, CaseService.Context context, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(context.Timezone);
        var local = promise.DeadlineDate.ToDateTime(new TimeOnly(6, 0));
        var until = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        if (CaseMachine.Peek(c.Status, CaseEvent.PtpRecorded) is not null)
        {
            await cases.FireAsync(c, CaseEvent.PtpRecorded, actorUserId, "ptp_recorded", null, ct);
        }

        await cases.SuppressAsync(c, until, "promise_active", ct);
    }

    // ---------------------------------------------------------------------------------------
    // Human transitions (SM-31)
    // ---------------------------------------------------------------------------------------

    public async Task<PromiseToPay> ConfirmAsync(Guid promiseId, decimal? amount, DateOnly? promisedDate, Guid actorUserId, CancellationToken ct)
    {
        var context = await cases.ContextAsync(ct);
        var promise = await LockAsync(promiseId, ct);
        var invoiceIds = await db.PtpInvoices.Where(x => x.PtpId == promise.Id).Select(x => x.InvoiceId).ToListAsync(ct);
        var covered = await db.Invoices.Where(i => invoiceIds.Contains(i.Id) && i.Status == InvoiceStatus.Open).SumAsync(i => i.BalanceCache, ct);

        var corrected = false;
        if (amount is { } a && a != promise.PromisedAmount)
        {
            if (a <= 0m || a > covered)
            {
                throw new CaseException("exceeds_covered_balance", "promisedAmount", new Dictionary<string, string> { ["coveredBalance"] = covered.ToString("F3", CultureInfo.InvariantCulture) });
            }

            promise.PromisedAmount = a;
            corrected = true;
        }

        if (promisedDate is { } d && d != promise.PromisedDate)
        {
            if (d < context.Today) throw new CaseException("promised_date_in_past", "promisedDate");
            var holidays = (await db.Holidays.Select(h => h.Date).ToListAsync(ct)).ToHashSet();
            promise.PromisedDate = d;
            promise.DeadlineDate = PtpRules.Deadline(d, context.Settings.PtpGraceBusinessDays, holidays);
            corrected = true;
        }

        if (promise.PromisedAmount > covered)
        {
            throw new CaseException("exceeds_covered_balance", "promisedAmount", new Dictionary<string, string> { ["coveredBalance"] = covered.ToString("F3", CultureInfo.InvariantCulture) });
        }

        // SM-36 on confirmation too: the promise becomes Active now, so overlaps are settled now.
        var overlapping = await db.PtpInvoices.Where(x => invoiceIds.Contains(x.InvoiceId) && x.PtpId != promise.Id)
            .Join(db.Promises.Where(p => p.Status == PtpStatus.Active), x => x.PtpId, p => p.Id, (x, p) => p).Distinct().ToListAsync(ct);
        foreach (var old in overlapping)
        {
            await ApplyAsync(old, PtpEvent.SupersededByNewPtp, actorUserId, "superseded", null, ct);
            old.SupersededById = promise.Id;
        }

        promise.ConfirmedBy = actorUserId;   // INV-13, before the status write
        await ApplyAsync(promise, PtpEvent.Confirm, actorUserId, corrected ? "human_correction" : "confirmed", null, ct);

        var c = await db.Cases.FirstAsync(x => x.Id == promise.CaseId, ct);
        await SuppressCaseAsync(c, promise, actorUserId, context, ct);
        return promise;
    }

    public async Task<PromiseToPay> RejectAsync(Guid promiseId, string reason, Guid actorUserId, CancellationToken ct)
    {
        var promise = await LockAsync(promiseId, ct);
        await ApplyAsync(promise, PtpEvent.Reject, actorUserId, reason, null, ct);
        return promise;
    }

    public async Task<PromiseToPay> CancelAsync(Guid promiseId, string reason, Guid actorUserId, CancellationToken ct)
    {
        var promise = await LockAsync(promiseId, ct);
        promise.CancelReason = reason;
        await ApplyAsync(promise, PtpEvent.Cancel, actorUserId, reason, null, ct);
        await ReleaseCaseAsync(promise, CaseEvent.PtpCancelled, actorUserId, "ptp_cancelled", ct);
        return promise;
    }

    // ---------------------------------------------------------------------------------------
    // Evaluation (SM-33, SM-34, SM-35) — the only way to Kept / PartiallyKept / Broken
    // ---------------------------------------------------------------------------------------

    /// <summary>The daily job's half: every Active promise whose deadline has arrived. Idempotent (SM-06).</summary>
    public async Task<int> EvaluateDueAsync(CancellationToken ct)
    {
        var context = await cases.ContextAsync(ct);
        var due = await db.Promises.Where(p => p.Status == PtpStatus.Active && p.DeadlineDate <= context.Today).Select(p => p.Id).ToListAsync(ct);
        var evaluated = 0;
        foreach (var id in due)
        {
            var promise = await LockAsync(id, ct);
            if (await EvaluateAsync(promise, context, deadlineReached: true, null, ct))
            {
                evaluated++;
            }
        }

        return evaluated;
    }

    /// <summary>Applies the verdict if there is one. Returns whether the promise changed state.</summary>
    private async Task<bool> EvaluateAsync(PromiseToPay promise, CaseService.Context context, bool deadlineReached, Guid? actorUserId, CancellationToken ct)
    {
        if (promise.Status != PtpStatus.Active)
        {
            return false;
        }

        var received = await ReceivedInWindowAsync(promise, ct);
        var verdict = PtpRules.Verdict(promise.PromisedAmount, received, context.Settings.PtpPartialThresholdPct, deadlineReached);
        if (verdict is null)
        {
            return false;
        }

        promise.ReceivedInWindow = received;
        promise.EvaluatedAt = time.GetUtcNow();
        promise.EvaluationNote = $"received {received.ToString("F3", CultureInfo.InvariantCulture)} of {promise.PromisedAmount.ToString("F3", CultureInfo.InvariantCulture)} {promise.Currency} in [{promise.CreatedAt.UtcDateTime:yyyy-MM-dd}, {promise.DeadlineDate:yyyy-MM-dd}]; threshold {context.Settings.PtpPartialThresholdPct.ToString("0.##", CultureInfo.InvariantCulture)}%";
        await ApplyAsync(promise, verdict.Value, actorUserId, PtpMachine.EventName(verdict.Value), promise.EvaluationNote, ct);

        switch (verdict)
        {
            case PtpEvent.PaymentCoversPromise:
                await ReleaseCaseAsync(promise, CaseEvent.PtpKept, actorUserId, "ptp_kept", ct);
                break;
            default:
                await MarkBrokenAsync(promise, actorUserId, ct);
                break;
        }

        return true;
    }

    /// <summary>
    /// What happens automatically on a broken (or partially kept) promise — and nothing else: the customer's
    /// counter, the case back to work at a recomputed priority, the rows that record it. No write-off, no
    /// credit note, no term change, no message (CLAUDE.md → Financial Safety; slice 6 doc §2).
    /// </summary>
    private async Task MarkBrokenAsync(PromiseToPay promise, Guid? actorUserId, CancellationToken ct)
    {
        await db.Customers.Where(x => x.Id == promise.CustomerId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BrokenPromiseCount12m, x => x.BrokenPromiseCount12m + 1), ct);
        await ReleaseCaseAsync(promise, CaseEvent.PtpBroken, actorUserId, "ptp_broken", ct);
    }

    /// <summary>C5 / ptp_kept: lift the suppression and rescore, if the case is still waiting on this promise.</summary>
    private async Task ReleaseCaseAsync(PromiseToPay promise, CaseEvent @event, Guid? actorUserId, string reason, CancellationToken ct)
    {
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == promise.CaseId, ct);
        if (c is null || CaseMachine.IsTerminal(c.Status))
        {
            return;
        }

        var stillActive = await db.Promises.AnyAsync(p => p.CaseId == c.Id && p.Status == PtpStatus.Active && p.Id != promise.Id, ct);
        if (stillActive)
        {
            await cases.RescoreOnlyAsync(c, ct);   // another promise still suppresses; the score reflects the count
            return;
        }

        await cases.SuppressAsync(c, null, null, ct);
        if (CaseMachine.Peek(c.Status, @event) is not null)
        {
            await cases.FireAsync(c, @event, actorUserId, reason, null, ct);
        }
        else
        {
            await cases.RescoreOnlyAsync(c, ct);
        }
    }

    /// <summary>
    /// SM-34: active allocations to the covered invoices from Confirmed payments received in [capture, deadline].
    /// A cheque's allocation exists only once it has cleared, so cheques count exactly when SM-42 says.
    /// </summary>
    public async Task<decimal> ReceivedInWindowAsync(PromiseToPay promise, CancellationToken ct)
    {
        var invoiceIds = await db.PtpInvoices.Where(x => x.PtpId == promise.Id).Select(x => x.InvoiceId).ToListAsync(ct);
        var from = DateOnly.FromDateTime(promise.CreatedAt.UtcDateTime);
        return await db.PaymentAllocations.Where(a => a.IsActive && invoiceIds.Contains(a.InvoiceId))
            .Join(db.Payments.Where(p => p.Status == PaymentStatus.Confirmed && p.ReceivedDate >= from && p.ReceivedDate <= promise.DeadlineDate), a => a.PaymentId, p => p.Id, (a, _) => a.Amount)
            .SumAsync(ct);
    }

    // ---------------------------------------------------------------------------------------
    // Hooks (SM-50, SM-51, SM-54)
    // ---------------------------------------------------------------------------------------

    public async Task InvoiceChangedAsync(Guid invoiceId, Guid? actorUserId, CancellationToken ct)
    {
        var ids = await db.PtpInvoices.Where(x => x.InvoiceId == invoiceId)
            .Join(db.Promises.Where(p => p.Status == PtpStatus.Active), x => x.PtpId, p => p.Id, (x, p) => p.Id).Distinct().ToListAsync(ct);
        if (ids.Count == 0)
        {
            return;
        }

        var context = await cases.ContextAsync(ct);
        foreach (var id in ids)
        {
            var promise = await LockAsync(id, ct);
            await EvaluateAsync(promise, context, deadlineReached: promise.DeadlineDate <= context.Today, actorUserId, ct);
        }
    }

    public async Task<Guid?> ChequeReceivedAsync(Cheque cheque, Guid? actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cheque);
        if (cheque.ChequeDate <= cheque.ReceivedDate)
        {
            return null;   // not post-dated: a payment in waiting, not a promise
        }

        var c = await db.Cases.FirstOrDefaultAsync(x => x.CustomerId == cheque.CustomerId && x.Status != CaseStatus.Resolved && x.Status != CaseStatus.Abandoned, ct);
        if (c is null)
        {
            return null;   // nothing is being chased, so there is nothing to suppress (slice 6 §2)
        }

        var context = await cases.ContextAsync(ct);
        var scoped = await db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId).ToListAsync(ct);
        var candidates = await db.Invoices.Where(i => scoped.Contains(i.Id) && i.Status == InvoiceStatus.Open && i.Currency == cheque.Currency)
            .Select(i => new { i.Id, i.DueDate, i.InvoiceNumber, i.BalanceCache }).ToListAsync(ct);
        var covered = PtpRules.CoverFifo(cheque.Amount, candidates.Select(i => (i.Id, i.DueDate, i.InvoiceNumber, i.BalanceCache)));
        if (covered.Count == 0)
        {
            return null;
        }

        // SM-32 still holds: the promise is the lesser of the cheque and what the covered invoices owe.
        var coveredBalance = candidates.Where(i => covered.Contains(i.Id)).Sum(i => i.BalanceCache);
        var amount = Math.Min(cheque.Amount, coveredBalance);
        var result = await CreateAsync(c, covered, amount, cheque.Currency, cheque.ChequeDate, PtpSources.Cheque, $"Post-dated cheque {cheque.ChequeNumber}", actorUserId, null, cheque.Id, context, ct);
        return result.Promise.Id;
    }

    public async Task ChequeBouncedAsync(Cheque cheque, Guid? actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cheque);
        if (cheque.PtpId is not { } id)
        {
            return;
        }

        var promise = await LockAsync(id, ct);
        if (promise.Status != PtpStatus.Active)
        {
            return;
        }

        promise.ReceivedInWindow = await ReceivedInWindowAsync(promise, ct);
        promise.EvaluatedAt = time.GetUtcNow();
        promise.EvaluationNote = $"cheque {cheque.ChequeNumber} bounced";
        await ApplyAsync(promise, PtpEvent.ChequeBounced, actorUserId, "cheque_bounced", promise.EvaluationNote, ct);
        await MarkBrokenAsync(promise, actorUserId, ct);
    }

    public Task<bool> HasActivePromiseAsync(Guid invoiceId, CancellationToken ct) =>
        db.PtpInvoices.Where(x => x.InvoiceId == invoiceId)
            .Join(db.Promises.Where(p => p.Status == PtpStatus.Active), x => x.PtpId, p => p.Id, (x, p) => p.Id)
            .AnyAsync(ct);

    // ---------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------

    /// <summary>SM-37 over promises evaluated in the trailing 12 months.</summary>
    public async Task<Reliability> ReliabilityAsync(Guid customerId, DateOnly today, CancellationToken ct)
    {
        var from = new DateTimeOffset(today.AddYears(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var statuses = await db.Promises
            .Where(p => p.CustomerId == customerId && p.EvaluatedAt != null && p.EvaluatedAt >= from
                && (p.Status == PtpStatus.Kept || p.Status == PtpStatus.PartiallyKept || p.Status == PtpStatus.Broken))
            .Select(p => p.Status).ToListAsync(ct);
        return PtpRules.ReliabilityOf(statuses);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<PromiseToPay> LockAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM promises_to_pay WHERE id = {id} FOR UPDATE", ct);
        return await db.Promises.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new CaseException("promise_not_found");
    }

    private async Task ApplyAsync(PromiseToPay promise, PtpEvent @event, Guid? actorUserId, string reasonCode, string? note, CancellationToken ct)
    {
        var from = promise.Status;
        var to = PtpMachine.Next(from, @event);
        promise.Status = to;
        promise.UpdatedAt = time.GetUtcNow();
        promise.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition(promise, from, to, PtpMachine.EventName(@event), actorUserId, note, reasonCode), ct);
        var c = await db.Cases.FirstAsync(x => x.Id == promise.CaseId, ct);
        await cases.AddActivityAsync(c, ActivityKinds.Ptp, actorUserId, $"Promise {from} → {to}: {PtpMachine.EventName(@event)}",
            new { promiseId = promise.Id, from = from.ToString(), to = to.ToString(), @event = PtpMachine.EventName(@event), reasonCode, receivedInWindow = promise.ReceivedInWindow?.ToString("F3", CultureInfo.InvariantCulture) }, ct);
    }

    private AuditEvent Transition(PromiseToPay p, PtpStatus? from, PtpStatus to, string @event, Guid? actor, string? note, string? reasonCode = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = "promise_to_pay.status_changed",
        EntityType = "promise_to_pay",
        EntityId = p.Id,
        FromState = from?.ToString(),
        ToState = to.ToString(),
        ReasonCode = reasonCode ?? @event,
        Note = note,
        AiSuggestionId = p.AiSuggestionId,
        Changes = JsonSerializer.Serialize(new { @event }, Json),
    };
}
