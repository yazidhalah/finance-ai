using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Security;

namespace FinanceAi.Api.Endpoints;

/// <summary>Doc 05 slice 1: registration, login, refresh, logout, tenant list and switch.</summary>
public static class AuthEndpoints
{
    /// <summary>
    /// Scoped to the auth path so it is never sent with an ordinary API call, and
    /// <c>SameSite=Strict</c> so it is never sent cross-site at all (SEC-04, SEC-63).
    /// </summary>
    public const string RefreshCookieName = "fa_refresh";

    private const string RefreshCookiePath = "/api/v1/auth";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var auth = api.MapGroup("/auth").RequireRateLimiting(RateLimitPolicies.Auth);

        auth.MapPost("/register", RegisterAsync).AllowAnonymousEndpoint().WithName("Register");
        auth.MapPost("/login", LoginAsync).AllowAnonymousEndpoint().WithName("Login");
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymousEndpoint().WithName("Refresh");
        auth.MapPost("/logout", LogoutAsync).RequiresAuthenticatedUser().WithName("Logout");
        auth.MapGet("/tenants", ListTenantsAsync).RequiresAuthenticatedUser().WithName("ListTenants");
        auth.MapPost("/switch-tenant", SwitchTenantAsync).RequiresAuthenticatedUser().WithName("SwitchTenant");

        return api;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpContext context,
        PlatformIdentityStore identity,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var validation = new Validation()
            .Require("email", request.Email).Email("email", request.Email)
            .Require("password", request.Password)
            .MinLength("password", request.Password, Argon2idPasswordHasher.MinimumPasswordLength, "too_short")
            .MaxLength("password", request.Password, 512)
            .Require("fullName", request.FullName).MaxLength("fullName", request.FullName, 200)
            .Require("organizationName", request.OrganizationName).MaxLength("organizationName", request.OrganizationName, 200)
            .Currency("baseCurrency", request.BaseCurrency)
            .Timezone("timezone", request.Timezone)
            .Locale("locale", request.Locale);

        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var result = await identity.RegisterAsync(
            new PlatformIdentityStore.RegisterCommand(
                request.Email!.Trim(),
                request.Password!,
                request.FullName!.Trim(),
                request.OrganizationName!.Trim(),
                request.BaseCurrency ?? "JOD",
                request.Timezone ?? "Asia/Amman",
                request.Locale ?? Locales.Default,
                context.ClientIp(),
                context.RequestId()),
            ct);

        // The outcome is recorded server-side but never rendered: the response is byte-identical
        // whether or not the email was already registered (SEC-07, AC-04).
        loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation(
            "Registration processed with outcome {Outcome}.", result.Outcome);

