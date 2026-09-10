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
