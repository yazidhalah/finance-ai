using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ops;

/// <summary>
/// The invariant job of doc 03 §7(b): every check of the table, as one SQL statement each, over the current
/// tenant's rows, plus the SEC-53 audit-chain verification. It reads, records and alerts; it never writes to a
/// business table (slice 15 D-6). Money comparisons happen in the database between numeric(19,3) columns —
/// nothing is re-added here (FIN-01).
/// </summary>
public sealed class InvariantService(TenantDbContext db, IAuditWriter audit, AuditChainVerifier chainVerifier, AgingService aging, AlertService alerts, TimeProvider time)
{
    private sealed record CheckRow(long Violations, string[] Samples);

    /// <summary>The checks, in the order of doc 03 §7. INV-05, INV-11 and INV-12 are structural or scenario-scoped and live in the test suites (slice 15 S1).</summary>
    private static readonly (string Id, string Description, string Sql)[] SqlChecks =
    [
        ("INV-01", "open_balance within [0, total] for every non-void invoice",
            "SELECT id FROM invoices WHERE tenant_id = @t AND status <> 'Void' AND (balance_cache < 0 OR balance_cache > total_amount)"),
        ("INV-09", "balance_cache equals the derived open balance (FIN-10)",
            """
            SELECT i.id FROM invoices i
            WHERE i.tenant_id = @t AND i.status <> 'Void' AND i.balance_cache <> i.total_amount
              - coalesce((SELECT sum(a.amount) FROM payment_allocations a JOIN payments p ON p.tenant_id = a.tenant_id AND p.id = a.payment_id WHERE a.tenant_id = i.tenant_id AND a.invoice_id = i.id AND a.is_active AND p.status = 'Confirmed'), 0)
              - coalesce((SELECT sum(c.amount) FROM credit_note_applications c JOIN credit_notes n ON n.tenant_id = c.tenant_id AND n.id = c.credit_note_id WHERE c.tenant_id = i.tenant_id AND c.invoice_id = i.id AND c.is_active AND n.status = 'Active'), 0)
              - coalesce((SELECT sum(w.amount) FROM write_offs w WHERE w.tenant_id = i.tenant_id AND w.invoice_id = i.id AND w.status = 'Approved'), 0)
              - coalesce((SELECT sum(h.withheld_amount) FROM withholding_deductions h WHERE h.tenant_id = i.tenant_id AND h.invoice_id = i.id AND h.is_active), 0)
            """),
        ("INV-10", "no Settled invoice with a balance, no Open invoice at zero (SM-12)",
            "SELECT id FROM invoices WHERE tenant_id = @t AND ((status = 'Settled' AND balance_cache > 0) OR (status = 'Open' AND balance_cache = 0))"),
        ("INV-02", "Σ active allocations ≤ payment amount; allocation, payment and invoice currencies agree",
            """
            SELECT p.id FROM payments p
            WHERE p.tenant_id = @t AND (
              p.amount < coalesce((SELECT sum(a.amount) FROM payment_allocations a WHERE a.tenant_id = p.tenant_id AND a.payment_id = p.id AND a.is_active), 0)
              OR EXISTS (SELECT 1 FROM payment_allocations a JOIN invoices i ON i.tenant_id = a.tenant_id AND i.id = a.invoice_id
                         WHERE a.tenant_id = p.tenant_id AND a.payment_id = p.id AND a.is_active AND (a.currency <> p.currency OR i.currency <> p.currency)))
            """),
        ("INV-03", "Σ active applications ≤ credit note amount",
            """
            SELECT n.id FROM credit_notes n
            WHERE n.tenant_id = @t AND n.amount < coalesce((SELECT sum(a.amount) FROM credit_note_applications a WHERE a.tenant_id = n.tenant_id AND a.credit_note_id = n.id AND a.is_active), 0)
            """),
        ("INV-06", "at most one non-terminal collection case per customer (SM-21)",
            """
            SELECT customer_id AS id FROM collection_cases
            WHERE tenant_id = @t AND status NOT IN ('Resolved','Abandoned')
            GROUP BY customer_id HAVING count(*) > 1
            """),
        ("INV-07", "at most one Active promise per invoice (SM-36)",
            """
            SELECT pi.invoice_id AS id FROM ptp_invoices pi JOIN promises_to_pay p ON p.tenant_id = pi.tenant_id AND p.id = pi.ptp_id
            WHERE pi.tenant_id = @t AND p.status = 'Active'
            GROUP BY pi.invoice_id HAVING count(*) > 1
            """),
        ("INV-13", "nothing AI-sourced sits in a state that needed a human without that human's id",
            """
            SELECT id FROM promises_to_pay WHERE tenant_id = @t AND status = 'Active' AND confirmed_by IS NULL
            UNION ALL
            SELECT id FROM messages WHERE tenant_id = @t AND status IN ('Queued','Sent','Delivered') AND approved_by IS NULL
            UNION ALL
            SELECT id FROM disputes WHERE tenant_id = @t AND source = 'ai_suggested' AND status <> 'Open' AND status <> 'Cancelled' AND raised_by IS NULL
            """),
    ];

