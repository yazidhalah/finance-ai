using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAi.TestSupport;

/// <summary>
/// Hosts the real API in-process against the test database. Nothing is stubbed: the same middleware,
/// the same authentication, the same transactions and the same RLS policies as production.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, CookieContainerHandler> jars = new();

    /// <summary>
    /// The host's clock. Defaults to the system clock; a test may pin it (FIN-58's day boundary, token
    /// expiry) and must reset it with <see cref="ResetClock"/> — tests in a collection run sequentially,
    /// so a pinned clock cannot leak into a concurrently running test.
    /// </summary>
    public SettableTimeProvider Clock { get; } = new();

    public void ResetClock() => this.Clock.Override = null;

    /// <summary>Pins the clock until the returned scope is disposed — for tests whose outcome must not depend on the hour they run at.</summary>
    public IDisposable PinClock(DateTimeOffset at)
    {
        this.Clock.Override = at;
        return new ClockScope(this);
    }

    private sealed class ClockScope(ApiFactory owner) : IDisposable
    {
        public void Dispose() => owner.ResetClock();
    }

    /// <summary>The mail host is Mailpit from .env; a test may make the next sends fail to prove the retry path.</summary>
    public SwitchableMailTransport Mail { get; } = new();

    /// <summary>
    /// The AI service, scripted. Integration tests never call Ollama: a test enqueues exactly the JSON a model
    /// (or a hijacked model, or a broken one) would return and asserts what the backend does with it. The
    /// live path is exercised by the Python suite and the evaluation harness under <c>services/ai</c>.
    /// </summary>
    public ScriptedAiClient Ai { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(this.Clock);
            services.AddSingleton<FinanceAi.Infrastructure.Messaging.IMailTransport>(this.Mail);
            services.AddSingleton<FinanceAi.Infrastructure.Ai.IAiClient>(this.Ai);
        });
    }

    /// <summary>
    /// A client that keeps cookies, so the refresh-token cookie behaves exactly as it does in a
    /// browser. Cookies over <c>http://localhost</c> are accepted despite <c>Secure</c>, matching
    /// browser behaviour for the local development origin.
    /// </summary>
    public HttpClient CreateCookieClient()
    {
        var handler = new CookieContainerHandler();
        var client = this.CreateDefaultClient(handler);
        this.jars.Add(client, handler);
        return client;
    }

    /// <summary>
    /// The raw refresh token this client is holding — what an attacker who stole the cookie would
    /// have. Used by the reuse-detection test (AC-13).
    /// </summary>
    public string? RefreshTokenOf(HttpClient client) =>
        this.jars.TryGetValue(client, out var handler) ? handler.RefreshToken : null;

    private sealed class CookieContainerHandler : DelegatingHandler
    {
        private readonly CookieContainer cookies = new();

        public string? RefreshToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var uri = request.RequestUri!;
            var header = this.cookies.GetCookieHeader(uri);

            if (!string.IsNullOrEmpty(header))
            {
                request.Headers.Remove("Cookie");
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    // Strip Secure so the in-memory container stores it for the http test host, the
                    // way a browser does for localhost. The header itself is asserted separately by
                    // AC-11, which is where that flag actually matters.
                    this.cookies.SetCookies(uri, setCookie.Replace("; secure", string.Empty, StringComparison.OrdinalIgnoreCase));

                    if (setCookie.StartsWith("fa_refresh=", StringComparison.Ordinal))
                    {
                        var value = setCookie["fa_refresh=".Length..].Split(';')[0];
                        this.RefreshToken = string.IsNullOrEmpty(value) ? null : value;
                    }
                }
            }

            return response;
        }
    }
}

/// <summary>Shapes the tests read back from the API. Mirrors the contracts in doc 05.</summary>
public sealed class SwitchableMailTransport : FinanceAi.Infrastructure.Messaging.IMailTransport
{
    private readonly FinanceAi.Infrastructure.Messaging.SmtpMailTransport inner = new();

    /// <summary>How many of the next sends should throw, to exercise retries.</summary>
    public int FailNext { get; set; }

    public int Sent { get; private set; }

    public Task<string> SendAsync(FinanceAi.Infrastructure.Messaging.OutgoingMail mail, CancellationToken ct)
    {
        if (this.FailNext > 0)
        {
            this.FailNext--;
            throw new System.Net.Mail.SmtpException("simulated SMTP failure");
        }

        this.Sent++;
        return this.inner.SendAsync(mail, ct);
    }
}

/// <summary>A queue of scripted AI answers; each classify call pops one. Empty queue = the service is down.</summary>
public sealed class ScriptedAiClient : FinanceAi.Infrastructure.Ai.IAiClient
{
    private readonly Queue<Func<FinanceAi.Infrastructure.Ai.ClassifyRequestPayload, FinanceAi.Infrastructure.Ai.AiCallResult>> queue = new();

