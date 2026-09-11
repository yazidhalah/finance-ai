using FinanceAi.Domain.Authorization;

namespace FinanceAi.Api.Authorization;

/// <summary>Endpoint metadata naming the permission an endpoint requires (API-12, SEC-12).</summary>
public sealed record RequiredPermission(string Permission);

/// <summary>
/// Endpoint metadata recording that an endpoint is intentionally reachable without a permission.
/// It exists so that "no permission" is a decision on record rather than an omission — the startup
/// assertion cannot tell the difference otherwise.
/// </summary>
public sealed record DeclaredAccess(DeclaredAccessKind Kind);

public enum DeclaredAccessKind
{
    /// <summary>Reachable without a token: login, registration, refresh.</summary>
    Anonymous,

    /// <summary>Any authenticated member of the tenant, with no further permission required.</summary>
    AuthenticatedOnly,
}

/// <summary>Slice 13: an endpoint an Owner/Admin may reach before enrolling a second factor — the profile, the auth routes, enrolment itself.</summary>
public sealed record AllowedWithoutMfa;

/// <summary>Slice 13 (SEC-09): the endpoint requires a fresh re-authentication proof in <c>X-Reauth</c>.</summary>
public sealed record RequiresReauthentication;

public static class EndpointPermissionExtensions
{
    public static TBuilder AllowsWithoutMfa<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AllowedWithoutMfa());
        return builder;
    }

    public static TBuilder RequiresReauth<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresReauthentication());
        return builder;
    }

    /// <summary>Declares the permission this endpoint authorizes on. Never a role name (SEC-12).</summary>
    public static TBuilder RequiresPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{permission}' is not a permission defined in doc 01 §5.1.", nameof(permission));
        }

        builder.WithMetadata(new RequiredPermission(permission));
        return builder;
    }

    /// <summary>Declares that this endpoint is deliberately reachable without a token.</summary>
    public static TBuilder AllowAnonymousEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new DeclaredAccess(DeclaredAccessKind.Anonymous));
        return builder;
    }

    /// <summary>
    /// Declares that this endpoint needs a valid session but no further permission — the caller's
    /// own profile, their own tenant list, their own logout.
    /// </summary>
    public static TBuilder RequiresAuthenticatedUser<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new DeclaredAccess(DeclaredAccessKind.AuthenticatedOnly));
        return builder;
    }
}

/// <summary>
/// SEC-10: authorization is deny-by-default, and an endpoint that declares nothing prevents the
/// application from booting. This converts a forgotten attribute from a vulnerability into a crash,
/// which is the whole point — a missing declaration is otherwise invisible until someone exploits it.
/// </summary>
public static class EndpointDeclarationAssertion
{
    /// <summary>
    /// Route prefixes exempt from the rule: endpoints the framework maps for its own tooling, which
    /// are never product surface. <c>/health</c> is deliberately <b>not</b> here — it declares
    /// anonymous access like anything else, so its reachability is a decision rather than a gap.
    /// </summary>
    private static readonly string[] ExemptPrefixes = ["/openapi", "/swagger", "/scalar"];

    public static void AssertEveryEndpointDeclaresAccess(IEnumerable<Endpoint> endpoints)
    {
        var undeclared = new List<string>();

        foreach (var endpoint in endpoints)
        {
            if (endpoint is not RouteEndpoint route || IsExempt(route))
            {
                continue;
            }

            var declaresPermission = route.Metadata.GetMetadata<RequiredPermission>() is not null;
            var declaresAccess = route.Metadata.GetMetadata<DeclaredAccess>() is not null;

            if (!declaresPermission && !declaresAccess)
            {
                undeclared.Add($"{string.Join('/', route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"])} /{route.RoutePattern.RawText?.TrimStart('/')}");
            }
        }

        if (undeclared.Count > 0)
        {
            throw new InvalidOperationException(
                "SEC-10: every endpoint must declare a required permission, or explicitly declare " +
                "anonymous / authenticated-only access. These do not:" +
                Environment.NewLine + string.Join(Environment.NewLine, undeclared.Select(e => "  " + e)));
        }
    }

    private static bool IsExempt(RouteEndpoint route)
    {
        var path = "/" + (route.RoutePattern.RawText?.TrimStart('/') ?? string.Empty);
        return ExemptPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
