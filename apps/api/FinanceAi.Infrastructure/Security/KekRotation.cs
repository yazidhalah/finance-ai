using Npgsql;

namespace FinanceAi.Infrastructure.Security;

/// <summary>
/// The bulk half of MFA KEK rotation (slice 17): re-seals every stored TOTP envelope under the current key. Runs on a
/// platform-scoped connection (the migrator's — table owner, still bound by FORCE RLS, admitted by the platform
/// policy on <c>users</c>). Nothing here reads a secret into a log: counts only.
/// </summary>
public static class KekRotation
{
    public sealed record Outcome(int Resealed, int AlreadyCurrent, int Unreadable);

    /// <summary>
    /// Re-seals what needs it. Throws before touching a row when any envelope cannot be opened with the keys this
    /// box holds — that is the moment the operator learns <c>MFA_KEK_BASE64_PREVIOUS</c> is still needed.
    /// </summary>
    public static async Task<Outcome> RunAsync(string connectionString, ISecretBox box, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (!box.Available) throw new InvalidOperationException("MFA_KEK_BASE64 is not set; nothing to rotate to.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.platform_scope', 'on', true)", connection, tx))
        {
            await scope.ExecuteNonQueryAsync(ct);
        }

        var rows = new List<(Guid Id, byte[]? Active, byte[]? Pending)>();
        await using (var select = new NpgsqlCommand("SELECT id, mfa_secret_enc, mfa_pending_secret_enc FROM users WHERE mfa_secret_enc IS NOT NULL OR mfa_pending_secret_enc IS NOT NULL", connection, tx))
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2)));
            }
        }

        var unreadable = rows.Count(r => (r.Active is not null && !box.CanOpen(r.Active)) || (r.Pending is not null && !box.CanOpen(r.Pending)));
        if (unreadable > 0)
        {
            throw new InvalidOperationException($"{unreadable} user(s) hold an MFA secret under a key this host does not have. Set MFA_KEK_BASE64_PREVIOUS to the key being retired and run again; nothing was changed.");
        }

        var resealed = 0;
        var current = 0;
        foreach (var (id, active, pending) in rows)
        {
            var newActive = active is not null && box.NeedsReseal(active) ? box.Seal(box.Open(active)) : null;
            var newPending = pending is not null && box.NeedsReseal(pending) ? box.Seal(box.Open(pending)) : null;
            if (newActive is null && newPending is null)
            {
                current++;
                continue;
            }

            await using var update = new NpgsqlCommand("UPDATE users SET mfa_secret_enc = coalesce(@a, mfa_secret_enc), mfa_pending_secret_enc = coalesce(@p, mfa_pending_secret_enc) WHERE id = @id", connection, tx);
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("a", (object?)newActive ?? DBNull.Value);
            update.Parameters.AddWithValue("p", (object?)newPending ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(ct);
            resealed++;
        }

        await tx.CommitAsync(ct);
        return new Outcome(resealed, current, 0);
    }
}
