using System.Net;
using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

/// <summary>Who caused the event (SEC-52). AI is never an actor — only a provenance (SM-04).</summary>
public static class ActorKinds
{
    public const string User = "user";
    public const string System = "system";
    public const string AiAssisted = "ai_assisted";
    public const string Support = "support";

    public static readonly IReadOnlyList<string> All = [User, System, AiAssisted, Support];
}

/// <summary>The auth event types this slice writes (SEC-50).</summary>
public static class AuditEventTypes
{
    public const string TenantCreated = "tenant.created";
    public const string UserRegistered = "user.registered";
    public const string MembershipCreated = "membership.created";
    public const string LoginSucceeded = "auth.login_succeeded";
    public const string LoginFailed = "auth.login_failed";
    public const string AccountLocked = "auth.account_locked";
    public const string LoggedOut = "auth.logged_out";
    public const string TokenRefreshed = "auth.token_refreshed";
    public const string RefreshReuseDetected = "auth.refresh_reuse_detected";
    public const string TenantSwitched = "auth.tenant_switched";
    public const string ProfileUpdated = "user.profile_updated";
    public const string OrganizationUpdated = "tenant.updated";

    /// <summary>SEC-94 (slice 31): the operator restored the instance from a backup; appended to every tenant's chain.</summary>
    public const string InstanceRestored = "instance.restored";
}

/// <summary>
/// An immutable record of something that happened (doc 04 §5.8). Append-only at the database
/// level: <c>UPDATE</c> and <c>DELETE</c> are revoked and a trigger raises (DM-28, AC-36).
/// Rows form a per-tenant hash chain so post-hoc editing by someone with database access is
/// detectable (SEC-53, AC-37).
/// </summary>
public sealed class AuditEvent : ITenantScoped
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? ActorUserId { get; set; }
    public string ActorKind { get; set; } = ActorKinds.User;
    public IPAddress? ActorIp { get; set; }

    public required string EventType { get; set; }
    public required string EntityType { get; set; }
    public required Guid EntityId { get; set; }

    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public string? ReasonCode { get; set; }
    public string? Note { get; set; }

    /// <summary>
    /// <c>{field: {old, new}}</c>. Money is serialized as a <b>string</b>, never a JSON number —
    /// JSON numbers are doubles in most parsers and would violate FIN-01 at the audit layer.
    /// </summary>
    public string? Changes { get; set; }

    public Guid? AiSuggestionId { get; set; }
    public string? RequestId { get; set; }

    public string? PrevHash { get; set; }
    public string Hash { get; set; } = string.Empty;
}
