using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FinanceAi.Domain.Entities;

namespace FinanceAi.Infrastructure.Audit;

/// <summary>
/// The per-tenant tamper-evident chain of SEC-53: <c>hash = H(prev_hash | canonical_row)</c>.
/// <para>
/// This is not a blockchain and makes no cryptographic custody claim. It detects <i>post-hoc
/// editing by someone with database access</i> — which, combined with the append-only trigger
/// (DM-28), means silently changing what the audit log says requires disabling a trigger and
/// rewriting every subsequent row.
/// </para>
/// </summary>
public static class AuditHash
{
    /// <summary>ASCII unit separator: cannot occur in any field, so fields cannot be confused.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// PostgreSQL <c>timestamptz</c> keeps microseconds; .NET keeps 100ns ticks. Truncating before
    /// both hashing and storing is what makes a hash recomputed from a database row match the one
    /// written — otherwise every chain would "fail" verification for a rounding reason.
    /// </summary>
    public static DateTimeOffset TruncateToStorage(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);

    public static string Compute(AuditEvent e, string? previousHash)
    {
        ArgumentNullException.ThrowIfNull(e);

        var canonical = new StringBuilder()
            .Append(previousHash ?? string.Empty).Append(FieldSeparator)
            .Append(e.TenantId.ToString()).Append(FieldSeparator)
            .Append(e.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(FieldSeparator)
            .Append(e.ActorUserId?.ToString() ?? string.Empty).Append(FieldSeparator)
            .Append(e.ActorKind).Append(FieldSeparator)
            .Append(e.ActorIp?.ToString() ?? string.Empty).Append(FieldSeparator)
            .Append(e.EventType).Append(FieldSeparator)
            .Append(e.EntityType).Append(FieldSeparator)
            .Append(e.EntityId.ToString()).Append(FieldSeparator)
            .Append(e.FromState ?? string.Empty).Append(FieldSeparator)
            .Append(e.ToState ?? string.Empty).Append(FieldSeparator)
            .Append(e.ReasonCode ?? string.Empty).Append(FieldSeparator)
            .Append(e.Note ?? string.Empty).Append(FieldSeparator)
            .Append(e.Changes ?? string.Empty).Append(FieldSeparator)
            .Append(e.AiSuggestionId?.ToString() ?? string.Empty).Append(FieldSeparator)
            .Append(e.RequestId ?? string.Empty)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
