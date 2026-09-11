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
    /// <summary>FIN-15 / FIN-42: open balance, unapplied cash and unapplied credit per currency — three figures, never netted.</summary>
    IReadOnlyList<CustomerPositionDto> Balances,
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


// ---------------------------------------------------------------------------------------
// Slice 3b — Payments, cheques, allocation, credit notes, withholding, write-off
// ---------------------------------------------------------------------------------------

public sealed record AllocationLineInput(Guid? InvoiceId, MoneyInput? Amount);

public sealed record PaymentRequest(
    Guid? CustomerId,
    MoneyInput? Amount,
    string? Method,
    string? ReceivedDate,
    string? EffectiveDate,
    string? Reference,
    string? Notes,
    IReadOnlyList<AllocationLineInput>? Allocations);

public sealed record AllocationRequest(IReadOnlyList<AllocationLineInput>? Lines);

public sealed record ReasonRequest(string? Reason);

public sealed record AllocationDto(Guid Id, Guid PaymentId, Guid InvoiceId, MoneyDto Amount, string EffectiveDate, bool IsActive, Guid? ReversalOfId, string? ReversalReason, string Method, DateTimeOffset CreatedAt, Guid? AllocatedBy);

public sealed record PaymentResponse(
    Guid Id, Guid CustomerId, MoneyDto Amount, string Currency, string Method, string ReceivedDate, string EffectiveDate,
    string? Reference, string Status, Guid? ChequeId, string? Notes,
    /// <summary>FIN-16: what is still on the customer's account, exact.</summary>
    MoneyDto Unallocated,
    IReadOnlyList<AllocationDto> Allocations, DateTimeOffset CreatedAt, string RowVersion);

public sealed record PaymentListResponse(IReadOnlyList<PaymentResponse> Items, string? NextCursor, int TotalCount);

public sealed record ProposalLineDto(Guid InvoiceId, string InvoiceNumber, string DueDate, MoneyDto OpenBalance, MoneyDto Proposed);

public sealed record AllocationProposalResponse(MoneyDto Available, IReadOnlyList<ProposalLineDto> Lines, MoneyDto RemainingAfterProposal);

public sealed record InvoiceResidualDto(Guid InvoiceId, MoneyDto OpenBalance, string Settlement, MoneyDto? ProposedRoundingAdjustment);

public sealed record AllocationResultResponse(PaymentResponse Payment, MoneyDto Unallocated, IReadOnlyList<InvoiceResidualDto> Invoices);

public sealed record ChequeRequest(Guid? CustomerId, string? ChequeNumber, string? BankName, MoneyInput? Amount, string? ChequeDate, string? ReceivedDate, string? Notes);

public sealed record ChequeTransitionRequest(string? Event, string? Reason, IReadOnlyList<AllocationLineInput>? Allocations);

public sealed record ChequeResponse(Guid Id, Guid CustomerId, string ChequeNumber, string? BankName, MoneyDto Amount, string ChequeDate, string ReceivedDate, bool IsPostDated, string Status, string? BouncedReason, string? ClearedDate, Guid? PaymentId, Guid? PtpId, string? Notes, string RowVersion);

public sealed record ChequeListResponse(IReadOnlyList<ChequeResponse> Items, string? NextCursor, int TotalCount);

public sealed record ChequeTransitionResponse(ChequeResponse Cheque, PaymentResponse? Payment, AllocationResultResponse? Allocation);

public sealed record WithholdingRequest(MoneyInput? BaseAmount, string? RatePct, MoneyInput? WithheldAmount, Guid? PaymentId, string? CertificateReference, bool? CertificateReceived);

public sealed record WithholdingDto(Guid Id, Guid InvoiceId, Guid? PaymentId, MoneyDto BaseAmount, string RatePct, MoneyDto WithheldAmount, string? CertificateReference, bool CertificateReceived, bool IsActive, DateTimeOffset CreatedAt, Guid? CreatedBy);

public sealed record CreditNoteRequest(Guid? CustomerId, string? NoteNumber, MoneyInput? Amount, string? IssueDate, string? ReasonCode, string? Notes, IReadOnlyList<AllocationLineInput>? Applications);

public sealed record CreditApplicationDto(Guid Id, Guid CreditNoteId, Guid InvoiceId, MoneyDto Amount, string EffectiveDate, bool IsActive, Guid? ReversalOfId, string? ReversalReason, DateTimeOffset CreatedAt, Guid? AppliedBy);

public sealed record CreditNoteResponse(Guid Id, Guid CustomerId, string? NoteNumber, MoneyDto Amount, string Currency, string IssueDate, string ReasonCode, string Status, MoneyDto Unapplied, IReadOnlyList<CreditApplicationDto> Applications, string? Notes, DateTimeOffset CreatedAt, string RowVersion);

