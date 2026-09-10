namespace FinanceAi.Domain.Authorization;

/// <summary>
/// The authorization primitive. Endpoints authorize on a permission, never on a role name
/// (SEC-12). The catalogue below is the code-side mirror of doc 01 §5.1; a unit test parses
/// that document and asserts the two agree (AC-21), so the document stays the source of truth.
/// </summary>
public static class Permissions
{
    public const string TenantRead = "tenant.read";
    public const string TenantSettingsWrite = "tenant.settings.write";
    public const string TenantTransferOwnership = "tenant.transfer_ownership";
    public const string TenantDelete = "tenant.delete";

    public const string UsersRead = "users.read";
    public const string UsersInvite = "users.invite";
    public const string UsersRoleWrite = "users.role.write";
    public const string UsersDeactivate = "users.deactivate";

    public const string CustomersRead = "customers.read";
    public const string CustomersWrite = "customers.write";
    public const string CustomersMerge = "customers.merge";

    public const string InvoicesRead = "invoices.read";
    public const string InvoicesImport = "invoices.import";
    public const string InvoicesWrite = "invoices.write";
    public const string InvoicesVoid = "invoices.void";

    public const string PaymentsRead = "payments.read";
    public const string PaymentsWrite = "payments.write";
    public const string PaymentsAllocate = "payments.allocate";

    public const string CreditNotesWrite = "credit_notes.write";
    public const string WriteoffPropose = "writeoff.propose";
    public const string WriteoffApprove = "writeoff.approve";

    public const string AgingRead = "aging.read";

    public const string CasesRead = "cases.read";
    public const string CasesWrite = "cases.write";
    public const string CasesAssign = "cases.assign";
    public const string CasesEscalate = "cases.escalate";

    public const string PtpWrite = "ptp.write";
    public const string DisputesWrite = "disputes.write";
    public const string DisputesResolve = "disputes.resolve";

    public const string MessagesDraft = "messages.draft";
    public const string MessagesSend = "messages.send";
    public const string TemplatesWrite = "templates.write";

    public const string AiSuggestionsRead = "ai.suggestions.read";
    public const string AiSuggestionsApprove = "ai.suggestions.approve";
    public const string AiSettingsWrite = "ai.settings.write";

    public const string AuditRead = "audit.read";
    public const string ExportRun = "export.run";

    /// <summary>Every permission the product defines, in doc 01 §5.1 order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TenantRead, TenantSettingsWrite, TenantTransferOwnership, TenantDelete,
        UsersRead, UsersInvite, UsersRoleWrite, UsersDeactivate,
        CustomersRead, CustomersWrite, CustomersMerge,
        InvoicesRead, InvoicesImport, InvoicesWrite, InvoicesVoid,
        PaymentsRead, PaymentsWrite, PaymentsAllocate,
        CreditNotesWrite, WriteoffPropose, WriteoffApprove,
        AgingRead,
        CasesRead, CasesWrite, CasesAssign, CasesEscalate,
        PtpWrite, DisputesWrite, DisputesResolve,
        MessagesDraft, MessagesSend, TemplatesWrite,
        AiSuggestionsRead, AiSuggestionsApprove, AiSettingsWrite,
        AuditRead, ExportRun,
    ];
}
