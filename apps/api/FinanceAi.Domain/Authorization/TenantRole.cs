namespace FinanceAi.Domain.Authorization;

/// <summary>
/// Roles are per tenant (doc 01 §5). A user account is global; it holds one membership row
/// per tenant with exactly one role. There is no cross-tenant role.
/// Values round-trip against the <c>tenant_memberships.role</c> CHECK constraint (DM-11).
/// </summary>
public enum TenantRole
{
    Owner,
    Admin,
    Accountant,
    Collector,
    Viewer,
}
