using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Cases;

/// <summary>A guard failure. Becomes <c>422 business_rule_violated</c> with the code.</summary>
public sealed class CaseException(string code, string? field = null, IReadOnlyDictionary<string, string>? meta = null) : Exception(code)
{
    public string Code { get; } = code;

    public string? Field { get; } = field;

    public IReadOnlyDictionary<string, string>? Meta { get; } = meta;
}

/// <summary>SM-50: the ledger tells the case layer that an invoice's balance or lifecycle changed.</summary>
public interface ICaseHooks
{
    Task InvoiceChangedAsync(Guid invoiceId, Guid? actorUserId, CancellationToken ct);
}

/// <summary>
/// Doc 02 §2 and doc 03 §8. Every transition goes through <see cref="TransitionAsync"/>: row lock, guard,
/// <see cref="CaseMachine.Next"/>, write, audit row, activity row, rescore — one method, one transaction
/// (SM-02). The sweep (<see cref="SweepAsync"/>) is the daily job of SM-06 and is idempotent.
/// </summary>
public sealed class CaseService(TenantDbContext db, IAuditWriter audit, TimeProvider time) : ICaseHooks
{
    public sealed record Context(DateOnly Today, string Timezone, TenantSettings Settings, PriorityWeights Weights);

    public sealed record SweepResult(int Created, int Resolved, int Resumed, int FollowedUp, int Rescored);

    public async Task<Context> ContextAsync(CancellationToken ct)
    {
        var tz = await db.Tenants.Select(t => t.Timezone).FirstAsync(ct);
        var settings = await db.TenantSettings.AsNoTracking().FirstAsync(ct);
        return new Context(AgingRules.TodayIn(tz, time.GetUtcNow()), tz, settings, PriorityWeights.For(settings.PriorityWeightsVersion));
    }

    // ---------------------------------------------------------------------------------------
    // The sweep (C1, C2 follow_up_due, C8 hold_expired, C10, SM-27 nightly rescoring)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Idempotent for a given day: a second run creates nothing, transitions nothing, and writes no audit row
    /// (T-13). Runs under a per-tenant advisory lock so two overlapping runs serialize.
    /// </summary>
    public async Task<SweepResult> SweepAsync(Guid? actorUserId, CancellationToken ct)
    {
        var context = await ContextAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('case_sweep'), hashtext({db.CurrentTenantId.ToString()}))", ct);

        var created = 0;
        var resolved = 0;
        var resumed = 0;
        var followedUp = 0;
        var rescored = 0;

