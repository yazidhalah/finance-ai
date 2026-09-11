using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinanceAi.Infrastructure.Ai;

// The request as the AI service's schema spells it (services/ai/schemas/classify_customer_reply.request.v1.json).
// Money is a string with three decimals (FIN-02) and there is no field for anything AI-30 forbids: the projection
// cannot carry an email, a phone or an id because the type has nowhere to put one.

public sealed record AiInvoiceInScope(
    [property: JsonPropertyName("invoice_number")] string InvoiceNumber,
    [property: JsonPropertyName("due_date")] string DueDate,
    [property: JsonPropertyName("open_amount")] string OpenAmount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("days_past_due")] int DaysPastDue);

public sealed record AiPriorPromises([property: JsonPropertyName("kept")] int Kept, [property: JsonPropertyName("broken")] int Broken);

public sealed record AiMessagePayload(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("received_at")] string ReceivedAt,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("declared_language")] string DeclaredLanguage);

public sealed record AiContextPayload(
    [property: JsonPropertyName("customer_display_name")] string? CustomerDisplayName,
    [property: JsonPropertyName("today")] string Today,
    [property: JsonPropertyName("case_status")] string? CaseStatus,
    [property: JsonPropertyName("invoices_in_scope")] IReadOnlyList<AiInvoiceInScope> InvoicesInScope,
    [property: JsonPropertyName("prior_promises")] AiPriorPromises PriorPromises);

public sealed record AiOptionsPayload([property: JsonPropertyName("min_confidence")] decimal MinConfidence);

public sealed record ClassifyRequestPayload(
    [property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("message")] AiMessagePayload Message,
    [property: JsonPropertyName("context")] AiContextPayload Context,
    [property: JsonPropertyName("options")] AiOptionsPayload Options);

public enum AiCallStatus { Ok, Unavailable, Rejected }

/// <summary>What came back, before the backend's own validation. <see cref="Body"/> is opaque JSON until <c>AiResponseValidator</c> accepts it.</summary>
public sealed record AiCallResult(AiCallStatus Status, JsonElement? Body, string? ServiceValidationStatus, string? InputHash, string? ErrorCode)
{
    public static AiCallResult Unavailable(string code) => new(AiCallStatus.Unavailable, null, null, null, code);
}

public sealed record AiHealth(bool Reachable, bool Ready, string? ModelName, string? Digest, string? PromptVersion, string? Error);

public interface IAiClient
{
    Task<AiCallResult> ClassifyAsync(ClassifyRequestPayload payload, CancellationToken ct);

    Task<AiHealth> HealthAsync(CancellationToken ct);
}

/// <summary>
/// The only path from the backend to the AI service: <c>AI_SERVICE_URL</c> on the internal network with the shared
/// token (AI-101). Nothing here retries into the unknown: a timeout or a 5xx is <see cref="AiCallStatus.Unavailable"/>
/// and the caller proceeds without AI (AI-10, PRD-28).
/// </summary>
public sealed class HttpAiClient : IAiClient
{
    // One pooled client for the process (no factory: Infrastructure has no framework reference and needs no new package).
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static string BaseUrl => Environment.GetEnvironmentVariable("AI_SERVICE_URL") is { Length: > 0 } u ? u.TrimEnd('/') : "http://127.0.0.1:8090";

    public static string? Token => Environment.GetEnvironmentVariable("AI_SERVICE_TOKEN") is { Length: > 0 } t ? t : null;

    public static TimeSpan Timeout => int.TryParse(Environment.GetEnvironmentVariable("AI_SERVICE_TIMEOUT_SECONDS"), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<AiCallResult> ClassifyAsync(ClassifyRequestPayload payload, CancellationToken ct)
    {
        if (Token is null) return AiCallResult.Unavailable("ai_not_configured");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var client = Http;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/internal/ai/v1/classify_customer_reply") { Content = JsonContent.Create(payload, options: Json) };
        request.Headers.Add("X-Service-Token", Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client.SendAsync(request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.GatewayTimeout or HttpStatusCode.BadGateway)
            {
                return AiCallResult.Unavailable(response.StatusCode == HttpStatusCode.TooManyRequests ? "ai_busy" : "ai_unavailable");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new AiCallResult(AiCallStatus.Rejected, null, null, null, $"ai_http_{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, timeout.Token);
            var header = response.Headers.TryGetValues("X-Ai-Validation-Status", out var vs) ? vs.FirstOrDefault() : null;
            var hash = response.Headers.TryGetValues("X-Ai-Input-Hash", out var hs) ? hs.FirstOrDefault() : null;
            return new AiCallResult(AiCallStatus.Ok, body, header, hash, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return AiCallResult.Unavailable(ex is JsonException ? "ai_bad_response" : "ai_unavailable");
        }
    }

    public async Task<AiHealth> HealthAsync(CancellationToken ct)
    {
        if (Token is null) return new AiHealth(false, false, null, null, null, "ai_not_configured");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var client = Http;
        try
        {
            using var ready = await client.GetAsync($"{BaseUrl}/ready", timeout.Token);
            if (ready.StatusCode == HttpStatusCode.ServiceUnavailable) return new AiHealth(true, false, null, null, null, "model_unavailable");
            if (!ready.IsSuccessStatusCode) return new AiHealth(true, false, null, null, null, $"ai_http_{(int)ready.StatusCode}");
            using var info = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/model-info");
            info.Headers.Add("X-Service-Token", Token);
            using var infoResponse = await client.SendAsync(info, timeout.Token);
            if (!infoResponse.IsSuccessStatusCode) return new AiHealth(true, true, null, null, null, null);
            var body = await infoResponse.Content.ReadFromJsonAsync<JsonElement>(Json, timeout.Token);
            return new AiHealth(true, true, Str(body, "name"), Str(body, "digest"), Str(body, "prompt_version"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AiHealth(false, false, null, null, null, "ai_unavailable");
        }
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
