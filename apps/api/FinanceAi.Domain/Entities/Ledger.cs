using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

public enum PaymentMethod { BankTransfer, Cheque, Cash, CliQ, Card, Other }

public enum PaymentStatus { Pending, Confirmed, Reversed }

public enum ChequeStatus { Received, Deposited, Cleared, Bounced, Returned, Cancelled }

public enum AllocationMethod { Manual, AutoExactMatch, ProposedFifo }

public enum CreditNoteStatus { Active, Void }

public enum WriteOffStatus { Proposed, Approved, Rejected, Reversed }

/// <summary>FIN-12: settlement is a pure function of the balance. Nothing may set it (SM-10).</summary>
public enum Settlement { Unpaid, PartiallyPaid, Paid }

/// <summary>FIN-40: the closed set of credit-note reasons.</summary>
public static class CreditNoteReasons
{
    public const string DisputeResolution = "dispute_resolution";
    public const string AgreedDiscount = "agreed_discount";
    public const string GoodsReturned = "goods_returned";
    public const string ServiceCredit = "service_credit";
    public const string BillingError = "billing_error";
    public const string BankCharges = "bank_charges";
    public const string RoundingAdjustment = "rounding_adjustment";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        [DisputeResolution, AgreedDiscount, GoodsReturned, ServiceCredit, BillingError, BankCharges, RoundingAdjustment, Other];
}

/// <summary>Money received. A payment carries unapplied cash until allocated (FIN-16, E4).</summary>
public sealed class Payment : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public PaymentMethod Method { get; set; }
    public DateOnly ReceivedDate { get; set; }

    /// <summary>FIN-57: what as-of aging reads. Defaults to the received date.</summary>
    public DateOnly EffectiveDate { get; set; }

    public string? Reference { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Confirmed;
    public Guid? ChequeId { get; set; }
    public string? Notes { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? RequestHash { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;
}

/// <summary>A-05, SM-51: a promise until it clears. Only <c>Cleared</c> creates money.</summary>
public sealed class Cheque : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public required string ChequeNumber { get; set; }
    public string? BankName { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateOnly ChequeDate { get; set; }
    public DateOnly ReceivedDate { get; set; }
    public ChequeStatus Status { get; set; } = ChequeStatus.Received;
    public string? BouncedReason { get; set; }
    public DateOnly? ClearedDate { get; set; }
    public Guid? PtpId { get; set; }
    public Guid? PaymentId { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long RowVersion { get; set; } = 1;

    public bool IsPostDated => this.ChequeDate > this.ReceivedDate;

    /// <summary>SM-51, the legal moves. Anything else is an invalid transition.</summary>
    public static ChequeStatus? Next(ChequeStatus from, string @event) => (from, @event) switch
    {
        (ChequeStatus.Received, "deposit") => ChequeStatus.Deposited,
        (ChequeStatus.Received, "cancel") => ChequeStatus.Cancelled,
        (ChequeStatus.Received, "return") => ChequeStatus.Returned,
        (ChequeStatus.Deposited, "clear") => ChequeStatus.Cleared,
        (ChequeStatus.Deposited, "bounce") => ChequeStatus.Bounced,
        (ChequeStatus.Cleared, "bounce") => ChequeStatus.Bounced,   // the bank can reverse a cleared cheque
        _ => null,
    };
}

/// <summary>FIN-22, FIN-23: one line of money moved to one invoice, reversible by a compensating row.</summary>
public sealed class PaymentAllocation : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ReversalOfId { get; set; }
    public string? ReversalReason { get; set; }
    public Guid? AllocatedBy { get; set; }
    public AllocationMethod Method { get; set; } = AllocationMethod.Manual;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>FIN-29: the customer paid net of tax it remitted to the authority. Entered, never computed.</summary>
public sealed class WithholdingDeduction : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid? PaymentId { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal RatePct { get; set; }
    public decimal WithheldAmount { get; set; }
    public required string Currency { get; set; }
    public string? CertificateReference { get; set; }
    public bool CertificateReceived { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ReversalOfId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public sealed class CreditNote : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public string? NoteNumber { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateOnly IssueDate { get; set; }
    public required string ReasonCode { get; set; }
    public Guid? DisputeId { get; set; }
    public CreditNoteStatus Status { get; set; } = CreditNoteStatus.Active;
    public Guid? ApprovedBy { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class CreditNoteApplication : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CreditNoteId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? ReversalOfId { get; set; }
    public string? ReversalReason { get; set; }
    public Guid? AppliedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>FIN-31..34: two humans, the computed balance, a register of its own.</summary>
public sealed class WriteOff : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string ReasonCode { get; set; }
    public string? Note { get; set; }
    public WriteOffStatus Status { get; set; } = WriteOffStatus.Proposed;
    public Guid ProposedBy { get; set; }
    public DateTimeOffset ProposedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ApprovedBy { get; set; }
    public bool SelfApproved { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? RejectedBy { get; set; }
    public Guid? ReversedBy { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }
    public long RowVersion { get; set; } = 1;
}

/// <summary>The pure functions of doc 03 §2, shared by the ledger, the property test and the reconciliation check.</summary>
public static class LedgerRules
{
    /// <summary>Doc 03 §2.1 as amended by slice 3b (D-1): withholding is the fourth instrument.</summary>
    public static decimal OpenBalance(decimal total, decimal allocatedPayments, decimal appliedCredits, decimal writtenOff, decimal withheld) =>
        InvoiceBalance.Derive(total, allocatedPayments, appliedCredits, writtenOff + withheld);

    /// <summary>FIN-12, FIN-13: exact zero, no epsilon.</summary>
    public static Settlement SettlementOf(decimal openBalance, decimal total) =>
        openBalance == 0m ? Settlement.Paid
        : openBalance == total ? Settlement.Unpaid
        : Settlement.PartiallyPaid;

    /// <summary>
    /// SM-12: the only system-driven invoice transitions, both pure consequences of the balance
    /// crossing zero. Returns the new status, or null when nothing changes.
    /// </summary>
    public static InvoiceStatus? TransitionFor(InvoiceStatus current, decimal openBalance) => (current, openBalance) switch
    {
        (InvoiceStatus.Open, 0m) => InvoiceStatus.Settled,        // I4
        (InvoiceStatus.Settled, > 0m) => InvoiceStatus.Open,      // I7
        _ => null,
    };

    /// <summary>
    /// FIN-25: oldest due date first, then invoice number, exact remainders, never beyond a balance.
    /// A proposal, never an action (FIN-26). Callers supply candidates already restricted to the
    /// same customer and currency and already excluding disputed/escalated invoices.
    /// </summary>
    public static IReadOnlyList<(Guid InvoiceId, decimal Amount)> ProposeFifo(
        decimal available, IEnumerable<(Guid InvoiceId, DateOnly DueDate, string InvoiceNumber, decimal OpenBalance)> candidates)
    {
        var lines = new List<(Guid, decimal)>();
        var remaining = available;

        foreach (var candidate in candidates.Where(c => c.OpenBalance > 0m).OrderBy(c => c.DueDate).ThenBy(c => c.InvoiceNumber, StringComparer.Ordinal))
        {
            if (remaining <= 0m)
            {
                break;
            }

            var amount = Math.Min(remaining, candidate.OpenBalance);
            lines.Add((candidate.InvoiceId, amount));
            remaining -= amount;
        }

        return lines;
    }
}
