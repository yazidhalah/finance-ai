namespace FinanceAi.Domain.Abstractions;

/// <summary>
/// Marks an entity that belongs to exactly one tenant. Layer 1 of the isolation strategy
/// (ADR-0001): <c>TenantDbContext</c> applies a global query filter to every
/// <see cref="ITenantScoped"/> entity and stamps <see cref="TenantId"/> on save, so a query
/// cannot be written without a tenant filter. Layers 2 (RLS) and 3 (composite foreign keys)
/// live in the migrations and hold even when this one is bypassed.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
