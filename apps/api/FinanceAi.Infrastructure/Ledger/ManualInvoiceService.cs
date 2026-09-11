using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Cases;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Ledger;

/// <summary>
/// Slice 23: the manual single invoice of A-11 ("a convenience for gap-filling") and the three editable fields of
/// doc 05. Everything an import checks is checked here the same way — reconciliation, currency and fx (FIN-06), the
/// duplicate rule — and the invoice enters AR by the same I2 transition and the same balance function (FIN-10).
/// Amounts are never edited: that is a credit note or a void-and-reimport.
/// </summary>
public sealed class ManualInvoiceService(TenantDbContext db, IAuditWriter audit, TimeProvider time, ICaseHooks cases)
{
    public sealed record Draft(Guid CustomerId, string InvoiceNumber, DateOnly IssueDate, DateOnly? DueDate, string Currency,
        decimal? NetAmount, decimal? TaxAmount, decimal? TotalAmount, decimal? FxRateToBase, string? PoReference, string? Notes, string? ExternalId);

    public sealed record Edit(DateOnly? DueDate, bool PoReferenceSet, string? PoReference, bool NotesSet, string? Notes);

    public sealed record Refusal(string Code, string? Field, string? Detail);

    public async Task<(Invoice? Invoice, Refusal? Refusal)> CreateAsync(Draft d, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(d);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == d.CustomerId, ct);
        if (customer is null) return (null, new Refusal(ImportErrorCodes.CustomerNotFound, "customerId", null));
        var tenant = await db.Tenants.Select(t => new { t.BaseCurrency }).FirstAsync(ct);

        // Reconciliation, not repair — the import's three cases (ImportService, slice 3a).
        decimal net, tax = d.TaxAmount ?? 0m, total;
        if (d.NetAmount is null && d.TotalAmount is null) return (null, new Refusal(ImportErrorCodes.MissingAmount, "netAmount", null));
        if (d.NetAmount is not null && d.TotalAmount is not null)
        {
            if (d.NetAmount.Value + tax != d.TotalAmount.Value) return (null, new Refusal(ImportErrorCodes.TotalsDoNotReconcile, "totalAmount", $"net {d.NetAmount.Value:F3} + tax {tax:F3} ≠ total {d.TotalAmount.Value:F3}"));
            net = d.NetAmount.Value; total = d.TotalAmount.Value;
        }
        else if (d.NetAmount is not null) { net = d.NetAmount.Value; total = net + tax; }
        else
        {
            total = d.TotalAmount!.Value; net = total - tax;
            if (net < 0m) return (null, new Refusal(ImportErrorCodes.TotalsDoNotReconcile, "taxAmount", "tax exceeds total"));
        }

        if (net < 0m || tax < 0m || total < 0m) return (null, new Refusal(ImportErrorCodes.NegativeAmount, "totalAmount", null));

        var dueDate = d.DueDate ?? d.IssueDate.AddDays(customer.PaymentTermsDays);   // FIN-72
        if (dueDate < d.IssueDate) return (null, new Refusal(ImportErrorCodes.DueBeforeIssue, "dueDate", null));

        decimal fx = 1m;
        if (!string.Equals(d.Currency, tenant.BaseCurrency, StringComparison.Ordinal))
        {
            if (d.FxRateToBase is not > 0m) return (null, new Refusal(ImportErrorCodes.MissingFxRate, "fxRateToBase", null));
            fx = d.FxRateToBase.Value;
        }

        var number = d.InvoiceNumber.Trim();
        if (await db.Invoices.AnyAsync(i => i.CustomerId == customer.Id && i.Status != InvoiceStatus.Void && i.InvoiceNumber.ToUpper() == number.ToUpper(), ct))
        {
            return (null, new Refusal(ImportErrorCodes.DuplicateInvoiceNumber, "invoiceNumber", number));
        }

        var now = time.GetUtcNow();
        var invoice = new Invoice
        {
            TenantId = db.CurrentTenantId,
            CustomerId = customer.Id,
            InvoiceNumber = number,
            IssueDate = d.IssueDate,
            DueDate = dueDate,
            Currency = d.Currency,
            NetAmount = net,
            TaxAmount = tax,
            TotalAmount = total,
            FxRateToBase = fx,
            BaseCurrency = tenant.BaseCurrency,
            PoReference = d.PoReference,
            Notes = d.Notes,
            ExternalId = d.ExternalId,
            Source = InvoiceSource.Manual,
            CreatedAt = now,
            CreatedBy = userId,
            UpdatedAt = now,
            UpdatedBy = userId,
        };
        InvoiceBalance.Recompute(invoice);   // FIN-10: the one function that writes the cache
        invoice.Status = InvoiceStatus.Open;  // I2, as the import commit does
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = userId,
            EventType = "invoice.status_changed",
            EntityType = "invoice",
            EntityId = invoice.Id,
            FromState = nameof(InvoiceStatus.Imported),
            ToState = nameof(InvoiceStatus.Open),
            ReasonCode = "manual_entry",
            Changes = JsonSerializer.Serialize(new { invoiceNumber = number, customerId = customer.Id, currency = d.Currency, totalAmount = total.ToString("F3", CultureInfo.InvariantCulture) }),
        }, ct);
        await cases.InvoiceChangedAsync(invoice.Id, userId, ct);
        return (invoice, null);
    }

    public async Task<(Invoice? Invoice, Refusal? Refusal)> EditAsync(Guid id, Edit edit, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return (null, new Refusal("not_found", null, null));
        if (invoice.Status == InvoiceStatus.Void) return (null, new Refusal("invoice_void", null, null));
        if (edit.DueDate is { } due && due < invoice.IssueDate) return (null, new Refusal(ImportErrorCodes.DueBeforeIssue, "dueDate", null));

        var changes = new Dictionary<string, object>();
        if (edit.DueDate is { } newDue && newDue != invoice.DueDate)
        {
            changes["dueDate"] = new { old = invoice.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), @new = newDue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            invoice.DueDate = newDue;
        }

        if (edit.PoReferenceSet && edit.PoReference != invoice.PoReference)
        {
            changes["poReference"] = new { old = invoice.PoReference, @new = edit.PoReference };
            invoice.PoReference = edit.PoReference;
        }

        if (edit.NotesSet && edit.Notes != invoice.Notes)
        {
            changes["notes"] = new { old = invoice.Notes, @new = edit.Notes };
            invoice.Notes = edit.Notes;
        }

        if (changes.Count == 0) return (invoice, null);
        invoice.UpdatedAt = time.GetUtcNow();
        invoice.UpdatedBy = userId;
        invoice.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = userId,
            EventType = "invoice.updated",
            EntityType = "invoice",
            EntityId = invoice.Id,
            Changes = JsonSerializer.Serialize(changes),
        }, ct);
        if (changes.ContainsKey("dueDate")) await cases.InvoiceChangedAsync(invoice.Id, userId, ct);   // overdue-ness may have changed
        return (invoice, null);
    }
}
