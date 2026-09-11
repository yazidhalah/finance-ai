using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ledger;

/// <summary>
/// FIN-10 (b) / INV-09 / T-45: asserts <c>balance_cache == derived</c> and INV-10 for every invoice
/// of the current tenant, in one set-based query over <c>v_invoice_balances</c> (DM-30, slice 4).
/// The nightly job that runs this per tenant (SEC-22) comes with the first background job; the
/// check itself is here so tests can call it after every scenario, the reconciliation endpoint can
/// expose it, and a mismatch has one definition.
/// </summary>
public sealed class BalanceReconciliation(TenantDbContext db)
{
    public sealed record Mismatch(Guid InvoiceId, string InvoiceNumber, string Currency, decimal Cached, decimal Derived, InvoiceStatus Status, string Rule);

    public sealed record Result(int InvoicesChecked, IReadOnlyList<Mismatch> Mismatches);

    public async Task<IReadOnlyList<Mismatch>> RunAsync(CancellationToken ct = default) => (await CheckAsync(ct)).Mismatches;

    public async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        var tenantId = db.CurrentTenantId;
        var rows = await db.Database.SqlQuery<Row>(
            $"""
             SELECT i.id AS "InvoiceId", i.invoice_number AS "InvoiceNumber", i.currency AS "Currency", i.status AS "Status",
                    v.balance_cache AS "Cached", v.open_balance AS "Derived"
             FROM invoices i
             JOIN v_invoice_balances v ON v.tenant_id = i.tenant_id AND v.invoice_id = i.id
             WHERE i.tenant_id = {tenantId} AND i.status <> 'Void'
             """).ToListAsync(ct);

        var mismatches = new List<Mismatch>();
        foreach (var row in rows)
        {
            var status = Enum.Parse<InvoiceStatus>(row.Status);
            if (row.Derived != row.Cached)
            {
                mismatches.Add(new Mismatch(row.InvoiceId, row.InvoiceNumber, row.Currency, row.Cached, row.Derived, status, "INV-09"));
            }

            // INV-10: a Settled invoice owes nothing; an Open one owes something.
            if ((status == InvoiceStatus.Settled && row.Derived > 0m) || (status == InvoiceStatus.Open && row.Derived == 0m))
            {
                mismatches.Add(new Mismatch(row.InvoiceId, row.InvoiceNumber, row.Currency, row.Cached, row.Derived, status, "INV-10"));
            }
        }

        return new Result(rows.Count, mismatches);
    }

    private sealed record Row(Guid InvoiceId, string InvoiceNumber, string Currency, string Status, decimal Cached, decimal Derived);
}
