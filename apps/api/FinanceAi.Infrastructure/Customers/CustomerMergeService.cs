using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Customers;

/// <summary>
/// DM-21 (slice 23): merging a duplicate customer into the one that stays. Two steps — a preview that counts what
/// would move and names what would conflict, and a confirmation bound to that exact preview by an HMAC token — and
/// one transaction that re-points every child row, marks the source as merged, and writes a high-severity audit event.
/// The source row is never deleted. A merge that would need to void an invoice or abandon a case is refused: those
/// are operations of their own, with their own audit trail.
/// </summary>
public sealed class CustomerMergeService(TenantDbContext db, IAuditWriter audit, TimeProvider time)
{
    public sealed record Table(string Name, int Rows);

    public sealed record Preview(Guid TargetId, Guid SourceId, IReadOnlyList<Table> Moves, IReadOnlyList<string> ConflictingInvoiceNumbers, bool BothHaveOpenCases, string ConfirmToken);

    public sealed record Refusal(string Code, IReadOnlyDictionary<string, string>? Meta = null);

    public const string SameCustomer = "same_customer";
    public const string AlreadyMerged = "already_merged";
    public const string InvoiceNumberConflict = "invoice_number_conflict";
    public const string BothHaveOpenCases = "both_have_open_cases";
    public const string ConfirmTokenStale = "confirm_token_stale";

    // A-15: one API instance. The key lives for the process; a token from an earlier process is simply stale.
    private static readonly byte[] ConfirmKey = RandomNumberGenerator.GetBytes(32);
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromMinutes(10);

    public async Task<(Preview? Preview, Refusal? Refusal, bool NotFound)> PreviewAsync(Guid targetId, Guid sourceId, CancellationToken ct)
    {
        if (targetId == sourceId) return (null, new Refusal(SameCustomer), false);
        var target = await db.Customers.FirstOrDefaultAsync(c => c.Id == targetId, ct);
        var source = await db.Customers.FirstOrDefaultAsync(c => c.Id == sourceId, ct);
        if (target is null || source is null) return (null, null, true);
        if (target.MergedIntoId is not null || source.MergedIntoId is not null) return (null, new Refusal(AlreadyMerged), false);

        var moves = await CountsAsync(sourceId, ct);
        var targetNumbers = await db.Invoices.Where(i => i.CustomerId == targetId && i.Status != InvoiceStatus.Void).Select(i => i.InvoiceNumber.ToUpper()).ToListAsync(ct);
        var conflicts = await db.Invoices.Where(i => i.CustomerId == sourceId && i.Status != InvoiceStatus.Void && targetNumbers.Contains(i.InvoiceNumber.ToUpper())).Select(i => i.InvoiceNumber).OrderBy(n => n).ToListAsync(ct);
        var openTarget = await db.Cases.AnyAsync(c => c.CustomerId == targetId && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned, ct);
        var openSource = await db.Cases.AnyAsync(c => c.CustomerId == sourceId && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Abandoned, ct);
        var token = Token(targetId, sourceId, moves, time.GetUtcNow());
        return (new Preview(targetId, sourceId, moves, conflicts, openTarget && openSource, token), null, false);
    }

