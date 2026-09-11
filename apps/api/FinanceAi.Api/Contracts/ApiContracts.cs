using FinanceAi.Api.Http;
using FinanceAi.Domain.Entities;

namespace FinanceAi.Api.Contracts;

// Request DTOs are explicit and closed: unknown fields are rejected rather than ignored (SEC-17),
// and no DTO anywhere exposes tenantId, status or another server-owned field as bindable — except
// SwitchTenantRequest, the single documented exception (API-01, API-02).

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? FullName,
    string? OrganizationName,
    string? BaseCurrency,
    string? Timezone,
    string? Locale);

/// <summary>
/// Deliberately says nothing about whether an account was created (SEC-07). The same body is
/// returned whether the email was free or already registered, so registration cannot be used to
/// discover who banks with whom.
/// </summary>
public sealed record RegisterResponse(string Status)
{
    public static RegisterResponse Accepted { get; } = new("pending");
}

public sealed record LoginRequest(string? Email, string? Password);

public sealed record UserDto(Guid Id, string FullName, string PreferredLocale, string Email);

public sealed record TenantDto(Guid Id, string Name, string BaseCurrency, string Timezone, string DefaultLocale);

/// <summary>The login/refresh/switch-tenant response of doc 05 slice 1.</summary>
public sealed record SessionResponse(
    string AccessToken,
    int ExpiresIn,
    UserDto User,
    TenantDto Tenant,
    string Role,
    IReadOnlyList<string> Permissions);

public sealed record SwitchTenantRequest(Guid? TenantId);

public sealed record MembershipDto(Guid TenantId, string Name, string Role, string BaseCurrency, string Timezone);

public sealed record TenantListResponse(IReadOnlyList<MembershipDto> Items);

public sealed record MeResponse(
    UserDto User,
    TenantDto Tenant,
    string Role,
    IReadOnlyList<string> Permissions);

public sealed record UpdateMeRequest(string? FullName, string? PreferredLocale);

public sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string? LegalName,
    string? TaxRegistrationNo,
    string BaseCurrency,
    string Timezone,
    string DefaultLocale,
    string Status,
    string RowVersion);

/// <summary>
/// <c>baseCurrency</c> is deliberately absent: doc 06 §6.1 makes it immutable once invoices exist,
/// and the guard cannot be written before the invoice table does. Sending it yields
/// <c>400 unexpected_field</c> rather than a change that silently skips a rule. Slice 3 adds both.
/// </summary>
public sealed record UpdateOrganizationRequest(
    string? Name,
    string? LegalName,
    string? TaxRegistrationNo,
    string? Timezone,
    string? DefaultLocale);

public sealed record MemberDto(
    Guid Id,
    Guid UserId,
    string Email,
    string FullName,
    string Role,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record MemberListResponse(IReadOnlyList<MemberDto> Items, int TotalCount);

public sealed record AuditEventDto(
    long Id,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string ActorKind,
    string EventType,
    string EntityType,
    Guid EntityId,
    string? FromState,
    string? ToState,
    string? ReasonCode,
    string? RequestId,
    string Hash);

public sealed record AuditListResponse(IReadOnlyList<AuditEventDto> Items, string? NextCursor);

/// <summary>Collects field errors so a request reports every problem at once, not the first one.</summary>
public sealed class Validation
{
    private readonly List<ApiProblems.FieldError> errors = [];

    public IReadOnlyList<ApiProblems.FieldError> Errors => this.errors;

    public bool HasErrors => this.errors.Count > 0;

    public Validation Require(string field, string? value, string messageKeySuffix = "required")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            this.Add(field, messageKeySuffix);
        }

        return this;
    }

    public Validation Email(string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (!value.Contains('@', StringComparison.Ordinal) || value.Length > 254 ||
             value.StartsWith('@') || value.EndsWith('@')))
        {
            this.Add(field, "invalid_email");
        }

        return this;
    }

    public Validation MinLength(string field, string? value, int minimum, string code)
    {
        if (value is not null && value.Length < minimum)
        {
            this.Add(field, code);
        }

        return this;
    }

    public Validation MaxLength(string field, string? value, int maximum)
    {
        if (value is not null && value.Length > maximum)
        {
            this.Add(field, "too_long");
        }

        return this;
    }

    public Validation Locale(string field, string? value)
    {
        if (value is not null && !Locales.IsSupported(value))
        {
            this.Add(field, "unsupported_locale");
        }

        return this;
    }

    public Validation Currency(string field, string? value)
    {
        if (value is not null && (value.Length != 3 || !value.All(char.IsAsciiLetterUpper)))
        {
            this.Add(field, "invalid_currency");
        }

        return this;
    }

    public Validation Timezone(string field, string? value)
    {
        if (value is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
        {
            this.Add(field, "unknown_timezone");
        }

        return this;
    }

    private void Add(string field, string code) =>
        this.errors.Add(new ApiProblems.FieldError(field, code, $"errors.{field}.{code}"));
}

// ---------------------------------------------------------------------------------------
// Slice 2 — Customers
// ---------------------------------------------------------------------------------------

/// <summary>
/// API-05: money is an object, and the amount is a <b>string</b> with exactly three decimals.
/// A JSON number is an IEEE-754 double in most clients and would violate FIN-01 the moment a
/// browser parsed it. No arithmetic happens on this type anywhere; it carries a stored figure.
/// </summary>
public sealed record MoneyDto(string Amount, string Currency)
{
    public static MoneyDto From(decimal amount, string currency) =>
        new(amount.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), currency);
}

