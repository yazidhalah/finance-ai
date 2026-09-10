using System.Net;
using FinanceAi.Domain.Authorization;

namespace FinanceAi.Api.Authorization;

/// <summary>
/// The authenticated caller of the current request, resolved by <c>TenantScopeMiddleware</c> from
/// the validated token plus a fresh read of the membership. Handlers read it; nothing else writes it.
/// </summary>
public sealed class CurrentUser
{
    public Guid UserId { get; private set; }

    public Guid TenantId { get; private set; }

    public TenantRole Role { get; private set; }

    public bool IsSet { get; private set; }

    /// <summary>
    /// Resolved from the role on every request rather than carried in the token, so a role change
    /// or a deactivation takes effect on the next request (SEC-08).
    /// </summary>
    public IReadOnlyList<string> Permissions { get; private set; } = [];

    public bool Can(string permission) => this.IsSet && RolePermissions.Grants(this.Role, permission);

    internal void Set(Guid userId, Guid tenantId, TenantRole role)
    {
        this.UserId = userId;
        this.TenantId = tenantId;
        this.Role = role;
        this.Permissions = RolePermissions.OrderedFor(role);
        this.IsSet = true;
    }
}

/// <summary>Per-request facts used for audit rows and log correlation (SEC-52, SEC-101).</summary>
public static class RequestFacts
{
    public static IPAddress? ClientIp(this HttpContext context) => context?.Connection.RemoteIpAddress;

    public static string? RequestId(this HttpContext context) => context?.TraceIdentifier;
}
