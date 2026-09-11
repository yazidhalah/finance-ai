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

    /// <summary>The mail host is Mailpit from .env; a test may make the next sends fail to prove the retry path.</summary>
    public SwitchableMailTransport Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(this.Clock);
            services.AddSingleton<FinanceAi.Infrastructure.Messaging.IMailTransport>(this.Mail);
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