    /// <summary>Every request the backend sent, so a test can assert the projection (AI-30) and the threshold it carried.</summary>
    public List<FinanceAi.Infrastructure.Ai.ClassifyRequestPayload> Requests { get; } = [];

    private readonly Queue<Func<FinanceAi.Infrastructure.Ai.BriefingRequestPayload, FinanceAi.Infrastructure.Ai.AiCallResult>> briefingQueue = new();

    /// <summary>Every briefing request (slice 10): the figures the backend handed the model, as strings (AI-80).</summary>
    public List<FinanceAi.Infrastructure.Ai.BriefingRequestPayload> BriefingRequests { get; } = [];

    /// <summary>Scripts the narrative for the next briefing call; a function receives the request so a test can echo its figures back.</summary>
    public void EnqueueBriefing(Func<FinanceAi.Infrastructure.Ai.BriefingRequestPayload, string> responseJson, string validationStatus = "valid") =>
        this.briefingQueue.Enqueue(r => new FinanceAi.Infrastructure.Ai.AiCallResult(FinanceAi.Infrastructure.Ai.AiCallStatus.Ok, JsonDocument.Parse(responseJson(r)).RootElement.Clone(), validationStatus, new string('b', 64), null));

    public void EnqueueBriefingUnavailable() => this.briefingQueue.Enqueue(_ => FinanceAi.Infrastructure.Ai.AiCallResult.Unavailable("ai_unavailable"));

    public Task<FinanceAi.Infrastructure.Ai.AiCallResult> BriefingAsync(FinanceAi.Infrastructure.Ai.BriefingRequestPayload payload, CancellationToken ct)
    {
        this.BriefingRequests.Add(payload);
        return Task.FromResult(this.briefingQueue.Count == 0 ? FinanceAi.Infrastructure.Ai.AiCallResult.Unavailable("ai_unavailable") : this.briefingQueue.Dequeue()(payload));
    }

    public bool Healthy { get; set; } = true;

    public void Enqueue(string responseJson, string validationStatus = "valid") =>
        this.queue.Enqueue(_ => new FinanceAi.Infrastructure.Ai.AiCallResult(FinanceAi.Infrastructure.Ai.AiCallStatus.Ok, JsonDocument.Parse(responseJson).RootElement.Clone(), validationStatus, new string('a', 64), null));

    public void EnqueueUnavailable(string code = "ai_unavailable") => this.queue.Enqueue(_ => FinanceAi.Infrastructure.Ai.AiCallResult.Unavailable(code));

    public void Clear()
    {
        this.queue.Clear();
        this.briefingQueue.Clear();
    }

    public Task<FinanceAi.Infrastructure.Ai.AiCallResult> ClassifyAsync(FinanceAi.Infrastructure.Ai.ClassifyRequestPayload payload, CancellationToken ct)
    {
        this.Requests.Add(payload);
        return Task.FromResult(this.queue.Count == 0 ? FinanceAi.Infrastructure.Ai.AiCallResult.Unavailable("ai_unavailable") : this.queue.Dequeue()(payload));
    }

    public Task<FinanceAi.Infrastructure.Ai.AiHealth> HealthAsync(CancellationToken ct) =>
        Task.FromResult(this.Healthy ? new FinanceAi.Infrastructure.Ai.AiHealth(true, true, "qwen3:4b", "359d7dd4bcda", "classify_customer_reply.v1", null) : new FinanceAi.Infrastructure.Ai.AiHealth(false, false, null, null, null, "ai_unavailable"));
}

