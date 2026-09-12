using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using Npgsql;

namespace FinanceAi.Infrastructure.Audit;

/// <summary>
/// SEC-94 (slice 31): a restore is a privileged operation that can resurrect deleted data, so it is recorded where
/// tenants can see it — an <c>instance.restored</c> event appended to every tenant's audit chain, hashed like any
/// other event (SEC-53), naming the dump file, its digest, the reason and who ran it. Run by the operator after a
/// real restore (`FinanceAi.Migrator record-restore`); the drill restores into a scratch database and records nothing.
/// </summary>
public static class RestoreRecord
{
    public sealed record Outcome(int TenantsRecorded, string Sha256);

    public static async Task<Outcome> RunAsync(string connectionString, string dumpPath, string reason, string performedBy, DateTimeOffset restoredAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
        if (!File.Exists(dumpPath)) throw new FileNotFoundException("The dump file must exist so its digest can be recorded.", dumpPath);

        string sha256;
        await using (var stream = File.OpenRead(dumpPath))
        {
            sha256 = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var tenants = new List<Guid>();
        await using (var tx = await connection.BeginTransactionAsync(ct))
        {
            await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', '', true), set_config('app.user_id', '', true), set_config('app.platform_scope', 'on', true)", connection, tx)) await scope.ExecuteNonQueryAsync(ct);
            await using (var list = new NpgsqlCommand("SELECT id FROM tenants ORDER BY created_at", connection, tx))
            await using (var reader = await list.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) tenants.Add(reader.GetGuid(0));
            }

            await tx.RollbackAsync(ct);
        }

        var changes = JsonSerializer.Serialize(new { dumpFile = Path.GetFileName(dumpPath), sha256, reason, performedBy, restoredAt = restoredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) });
        foreach (var tenantId in tenants)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.user_id', '', true), set_config('app.platform_scope', 'off', true)", connection, tx))
            {
                scope.Parameters.AddWithValue("t", tenantId.ToString());
                await scope.ExecuteNonQueryAsync(ct);
            }

            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, hashtext(@k))", connection, tx))
            {
                lockCommand.Parameters.AddWithValue("ns", AuditWriter.LockNamespace);
                lockCommand.Parameters.AddWithValue("k", tenantId.ToString());
                await lockCommand.ExecuteNonQueryAsync(ct);
            }

            string? previous;
            await using (var last = new NpgsqlCommand("SELECT hash FROM audit_events WHERE tenant_id = @t ORDER BY id DESC LIMIT 1", connection, tx))
            {
                last.Parameters.AddWithValue("t", tenantId);
                previous = await last.ExecuteScalarAsync(ct) as string;
            }

            var e = new AuditEvent
            {
                TenantId = tenantId,
                OccurredAt = AuditHash.TruncateToStorage(DateTimeOffset.UtcNow),
                ActorKind = ActorKinds.System,
                EventType = AuditEventTypes.InstanceRestored,
                EntityType = "tenant",
                EntityId = tenantId,
                Note = reason,
                Changes = changes,
                PrevHash = previous,
            };
            e.Hash = AuditHash.Compute(e, previous);

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO audit_events (tenant_id, occurred_at, actor_user_id, actor_kind, actor_ip, event_type, entity_type, entity_id, from_state, to_state, reason_code, note, changes, ai_suggestion_id, request_id, prev_hash, hash)
                VALUES (@t, @at, NULL, @kind, NULL, @type, 'tenant', @t, NULL, NULL, NULL, @note, @changes::jsonb, NULL, NULL, @prev, @hash)
                """, connection, tx))
            {
                insert.Parameters.AddWithValue("t", tenantId);
                insert.Parameters.AddWithValue("at", e.OccurredAt);
                insert.Parameters.AddWithValue("kind", e.ActorKind);
                insert.Parameters.AddWithValue("type", e.EventType);
                insert.Parameters.AddWithValue("note", e.Note!);
                insert.Parameters.AddWithValue("changes", e.Changes!);
                insert.Parameters.AddWithValue("prev", (object?)e.PrevHash ?? DBNull.Value);
                insert.Parameters.AddWithValue("hash", e.Hash);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }

        return new Outcome(tenants.Count, sha256);
    }
}