/// <summary>Inbound money: parsed to <c>decimal</c>, refused if it carries more than three decimals.</summary>
public sealed record MoneyInput(string? Amount, string? Currency)
{
    public bool TryParse(out decimal amount)
    {
        amount = 0;
        return this.Amount is not null
            && decimal.TryParse(
                this.Amount,
                System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out amount)
            && amount >= 0
            && amount.Scale <= 3;
    }
}

public sealed record CustomerRequest(
    string? Code,
    string? NameAr,
    string? NameEn,
    string? LegalName,
    string? TaxRegistrationNo,
    string? PreferredLanguage,
    int? PaymentTermsDays,
    MoneyInput? CreditLimit,
    string? DefaultCurrency,
    string? RiskFlag,
    string? Status,
    string? Notes,
    /// <summary>PATCH only: a null field means "unchanged", so removing a limit needs an explicit flag.</summary>
    bool? ClearCreditLimit);

public sealed record CustomerResponse(
    Guid Id,
    string? Code,
    string? NameAr,
    string? NameEn,
    string? LegalName,
    string? TaxRegistrationNo,
    string PreferredLanguage,
    int PaymentTermsDays,
    MoneyDto? CreditLimit,
    string DefaultCurrency,
    string RiskFlag,
    string Status,
    int BrokenPromiseCount12m,
    int BouncedChequeCount12m,
    string? Notes,
    /// <summary>FIN-15 balance blocks per currency; one per currency with an Open invoice. Empty means no open invoices.</summary>
    IReadOnlyList<CustomerBalanceDto> Balances,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RowVersion);

public sealed record CustomerListResponse(IReadOnlyList<CustomerResponse> Items, string? NextCursor, int TotalCount);

public sealed record ContactRequest(
    string? Name,
    string? RoleTitle,
    string? Email,
    string? PhoneE164,
    bool? IsPrimary,
    bool? IsBilling,
    string? PreferredLanguage);

public sealed record ContactResponse(
    Guid Id,
    Guid CustomerId,
    string Name,
    string? RoleTitle,
    string? Email,
    string? PhoneE164,
    bool IsPrimary,
    bool IsBilling,
    string? PreferredLanguage,
    string RowVersion);

public sealed record ContactListResponse(IReadOnlyList<ContactResponse> Items);

public sealed record DuplicateCandidate(Guid CustomerId, Guid OtherCustomerId, string? NameA, string? NameB, double Similarity);

public sealed record DuplicateListResponse(IReadOnlyList<DuplicateCandidate> Items);

// ---------------------------------------------------------------------------------------
// Slice 3 — Invoice import
// ---------------------------------------------------------------------------------------

public sealed record ImportBatchResponse(
    Guid Id,
    string FileName,
    string FileKind,
    long FileSize,
    string Status,
    IReadOnlyList<string> Headers,
    IReadOnlyDictionary<string, string>? ColumnMap,
    string DateFormat,
    string DecimalSeparator,
    Guid? MappingId,
    int RowCount,
    int AcceptedCount,
    int RejectedCount,
    int DuplicateCount,
    int WarningCount,
    bool Forced,
    /// <summary>FIN-04: one entry per currency, never a grand total.</summary>
    IReadOnlyList<ControlTotalDto> ControlTotals,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CommittedAt,
    string RowVersion);

public sealed record ControlTotalDto(string Currency, MoneyDto Total, int Count);

public sealed record ImportBatchListResponse(IReadOnlyList<ImportBatchResponse> Items, string? NextCursor, int TotalCount);

public sealed record ImportRowResponse(
    Guid Id,
    int RowNo,
    IReadOnlyDictionary<string, string> Raw,
    IReadOnlyDictionary<string, string?>? Parsed,
    string Outcome,
    string? ErrorCode,
    string? ErrorDetail,
    Guid? CustomerId,
    Guid? InvoiceId);

public sealed record ImportRowListResponse(IReadOnlyList<ImportRowResponse> Items, string? NextCursor, int TotalCount);

public sealed record ApplyMappingRequest(
    IReadOnlyDictionary<string, string>? ColumnMap,
    string? DateFormat,
    string? DecimalSeparator,
    Guid? MappingId,
    /// <summary>When set, the applied mapping is also saved under this name for reuse.</summary>
    string? SaveAs);

public sealed record ResolveRowRequest(string? Action, Guid? CustomerId);

public sealed record CommitResponse(Guid BatchId, string Status, int InvoicesCreated, IReadOnlyList<ControlTotalDto> Totals);

public sealed record ImportMappingRequest(string? Name, IReadOnlyDictionary<string, string>? ColumnMap, string? DateFormat, string? DecimalSeparator);

public sealed record ImportMappingResponse(Guid Id, string Name, IReadOnlyDictionary<string, string> ColumnMap, string DateFormat, string DecimalSeparator);

public sealed record ImportMappingListResponse(IReadOnlyList<ImportMappingResponse> Items);

public sealed record InvoiceResponse(
    Guid Id,
    Guid CustomerId,
    string InvoiceNumber,
    string Status,
    string IssueDate,
    string DueDate,
    string Currency,
    MoneyDto NetAmount,
    MoneyDto TaxAmount,
    MoneyDto TotalAmount,
    /// <summary>FIN-10: the derived balance. Equal to the total until 3b introduces allocations.</summary>
    MoneyDto OpenBalance,
    string FxRateToBase,
    string BaseCurrency,
    string? PoReference,
    string? ExternalId,
    string? Notes,
    string Source,
    Guid? ImportBatchId,
    DateTimeOffset CreatedAt,
    string RowVersion);

public sealed record InvoiceListResponse(IReadOnlyList<InvoiceResponse> Items, string? NextCursor, int TotalCount);

/// <summary>FIN-15: the open balance per currency, never netted, never summed across currencies.</summary>
public sealed record CustomerBalanceDto(string Currency, MoneyDto OpenBalance, int OpenInvoiceCount);
