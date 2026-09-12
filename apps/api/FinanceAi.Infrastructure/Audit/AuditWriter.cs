using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Audit;

/// <summary>
/// Writes the immutable record required by SEC-50. Every auth event in this slice goes through
/// here; every state transition and money mutation in later slices must too.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>
    /// Appends many events to one tenant's chain under a single lock, with one save. An import that
    /// accepts thousands of invoices writes thousands of I2 transitions (INV-12); one round trip per
    /// row would be the slowest thing in the product.
    /// </summary>
    Task WriteManyAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken ct = default);
}

public sealed class AuditWriter(TenantDbContext db) : IAuditWriter
{
    /// <summary>Arbitrary but fixed class id for this advisory-lock namespace.</summary>
    internal const int LockNamespace = 0x41554449;

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        // The tenant is always explicit here rather than inherited from the query filter, because
        // the audit writer must work identically in a request, in an identity flow that has just
        // established a tenant, and in a background job that declares one (SEC-22). Passing the
        // wrong tenant is not a leak: the RLS policy on audit_events has no platform clause, so the
        // insert fails unless it matches app.tenant_id.
        var tenantId = auditEvent.TenantId == Guid.Empty ? db.CurrentTenantId : auditEvent.TenantId;
        if (tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("An audit event must name the tenant it belongs to (SEC-52).");
        }

        auditEvent.TenantId = tenantId;

        // Serialize chain appends per tenant. Two concurrent writers would otherwise read the same
        // prev_hash and produce two rows claiming the same predecessor — a chain that forks is a
        // chain that cannot be verified. Transaction-scoped, so it is released on commit.
        var lockKey = tenantId.ToString();
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({LockNamespace}, hashtext({lockKey}))", ct);

        var previousHash = await db.AuditEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.Id)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync(ct);

        auditEvent.OccurredAt = AuditHash.TruncateToStorage(auditEvent.OccurredAt);
        auditEvent.PrevHash = previousHash;
        auditEvent.Hash = AuditHash.Compute(auditEvent, previousHash);

        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }

    public async Task WriteManyAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);
        if (auditEvents.Count == 0)
        {
            return;
        }

        var tenantId = auditEvents[0].TenantId == Guid.Empty ? db.CurrentTenantId : auditEvents[0].TenantId;
        if (tenantId == Guid.Empty || auditEvents.Any(e => e.TenantId != Guid.Empty && e.TenantId != tenantId))
        {
            throw new InvalidOperationException("A batch of audit events must belong to exactly one tenant (SEC-52).");
        }

        var lockKey = tenantId.ToString();
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({LockNamespace}, hashtext({lockKey}))", ct);

        var previousHash = await db.AuditEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.Id)
            .Select(e => e.Hash)
            .FirstOrDefaultAsync(ct);

        foreach (var auditEvent in auditEvents)
        {
            auditEvent.TenantId = tenantId;
            auditEvent.OccurredAt = AuditHash.TruncateToStorage(auditEvent.OccurredAt);
            auditEvent.PrevHash = previousHash;
            auditEvent.Hash = AuditHash.Compute(auditEvent, previousHash);
            previousHash = auditEvent.Hash;
        }

        // Identity ids are assigned in insertion order within one INSERT ... so the chain order and
        // the id order agree, which is what the verifier walks.
        db.AuditEvents.AddRange(auditEvents);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Recomputes a tenant's chain. Used by AC-37 and by the daily verification job (SEC-53).</summary>
public sealed class AuditChainVerifier(TenantDbContext db)
{
    public sealed record Result(bool IsIntact, long? FirstBrokenId, int RowsChecked);

    public async Task<Result> VerifyAsync(Guid? tenantId = null, CancellationToken ct = default)
    {
        var scope = tenantId ?? db.CurrentTenantId;
        var events = await db.AuditEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == scope)
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        string? previousHash = null;
        foreach (var e in events)
        {
            if (e.PrevHash != previousHash || e.Hash != AuditHash.Compute(e, previousHash))
            {
                return new Result(false, e.Id, events.Count);
            }

            previousHash = e.Hash;
        }

        return new Result(true, null, events.Count);
    }
}