    /// <summary>Runs every check and records the run. Raises the alerts the outcome warrants. Returns the stored run.</summary>
    public async Task<InvariantRun> RunAsync(string trigger, Guid? actorUserId, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var tenantId = db.CurrentTenantId;
        var results = new List<InvariantCheckResult>(SqlChecks.Length + 3);

        foreach (var (id, description, sql) in SqlChecks)
        {
            // The statement is a compile-time constant from the table above; only the tenant id is a parameter.
            var statement = "WITH v AS (" + sql + ") " +
                "SELECT (SELECT count(*) FROM v) AS \"Violations\", " +
                "coalesce((SELECT array_agg(id::text) FROM (SELECT id FROM v LIMIT 5) s), ARRAY[]::text[]) AS \"Samples\"";
#pragma warning disable EF1002 // constant SQL, parameterised tenant id
            var row = await db.Database.SqlQueryRaw<CheckRow>(statement, new Npgsql.NpgsqlParameter("t", tenantId)).FirstAsync(ct);
#pragma warning restore EF1002
            results.Add(new InvariantCheckResult(id, description, row.Violations, row.Samples));
        }

        // INV-04: settlement is derived, never stored (doc 04 §5.2), so it holds by construction; recorded so the list is the doc's list.
        results.Add(new InvariantCheckResult("INV-04", "settlement status is the pure function of the balance (derived, not stored)", 0, []));

        results.Add(await AgingMatchesLedgerAsync(tenantId, ct));
        results.Add(await AuditChainAsync(ct));

        var violations = results.Where(r => r.Violations > 0).ToList();
        var run = new InvariantRun
        {
            TenantId = tenantId,
            RanAt = time.GetUtcNow(),
            Trigger = trigger,
            ActorUserId = actorUserId,
            Status = violations.Count == 0 ? InvariantRunStatus.Ok : InvariantRunStatus.Violations,
            ChecksJson = JsonSerializer.Serialize(results, AlertService.JsonOptions),
            DurationMs = (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue),
        };
        db.InvariantRuns.Add(run);
        await db.SaveChangesAsync(ct);

        if (trigger == InvariantRunTrigger.Manual)
        {
            await audit.WriteAsync(new AuditEvent
            {
                TenantId = tenantId,
                ActorUserId = actorUserId,
                ActorKind = actorUserId is null ? ActorKinds.System : ActorKinds.User,
                EventType = "invariants.run",
                EntityType = "invariant_run",
                EntityId = run.Id,
                ToState = run.Status,
                Changes = JsonSerializer.Serialize(new { violations = violations.Select(v => new { v.Id, v.Violations }) }, AlertService.JsonOptions),
            }, ct);
        }

        var chain = violations.FirstOrDefault(v => v.Id == "SEC-53");
        if (chain is not null)
        {
            await alerts.RaiseAsync(AlertKinds.AuditChainBreak, AlertSeverity.Critical,
                $"The audit chain is broken at event {chain.Samples.FirstOrDefault() ?? "?"}.",
                new { runId = run.Id, firstBrokenId = chain.Samples.FirstOrDefault() }, ct);
        }

        var business = violations.Where(v => v.Id != "SEC-53").ToList();
        if (business.Count > 0)
        {
            await alerts.RaiseAsync(AlertKinds.InvariantViolation, AlertSeverity.Critical,
                $"{business.Count} invariant(s) violated: {string.Join(", ", business.Select(v => $"{v.Id} ×{v.Violations}"))}.",
                new { runId = run.Id, checks = business }, ct);
        }

        return run;
    }

    /// <summary>INV-08: the aging report's total per currency equals the ledger's open balance per currency, today.</summary>
    private async Task<InvariantCheckResult> AgingMatchesLedgerAsync(Guid tenantId, CancellationToken ct)
    {
        var report = await aging.ReportAsync(null, null, null, null, byCustomer: false, ct);
        var ledger = await db.Invoices
            .Where(i => i.Status == InvoiceStatus.Open && i.IssueDate <= report.AsOf)
            .GroupBy(i => i.Currency)
            .Select(g => new { Currency = g.Key, Total = g.Sum(i => i.BalanceCache) })
            .ToListAsync(ct);

        var mismatched = new List<string>();
        foreach (var currency in ledger.Select(l => l.Currency).Union(report.Currencies.Select(c => c.Currency)).Order(StringComparer.Ordinal))
        {
            var fromLedger = ledger.FirstOrDefault(l => l.Currency == currency)?.Total ?? 0m;
            var fromAging = report.Currencies.FirstOrDefault(c => c.Currency == currency)?.Total ?? 0m;
            if (fromLedger != fromAging)
            {
                mismatched.Add(currency);
            }
        }

        return new InvariantCheckResult("INV-08", "aging buckets sum to the open AR balance per currency", mismatched.Count, mismatched.Take(5).ToList());
    }

    private async Task<InvariantCheckResult> AuditChainAsync(CancellationToken ct)
    {
        var result = await chainVerifier.VerifyAsync(null, ct);
        return new InvariantCheckResult("SEC-53", $"the audit chain is intact ({result.RowsChecked.ToString(CultureInfo.InvariantCulture)} events)",
            result.IsIntact ? 0 : 1, result.IsIntact ? [] : [result.FirstBrokenId!.Value.ToString(CultureInfo.InvariantCulture)]);
    }

    public Task<InvariantRun?> LatestAsync(CancellationToken ct) =>
        db.InvariantRuns.OrderByDescending(r => r.RanAt).ThenByDescending(r => r.Id).FirstOrDefaultAsync(ct);
}
