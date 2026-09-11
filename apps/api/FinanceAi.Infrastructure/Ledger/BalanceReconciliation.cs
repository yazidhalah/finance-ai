using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ledger;

/// <summary>
/// FIN-10 (b) / INV-09 / T-45: asserts <c>balance_cache == derived</c> and INV-10 for every invoice
/// of the current tenant. The nightly job that runs this per tenant (SEC-22) is slice 5's; the
/// check itself is here so tests can call it after every scenario, and so a mismatch has one
/// definition.
/// </summary>
public sealed class BalanceReconciliation(TenantDbContext db, LedgerService ledger)
{
    public sealed record Mismatch(Guid InvoiceId, string InvoiceNumber, decimal Cached, decimal Derived, InvoiceStatus Status, string Rule);

    public async Task<IReadOnlyList<Mismatch>> RunAsync(CancellationToken ct = default)
    {
        var mismatches = new List<Mismatch>();

        foreach (var invoice in await db.Invoices.Where(i => i.Status != InvoiceStatus.Void).ToListAsync(ct))
        {
            var inputs = await ledger.InputsAsync(invoice.Id, ct);
            var derived = LedgerRules.OpenBalance(invoice.TotalAmount, inputs.AllocatedPayments, inputs.AppliedCredits, inputs.WrittenOff, inputs.Withheld);

            if (derived != invoice.BalanceCache)
            {
                mismatches.Add(new Mismatch(invoice.Id, invoice.InvoiceNumber, invoice.BalanceCache, derived, invoice.Status, "INV-09"));
            }

            // INV-10: a Settled invoice owes nothing; an Open one owes something.
            if ((invoice.Status == InvoiceStatus.Settled && derived > 0m) || (invoice.Status == InvoiceStatus.Open && derived == 0m))
            {
                mismatches.Add(new Mismatch(invoice.Id, invoice.InvoiceNumber, invoice.BalanceCache, derived, invoice.Status, "INV-10"));
            }
        }

        return mismatches;
    }
}