        return TypedResults.Accepted((string?)null, RegisterResponse.Accepted);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        PlatformIdentityStore identity,
        IAccessTokenIssuer tokens,
        CancellationToken ct)
    {
        var validation = new Validation()
            .Require("email", request.Email)
            .Require("password", request.Password);

        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var result = await identity.AuthenticateAsync(
            request.Email!.Trim(), request.Password!, context.ClientIp(), context.RequestId(), ct);

        switch (result.Outcome)
        {
            case PlatformIdentityStore.AuthenticationOutcome.Locked:
                return ApiProblems.Create(
                    context, StatusCodes.Status423Locked, "account_locked",
                    "The account is temporarily locked after repeated failed sign-in attempts.",
                    "errors.auth.account_locked");

            case PlatformIdentityStore.AuthenticationOutcome.Failed:
                // One message for a wrong password, an unknown email, a disabled account and a
                // membership-less user. Anything more specific is an enumeration oracle (SEC-06/07).
                return ApiProblems.Create(
                    context, StatusCodes.Status401Unauthorized, "invalid_credentials",
                    "The email address or password is incorrect.",
                    "errors.auth.invalid_credentials");

            default:
                var session = result.Session!;
                var refresh = await identity.IssueRefreshTokenAsync(session.TenantId, session.UserId, null, ct);
                SetRefreshCookie(context, refresh);
                return TypedResults.Ok(ToSessionResponse(session, tokens));
        }
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        PlatformIdentityStore identity,
        IAccessTokenIssuer tokens,
        CancellationToken ct)
    {
        if (context.Request.Cookies[RefreshCookieName] is not { Length: > 0 } rawToken)
        {
            return ApiProblems.UnauthenticatedProblem(context);
        }

        var result = await identity.RedeemRefreshTokenAsync(
            rawToken, context.ClientIp(), context.RequestId(), ct);

        if (result.Outcome != PlatformIdentityStore.RefreshOutcome.Rotated)
        {
            // Reuse and rejection are reported identically. Reuse has already revoked the family
            // server-side (SEC-04); telling the presenter which case they hit helps only an attacker.
            ClearRefreshCookie(context);
            return ApiProblems.UnauthenticatedProblem(context);
        }

        SetRefreshCookie(context, result.Replacement!);

        var session = result.Session!;
        return TypedResults.Ok(ToSessionResponse(session, tokens));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        CurrentUser currentUser,
        PlatformIdentityStore identity,
        CancellationToken ct)
    {
        var rawToken = context.Request.Cookies[RefreshCookieName];
        await identity.LogoutAsync(rawToken, currentUser.UserId, context.ClientIp(), context.RequestId(), ct);

        ClearRefreshCookie(context);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListTenantsAsync(
        CurrentUser currentUser, PlatformIdentityStore identity, CancellationToken ct)
    {
        var memberships = await identity.ListMembershipsAsync(currentUser.UserId, ct);

        return TypedResults.Ok(new TenantListResponse(
            memberships
                .Select(m => new MembershipDto(m.TenantId, m.TenantName, m.Role.ToString(), m.BaseCurrency, m.Timezone))
                .ToList()));
    }

    private static async Task<IResult> SwitchTenantAsync(
        SwitchTenantRequest request,
        HttpContext context,
        CurrentUser currentUser,
        PlatformIdentityStore identity,
        IAccessTokenIssuer tokens,
        CancellationToken ct)
    {
        if (request.TenantId is not { } targetTenantId || targetTenantId == Guid.Empty)
        {
            return ApiProblems.ValidationProblem(
                context, [new ApiProblems.FieldError("tenantId", "required", "errors.tenantId.required")]);
        }

        var session = await identity.SwitchTenantAsync(
            currentUser.UserId, targetTenantId, context.ClientIp(), context.RequestId(), ct);

        // A tenant the caller is not a member of is indistinguishable from one that does not exist
        // (API-03, SEC-13, AC-18).
        if (session is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var refresh = await identity.IssueRefreshTokenAsync(session.TenantId, session.UserId, null, ct);
        SetRefreshCookie(context, refresh);

        return TypedResults.Ok(ToSessionResponse(session, tokens));
    }

    private static SessionResponse ToSessionResponse(
        PlatformIdentityStore.AuthenticatedSession session, IAccessTokenIssuer tokens)
    {
        var (accessToken, _) = tokens.Issue(session.UserId, session.TenantId, session.Role);

        return new SessionResponse(
            accessToken,
            (int)tokens.Lifetime.TotalSeconds,
            new UserDto(session.UserId, session.FullName, session.PreferredLocale, session.Email),
            new TenantDto(
                session.TenantId, session.TenantName, session.BaseCurrency,
                session.Timezone, session.TenantDefaultLocale),
            session.Role.ToString(),
            RolePermissions.OrderedFor(session.Role));
    }

    private static void SetRefreshCookie(HttpContext context, PlatformIdentityStore.IssuedRefreshToken token) =>
        context.Response.Cookies.Append(RefreshCookieName, token.RawToken, new CookieOptions
        {
            HttpOnly = true,     // unreadable from JavaScript, so XSS cannot steal the session
            Secure = true,       // SEC-04; also permitted on http://localhost by browsers
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = token.ExpiresAt,
            IsEssential = true,
        });

    private static void ClearRefreshCookie(HttpContext context) =>
        context.Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
        });
}
