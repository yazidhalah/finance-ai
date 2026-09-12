using Microsoft.AspNetCore.Http.HttpResults;
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
        // Slice 12: the invitee has no session yet. Anonymous by design, rate limited with the rest of the group.
        auth.MapPost("/accept-invitation", AcceptInvitationAsync).AllowAnonymousEndpoint().WithName("AcceptInvitation");
        // Slice 13: password reset is anonymous by nature; MFA enrolment and re-authentication need a session but not a second factor yet.
        auth.MapPost("/forgot-password", ForgotPasswordAsync).AllowAnonymousEndpoint().WithName("ForgotPassword");
        auth.MapPost("/reset-password", ResetPasswordAsync).AllowAnonymousEndpoint().WithName("ResetPassword");
        auth.MapPost("/verify-email", VerifyEmailAsync).AllowAnonymousEndpoint().WithName("VerifyEmail");   // slice 24
        auth.MapPost("/mfa/enroll", MfaEnrollAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("MfaEnroll");
        auth.MapPost("/mfa/verify", MfaVerifyAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("MfaVerify");
        auth.MapPost("/reauthenticate", ReauthenticateAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("Reauthenticate");
        auth.MapPost("/logout", LogoutAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("Logout");
        auth.MapGet("/tenants", ListTenantsAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("ListTenants");
        auth.MapPost("/switch-tenant", SwitchTenantAsync).RequiresAuthenticatedUser().AllowsWithoutMfa().WithName("SwitchTenant");

        return api;
    }

    private static async Task<Results<Accepted<RegisterResponse>, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        HttpContext context,
        PlatformIdentityStore identity,
        FinanceAi.Infrastructure.Messaging.IMailTransport mail,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var validation = new Validation()
            .Require("email", request.Email).Email("email", request.Email)
            .Require("password", request.Password)
            .MinLength("password", request.Password, Argon2idPasswordHasher.MinimumPasswordLength, "too_short")
            .MaxLength("password", request.Password, 512).NotBreached("password", request.Password)
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

        // Slice 24: the verification link, to the address that registered (doc 05 "email verification required").
        if (result.VerificationToken is not null && FinanceAi.Infrastructure.Messaging.OutboundSwitch.GloballyEnabled)
        {
            var link = $"{FinanceAi.Infrastructure.Members.MembersService.WebOrigin}/verify-email?token={result.VerificationToken}";
            var arabic = (result.Locale ?? Locales.Default).StartsWith("ar", StringComparison.Ordinal);
            try
            {
                await mail.SendAsync(new FinanceAi.Infrastructure.Messaging.OutgoingMail(request.Email!.Trim(),
                    arabic ? "تأكيد بريدك الإلكتروني — finance-ai" : "Verify your email — finance-ai",
                    arabic ? $"لتفعيل حسابك افتح الرابط التالي خلال 24 ساعة:\n{link}\n\nإن لم تسجّل في finance-ai فتجاهل هذه الرسالة." : $"To activate your account, open this link within 24 hours:\n{link}\n\nIf you did not register with finance-ai, ignore this message.",
                    arabic ? "ar" : "en", $"verify-{result.UserId:N}-{Guid.CreateVersion7():N}"), ct);
            }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or FormatException or InvalidOperationException or System.IO.IOException)
            {
                // A mail that cannot be sent must not fail — or distinguish — the registration (SEC-07). The address can
                // still be verified through a password reset (D-4). Logged without the address (SEC-41).
                loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogWarning("Verification mail could not be sent: {Reason}.", ex.GetType().Name);
            }
        }

        // The outcome is recorded server-side but never rendered: the response is byte-identical
        // whether or not the email was already registered (SEC-07, AC-04).
        loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation(
            "Registration processed with outcome {Outcome}.", result.Outcome);

        return TypedResults.Accepted((string?)null, RegisterResponse.Accepted);
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> LoginAsync(
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
            request.Email!.Trim(), request.Password!, request.Totp, context.ClientIp(), context.RequestId(), ct);

        switch (result.Outcome)
        {
            case PlatformIdentityStore.AuthenticationOutcome.EmailUnverified:
                // Only after the password matched (slice 24): the address exists, is theirs, and is unverified.
                return ApiProblems.Create(
                    context, StatusCodes.Status401Unauthorized, "email_unverified",
                    "Verify your email address with the link we sent before signing in.",
                    "errors.auth.email_unverified");

            case PlatformIdentityStore.AuthenticationOutcome.MfaRequired:
                // Only reachable after the password matched (slice 13): the client shows the TOTP step and resubmits.
                return ApiProblems.Create(
                    context, StatusCodes.Status401Unauthorized, "mfa_required",
                    "Enter the code from your authenticator app.",
                    "errors.auth.mfa_required");

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

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> RefreshAsync(
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

    private static async Task<Results<NoContent, ProblemHttpResult>> LogoutAsync(
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

    private static async Task<Results<Ok<TenantListResponse>, ProblemHttpResult>> ListTenantsAsync(
        CurrentUser currentUser, PlatformIdentityStore identity, CancellationToken ct)
    {
        var memberships = await identity.ListMembershipsAsync(currentUser.UserId, ct);

        return TypedResults.Ok(new TenantListResponse(
            memberships
                .Select(m => new MembershipDto(m.TenantId, m.TenantName, m.Role.ToString(), m.BaseCurrency, m.Timezone))
                .ToList()));
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> SwitchTenantAsync(
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
        var (accessToken, _) = tokens.Issue(session.UserId, session.TenantId, session.Role, session.MfaUsed ? "mfa" : "pwd");

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

    /// <summary>
    /// Every failure is <c>400 invitation_invalid</c> with the same body: an unknown, expired, revoked or used token is
    /// not distinguished (SEC-07). A valid token for an address without a user needs a name and a password.
    /// </summary>
    private static async Task<Results<Ok<AcceptInvitationResponse>, ProblemHttpResult>> AcceptInvitationAsync(AcceptInvitationRequest request, HttpContext context, PlatformIdentityStore identity, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var validation = new Validation().Require("token", request.Token).MaxLength("fullName", request.FullName, 200).MaxLength("password", request.Password, 512);
        if (request.Password is { Length: > 0 }) validation.MinLength("password", request.Password, Argon2idPasswordHasher.MinimumPasswordLength, "too_short").NotBreached("password", request.Password);
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);

        var result = await identity.AcceptInvitationAsync(new PlatformIdentityStore.AcceptInvitationCommand(request.Token!.Trim(), request.FullName, request.Password, context.ClientIp(), context.RequestId()), ct);
        loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation("Invitation acceptance processed with outcome {Outcome}.", result.Outcome);
        return result.Outcome switch
        {
            PlatformIdentityStore.AcceptInvitationOutcome.Accepted => TypedResults.Ok(new AcceptInvitationResponse(result.Email!, result.TenantName!, result.CreatedUser)),
            PlatformIdentityStore.AcceptInvitationOutcome.PasswordRequired => ApiProblems.Create(context, StatusCodes.Status400BadRequest, "password_required", "This address has no account yet; a name and a password are needed.", "errors.invitation.password_required",
                [new ApiProblems.FieldError("password", "required", "errors.password.required")]),
            _ => ApiProblems.Create(context, StatusCodes.Status400BadRequest, "invitation_invalid", "This invitation cannot be used.", "errors.invitation.invalid"),
        };
    }

    // ---------------------------------------------------------------------------------------
    // Slice 13 — MFA, re-authentication, password reset
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Ok<MfaEnrolmentResponse>, ProblemHttpResult>> MfaEnrollAsync(HttpContext context, CurrentUser user, PlatformIdentityStore identity, CancellationToken ct)
    {
        var enrolment = await identity.EnrollMfaAsync(user.UserId, ct);
        return enrolment is null
            ? ApiProblems.Create(context, StatusCodes.Status503ServiceUnavailable, "mfa_unavailable", "Second-factor enrolment is not configured on this server.", "errors.auth.mfa_unavailable")
            : TypedResults.Ok(new MfaEnrolmentResponse(enrolment.SecretBase32, enrolment.ProvisioningUri));
    }

    private static async Task<Results<Ok<MfaActivatedResponse>, ProblemHttpResult>> MfaVerifyAsync(MfaVerifyRequest request, HttpContext context, CurrentUser user, PlatformIdentityStore identity, CancellationToken ct)
    {
        if (request.Code is not { Length: 6 } || !request.Code.All(char.IsAsciiDigit)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("code", "invalid", "errors.validation.code.invalid")]);
        var activation = await identity.VerifyMfaAsync(user.UserId, user.TenantId, request.Code, context.ClientIp(), context.RequestId(), ct);
        return activation.Activated
            ? TypedResults.Ok(new MfaActivatedResponse(true, activation.RecoveryCodes))
            : ApiProblems.Create(context, StatusCodes.Status400BadRequest, "mfa_code_invalid", "The code did not match. Enrol again if the secret was lost.", "errors.auth.mfa_code_invalid");
    }

    private static async Task<Results<Ok<ReauthenticateResponse>, ProblemHttpResult>> ReauthenticateAsync(ReauthenticateRequest request, HttpContext context, CurrentUser user, PlatformIdentityStore identity, IAccessTokenIssuer tokens, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Password)) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("password", "required", "errors.password.required")]);
        if (!await identity.ReauthenticateAsync(user.UserId, request.Password, request.Totp, ct))
        {
            return ApiProblems.Create(context, StatusCodes.Status401Unauthorized, "invalid_credentials", "The password or code is incorrect.", "errors.auth.invalid_credentials");
        }

        var (proof, _) = tokens.IssueReauth(user.UserId);
        return TypedResults.Ok(new ReauthenticateResponse(proof, (int)RsaAccessTokenIssuer.ReauthLifetime.TotalSeconds));
    }

    private static async Task<Results<Accepted<AcceptedResponse>, ProblemHttpResult>> ForgotPasswordAsync(ForgotPasswordRequest request, HttpContext context, PlatformIdentityStore identity, FinanceAi.Infrastructure.Messaging.IMailTransport mail, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var validation = new Validation().Require("email", request.Email).Email("email", request.Email);
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        var reset = await identity.ForgotPasswordAsync(request.Email!.Trim(), ct);
        if (reset is not null && FinanceAi.Infrastructure.Messaging.OutboundSwitch.GloballyEnabled)
        {
            var link = $"{FinanceAi.Infrastructure.Members.MembersService.WebOrigin}/reset-password?token={reset.Token}";
            var arabic = reset.Locale.StartsWith("ar", StringComparison.Ordinal);
            await mail.SendAsync(new FinanceAi.Infrastructure.Messaging.OutgoingMail(reset.Email,
                arabic ? "إعادة تعيين كلمة المرور — finance-ai" : "Reset your password — finance-ai",
                arabic ? $"لإعادة تعيين كلمة مرورك افتح الرابط التالي خلال ساعة:\n{link}\n\nإن لم تطلب ذلك فتجاهل هذه الرسالة." : $"To reset your password, open this link within the hour:\n{link}\n\nIf you did not ask for this, ignore this message.",
                arabic ? "ar" : "en", $"reset-{reset.UserId:N}-{Guid.CreateVersion7():N}"), ct);
        }

        // Recorded, never rendered (SEC-07).
        loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogInformation("Password reset requested; user found: {Found}.", reset is not null);
        return TypedResults.Accepted((string?)null, new AcceptedResponse(true));
    }

    /// <summary>Slice 24: one shape for every token (SEC-07); <c>accepted</c> says whether the address is now verified.</summary>
    private static async Task<Results<Ok<AcceptedResponse>, ProblemHttpResult>> VerifyEmailAsync(VerifyEmailRequest request, HttpContext context, PlatformIdentityStore identity, CancellationToken ct)
    {
        var validation = new Validation().Require("token", request.Token);
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        return TypedResults.Ok(new AcceptedResponse(await identity.VerifyEmailAsync(request.Token!.Trim(), ct)));
    }

    private static async Task<Results<Ok<AcceptedResponse>, ProblemHttpResult>> ResetPasswordAsync(ResetPasswordRequest request, HttpContext context, PlatformIdentityStore identity, CancellationToken ct)
    {
        var validation = new Validation().Require("token", request.Token).Require("password", request.Password)
            .MinLength("password", request.Password, Argon2idPasswordHasher.MinimumPasswordLength, "too_short").MaxLength("password", request.Password, 512).NotBreached("password", request.Password);
        if (validation.HasErrors) return ApiProblems.ValidationProblem(context, validation.Errors);
        return await identity.ResetPasswordAsync(request.Token!.Trim(), request.Password!, context.ClientIp(), context.RequestId(), ct)
            ? TypedResults.Ok(new AcceptedResponse(true))
            : ApiProblems.Create(context, StatusCodes.Status400BadRequest, "reset_invalid", "This reset link cannot be used.", "errors.auth.reset_invalid");
    }
}
