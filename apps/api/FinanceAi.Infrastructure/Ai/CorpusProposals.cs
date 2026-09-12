using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace FinanceAi.Infrastructure.Ai;

/// <summary>
/// T-109 (doc 09 §5.4): the human corrections stored on AI suggestions — every <c>edit-and-approve</c>, every
/// <c>reject</c> that a person later labelled, every manual label over a pending suggestion — proposed as corpus
/// rows in the exact column contract of <c>services/ai/evaluations/import_corpus.py</c>. The importer redacts
/// contact details and refuses rows without provenance; this export writes <c>consented</c> only because the operator
/// named the tenant and a written-consent reference on the command line (T-92): there is no unattended path.
/// Runs inside one transaction with the tenant scope set, so RLS applies exactly as it does for the application.
/// </summary>
public static class CorpusProposals
{
    public sealed record Summary(int Proposed, int AwaitingLabel, int Skipped);

    public static async Task<(string Csv, Summary Summary)> RenderAsync(string connectionString, Guid tenantId, string consentReference, DateOnly? since, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(consentReference)) throw new ArgumentException("A written-consent reference is required (T-92).", nameof(consentReference));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var scope = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true), set_config('app.user_id', '', true), set_config('app.platform_scope', 'off', true)", connection, tx))
        {
            scope.Parameters.AddWithValue("t", tenantId.ToString());
            await scope.ExecuteNonQueryAsync(ct);
        }

        const string sql = """
            SELECT s.id, s.classification, s.confidence, s.human_decision, s.human_correction::text, s.decided_at,
                   m.id, coalesce(m.body_normalized, m.body_raw), m.detected_language, m.human_classification, m.channel
            FROM ai_suggestions s
            JOIN inbound_messages m ON m.tenant_id = s.tenant_id AND m.id = s.subject_id
            WHERE s.tenant_id = @t AND s.subject_type = 'inbound_message' AND s.operation = 'classify_customer_reply'
              AND s.human_decision IN ('edited', 'rejected')
              AND (@since::timestamptz IS NULL OR s.decided_at >= @since::timestamptz)
            ORDER BY s.decided_at, s.id
            """;
        var csv = new StringBuilder("id,text,label,language,provenance,note,expect\n");
        var proposed = 0; var awaiting = 0; var skipped = 0;
        await using (var command = new NpgsqlCommand(sql, connection, tx))
        {
            command.Parameters.AddWithValue("t", tenantId);
            command.Parameters.Add(new NpgsqlParameter<DateTime?>("since", NpgsqlTypes.NpgsqlDbType.TimestampTz) { TypedValue = since is { } d ? d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : null });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var suggestionId = reader.GetGuid(0);
                var aiClassification = reader.IsDBNull(1) ? null : reader.GetString(1);
                var confidence = reader.GetDecimal(2);
                var decision = reader.GetString(3);
                var correction = reader.IsDBNull(4) ? null : reader.GetString(4);
                var text = reader.GetString(7);
                var language = reader.IsDBNull(8) ? null : reader.GetString(8);
                var humanLabel = reader.IsDBNull(9) ? null : reader.GetString(9);

                if (string.IsNullOrWhiteSpace(humanLabel))
                {
                    awaiting++;                                            // rejected and not yet labelled by hand: nothing to teach from
                    continue;
                }

                if (text.Length > 4_000) { skipped++; continue; }          // a pasted thread, not a reply; the labeller decides by hand
                var note = $"consent: {consentReference}; decision: {decision}; ai: {aiClassification ?? "none"} ({confidence.ToString("F3", CultureInfo.InvariantCulture)})";
                var expect = Expect(correction);
                csv.Append(Field($"h-{suggestionId:N}"[..14])).Append(',').Append(Field(text)).Append(',').Append(Field(humanLabel)).Append(',')
                   .Append(Field(language is "ar" or "en" or "ar_latin" or "mixed" ? language : "mixed")).Append(",consented,")
                   .Append(Field(note)).Append(',').Append(Field(expect ?? string.Empty)).Append('\n');
                proposed++;
            }
        }

        await tx.RollbackAsync(ct);                                        // read-only by construction
        return (csv.ToString(), new Summary(proposed, awaiting, skipped));
    }

    /// <summary>The human's values, in the keys the evaluation harness compares (amount and date as text).</summary>
    private static string? Expect(string? correction)
    {
        if (correction is null) return null;
        using var doc = JsonDocument.Parse(correction);
        var root = doc.RootElement;
        var expect = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.String) expect["amount_numeric"] = amount.GetString()!;
        if (root.TryGetProperty("promisedDate", out var date) && date.ValueKind == JsonValueKind.String) expect["date_iso"] = date.GetString()!;
        if (root.TryGetProperty("disputeReasonCode", out var reason) && reason.ValueKind == JsonValueKind.String) expect["dispute_reason_code"] = reason.GetString()!;
        return expect.Count == 0 ? null : JsonSerializer.Serialize(expect);
    }

    private static string Field(string value)
    {
        if (value.IndexOfAny(['"', ',', '\n', '\r']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