        // C1: customers past grace with no non-terminal case.
        var graceCutoff = context.Today.AddDays(-context.Settings.GraceDaysBeforeCase);
        var withOpenCase = db.Cases.Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned).Select(c => c.CustomerId);
        var candidates = await db.Invoices
            .Where(i => i.Status == InvoiceStatus.Open && i.BalanceCache > 0m && i.DueDate < graceCutoff)
            .Where(i => !withOpenCase.Contains(i.CustomerId))
            .Select(i => i.CustomerId)
            .Distinct()
            .ToListAsync(ct);

        // SM-03: the sweep's transitions are the system's, whoever pressed the button; the button press is audited once.
        foreach (var customerId in candidates)
        {
            await OpenCaseAsync(customerId, context, null, ct);
            created++;
        }

        // Existing active cases: scope, time-based transitions, score.
        var active = await db.Cases.Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned).ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var c in active)
        {
            if (c.Status == CaseStatus.OnHold && c.HoldUntil is { } until && until <= context.Today)
            {
                await ApplyTransitionAsync(c, CaseEvent.HoldExpired, null, "hold_expired", null, null, context, ct);
                resumed++;
            }
            else if (c.Status == CaseStatus.AwaitingCustomer && c.NextActionAt is { } at && at <= now)
            {
                await ApplyTransitionAsync(c, CaseEvent.FollowUpDue, null, "follow_up_due", null, null, context, ct);
                followedUp++;
            }

            var outcome = await RefreshScopeAsync(c, context, null, "sweep", ct);
            if (outcome == CaseStatus.Resolved)
            {
                resolved++;
            }
            else
            {
                rescored++;
            }
        }

        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = actorUserId,
            ActorKind = actorUserId is null ? ActorKinds.System : ActorKinds.User,
            EventType = "collection_case.sweep_run",
            EntityType = "tenant",
            EntityId = db.CurrentTenantId,
            Changes = JsonSerializer.Serialize(new { created, resolved, resumed, followedUp, rescored, day = context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, JsonOptions),
        }, ct);

        return new SweepResult(created, resolved, resumed, followedUp, rescored);
    }

    /// <summary>Manual creation (doc 05): rare; the sweep normally does this.</summary>
    public async Task<CollectionCase> CreateManualAsync(Guid customerId, Guid actorUserId, CancellationToken ct)
    {
        var context = await ContextAsync(ct);
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
        {
            throw new CaseException("customer_not_found", "customerId");
        }

        if (await db.Cases.AnyAsync(c => c.CustomerId == customerId && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned, ct))
        {
            throw new CaseException("duplicate", "customerId");
        }

        if (!await db.Invoices.AnyAsync(i => i.CustomerId == customerId && i.Status == InvoiceStatus.Open && i.BalanceCache > 0m && i.DueDate < context.Today, ct))
        {
            throw new CaseException("no_overdue_invoices", "customerId");
        }

        return await OpenCaseAsync(customerId, context, actorUserId, ct);
    }

    private async Task<CollectionCase> OpenCaseAsync(Guid customerId, Context context, Guid? actorUserId, CancellationToken ct)
    {
        // D-4: the number is allocated under the tenant's advisory lock (the sweep holds it; manual creation takes it).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext('case_number'), hashtext({db.CurrentTenantId.ToString()}))", ct);
        var next = (await db.Cases.MaxAsync(c => (long?)c.CaseNumber, ct) ?? 0) + 1;
        var now = time.GetUtcNow();

        var c = new CollectionCase
        {
            TenantId = db.CurrentTenantId,
            CustomerId = customerId,
            CaseNumber = next,
            Status = CaseStatus.Open,
            WeightsVersion = context.Weights.Version,
            OpenedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Cases.Add(c);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(Transition(c, null, CaseStatus.Open, CaseMachine.EventName(CaseEvent.InvoiceBecameOverdue), actorUserId, null), ct);
        await AddActivityAsync(c, ActivityKinds.StatusChange, actorUserId, $"Case #{c.CaseNumber} opened: invoice_became_overdue", new { from = (string?)null, to = "Open", @event = "invoice_became_overdue" }, ct);
        await RefreshScopeAsync(c, context, actorUserId, "opened", ct);
        return c;
    }

    // ---------------------------------------------------------------------------------------
    // Scope and score
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Brings <c>case_invoices</c> in line with the customer's ledger (SM-20): past-due Open invoices join, anything
    /// no longer Open leaves with a reason. Then C10 if nothing is left, else a rescore. Returns the resulting status.
    /// </summary>
    private async Task<CaseStatus> RefreshScopeAsync(CollectionCase c, Context context, Guid? actorUserId, string reasonCode, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.CaseInvoices.Where(x => x.CaseId == c.Id).ToListAsync(ct);
        var eligible = await db.Invoices
            .Where(i => i.CustomerId == c.CustomerId && i.Status == InvoiceStatus.Open && i.BalanceCache > 0m && i.DueDate < context.Today)
            .Select(i => new { i.Id, i.Status, i.BalanceCache, i.FxRateToBase, i.DueDate })
            .ToListAsync(ct);
        var eligibleIds = eligible.Select(i => i.Id).ToHashSet();

        foreach (var row in rows.Where(r => r.RemovedAt is null && !eligibleIds.Contains(r.InvoiceId)))
        {
            var status = await db.Invoices.Where(i => i.Id == row.InvoiceId).Select(i => i.Status).FirstAsync(ct);
            row.RemovedAt = now;
            row.RemovedReason = status.ToString().ToLowerInvariant();
            await AddActivityAsync(c, ActivityKinds.System, actorUserId, $"Invoice left scope: {row.RemovedReason}", new { invoiceId = row.InvoiceId, reason = row.RemovedReason }, ct);
        }

        foreach (var invoice in eligible)
        {
            var existing = rows.FirstOrDefault(r => r.InvoiceId == invoice.Id);
            if (existing is null)
            {
                db.CaseInvoices.Add(new CaseInvoice { TenantId = c.TenantId, CaseId = c.Id, InvoiceId = invoice.Id, AddedAt = now });
            }
            else if (existing.RemovedAt is not null)
            {
                existing.RemovedAt = null;
                existing.RemovedReason = null;
                existing.AddedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);

        if (eligible.Count == 0 && !CaseMachine.IsTerminal(c.Status))
        {
            // C10. Any past-due Open invoice would be in scope; none means everything is settled, written off or void.
            // A customer whose only remaining Open invoices are not yet due has nothing to chase either.
            await ApplyTransitionAsync(c, CaseEvent.BalanceZero, actorUserId, "balance_zero", null, null, context, ct);
            return c.Status;
        }

        await RescoreAsync(c, context, ct);
        return c.Status;
    }

    /// <summary>FIN-80 / SM-27. Inputs are stored facts; the result and its breakdown are stored with the case.</summary>
    private async Task RescoreAsync(CollectionCase c, Context context, CancellationToken ct)
    {
        var scoped = db.CaseInvoices.Where(x => x.CaseId == c.Id && x.RemovedAt == null).Select(x => x.InvoiceId);
        var invoices = await db.Invoices.Where(i => scoped.Contains(i.Id) && i.Status == InvoiceStatus.Open)
            .Select(i => new { i.BalanceCache, i.FxRateToBase, i.DueDate }).ToListAsync(ct);
        var customer = await db.Customers.Where(x => x.Id == c.CustomerId)
            .Select(x => new { x.BrokenPromiseCount12m, x.BouncedChequeCount12m, x.RiskFlag }).FirstAsync(ct);

        // Ranking input only, never shown as money: the same per-invoice rounding as the aging report.
        c.OverdueBalanceBase = invoices.Sum(i => AgingRules.ToBaseIndicative(i.BalanceCache, i.FxRateToBase));
        c.MaxDaysPastDue = invoices.Count == 0 ? 0 : invoices.Max(i => context.Today.DayNumber - i.DueDate.DayNumber);
        c.InvoiceCount = invoices.Count;

        var now = time.GetUtcNow();
        var sinceContact = c.LastContactAt is { } last ? (int?)Math.Max(0, (int)(now - last).TotalDays) : null;
        var result = PriorityScore.Compute(
            new PriorityInputs(c.OverdueBalanceBase, c.MaxDaysPastDue, customer.BrokenPromiseCount12m, customer.BouncedChequeCount12m, customer.RiskFlag, sinceContact, c.Status == CaseStatus.Disputed),
            context.Weights);

        c.PriorityScore = result.Score;
        c.WeightsVersion = result.WeightsVersion;
        c.PriorityFactors = JsonSerializer.Serialize(result.Factors, JsonOptions);
        c.ScoredAt = now;
        c.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<PriorityFactor> Factors(CollectionCase c) =>
        JsonSerializer.Deserialize<List<PriorityFactor>>(c.PriorityFactors, JsonOptions) ?? [];

    // ---------------------------------------------------------------------------------------
    // Transitions (SM-02)
    // ---------------------------------------------------------------------------------------

    public async Task<CollectionCase> TransitionAsync(Guid caseId, CaseEvent @event, Guid? actorUserId, string? reason, string? note, DateOnly? holdUntil, CancellationToken ct)
    {
        var context = await ContextAsync(ct);
        var c = await LockAsync(caseId, ct);

        switch (@event)
        {
            case CaseEvent.Hold when string.IsNullOrWhiteSpace(reason) || holdUntil is null:
                throw new CaseException("hold_requires_reason_and_until", holdUntil is null ? "holdUntil" : "reasonCode");
            case CaseEvent.Hold when holdUntil <= context.Today:
                throw new CaseException("hold_until_must_be_future", "holdUntil");
            case CaseEvent.Escalate or CaseEvent.Abandon when string.IsNullOrWhiteSpace(reason):
                throw new CaseException("reason_required", "reasonCode");
        }

        await ApplyTransitionAsync(c, @event, actorUserId, reason ?? CaseMachine.EventName(@event), note, holdUntil, context, ct);
        return c;
    }

    private async Task ApplyTransitionAsync(CollectionCase c, CaseEvent @event, Guid? actorUserId, string reasonCode, string? note, DateOnly? holdUntil, Context context, CancellationToken ct)
    {
        var from = c.Status;
        var to = CaseMachine.Next(from, @event);
        var now = time.GetUtcNow();

        c.Status = to;
        switch (@event)
        {
            case CaseEvent.Hold:
                c.HoldUntil = holdUntil;
                c.HoldReason = reasonCode;
                c.NextActionAt = null;
                c.NextActionReason = null;
                break;
            case CaseEvent.Resume or CaseEvent.HoldExpired:
                c.HoldUntil = null;
                c.HoldReason = null;
                break;
            case CaseEvent.Escalate:
                // SM-26: set once, never cleared. Nothing scheduled survives.
                c.EscalatedAt = now;
                c.EscalatedBy = actorUserId;
                c.EscalationReason = reasonCode;
                c.NextActionAt = null;
                c.NextActionReason = null;
                c.HoldUntil = null;
                c.HoldReason = null;
                break;
            case CaseEvent.FollowUpDue or CaseEvent.ContactLogged or CaseEvent.ReplyReceived:
                c.NextActionAt = null;
                c.NextActionReason = null;
                break;
        }

        if (CaseMachine.IsTerminal(to))
        {
            c.ClosedAt = now;
            c.CloseReason = reasonCode;
            c.NextActionAt = null;
            c.NextActionReason = null;
            c.HoldUntil = null;
            c.HoldReason = null;
        }

        c.UpdatedAt = now;
        c.RowVersion++;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(Transition(c, from, to, CaseMachine.EventName(@event), actorUserId, note, reasonCode), ct);
        await AddActivityAsync(c, ActivityKinds.StatusChange, actorUserId, $"{from} → {to}: {CaseMachine.EventName(@event)}",
            new { from = from.ToString(), to = to.ToString(), @event = CaseMachine.EventName(@event), reasonCode, note }, ct);

        if (!CaseMachine.IsTerminal(to))
        {
            await RescoreAsync(c, context, ct);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Activities, snooze, assignment
    // ---------------------------------------------------------------------------------------

    public async Task<CaseActivity> LogActivityAsync(Guid caseId, string kind, string summary, string? detail, Guid actorUserId, CancellationToken ct)
    {
        if (!ActivityKinds.UserLoggable.Contains(kind))
        {
            throw new CaseException("invalid_activity_kind", "kind");
        }

        var context = await ContextAsync(ct);
        var c = await LockAsync(caseId, ct);
        if (CaseMachine.IsTerminal(c.Status))
        {
            throw new CaseException("case_closed");
        }

        var activity = await AddActivityAsync(c, kind, actorUserId, summary, detail is null ? null : JsonDocument.Parse(detail).RootElement, ct);

        if (ActivityKinds.IsContact(kind))
        {
            c.LastContactAt = activity.OccurredAt;
            c.UpdatedAt = activity.OccurredAt;
            await db.SaveChangesAsync(ct);

            // C2: contact on a fresh or waiting case brings it back to work.
            if (CaseMachine.Peek(c.Status, CaseEvent.ContactLogged) is not null)
            {
                await ApplyTransitionAsync(c, CaseEvent.ContactLogged, actorUserId, "contact_logged", null, null, context, ct);
            }
            else
            {
                await RescoreAsync(c, context, ct);
            }
        }

        return activity;
    }

    /// <summary>Queue suppression only — the state does not change (doc 05 <c>/snooze</c>).</summary>
    public async Task<CollectionCase> SnoozeAsync(Guid caseId, DateOnly untilDate, string? reason, Guid actorUserId, CancellationToken ct)
    {
        var context = await ContextAsync(ct);
        var c = await LockAsync(caseId, ct);
        if (CaseMachine.IsTerminal(c.Status))
        {
            throw new CaseException("case_closed");
        }

        if (untilDate <= context.Today)
        {
            throw new CaseException("snooze_until_must_be_future", "untilDate");
        }

        // SM-26: an escalated case has no automation to resume; snoozing it is meaningless and refused.
        if (c.AutomationDisabled)
        {
            throw new CaseException("automation_disabled");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(context.Timezone);
        var localMidnight = new DateTimeOffset(untilDate.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(untilDate.ToDateTime(TimeOnly.MinValue)));
        c.NextActionAt = localMidnight.ToUniversalTime();
        c.NextActionReason = reason ?? "snoozed";
        c.UpdatedAt = time.GetUtcNow();
        c.RowVersion++;
        await db.SaveChangesAsync(ct);
        await AddActivityAsync(c, ActivityKinds.System, actorUserId, $"Snoozed until {untilDate:yyyy-MM-dd}", new { untilDate = untilDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason }, ct);
        return c;
    }

    public async Task<CollectionCase> AssignAsync(Guid caseId, Guid? userId, Guid actorUserId, CancellationToken ct)
    {
        var c = await LockAsync(caseId, ct);
        if (userId is { } target)
        {
            var membership = await db.TenantMemberships.Where(m => m.UserId == target).Select(m => new { m.Status }).FirstOrDefaultAsync(ct);
            if (membership is null)
            {
                throw new CaseException("member_not_found", "userId");
            }

            if (membership.Status != MembershipStatus.Active)
            {
                throw new CaseException("member_not_active", "userId");
            }
        }

        c.AssignedTo = userId;
        c.UpdatedAt = time.GetUtcNow();
        c.RowVersion++;
        await db.SaveChangesAsync(ct);
        await AddActivityAsync(c, ActivityKinds.System, actorUserId, userId is null ? "Unassigned" : "Assigned", new { assignedTo = userId }, ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = actorUserId,
            EventType = "case.assigned",
            EntityType = "collection_case",
            EntityId = c.Id,
            Changes = JsonSerializer.Serialize(new { assignedTo = userId }, JsonOptions),
        }, ct);
        return c;
    }

    // ---------------------------------------------------------------------------------------
    // SM-50: the ledger calls this after every balance or lifecycle change
    // ---------------------------------------------------------------------------------------

    public async Task InvoiceChangedAsync(Guid invoiceId, Guid? actorUserId, CancellationToken ct)
    {
        var customerId = await db.Invoices.Where(i => i.Id == invoiceId).Select(i => (Guid?)i.CustomerId).FirstOrDefaultAsync(ct);
        if (customerId is null)
        {
            return;
        }

        var c = await db.Cases.FirstOrDefaultAsync(x => x.CustomerId == customerId && x.Status != CaseStatus.Resolved && x.Status != CaseStatus.Abandoned, ct);
        if (c is null)
        {
            return;   // an invoice reopening after resolution gets a new case at the next sweep (SM-05)
        }

        await LockAsync(c.Id, ct);
        var context = await ContextAsync(ct);
        await RefreshScopeAsync(c, context, actorUserId, "ledger", ct);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private async Task<CollectionCase> LockAsync(Guid caseId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM collection_cases WHERE id = {caseId} FOR UPDATE", ct);
        return await db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, ct) ?? throw new CaseException("case_not_found");
    }

    private async Task<CaseActivity> AddActivityAsync(CollectionCase c, string kind, Guid? actorUserId, string summary, object? detail, CancellationToken ct)
    {
        var activity = new CaseActivity
        {
            TenantId = c.TenantId,
            CaseId = c.Id,
            Kind = kind,
            OccurredAt = time.GetUtcNow(),
            ActorUserId = actorUserId,
            ActorKind = actorUserId is null ? ActorKinds.System : ActorKinds.User,
            Summary = summary.Length > 2000 ? summary[..2000] : summary,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail, JsonOptions),
        };
        db.CaseActivities.Add(activity);
        await db.SaveChangesAsync(ct);
        return activity;
    }

    private AuditEvent Transition(CollectionCase c, CaseStatus? from, CaseStatus to, string @event, Guid? actor, string? note, string? reasonCode = null) => new()
    {
        TenantId = db.CurrentTenantId,
        ActorUserId = actor,
        ActorKind = actor is null ? ActorKinds.System : ActorKinds.User,
        EventType = "collection_case.status_changed",
        EntityType = "collection_case",
        EntityId = c.Id,
        FromState = from?.ToString(),
        ToState = to.ToString(),
        ReasonCode = reasonCode ?? @event,
        Note = note,
        Changes = JsonSerializer.Serialize(new { @event }, JsonOptions),
    };
}
