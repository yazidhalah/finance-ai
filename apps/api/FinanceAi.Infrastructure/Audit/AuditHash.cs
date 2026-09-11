using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            .Append(CanonicalJson(e.Changes)).Append(FieldSeparator)
            .Append(e.AiSuggestionId?.ToString() ?? string.Empty).Append(FieldSeparator)
            .Append(e.RequestId ?? string.Empty)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

    /// <summary>
    /// <c>changes</c> is <c>jsonb</c>, and jsonb hands back a different string from the one written (its own key order,
    /// its own spacing, no escapes). Hashing the string as written would make every row that carries changes fail
    /// verification (slice 15 found exactly that). Both the writer and the verifier hash this canonical form instead:
    /// object keys sorted ordinally at every level, no whitespace, no escaping — the same string from either side.
    /// </summary>
    public static string CanonicalJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return string.Empty;
        }

        var node = JsonNode.Parse(json);
        return node is null ? "null" : Sort(node).ToJsonString(CanonicalOptions);
    }

    private static JsonNode Sort(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Sort(p.Value)))),
        JsonArray a => new JsonArray(a.Select(v => v is null ? null : Sort(v)).ToArray()),
        _ => node.DeepClone(),
    };
}