/// <summary>Builds model outputs exactly as the service would return them (schema v1), so tests script realistic JSON.</summary>
public static class AiScript
{
    /// <summary>A daily_briefing.v1 output. Pass the narrative the "model" wrote; the guard decides whether a person sees it.</summary>
    public static string Briefing(string language, string narrative, IReadOnlyList<string>? highlights = null, IReadOnlyList<string>? numbersUsed = null, decimal confidence = 0.9m, string reason = "generated_from_metrics") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = "daily_briefing.v1",
            ["language"] = language,
            ["narrative"] = narrative,
            ["highlights"] = highlights ?? [],
            ["numbers_used"] = numbersUsed ?? ["totalOverdue"],
            ["confidence"] = confidence,
            ["reason_code"] = reason,
            ["model"] = new Dictionary<string, object?> { ["name"] = "qwen3:4b", ["digest"] = "359d7dd4bcda", ["prompt_version"] = "daily_briefing.v1", ["latency_ms"] = 2000 },
        });

    public static string Response(
        string classification, decimal confidence, string reasonCode = "acknowledges_without_commitment", string language = "en",
        string? amountText = null, string? amountNumeric = null, string? currency = null, string? dateText = null, string? dateIso = null, bool? dateRelative = null,
        IReadOnlyList<string>? invoiceNumbers = null, string? paymentMethod = null, string? reference = null, bool suspicious = false, bool review = false,
        string? rationale = "scripted", IReadOnlyList<(string Classification, decimal Confidence)>? secondary = null, string model = "qwen3:4b", string digest = "359d7dd4bcda", string promptVersion = "classify_customer_reply.v1")
    {
        var body = new Dictionary<string, object?>
        {
            ["schema_version"] = "classify_customer_reply.v1",
            ["classification"] = classification,
            ["confidence"] = confidence,
            ["reason_code"] = reasonCode,
            ["detected_language"] = language,
            ["extracted"] = new Dictionary<string, object?>
            {
                ["mentioned_amount_text"] = amountText,
                ["mentioned_amount_numeric"] = amountNumeric,
                ["mentioned_currency"] = currency,
                ["mentioned_date_text"] = dateText,
                ["mentioned_date_iso"] = dateIso,
                ["date_is_relative"] = dateRelative,
                ["referenced_invoice_numbers"] = invoiceNumbers ?? [],
                ["payment_method_mentioned"] = paymentMethod,
                ["payment_reference_text"] = reference,
            },
            ["secondary_classifications"] = (secondary ?? []).Select(s => new Dictionary<string, object?> { ["classification"] = s.Classification, ["confidence"] = s.Confidence }).ToList(),
            ["sentiment"] = "neutral",
            ["requires_human_review"] = review,
            ["contains_suspicious_instructions"] = suspicious,
            ["rationale"] = rationale,
            ["model"] = new Dictionary<string, object?> { ["name"] = model, ["digest"] = digest, ["prompt_version"] = promptVersion, ["latency_ms"] = 1234 },
        };
        return JsonSerializer.Serialize(body);
    }
}

public sealed class SettableTimeProvider : TimeProvider
{
    public DateTimeOffset? Override { get; set; }

    public override DateTimeOffset GetUtcNow() => this.Override?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
}

public sealed record SessionResponse(
    string AccessToken,
    int ExpiresIn,
    UserDto User,
    TenantDto Tenant,
    string Role,
    IReadOnlyList<string> Permissions);

public sealed record UserDto(Guid Id, string FullName, string PreferredLocale, string Email);

public sealed record TenantDto(Guid Id, string Name, string BaseCurrency, string Timezone, string DefaultLocale);

public sealed record MeResponse(UserDto User, TenantDto Tenant, string Role, IReadOnlyList<string> Permissions);

public sealed record OrganizationResponse(
    Guid Id, string Name, string? LegalName, string? TaxRegistrationNo, string BaseCurrency,
    string Timezone, string DefaultLocale, string Status, string RowVersion);

public sealed record MembershipDto(Guid TenantId, string Name, string Role, string BaseCurrency, string Timezone);

public sealed record TenantListResponse(IReadOnlyList<MembershipDto> Items);

public sealed record MemberDto(
    Guid Id, Guid UserId, string Email, string FullName, string Role, string Status, DateTimeOffset CreatedAt);

public sealed record MemberListResponse(IReadOnlyList<MemberDto> Items, int TotalCount);

public sealed record AuditEventDto(
    long Id, DateTimeOffset OccurredAt, Guid? ActorUserId, string ActorKind, string EventType,
    string EntityType, Guid EntityId, string? FromState, string? ToState, string? ReasonCode,
    string? RequestId, string Hash);

public sealed record AuditListResponse(IReadOnlyList<AuditEventDto> Items, string? NextCursor);

public sealed record ProblemResponse(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("messageKey")] string? MessageKey,
    [property: JsonPropertyName("traceId")] string? TraceId);

/// <summary>Building an organization with real users is the first step of nearly every test (T-04).</summary>
public static class ApiScenario
{
    public const string ValidPassword = "correct horse battery staple";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Organization(
        Guid TenantId, Guid OwnerUserId, string OwnerEmail, string Name, SessionResponse OwnerSession);

    /// <summary>Registers an organization and signs its Owner in.</summary>
    public static async Task<Organization> CreateOrganizationAsync(
        this ApiFactory factory, string? name = null, string? locale = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var email = $"owner-{suffix}@example.jo";
        var organizationName = name ?? $"Org {suffix}";

        using var client = factory.CreateCookieClient();

        var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = ValidPassword,
            fullName = "Rana Owner",
            organizationName,
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = locale ?? "ar-JO",
        }, Json);

        registration.EnsureSuccessStatusCode();

        var session = await LoginAsync(factory, email, ValidPassword);

        return new Organization(session.Tenant.Id, session.User.Id, email, organizationName, session);
    }

    public static async Task<SessionResponse> LoginAsync(this ApiFactory factory, string email, string password)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var client = factory.CreateCookieClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Json);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>(Json))!;
    }

    /// <summary>An HTTP client already carrying a bearer token.</summary>
    public static HttpClient AuthenticatedClient(this ApiFactory factory, SessionResponse session)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(session);

        var client = factory.CreateCookieClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return client;
    }
}