public sealed record CreditNoteListResponse(IReadOnlyList<CreditNoteResponse> Items, string? NextCursor, int TotalCount);

public sealed record WriteOffProposeRequest(string? ReasonCode, string? Note);

public sealed record WriteOffApproveRequest(bool? SelfApproved);

public sealed record WriteOffResponse(Guid Id, Guid InvoiceId, MoneyDto Amount, string ReasonCode, string? Note, string Status, Guid ProposedBy, DateTimeOffset ProposedAt, Guid? ApprovedBy, bool SelfApproved, DateTimeOffset? ApprovedAt, Guid? RejectedBy, Guid? ReversedBy, string? ReversalReason, string RowVersion);

public sealed record WriteOffListResponse(IReadOnlyList<WriteOffResponse> Items, string? NextCursor, int TotalCount);

/// <summary>Doc 06 §6.5: the money history panel — "why is the balance this?" (PRD-04).</summary>
public sealed record MoneyHistoryEntry(string Kind, Guid Id, string Date, MoneyDto Amount, string Effect, bool IsActive, string? Reference, string? ReasonCode, Guid? Actor, DateTimeOffset RecordedAt);

public sealed record InvoiceDetailResponse(
    InvoiceResponse Invoice,
    /// <summary>FIN-12: the pure function of the balance.</summary>
    string Settlement,
    IReadOnlyList<MoneyHistoryEntry> History,
    IReadOnlyList<WithholdingDto> Withholding,
    IReadOnlyList<WriteOffResponse> WriteOffs);

public sealed record CustomerPositionDto(string Currency, MoneyDto OpenBalance, int OpenInvoiceCount, MoneyDto UnappliedCash, MoneyDto UnappliedCredit);

// ---------------------------------------------------------------------------------------
// Slice 4 — Aging (doc 05 slice 4). Read-only figures; every amount is a MoneyDto string.
// ---------------------------------------------------------------------------------------

public sealed record AgingBucketDto(string Bucket, MoneyDto Amount, int InvoiceCount, MoneyDto DisputedAmount);

public sealed record AgingCustomerRowDto(Guid CustomerId, string? Code, string? NameAr, string? NameEn, IReadOnlyList<AgingBucketDto> Buckets, MoneyDto Total, MoneyDto DisputedTotal, int InvoiceCount);

public sealed record AgingCurrencyDto(
    string Currency, IReadOnlyList<AgingBucketDto> Buckets, MoneyDto Total, MoneyDto DisputedTotal, int InvoiceCount,
    MoneyDto UnappliedCash, MoneyDto UnappliedCredit, IReadOnlyList<AgingCustomerRowDto>? Customers);

/// <summary>FIN-55 / FIN-06: a converted figure is always marked indicative.</summary>
public sealed record IndicativeMoneyDto(string Amount, string Currency, bool Indicative);

public sealed record AgingReportResponse(
    string AsOf, string Basis, string Timezone, IReadOnlyList<int> BucketBoundaries, IReadOnlyList<string> BucketKeys,
    IReadOnlyList<AgingCurrencyDto> Currencies, IndicativeMoneyDto BaseCurrencyTotal, bool DisputedAvailable, string ExplanationKey);

public sealed record AgedInvoiceDto(Guid InvoiceId, string InvoiceNumber, string Currency, string IssueDate, string DueDate, MoneyDto TotalAmount, MoneyDto OpenBalance, int DaysPastDue, string Bucket);

/// <summary>FIN-61: advisory, with its sample size. <c>averageDaysToPay</c> is days, one decimal, or null.</summary>
public sealed record AgingCustomerDetailResponse(Guid CustomerId, string AsOf, string Basis, IReadOnlyList<AgedInvoiceDto> Invoices, string? AverageDaysToPay, int AverageDaysToPaySampleSize);

/// <summary>FIN-60. <c>dso</c> is null with <c>insufficientHistory: true</c> below 90 days of history.</summary>
public sealed record DsoDto(string Currency, string? Dso, bool InsufficientHistory, MoneyDto ArAtPeriodEnd, MoneyDto CreditSalesInPeriod, int DaysInPeriod, string PeriodStart, string PeriodEnd);

public sealed record DsoResponse(string AsOf, IReadOnlyList<DsoDto> Currencies, string DisclaimerKey);

public sealed record ReconciliationMismatchDto(Guid InvoiceId, string InvoiceNumber, string Rule, MoneyDto Cached, MoneyDto Derived, string Status);

public sealed record ReconciliationResponse(string CheckedAt, int InvoicesChecked, IReadOnlyList<ReconciliationMismatchDto> Mismatches);
