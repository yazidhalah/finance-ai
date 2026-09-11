namespace FinanceAi.Domain.Authorization;

/// <summary>
/// The single role → permission mapping table required by SEC-12. Doc 01 §5.1 is the
/// specification; <c>RolePermissionMap_MatchesProductRequirementsDocument</c> (AC-21) parses
/// the document and fails if this table drifts from it.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<TenantRole, IReadOnlySet<string>> Map =
        new Dictionary<TenantRole, IReadOnlySet<string>>
        {
            [TenantRole.Owner] = Set(Permissions.All),

            [TenantRole.Admin] = Set(
            [
                Permissions.TenantRead, Permissions.TenantSettingsWrite,
                Permissions.UsersRead, Permissions.UsersInvite, Permissions.UsersRoleWrite,
                Permissions.UsersDeactivate,
                Permissions.CustomersRead, Permissions.CustomersWrite, Permissions.CustomersMerge,
                Permissions.InvoicesRead, Permissions.InvoicesImport, Permissions.InvoicesWrite,
                Permissions.InvoicesVoid,
                Permissions.PaymentsRead, Permissions.PaymentsWrite, Permissions.PaymentsAllocate,
                Permissions.CreditNotesWrite, Permissions.WriteoffPropose, Permissions.WriteoffApprove,
                Permissions.AgingRead,
                Permissions.CasesRead, Permissions.CasesWrite, Permissions.CasesAssign,
                Permissions.CasesEscalate,
                Permissions.PtpWrite, Permissions.DisputesWrite, Permissions.DisputesResolve,
                Permissions.MessagesDraft, Permissions.MessagesSend, Permissions.TemplatesWrite,
                Permissions.AiSuggestionsRead, Permissions.AiSuggestionsApprove,
                Permissions.AiSettingsWrite,
                Permissions.AuditRead, Permissions.ExportRun,
            ]),

            [TenantRole.Accountant] = Set(
            [
                Permissions.TenantRead,
                Permissions.UsersRead,
                Permissions.CustomersRead, Permissions.CustomersWrite, Permissions.CustomersMerge,
                Permissions.InvoicesRead, Permissions.InvoicesImport, Permissions.InvoicesWrite,
                Permissions.InvoicesVoid,
                Permissions.PaymentsRead, Permissions.PaymentsWrite, Permissions.PaymentsAllocate,
                Permissions.CreditNotesWrite, Permissions.WriteoffPropose,
                Permissions.AgingRead,
                Permissions.CasesRead, Permissions.CasesWrite, Permissions.CasesAssign,
                Permissions.PtpWrite, Permissions.DisputesWrite, Permissions.DisputesResolve,
                Permissions.MessagesDraft, Permissions.MessagesSend, Permissions.TemplatesWrite,
                Permissions.AiSuggestionsRead, Permissions.AiSuggestionsApprove,
                Permissions.AuditRead, Permissions.ExportRun,
            ]),

            [TenantRole.Collector] = Set(
            [
                Permissions.TenantRead,
                Permissions.CustomersRead,
                Permissions.InvoicesRead,
                Permissions.PaymentsRead,
                Permissions.AgingRead,
                Permissions.CasesRead, Permissions.CasesWrite,
                Permissions.PtpWrite, Permissions.DisputesWrite,
                Permissions.MessagesDraft, Permissions.MessagesSend,
                Permissions.AiSuggestionsRead, Permissions.AiSuggestionsApprove,
            ]),

            [TenantRole.Viewer] = Set(
            [
                Permissions.TenantRead,
                Permissions.CustomersRead,
                Permissions.InvoicesRead,
                Permissions.PaymentsRead,
                Permissions.AgingRead,
                Permissions.CasesRead,
                Permissions.AiSuggestionsRead,
                Permissions.AuditRead, Permissions.ExportRun,
            ]),
        };

    private static IReadOnlySet<string> Set(IEnumerable<string> permissions) =>
        permissions.ToHashSet(StringComparer.Ordinal);

    /// <summary>The effective permissions of a role, as returned by <c>/me</c>.</summary>
    public static IReadOnlySet<string> For(TenantRole role) => Map[role];

    /// <summary>
    /// PRD-14: the one role the spec names as subject to <c>collector_sees_only_assigned</c>. A visibility
    /// filter, not an authorization — permissions still decide what a scoped user may do.
    /// </summary>
    public static bool IsAssignmentScoped(TenantRole role) => role == TenantRole.Collector;

    public static bool Grants(TenantRole role, string permission) =>
        Map[role].Contains(permission);

    /// <summary>Ordered for stable API output and stable test assertions.</summary>
    public static IReadOnlyList<string> OrderedFor(TenantRole role) =>
        Permissions.All.Where(p => Map[role].Contains(p)).ToList();
}
