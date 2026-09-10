using FinanceAi.Domain.Authorization;

namespace FinanceAi.Domain.Entities;

public enum TenantStatus { Active, Suspended, Closed }

/// <summary>
/// An Organization: one paying SME, and the isolation boundary for all business data.
/// "Organization" is the UI term; <c>tenants</c> is the table (docs/README glossary).
/// A platform table (DM-06): it has no <c>tenant_id</c> of its own.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? LegalName { get; set; }
    public string? TaxRegistrationNo { get; set; }

    /// <summary>ISO 4217. Immutable after the first invoice exists (doc 06 §6.1).</summary>
    public string BaseCurrency { get; set; } = "JOD";

    /// <summary>IANA zone; all business-day logic is Asia/Amman by default (A-10).</summary>
    public string Timezone { get; set; } = "Asia/Amman";

    public string DefaultLocale { get; set; } = Locales.Default;
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optimistic concurrency token; the value a client sends back in <c>If-Match</c> (API-09).</summary>
    public long RowVersion { get; set; } = 1;
}

/// <summary>The two locales of v1 (UI-10, ADR-0005). Arabic is the default (PRD-03).</summary>
public static class Locales
{
    public const string Arabic = "ar-JO";
    public const string English = "en-JO";
    public const string Default = Arabic;

    public static readonly IReadOnlyList<string> All = [Arabic, English];

    public static bool IsSupported(string? locale) =>
        locale is not null && All.Contains(locale, StringComparer.Ordinal);
}
