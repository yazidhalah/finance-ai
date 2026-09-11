using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>Doc 02 §1.2. The stored lifecycle; settlement, overdue-ness and dispute are derived (SM-10).</summary>
public enum InvoiceStatus { Imported, Open, Settled, WrittenOff, Void }

public enum InvoiceSource { Import, Manual, Api }

/// <summary>
/// A receivable (doc 04 §5.2). Every amount is <c>decimal</c> at scale 3; no arithmetic happens on
/// this type except in <see cref="InvoiceBalance"/>, the one function allowed to write
/// <see cref="BalanceCache"/> (FIN-10).
/// </summary>
public sealed class Invoice : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public required string InvoiceNumber { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Imported;
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public required string Currency { get; set; }
    public decimal NetAmount { get; set; }

    /// <summary>Imported, never computed (A-03).</summary>
    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    /// <summary>FIN-10: a cache of the derived balance, written only by <see cref="InvoiceBalance.Recompute"/>.</summary>
    public decimal BalanceCache { get; private set; }

    /// <summary>FIN-06: frozen on the document at import. 1 for base-currency invoices.</summary>
    public decimal FxRateToBase { get; set; } = 1;

    public required string BaseCurrency { get; set; }
    public string? PoReference { get; set; }
    public string? Notes { get; set; }
    public InvoiceSource Source { get; set; } = InvoiceSource.Import;
    public Guid? ImportBatchId { get; set; }
    public string? ExternalId { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedBy { get; set; }
    public long RowVersion { get; set; } = 1;

    internal void SetBalance(decimal balance) => this.BalanceCache = balance;
}

/// <summary>
/// The single place that derives and writes an invoice balance (FIN-10, INV-01). Slice 3b extends
/// the inputs with allocations, credit applications and write-offs; until then there are none,
/// and the balance is the total. Rounding: none — every input is already at scale 3.
/// </summary>
public static class InvoiceBalance
{
    public static decimal Derive(decimal totalAmount, decimal allocatedPayments = 0m, decimal appliedCredits = 0m, decimal writtenOff = 0m)
    {
        var balance = totalAmount - allocatedPayments - appliedCredits - writtenOff;

        if (balance < 0m || balance > totalAmount)
        {
            throw new InvalidOperationException(
                $"INV-01 violated: derived balance {balance} is outside [0, {totalAmount}].");
        }

        return balance;
    }

    public static void Recompute(Invoice invoice, decimal allocatedPayments = 0m, decimal appliedCredits = 0m, decimal writtenOff = 0m)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        invoice.SetBalance(Derive(invoice.TotalAmount, allocatedPayments, appliedCredits, writtenOff));
    }
}

/// <summary>
/// FIN-04: amounts in different currencies are never summed. This accumulator keeps one running
/// total per currency and throws — never coerces — if asked for a single figure across them.
/// </summary>
public sealed class MoneyTotals
{
    private readonly SortedDictionary<string, (decimal Amount, int Count)> totals = new(StringComparer.Ordinal);

    public void Add(string currency, decimal amount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var (existingAmount, existingCount) = this.totals.TryGetValue(currency, out var existing) ? existing : (0m, 0);
        this.totals[currency] = (existingAmount + amount, existingCount + 1);
    }

    public IReadOnlyList<(string Currency, decimal Amount, int Count)> PerCurrency =>
        this.totals.Select(kv => (kv.Key, kv.Value.Amount, kv.Value.Count)).ToList();

    /// <summary>Only meaningful when exactly one currency is present. Anything else is FIN-04's forbidden sum.</summary>
    public decimal Single()
    {
        if (this.totals.Count != 1)
        {
            throw new InvalidOperationException(
                $"FIN-04: cannot produce a single total across {this.totals.Count} currencies ({string.Join(", ", this.totals.Keys)}).");
        }

        return this.totals.Values.First().Amount;
    }
}
