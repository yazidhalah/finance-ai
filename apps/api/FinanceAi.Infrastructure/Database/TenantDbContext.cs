using System.Reflection;
using FinanceAi.Domain.Abstractions;
using FinanceAi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Database;

/// <summary>
/// Layer 1 of the isolation strategy (ADR-0001): every <see cref="ITenantScoped"/> entity gets a
/// global query filter automatically, and every insert is stamped with the current tenant. A new
/// entity that implements the interface is covered without anyone remembering to do anything.
/// <para>
/// This layer catches the common case. It is not trusted on its own: layer 2 (RLS, forced) and
/// layer 3 (composite foreign keys) are proved to work independently of it by AC-32 and AC-33.
/// </para>
/// </summary>
public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public Guid CurrentTenantId => tenant.TenantIdOrEmpty;

    public DbSet<Tenant> Tenants => this.Set<Tenant>();

    public DbSet<User> Users => this.Set<User>();

    public DbSet<TenantMembership> TenantMemberships => this.Set<TenantMembership>();

    public DbSet<TenantSettings> TenantSettings => this.Set<TenantSettings>();

    public DbSet<RefreshToken> RefreshTokens => this.Set<RefreshToken>();

    public DbSet<AuditEvent> AuditEvents => this.Set<AuditEvent>();

    public override int SaveChanges()
    {
        this.StampTenant();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        this.StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        ConfigureTenants(model);
        ConfigureUsers(model);
        ConfigureMemberships(model);
        ConfigureSettings(model);
        ConfigureRefreshTokens(model);
        ConfigureAuditEvents(model);

        this.ApplyTenantQueryFilters(model);
    }

    /// <summary>
    /// Adds <c>WHERE tenant_id = @currentTenant</c> to every <see cref="ITenantScoped"/> entity by
    /// reflection, so the filter cannot be forgotten when a table is added in a later slice.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder model)
    {
        var apply = typeof(TenantDbContext).GetMethod(
            nameof(ApplyTenantQueryFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var entity in model.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            {
                apply.MakeGenericMethod(entity.ClrType).Invoke(this, [model]);
            }
        }
    }

    /// <summary>
    /// The filter closes over the context instance rather than over a tenant id value, so EF
    /// parameterizes it per query instead of baking one tenant into the cached model.
    /// </summary>
    private void ApplyTenantQueryFilter<TEntity>(ModelBuilder model)
        where TEntity : class, ITenantScoped =>
        model.Entity<TEntity>().HasQueryFilter(e => e.TenantId == this.CurrentTenantId);

    private void StampTenant()
    {
        foreach (var entry in this.ChangeTracker.Entries<ITenantScoped>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = tenant.TenantId;
            }
        }
    }

    private static void ConfigureTenants(ModelBuilder model) =>
        model.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.LegalName).HasColumnName("legal_name");
            e.Property(x => x.TaxRegistrationNo).HasColumnName("tax_registration_no");
            e.Property(x => x.BaseCurrency).HasColumnName("base_currency").HasColumnType("char(3)");
            e.Property(x => x.Timezone).HasColumnName("timezone");
            e.Property(x => x.DefaultLocale).HasColumnName("default_locale");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

    private static void ConfigureUsers(ModelBuilder model) =>
        model.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
            e.Property(x => x.EmailVerifiedAt).HasColumnName("email_verified_at");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.FullName).HasColumnName("full_name");
            e.Property(x => x.PreferredLocale).HasColumnName("preferred_locale");
            e.Property(x => x.MfaSecretEnc).HasColumnName("mfa_secret_enc");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

    private static void ConfigureMemberships(ModelBuilder model) =>
        model.Entity<TenantMembership>(e =>
        {
            e.ToTable("tenant_memberships");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Role).HasColumnName("role").HasConversion<string>();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.InvitedBy).HasColumnName("invited_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

    private static void ConfigureSettings(ModelBuilder model) =>
        model.Entity<TenantSettings>(e =>
        {
            e.ToTable("tenant_settings");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AgingBasis).HasColumnName("aging_basis");
            e.Property(x => x.AgingBucketDays).HasColumnName("aging_bucket_days");
            e.Property(x => x.GraceDaysBeforeCase).HasColumnName("grace_days_before_case");
            e.Property(x => x.PtpGraceBusinessDays).HasColumnName("ptp_grace_business_days");
            e.Property(x => x.PtpPartialThresholdPct).HasColumnName("ptp_partial_threshold_pct");
            e.Property(x => x.RequireApprovalBeforeSend).HasColumnName("require_approval_before_send");
            e.Property(x => x.ExactMatchAutoAllocation).HasColumnName("exact_match_auto_allocation");
            e.Property(x => x.AutoClearResidualBelow).HasColumnName("auto_clear_residual_below");
            e.Property(x => x.AllowSplitDunningDuringDispute).HasColumnName("allow_split_dunning_during_dispute");
            e.Property(x => x.CollectorSeesOnlyAssigned).HasColumnName("collector_sees_only_assigned");
            e.Property(x => x.AiEnabled).HasColumnName("ai_enabled");
            e.Property(x => x.AiMinConfidence).HasColumnName("ai_min_confidence");
            e.Property(x => x.DunningCadenceDays).HasColumnName("dunning_cadence_days");
            e.Property(x => x.QuietHoursStart).HasColumnName("quiet_hours_start");
            e.Property(x => x.QuietHoursEnd).HasColumnName("quiet_hours_end");
            e.Property(x => x.BriefingSendAt).HasColumnName("briefing_send_at");
            e.Property(x => x.PriorityWeightsVersion).HasColumnName("priority_weights_version");
        });

    private static void ConfigureRefreshTokens(ModelBuilder model) =>
        model.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.FamilyId).HasColumnName("family_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RotatedAt).HasColumnName("rotated_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokedReason).HasColumnName("revoked_reason");
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

    private static void ConfigureAuditEvents(ModelBuilder model) =>
        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.ActorKind).HasColumnName("actor_kind");
            e.Property(x => x.ActorIp).HasColumnName("actor_ip");
            e.Property(x => x.EventType).HasColumnName("event_type");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.FromState).HasColumnName("from_state");
            e.Property(x => x.ToState).HasColumnName("to_state");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.Changes).HasColumnName("changes").HasColumnType("jsonb");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.RequestId).HasColumnName("request_id");
            e.Property(x => x.PrevHash).HasColumnName("prev_hash");
            e.Property(x => x.Hash).HasColumnName("hash");
        });
}