    /// <summary>Confirms a preview: the token must match the current counts (nothing changed since the human looked) and be under ten minutes old.</summary>
    public async Task<(Preview? Result, Refusal? Refusal, bool NotFound)> ConfirmAsync(Guid targetId, Guid sourceId, string confirmToken, Guid userId, CancellationToken ct)
    {
        var (preview, refusal, notFound) = await PreviewAsync(targetId, sourceId, ct);
        if (preview is null) return (null, refusal, notFound);
        if (preview.ConflictingInvoiceNumbers.Count > 0) return (null, new Refusal(InvoiceNumberConflict, new Dictionary<string, string> { ["invoiceNumbers"] = string.Join(", ", preview.ConflictingInvoiceNumbers) }), false);
        if (preview.BothHaveOpenCases) return (null, new Refusal(BothHaveOpenCases), false);
        if (!TokenValid(confirmToken, targetId, sourceId, preview.Moves, time.GetUtcNow())) return (null, new Refusal(ConfirmTokenStale), false);

        // The request's tenant scope is already one transaction (DatabaseScope): every statement below commits or rolls back together.
        var now = time.GetUtcNow();
        var source = await db.Customers.FirstAsync(c => c.Id == sourceId, ct);
        var target = await db.Customers.FirstAsync(c => c.Id == targetId, ct);

        // One primary contact per customer: the target's wins.
        if (await db.CustomerContacts.AnyAsync(c => c.CustomerId == targetId && c.IsPrimary, ct))
        {
            await db.CustomerContacts.Where(c => c.CustomerId == sourceId && c.IsPrimary).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsPrimary, false), ct);
        }

        await db.CustomerContacts.Where(c => c.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CustomerId, targetId), ct);
        await db.Invoices.Where(i => i.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(i => i.CustomerId, targetId), ct);
        await db.ImportRows.Where(r => r.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(r => r.CustomerId, (Guid?)targetId), ct);
        await db.Payments.Where(p => p.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomerId, targetId), ct);
        await db.Cheques.Where(c => c.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CustomerId, targetId), ct);
        await db.CreditNotes.Where(n => n.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(n => n.CustomerId, targetId), ct);
        await db.Cases.Where(c => c.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CustomerId, targetId), ct);
        await db.Promises.Where(p => p.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CustomerId, targetId), ct);
        await db.Disputes.Where(d => d.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.CustomerId, targetId), ct);
        await db.VerificationTasks.Where(t => t.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.CustomerId, targetId), ct);
        await db.Messages.Where(m => m.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CustomerId, targetId), ct);
        await db.InboundMessages.Where(m => m.CustomerId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CustomerId, (Guid?)targetId), ct);

        source.MergedIntoId = targetId;
        source.MergedAt = now;
        source.Status = CustomerStatus.Inactive;
        source.UpdatedAt = now;
        source.UpdatedBy = userId;
        source.RowVersion++;
        target.UpdatedAt = now;
        target.UpdatedBy = userId;
        target.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditEvent
        {
            TenantId = db.CurrentTenantId,
            ActorUserId = userId,
            EventType = "customer.merged",
            EntityType = "customer",
            EntityId = targetId,
            FromState = sourceId.ToString(),
            ToState = targetId.ToString(),
            ReasonCode = "merge",
            Note = "irreversible",
            Changes = JsonSerializer.Serialize(new { sourceCustomerId = sourceId, moved = preview.Moves.ToDictionary(m => m.Name, m => m.Rows) }),
        }, ct);
        return (preview with { ConfirmToken = string.Empty }, null, false);
    }

    private async Task<IReadOnlyList<Table>> CountsAsync(Guid sourceId, CancellationToken ct) =>
    [
        new("customer_contacts", await db.CustomerContacts.CountAsync(c => c.CustomerId == sourceId, ct)),
        new("invoices", await db.Invoices.CountAsync(i => i.CustomerId == sourceId, ct)),
        new("import_rows", await db.ImportRows.CountAsync(r => r.CustomerId == sourceId, ct)),
        new("payments", await db.Payments.CountAsync(p => p.CustomerId == sourceId, ct)),
        new("cheques", await db.Cheques.CountAsync(c => c.CustomerId == sourceId, ct)),
        new("credit_notes", await db.CreditNotes.CountAsync(n => n.CustomerId == sourceId, ct)),
        new("collection_cases", await db.Cases.CountAsync(c => c.CustomerId == sourceId, ct)),
        new("promises_to_pay", await db.Promises.CountAsync(p => p.CustomerId == sourceId, ct)),
        new("disputes", await db.Disputes.CountAsync(d => d.CustomerId == sourceId, ct)),
        new("payment_verification_tasks", await db.VerificationTasks.CountAsync(t => t.CustomerId == sourceId, ct)),
        new("messages", await db.Messages.CountAsync(m => m.CustomerId == sourceId, ct)),
        new("inbound_messages", await db.InboundMessages.CountAsync(m => m.CustomerId == sourceId, ct)),
    ];

    private static string Token(Guid target, Guid source, IReadOnlyList<Table> moves, DateTimeOffset at)
    {
        var minute = at.ToUnixTimeSeconds() / 60;
        return $"{minute.ToString(CultureInfo.InvariantCulture)}.{Mac(target, source, moves, minute)}";
    }

    private static bool TokenValid(string token, Guid target, Guid source, IReadOnlyList<Table> moves, DateTimeOffset now)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)) return false;
        var age = now.ToUnixTimeSeconds() / 60 - minute;
        if (age < 0 || age > ConfirmWindow.TotalMinutes) return false;
        var expected = Encoding.ASCII.GetBytes(Mac(target, source, moves, minute));
        var given = Encoding.ASCII.GetBytes(parts[1]);
        return expected.Length == given.Length && CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static string Mac(Guid target, Guid source, IReadOnlyList<Table> moves, long minute)
    {
        var canonical = $"{target}|{source}|{minute}|{string.Join(",", moves.Select(m => $"{m.Name}={m.Rows.ToString(CultureInfo.InvariantCulture)}"))}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(ConfirmKey, Encoding.UTF8.GetBytes(canonical)));
    }
}
