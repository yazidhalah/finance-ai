using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Cases;

/// <summary>
/// Doc 02 §4. A dispute never moves money (SM-41); an acceptance creates and applies a credit note in the same
/// transaction (SM-45) through the ledger, which enforces SM-46 the way it enforces every other application.
/// Every terminal state but timeout names a human (SM-47). The AI has no path in here (slice 9 may raise a
/// <c>Proposed</c>-like claim later; it will still land in <c>Open</c> and be resolved by a person).
/// </summary>
public sealed class DisputeService(TenantDbContext db, IAuditWriter audit, TimeProvider time, CaseService cases, LedgerService ledger) : IDisputeHooks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record RaiseResult(Dispute Dispute, PaymentVerificationTask? VerificationTask);

    public sealed record ResolveResult(Dispute Dispute, CreditNote? CreditNote);

    public sealed record DunningEligibility(Guid InvoiceId, bool Allowed, string? Reason);

    // ---------------------------------------------------------------------------------------
    // Raise (SM-40, SM-43, SM-44, SM-48, C6)
    // ---------------------------------------------------------------------------------------

    public async Task<RaiseResult> RaiseAsync(Guid invoiceId, string reasonCode, decimal amount, string? claim, string source, Guid? actorUserId, Guid? aiSuggestionId, CancellationToken ct)
    {
        if (!DisputeReasons.All.Contains(reasonCode, StringComparer.Ordinal))
        {
            throw new CaseException("invalid_reason_code", "reasonCode");
        }

        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM invoices WHERE id = {invoiceId} FOR UPDATE", ct);
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw new CaseException("invoice_not_found");
        if (invoice.Status != InvoiceStatus.Open)
        {
            throw new CaseException("invoice_not_open", null, new Dictionary<string, string> { ["status"] = invoice.Status.ToString() });
        }

        if (amount <= 0m || amount > invoice.BalanceCache)
        {
            throw new CaseException("exceeds_open_balance", "disputedAmount", new Dictionary<string, string> { ["openBalance"] = F3(invoice.BalanceCache), ["currency"] = invoice.Currency });
        }

        if (await db.Disputes.AnyAsync(d => d.InvoiceId == invoiceId && (d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer), ct))
        {
            throw new CaseException("duplicate", "invoiceId");
        }

        var context = await cases.ContextAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(context.Timezone);
        var holidays = (await db.Holidays.Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow();
        var c = await db.Cases.FirstOrDefaultAsync(x => x.CustomerId == invoice.CustomerId && x.Status != CaseStatus.Resolved && x.Status != CaseStatus.Abandoned, ct);

        var dispute = new Dispute
        {
            TenantId = db.CurrentTenantId,
            InvoiceId = invoice.Id,
            CustomerId = invoice.CustomerId,
            CaseId = c?.Id,
            ReasonCode = reasonCode,
            DisputedAmount = amount,
            Currency = invoice.Currency,
            CustomerClaim = claim,
            RaisedAt = now,
            RaisedBy = actorUserId,
            Source = source,
            AiSuggestionId = aiSuggestionId,
            FirstResponseDueAt = DisputeSla.DueAt(context.Today, DisputeSla.FirstResponseBusinessDays, holidays, zone),
            ResolutionDueAt = DisputeSla.DueAt(context.Today, DisputeSla.ResolutionBusinessDays, holidays, zone),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Disputes.Add(dispute);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition(dispute, null, DisputeStatus.Open, "dispute_raised", actorUserId, claim is null ? null : "claim recorded"), ct);

        PaymentVerificationTask? task = null;
        if (reasonCode == DisputeReasons.AlreadyPaid)
        {
            // SM-44: "they say they paid" is a task to find the payment, not an investigation of the claim.
            task = new PaymentVerificationTask
            {
                TenantId = db.CurrentTenantId,
                InvoiceId = invoice.Id,
                CustomerId = invoice.CustomerId,
                DisputeId = dispute.Id,
                Source = "dispute",
                Claim = claim,
                CreatedAt = now,
                CreatedBy = actorUserId,
            };
            db.VerificationTasks.Add(task);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(Event("payment_verification.opened", "payment_verification_task", task.Id, actorUserId), ct);
        }

        if (c is not null)
        {
            await cases.AddActivityAsync(c, ActivityKinds.Dispute, actorUserId, $"Dispute raised on {invoice.InvoiceNumber}: {reasonCode}, {F3(amount)} {invoice.Currency}",
                new { disputeId = dispute.Id, invoiceId = invoice.Id, reasonCode, amount = F3(amount), currency = invoice.Currency }, ct);
            // C6 / SM-52: from PromiseActive too; the promise stays Active and simply stops mattering to the queue.
            if (CaseMachine.Peek(c.Status, CaseEvent.DisputeOpened) is not null)
            {
                await cases.FireAsync(c, CaseEvent.DisputeOpened, actorUserId, "dispute_opened", null, ct);
            }
            else
            {
                await cases.RescoreOnlyAsync(c, ct);
            }
        }

        return new RaiseResult(dispute, task);
    }

    // ---------------------------------------------------------------------------------------
    // Updates (disputes.write) and the sweep's timeout
    // ---------------------------------------------------------------------------------------

    public async Task<Dispute> TransitionAsync(Guid disputeId, DisputeEvent @event, Guid? actorUserId, string? reason, Guid? assignTo, CancellationToken ct)
    {
        if (DisputeMachine.IsResolution(@event))
        {
            throw new CaseException("use_resolve");   // SM-47: resolutions have their own door and permission
        }

        var dispute = await LockAsync(disputeId, ct);
        var now = time.GetUtcNow();

        switch (@event)
        {
            case DisputeEvent.Assign:
                var target = assignTo ?? actorUserId ?? throw new CaseException("assignee_required", "assignedTo");
                var membership = await db.TenantMemberships.Where(m => m.UserId == target).Select(m => new { m.Status }).FirstOrDefaultAsync(ct) ?? throw new CaseException("member_not_found", "assignedTo");
                if (membership.Status != MembershipStatus.Active) throw new CaseException("member_not_active", "assignedTo");
                dispute.AssignedTo = target;
                dispute.FirstResponseAt ??= now;   // SM-48: assignment is the first response
                break;
            case DisputeEvent.RequestInfo:
                dispute.PendingSince = now;        // SM-48: the resolution clock pauses
                break;
            case DisputeEvent.InfoReceived or DisputeEvent.Timeout:
                if (dispute.PendingSince is { } since)
                {
                    dispute.ResolutionDueAt += now - since;   // the clock resumes where it stopped
                    dispute.PendingSince = null;
                }

                break;
            case DisputeEvent.Cancel or DisputeEvent.CustomerWithdrew:
                if (string.IsNullOrWhiteSpace(reason)) throw new CaseException("reason_required", "reason");
                dispute.CloseReason = reason;
                dispute.ResolvedAt = now;
                dispute.ResolvedBy = actorUserId;
                break;
        }

        await ApplyAsync(dispute, @event, actorUserId, reason ?? DisputeMachine.EventName(@event), null, ct);
        if (DisputeMachine.IsTerminal(dispute.Status))
        {
            await ReleaseCaseAsync(dispute, actorUserId, ct);
        }

        return dispute;
    }

    /// <summary>SM-48: a customer who never answers does not stop the clock forever. Idempotent per day.</summary>
    public async Task<int> TimeOutPendingAsync(CancellationToken ct)
    {
        var context = await cases.ContextAsync(ct);
        var holidays = (await db.Holidays.Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var pending = await db.Disputes.Where(d => d.Status == DisputeStatus.PendingCustomer && d.PendingSince != null).Select(d => new { d.Id, d.PendingSince }).ToListAsync(ct);
        var count = 0;
        foreach (var p in pending)
        {
            var since = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.PendingSince!.Value, TimeZoneInfo.FindSystemTimeZoneById(context.Timezone)).DateTime);
            if (BusinessDays.Add(since, DisputeSla.PendingCustomerTimeoutBusinessDays, holidays) <= context.Today)
            {
                await TransitionAsync(p.Id, DisputeEvent.Timeout, null, "timeout", null, ct);
                count++;
            }
        }

        return count;
    }

    // ---------------------------------------------------------------------------------------
    // Resolution (disputes.resolve; SM-45, SM-46, SM-47)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// D-3: the dispute is written first, then the credit note is created and applied through the ledger. If the
    /// ledger refuses (SM-46 is its <c>exceeds_open_balance</c>), the exception unwinds the whole request
    /// transaction — the dispute write included. AC-05 proves it.
    /// </summary>
    public async Task<ResolveResult> ResolveAsync(Guid disputeId, DisputeEvent outcome, decimal? amount, string? reason, Guid actorUserId, CancellationToken ct)
    {
        if (!DisputeMachine.IsResolution(outcome))
        {
            throw new CaseException("not_a_resolution", "outcome");
        }

        var dispute = await LockAsync(disputeId, ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM invoices WHERE id = {dispute.InvoiceId} FOR UPDATE", ct);
        var invoice = await db.Invoices.FirstAsync(i => i.Id == dispute.InvoiceId, ct);

        if (outcome == DisputeEvent.Reject && string.IsNullOrWhiteSpace(reason))
        {
            throw new CaseException("reason_required", "reason");
        }

        if (DisputeRules.CheckResolutionAmount(outcome, amount, dispute.DisputedAmount, invoice.BalanceCache) is { } code)
        {
            throw new CaseException(code, "resolutionAmount", new Dictionary<string, string> { ["openBalance"] = F3(invoice.BalanceCache), ["disputedAmount"] = F3(dispute.DisputedAmount) });
        }

        var now = time.GetUtcNow();
        dispute.ResolvedAt = now;
        dispute.ResolvedBy = actorUserId;   // SM-47: always a named human
        dispute.ResolutionNote = reason;
        dispute.CloseReason = DisputeMachine.EventName(outcome);

        CreditNote? note = null;
        if (outcome is DisputeEvent.Accept or DisputeEvent.PartiallyAccept)
        {
            var credited = outcome == DisputeEvent.Accept ? dispute.DisputedAmount : amount!.Value;
            dispute.ResolutionAmount = credited;

            // SM-45: the money moves in the model in the same transaction, through the ordinary instrument.
            note = await ledger.CreateCreditNoteAsync(new CreditNote
            {
                CustomerId = dispute.CustomerId,
                Amount = credited,
                Currency = dispute.Currency,
                IssueDate = (await cases.ContextAsync(ct)).Today,
                ReasonCode = CreditNoteReasons.DisputeResolution,
                DisputeId = dispute.Id,
                Notes = $"Dispute {dispute.ReasonCode} on {invoice.InvoiceNumber}",
            }, actorUserId, ct);
            dispute.CreditNoteId = note.Id;
            await db.SaveChangesAsync(ct);
            await ledger.ApplyCreditNoteAsync(note, [new LedgerService.AllocationLine(invoice.Id, credited)], actorUserId, ct);
        }

        await ApplyAsync(dispute, outcome, actorUserId, DisputeMachine.EventName(outcome), reason, ct);
        await ReleaseCaseAsync(dispute, actorUserId, ct);
        return new ResolveResult(dispute, note);
    }

    /// <summary>C7 when the last open dispute of the case closes; a rescore either way (the dampener lifts).</summary>
    private async Task ReleaseCaseAsync(Dispute dispute, Guid? actorUserId, CancellationToken ct)
    {
        if (dispute.CaseId is not { } caseId)
        {
            return;
        }

        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct);
        if (c is null || CaseMachine.IsTerminal(c.Status))
        {
            return;
        }

        await db.Entry(c).ReloadAsync(ct);
        var stillOpen = await db.Disputes.AnyAsync(d => d.CaseId == caseId && d.Id != dispute.Id && (d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer), ct);
        if (!stillOpen && CaseMachine.Peek(c.Status, CaseEvent.AllDisputesResolved) is not null)
        {
            await cases.FireAsync(c, CaseEvent.AllDisputesResolved, actorUserId, "all_disputes_resolved", null, ct);
        }
        else
        {
            await cases.RescoreOnlyAsync(c, ct);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The dunning guard (SM-25) — the contract slice 8's send path must call
    // ---------------------------------------------------------------------------------------

    public const string DisputeBlocksSend = "dispute_blocks_send";

    public async Task<IReadOnlyList<DunningEligibility>> DunningEligibilityAsync(Guid caseId, CancellationToken ct)
    {
        var c = await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct) ?? throw new CaseException("case_not_found");
        var settings = await db.TenantSettings.Select(s => s.AllowSplitDunningDuringDispute).FirstAsync(ct);
        var invoiceIds = await db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId).ToListAsync(ct);
        var disputed = (await db.Disputes.Where(d => d.CustomerId == c.CustomerId && (d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer)).Select(d => d.InvoiceId).ToListAsync(ct)).ToHashSet();
        return invoiceIds.Select(id =>
            disputed.Contains(id) ? new DunningEligibility(id, false, DisputeBlocksSend)
            : disputed.Count > 0 && !settings ? new DunningEligibility(id, false, DisputeBlocksSend)
            : new DunningEligibility(id, true, null)).ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Evidence (SEC-40 / SEC-44)
    // ---------------------------------------------------------------------------------------

    public async Task<DisputeEvidence> AttachEvidenceAsync(Guid disputeId, string? fileName, byte[] content, Guid actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var dispute = await db.Disputes.FirstOrDefaultAsync(d => d.Id == disputeId, ct) ?? throw new CaseException("dispute_not_found");
        if (content.Length == 0 || content.Length > EvidenceInspector.MaxBytes)
        {
            throw new CaseException("file_too_large", "file", new Dictionary<string, string> { ["maxBytes"] = EvidenceInspector.MaxBytes.ToString(CultureInfo.InvariantCulture) });
        }

        var contentType = EvidenceInspector.SniffContentType(content) ?? throw new CaseException("unsupported_file_type", "file");
        var safeName = EvidenceInspector.SafeFileName(fileName);
        if (!EvidenceInspector.ExtensionMatches(safeName, contentType))
        {
            throw new CaseException("file_extension_mismatch", "file");
        }

        var evidence = new DisputeEvidence
        {
            TenantId = db.CurrentTenantId,
            DisputeId = dispute.Id,
            FileName = safeName,
            ContentType = contentType,
            SizeBytes = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Content = content,
            UploadedBy = actorUserId,
            UploadedAt = time.GetUtcNow(),
        };
        db.DisputeEvidence.Add(evidence);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("dispute.evidence_attached", "dispute", dispute.Id, actorUserId, note: $"{safeName} ({contentType}, {content.Length} bytes, sha256 {evidence.Sha256[..12]}…)"), ct);
        return evidence;
    }

    // ---------------------------------------------------------------------------------------
    // Payment verification tasks (SM-44, SM-10)
    // ---------------------------------------------------------------------------------------

    /// <summary>Records what was found. Never touches the invoice: a found payment was recorded through <c>/payments</c> and settlement followed there.</summary>
    public async Task<PaymentVerificationTask> ResolveVerificationAsync(Guid taskId, VerificationOutcome outcome, Guid? paymentId, string? notes, Guid actorUserId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payment_verification_tasks WHERE id = {taskId} FOR UPDATE", ct);
        var task = await db.VerificationTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw new CaseException("task_not_found");
        if (task.Status != "Open")
        {
            throw new CaseException("task_closed");
        }

        if (outcome is VerificationOutcome.PaymentFound or VerificationOutcome.Partial)
        {
            if (paymentId is null) throw new CaseException("payment_required", "paymentId");
            if (!await db.Payments.AnyAsync(p => p.Id == paymentId && p.CustomerId == task.CustomerId, ct)) throw new CaseException("payment_not_found", "paymentId");
        }

        task.Status = "Resolved";
        task.Outcome = outcome switch { VerificationOutcome.PaymentFound => "payment_found", VerificationOutcome.Partial => "partial", _ => "no_payment_found" };
        task.PaymentId = outcome == VerificationOutcome.NoPaymentFound ? null : paymentId;
        task.Notes = notes;
        task.ResolvedAt = time.GetUtcNow();
        task.ResolvedBy = actorUserId;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event("payment_verification.resolved", "payment_verification_task", task.Id, actorUserId, task.Outcome, notes), ct);
        return task;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<Dispute> LockAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM disputes WHERE id = {id} FOR UPDATE", ct);
        return await db.Disputes.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new CaseException("dispute_not_found");
    }

    private async Task ApplyAsync(Dispute d, DisputeEvent @event, Guid? actorUserId, string reasonCode, string? note, CancellationToken ct)
    {
        var from = d.Status;
        var to = DisputeMachine.Next(from, @event);
        d.Status = to;
        d.UpdatedAt = time.GetUtcNow();
        d.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Transition(d, from, to, DisputeMachine.EventName(@event), actorUserId, note, reasonCode), ct);
        if (d.CaseId is { } caseId && await db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct) is { } c)
        {
            await cases.AddActivityAsync(c, ActivityKinds.Dispute, actorUserId, $"Dispute {from} → {to}: {DisputeMachine.EventName(@event)}",
                new { disputeId = d.Id, from = from.ToString(), to = to.ToString(), @event = DisputeMachine.EventName(@event), reasonCode, resolutionAmount = d.ResolutionAmount is { } r ? F3(r) : null, creditNoteId = d.CreditNoteId }, ct);
        }
    }

    private static string F3(decimal v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private AuditEvent Transition(Dispute d, DisputeStatus? from, DisputeStatus to, string @event, Guid? actor, string? note, string? reasonCode = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = "dispute.status_changed",
        EntityType = "dispute",
        EntityId = d.Id,
        FromState = from?.ToString(),
        ToState = to.ToString(),
        ReasonCode = reasonCode ?? @event,
        Note = note,
        AiSuggestionId = d.AiSuggestionId,
        Changes = JsonSerializer.Serialize(new { @event }, Json),
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
