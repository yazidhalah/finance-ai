using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Endpoints;
using FinanceAi.Api.Http;
using FinanceAi.Api.Logging;
using FinanceAi.Api.Middleware;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Configuration;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

DotEnv.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new RedactingJsonLoggerProvider(Console.Out));

builder.Services.AddApiServices();

var app = builder.Build();

// Order is load-bearing. Routing comes before the rate limiter and the tenant scope because both
// read endpoint metadata — the rate-limit policy and the declared permission — which does not exist
// until an endpoint has been selected. The tenant scope comes after authentication because the
// tenant is read from the validated principal (SEC-11, SEC-20).
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSecurityHeaders();
app.UseRouting();
app.UseCors(ApiServiceRegistration.SpaCorsPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantScopeMiddleware>();

// Declared anonymous rather than exempted: deny-by-default (SEC-10) applies to the liveness probe
// too, and "the load balancer gets a 401" is a bad way to find that out.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymousEndpoint()
    .ExcludeFromDescription();

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapMeEndpoints();
api.MapOrganizationEndpoints();
api.MapAuditEndpoints();
api.MapCustomerEndpoints();
api.MapImportEndpoints();
api.MapLedgerEndpoints()
    .MapReportEndpoints()
    .MapCaseEndpoints()
    .MapPromiseEndpoints()
    .MapDisputeEndpoints()
    .MapMessagingEndpoints()
    .MapAiEndpoints()
    .MapBriefingEndpoints();

// SEC-10: refuse to boot if any endpoint forgot to declare how it is authorized. This runs before
// the first request is served, so the failure mode of a forgotten declaration is a crash at deploy
// time rather than an open endpoint in production (AC-23).
EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess(
    app.Services.GetRequiredService<EndpointDataSource>().Endpoints);

await app.RunAsync();

/// <summary>Rate-limit policies (API-13, SEC-70).</summary>
public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string General = "general";

    /// <summary>API-13: 10 requests per minute on auth endpoints.</summary>
    public const int DefaultAuthPermitsPerMinute = 10;

    public const int DefaultGeneralPermitsPerMinute = 600;

    /// <summary>
    /// Configurable so a test can drive the limiter to rejection deliberately without every other
    /// test tripping over it. The default is the specified value, and a unit test pins that.
    /// </summary>
    public static int AuthPermitsPerMinute =>
        ReadPositiveInt("AUTH_RATE_LIMIT_PER_MINUTE", DefaultAuthPermitsPerMinute);

    public static int GeneralPermitsPerMinute =>
        ReadPositiveInt("GENERAL_RATE_LIMIT_PER_MINUTE", DefaultGeneralPermitsPerMinute);

    private static int ReadPositiveInt(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value > 0
            ? value
            : fallback;
}

public static class ApiServiceRegistration
{
    public const string SpaCorsPolicy = "spa";

    public static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var appConnectionString = PostgresConnections.For(DatabaseRole.App);

        // Scoped factory so PlatformIdentityStore can work on its own connection without nesting a
        // transaction inside the request's tenant scope.
        services.AddDbContextFactory<TenantDbContext>(
            options => options.UseNpgsql(appConnectionString),
            ServiceLifetime.Scoped);

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<TenantDbContext>>().CreateDbContext());

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<CurrentUser>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<AuditChainVerifier>();
        services.AddScoped<PlatformIdentityStore>();

        // Doc 10 §2.4: a customer with an open invoice cannot be deleted. Real since slice 3.
        services.AddScoped<FinanceAi.Domain.Entities.ICustomerBalanceGuard, FinanceAi.Infrastructure.Import.OpenInvoiceBalanceGuard>();
        services.AddScoped<FinanceAi.Infrastructure.Import.ImportService>();
        services.AddScoped<FinanceAi.Infrastructure.Ledger.LedgerService>();
        services.AddScoped<FinanceAi.Infrastructure.Ledger.BalanceReconciliation>();
        services.AddScoped<FinanceAi.Infrastructure.Reports.AgingService>();
        services.AddScoped<FinanceAi.Infrastructure.Cases.CaseService>();
        services.AddScoped<FinanceAi.Infrastructure.Cases.ICaseHooks>(sp => sp.GetRequiredService<FinanceAi.Infrastructure.Cases.CaseService>());
        services.AddScoped<FinanceAi.Infrastructure.Cases.PromiseService>();
        services.AddScoped<FinanceAi.Infrastructure.Cases.IPromiseHooks>(sp => sp.GetRequiredService<FinanceAi.Infrastructure.Cases.PromiseService>());
        services.AddScoped<FinanceAi.Infrastructure.Cases.DisputeService>();
        services.AddScoped<FinanceAi.Infrastructure.Cases.IDisputeHooks>(sp => sp.GetRequiredService<FinanceAi.Infrastructure.Cases.DisputeService>());
        services.AddSingleton<FinanceAi.Infrastructure.Messaging.IMailTransport, FinanceAi.Infrastructure.Messaging.SmtpMailTransport>();
        services.AddScoped<FinanceAi.Infrastructure.Messaging.MessagingService>();
        services.AddScoped<FinanceAi.Infrastructure.Messaging.IMessagingHooks>(sp => sp.GetRequiredService<FinanceAi.Infrastructure.Messaging.MessagingService>());
        services.AddSingleton<FinanceAi.Infrastructure.Ai.IAiClient, FinanceAi.Infrastructure.Ai.HttpAiClient>();
        services.AddScoped<FinanceAi.Infrastructure.Ai.InboundService>();
        services.AddScoped<FinanceAi.Infrastructure.Briefings.BriefingService>();
        services.AddScoped<FinanceAi.Infrastructure.Members.MembersService>();
        services.AddScoped<FinanceAi.Infrastructure.Cases.IBriefingHooks>(sp => sp.GetRequiredService<FinanceAi.Infrastructure.Briefings.BriefingService>());

        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<FinanceAi.Infrastructure.Security.ISecretBox, FinanceAi.Infrastructure.Security.AesGcmSecretBox>();
        services.AddSingleton(TimeProvider.System);

        var issuer = new RsaAccessTokenIssuer(SigningKey.LoadFromEnvironment());
        services.AddSingleton<IAccessTokenIssuer>(issuer);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = RsaAccessTokenIssuer.Issuer,
                    ValidateAudience = true,
                    ValidAudience = RsaAccessTokenIssuer.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = issuer.PublicKey,
                    ValidateLifetime = true,

                    // An unsigned or differently-signed token is rejected outright: without this,
                    // "alg: none" is a complete authentication bypass (AC-16).
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorization();

        // Without this, minimal-API parameter binding swallows a malformed or unexpected body and
        // returns a bodiless 400 — so the caller learns nothing and doc 05 §0.2's problem shape is
        // never produced. Letting the exception through hands it to ExceptionHandlingMiddleware.
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        services.ConfigureHttpJsonOptions(options =>
        {
            // SEC-17: an unknown field is an error, not something to ignore. This is what rejects a
            // body carrying tenantId, status or approvedBy instead of silently dropping it (API-01).
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.PropertyNameCaseInsensitive = false;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    context.HttpContext.Response.Headers.RetryAfter = "60";
                }

                await ApiProblems.Create(
                    context.HttpContext, StatusCodes.Status429TooManyRequests, ApiProblems.RateLimited,
                    "Too many requests.", "errors.rate_limited").ExecuteAsync(context.HttpContext);
            };

            // API-13: per client address. Brute forcing a password over the network becomes
            // uninteresting long before the account lock does.
            options.AddPolicy(RateLimitPolicies.Auth, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = RateLimitPolicies.AuthPermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));

            options.AddPolicy(RateLimitPolicies.General, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.UserIdOrNull()?.ToString()
                    ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = RateLimitPolicies.GeneralPermitsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
        });

        services.AddCors(options => options.AddPolicy(SpaCorsPolicy, policy => policy
            // SEC-62: an explicit allowlist, credentials only for the known SPA origin, no wildcard.
            .WithOrigins(SpaOrigins())
            .AllowCredentials()
            .WithHeaders("Authorization", "Content-Type", "If-Match", "Accept-Language")
            .WithMethods("GET", "POST", "PATCH", "DELETE", "OPTIONS")
            .WithExposedHeaders("Content-Language", "Retry-After")));

        return services;
    }

    private static string[] SpaOrigins() =>
        (Environment.GetEnvironmentVariable("WEB_ORIGINS") ?? "http://localhost:5173")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>SEC-61: the security headers every response carries.</summary>
public static class SecurityHeaderExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";

            // The API serves JSON only; nothing may frame it, and it may load nothing.
            headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            if (context.Request.IsHttps)
            {
                headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
            }

            await next();
        });
    }
}

/// <summary>Loads the RS256 signing key from the environment. Never from source (SEC-67).</summary>
public static class SigningKey
{
    public const string EnvironmentVariable = "JWT_SIGNING_KEY_PEM_BASE64";

    public static string LoadFromEnvironment()
    {
        var encoded = PostgresConnections.Require(EnvironmentVariable);

        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} must be a base64-encoded PKCS#8 PEM private key. " +
                "Generate one with: openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 | base64 -w0",
                ex);
        }
    }
}

/// <summary>Exposed so the integration test host can reference this assembly's entry point.</summary>
public partial class Program;
