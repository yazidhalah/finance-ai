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

    public DbSet<Customer> Customers => this.Set<Customer>();

    public DbSet<CustomerContact> CustomerContacts => this.Set<CustomerContact>();

    public DbSet<Invoice> Invoices => this.Set<Invoice>();

    public DbSet<ImportMapping> ImportMappings => this.Set<ImportMapping>();

    public DbSet<ImportBatch> ImportBatches => this.Set<ImportBatch>();

    public DbSet<ImportRow> ImportRows => this.Set<ImportRow>();

    public DbSet<Payment> Payments => this.Set<Payment>();

    public DbSet<Cheque> Cheques => this.Set<Cheque>();

    public DbSet<PaymentAllocation> PaymentAllocations => this.Set<PaymentAllocation>();

    public DbSet<WithholdingDeduction> WithholdingDeductions => this.Set<WithholdingDeduction>();

    public DbSet<CreditNote> CreditNotes => this.Set<CreditNote>();

    public DbSet<CreditNoteApplication> CreditNoteApplications => this.Set<CreditNoteApplication>();

    public DbSet<WriteOff> WriteOffs => this.Set<WriteOff>();

    public DbSet<CollectionCase> Cases => this.Set<CollectionCase>();

    public DbSet<CaseInvoice> CaseInvoices => this.Set<CaseInvoice>();

    public DbSet<CaseActivity> CaseActivities => this.Set<CaseActivity>();

    public DbSet<PromiseToPay> Promises => this.Set<PromiseToPay>();

    public DbSet<PtpInvoice> PtpInvoices => this.Set<PtpInvoice>();

    public DbSet<TenantHoliday> Holidays => this.Set<TenantHoliday>();

    public DbSet<Dispute> Disputes => this.Set<Dispute>();

    public DbSet<DisputeEvidence> DisputeEvidence => this.Set<DisputeEvidence>();

    public DbSet<PaymentVerificationTask> VerificationTasks => this.Set<PaymentVerificationTask>();

    public DbSet<MessageTemplate> Templates => this.Set<MessageTemplate>();

    public DbSet<OutboundMessage> Messages => this.Set<OutboundMessage>();

    public DbSet<InboundMessage> InboundMessages => this.Set<InboundMessage>();

    public DbSet<AiSuggestion> AiSuggestions => this.Set<AiSuggestion>();

    public DbSet<DailyBriefing> DailyBriefings => this.Set<DailyBriefing>();

    public DbSet<MemberInvitation> MemberInvitations => this.Set<MemberInvitation>();

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
        ConfigureCustomers(model);
        ConfigureCustomerContacts(model);
        ConfigureInvoices(model);
        ConfigureImport(model);
        ConfigureLedger(model);
        ConfigureCases(model);
        ConfigurePromises(model);
        ConfigureDisputes(model);
        ConfigureMessaging(model);
        ConfigureAi(model);
        ConfigureBriefings(model);
        ConfigureInvitations(model);

        this.ApplyTenantQueryFilters(model);

        // DM-08: a soft-deleted customer is invisible to ordinary queries. Named so it composes with
        // the tenant filter rather than replacing it, and so a query that must see deleted rows
        // (a future "restore") can ignore this one filter by name and still keep the tenant one.
        model.Entity<Customer>().HasQueryFilter("SoftDelete", c => c.DeletedAt == null);
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
        model.Entity<TEntity>().HasQueryFilter("Tenant", e => e.TenantId == this.CurrentTenantId);

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
            e.Property(x => x.BriefingLanguage).HasColumnName("briefing_language").HasColumnType("char(2)");
            e.Property(x => x.BriefingEmailEnabled).HasColumnName("briefing_email_enabled");
            e.Property(x => x.BriefingRecipientUserIds).HasColumnName("briefing_recipient_user_ids");
            e.Property(x => x.DunningCadenceDays).HasColumnName("dunning_cadence_days");
            e.Property(x => x.QuietHoursStart).HasColumnName("quiet_hours_start");
            e.Property(x => x.QuietHoursEnd).HasColumnName("quiet_hours_end");
            e.Property(x => x.BriefingSendAt).HasColumnName("briefing_send_at");
            e.Property(x => x.PriorityWeightsVersion).HasColumnName("priority_weights_version");
            e.Property(x => x.OutboundSendingEnabled).HasColumnName("outbound_sending_enabled");
            e.Property(x => x.DailySendCap).HasColumnName("daily_send_cap");
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

    private static void ConfigureCustomers(ModelBuilder model) =>
        model.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.NameAr).HasColumnName("name_ar");
            e.Property(x => x.NameEn).HasColumnName("name_en");
            e.Property(x => x.LegalName).HasColumnName("legal_name");
            e.Property(x => x.TaxRegistrationNo).HasColumnName("tax_registration_no");
            e.Property(x => x.PreferredLanguage).HasColumnName("preferred_language");
            e.Property(x => x.PaymentTermsDays).HasColumnName("payment_terms_days");
            e.Property(x => x.CreditLimitAmount).HasColumnName("credit_limit_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.CreditLimitCurrency).HasColumnName("credit_limit_currency").HasColumnType("char(3)");
            e.Property(x => x.DefaultCurrency).HasColumnName("default_currency").HasColumnType("char(3)");
            e.Property(x => x.RiskFlag).HasColumnName("risk_flag").HasConversion<string>();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.BrokenPromiseCount12m).HasColumnName("broken_promise_count_12m");
            e.Property(x => x.BouncedChequeCount12m).HasColumnName("bounced_cheque_count_12m");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.NormalizedName).HasColumnName("normalized_name").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

    private static void ConfigureCustomerContacts(ModelBuilder model) =>
        model.Entity<CustomerContact>(e =>
        {
            e.ToTable("customer_contacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.RoleTitle).HasColumnName("role_title");
            e.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
            e.Property(x => x.PhoneE164).HasColumnName("phone_e164");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.IsBilling).HasColumnName("is_billing");
            e.Property(x => x.PreferredLanguage).HasColumnName("preferred_language");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

    private static void ConfigureInvoices(ModelBuilder model) =>
        model.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.InvoiceNumber).HasColumnName("invoice_number");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.IssueDate).HasColumnName("issue_date");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.NetAmount).HasColumnName("net_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.TotalAmount).HasColumnName("total_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.BalanceCache).HasColumnName("balance_cache").HasColumnType("numeric(19,3)");
            e.Property(x => x.FxRateToBase).HasColumnName("fx_rate_to_base").HasColumnType("numeric(18,8)");
            e.Property(x => x.BaseCurrency).HasColumnName("base_currency").HasColumnType("char(3)");
            e.Property(x => x.PoReference).HasColumnName("po_reference");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.Source).HasColumnName("source").HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<InvoiceSource>(v, true));
            e.Property(x => x.ImportBatchId).HasColumnName("import_batch_id");
            e.Property(x => x.ExternalId).HasColumnName("external_id");
            e.Property(x => x.SettledAt).HasColumnName("settled_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Ignore(x => x.Settlement);
        });

    private static void ConfigureImport(ModelBuilder model)
    {
        model.Entity<ImportMapping>(e =>
        {
            e.ToTable("import_mappings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.ColumnMap).HasColumnName("column_map").HasColumnType("jsonb");
            e.Property(x => x.DateFormat).HasColumnName("date_format");
            e.Property(x => x.DecimalSeparator).HasColumnName("decimal_separator").HasColumnType("char(1)");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        model.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.FileName).HasColumnName("file_name");
            e.Property(x => x.FileHash).HasColumnName("file_hash");
            e.Property(x => x.FileSize).HasColumnName("file_size");
            e.Property(x => x.FileKind).HasColumnName("file_kind").HasConversion(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<ImportFileKind>(v, true));
            e.Property(x => x.FileContent).HasColumnName("file_content");
            e.Property(x => x.MappingId).HasColumnName("mapping_id");
            e.Property(x => x.ColumnMap).HasColumnName("column_map").HasColumnType("jsonb");
            e.Property(x => x.DateFormat).HasColumnName("date_format");
            e.Property(x => x.DecimalSeparator).HasColumnName("decimal_separator").HasColumnType("char(1)");
            e.Property(x => x.Headers).HasColumnName("headers").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.RowCount).HasColumnName("row_count");
            e.Property(x => x.AcceptedCount).HasColumnName("accepted_count");
            e.Property(x => x.RejectedCount).HasColumnName("rejected_count");
            e.Property(x => x.DuplicateCount).HasColumnName("duplicate_count");
            e.Property(x => x.WarningCount).HasColumnName("warning_count");
            e.Property(x => x.Forced).HasColumnName("forced");
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.Property(x => x.CommittedAt).HasColumnName("committed_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        model.Entity<ImportRow>(e =>
        {
            e.ToTable("import_rows");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BatchId).HasColumnName("batch_id");
            e.Property(x => x.RowNo).HasColumnName("row_no");
            e.Property(x => x.Raw).HasColumnName("raw").HasColumnType("jsonb");
            e.Property(x => x.Parsed).HasColumnName("parsed").HasColumnType("jsonb");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>();
            e.Property(x => x.ErrorCode).HasColumnName("error_code");
            e.Property(x => x.ErrorDetail).HasColumnName("error_detail");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        });
    }

    private static void ConfigureLedger(ModelBuilder model)
    {
        model.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.Method).HasColumnName("method").HasConversion<string>();
            e.Property(x => x.ReceivedDate).HasColumnName("received_date");
            e.Property(x => x.EffectiveDate).HasColumnName("effective_date");
            e.Property(x => x.Reference).HasColumnName("reference");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.ChequeId).HasColumnName("cheque_id");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.Property(x => x.ReversedAt).HasColumnName("reversed_at");
            e.Property(x => x.ReversalReason).HasColumnName("reversal_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        model.Entity<Cheque>(e =>
        {
            e.ToTable("cheques");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.ChequeNumber).HasColumnName("cheque_number");
            e.Property(x => x.BankName).HasColumnName("bank_name");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.ChequeDate).HasColumnName("cheque_date");
            e.Property(x => x.ReceivedDate).HasColumnName("received_date");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.BouncedReason).HasColumnName("bounced_reason");
            e.Property(x => x.ClearedDate).HasColumnName("cleared_date");
            e.Property(x => x.PtpId).HasColumnName("ptp_id");
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Ignore(x => x.IsPostDated);
        });

        model.Entity<PaymentAllocation>(e =>
        {
            e.ToTable("payment_allocations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.EffectiveDate).HasColumnName("effective_date");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.ReversalOfId).HasColumnName("reversal_of_id");
            e.Property(x => x.ReversalReason).HasColumnName("reversal_reason");
            e.Property(x => x.AllocatedBy).HasColumnName("allocated_by");
            e.Property(x => x.Method).HasColumnName("method").HasConversion(
                v => v == AllocationMethod.AutoExactMatch ? "auto_exact_match" : v == AllocationMethod.ProposedFifo ? "proposed_fifo" : "manual",
                v => v == "auto_exact_match" ? AllocationMethod.AutoExactMatch : v == "proposed_fifo" ? AllocationMethod.ProposedFifo : AllocationMethod.Manual);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        model.Entity<WithholdingDeduction>(e =>
        {
            e.ToTable("withholding_deductions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.BaseAmount).HasColumnName("base_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.RatePct).HasColumnName("rate_pct").HasColumnType("numeric(5,2)");
            e.Property(x => x.WithheldAmount).HasColumnName("withheld_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.CertificateReference).HasColumnName("certificate_reference");
            e.Property(x => x.CertificateReceived).HasColumnName("certificate_received");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.ReversalOfId).HasColumnName("reversal_of_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
        });

        model.Entity<CreditNote>(e =>
        {
            e.ToTable("credit_notes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.NoteNumber).HasColumnName("note_number");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.IssueDate).HasColumnName("issue_date");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.DisputeId).HasColumnName("dispute_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.VoidedAt).HasColumnName("voided_at");
            e.Property(x => x.VoidReason).HasColumnName("void_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        model.Entity<CreditNoteApplication>(e =>
        {
            e.ToTable("credit_note_applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CreditNoteId).HasColumnName("credit_note_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.EffectiveDate).HasColumnName("effective_date");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.ReversalOfId).HasColumnName("reversal_of_id");
            e.Property(x => x.ReversalReason).HasColumnName("reversal_reason");
            e.Property(x => x.AppliedBy).HasColumnName("applied_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        model.Entity<WriteOff>(e =>
        {
            e.ToTable("write_offs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.ProposedBy).HasColumnName("proposed_by");
            e.Property(x => x.ProposedAt).HasColumnName("proposed_at");
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by");
            e.Property(x => x.SelfApproved).HasColumnName("self_approved");
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.RejectedBy).HasColumnName("rejected_by");
            e.Property(x => x.ReversedBy).HasColumnName("reversed_by");
            e.Property(x => x.ReversedAt).HasColumnName("reversed_at");
            e.Property(x => x.ReversalReason).HasColumnName("reversal_reason");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });
    }

    private static void ConfigureCases(ModelBuilder model)
    {
        model.Entity<CollectionCase>(e =>
        {
            e.ToTable("collection_cases");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.CaseNumber).HasColumnName("case_number");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.PriorityScore).HasColumnName("priority_score");
            e.Property(x => x.WeightsVersion).HasColumnName("weights_version");
            e.Property(x => x.PriorityFactors).HasColumnName("priority_factors").HasColumnType("jsonb");
            e.Property(x => x.ScoredAt).HasColumnName("scored_at");
            e.Property(x => x.OverdueBalanceBase).HasColumnName("overdue_balance_base").HasColumnType("numeric(19,3)");
            e.Property(x => x.MaxDaysPastDue).HasColumnName("max_days_past_due");
            e.Property(x => x.InvoiceCount).HasColumnName("invoice_count");
            e.Property(x => x.AssignedTo).HasColumnName("assigned_to");
            e.Property(x => x.OpenedAt).HasColumnName("opened_at");
            e.Property(x => x.NextActionAt).HasColumnName("next_action_at");
            e.Property(x => x.NextActionReason).HasColumnName("next_action_reason");
            e.Property(x => x.HoldUntil).HasColumnName("hold_until");
            e.Property(x => x.HoldReason).HasColumnName("hold_reason");
            e.Property(x => x.EscalatedAt).HasColumnName("escalated_at");
            e.Property(x => x.EscalatedBy).HasColumnName("escalated_by");
            e.Property(x => x.EscalationReason).HasColumnName("escalation_reason");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.CloseReason).HasColumnName("close_reason");
            e.Property(x => x.LastContactAt).HasColumnName("last_contact_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
            e.Ignore(x => x.AutomationDisabled);
        });

        model.Entity<CaseInvoice>(e =>
        {
            e.ToTable("case_invoices");
            e.HasKey(x => new { x.TenantId, x.CaseId, x.InvoiceId });
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CaseId).HasColumnName("case_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.AddedAt).HasColumnName("added_at");
            e.Property(x => x.RemovedAt).HasColumnName("removed_at");
            e.Property(x => x.RemovedReason).HasColumnName("removed_reason");
        });

        model.Entity<CaseActivity>(e =>
        {
            e.ToTable("case_activities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CaseId).HasColumnName("case_id");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.ActorKind).HasColumnName("actor_kind");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.Summary).HasColumnName("summary");
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
        });
    }

    private static void ConfigurePromises(ModelBuilder model)
    {
        model.Entity<PromiseToPay>(e =>
        {
            e.ToTable("promises_to_pay");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CaseId).HasColumnName("case_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.PromisedAmount).HasColumnName("promised_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.PromisedDate).HasColumnName("promised_date");
            e.Property(x => x.DeadlineDate).HasColumnName("deadline_date");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.CapturedBy).HasColumnName("captured_by");
            e.Property(x => x.ConfirmedBy).HasColumnName("confirmed_by");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.ChequeId).HasColumnName("cheque_id");
            e.Property(x => x.SupersededById).HasColumnName("superseded_by_id");
            e.Property(x => x.CancelReason).HasColumnName("cancel_reason");
            e.Property(x => x.EvaluatedAt).HasColumnName("evaluated_at");
            e.Property(x => x.ReceivedInWindow).HasColumnName("received_in_window").HasColumnType("numeric(19,3)");
            e.Property(x => x.EvaluationNote).HasColumnName("evaluation_note");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        model.Entity<PtpInvoice>(e =>
        {
            e.ToTable("ptp_invoices");
            e.HasKey(x => new { x.TenantId, x.PtpId, x.InvoiceId });
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PtpId).HasColumnName("ptp_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
        });

        model.Entity<TenantHoliday>(e =>
        {
            e.ToTable("tenant_holidays");
            e.HasKey(x => new { x.TenantId, x.Date });
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Date).HasColumnName("date");
            e.Property(x => x.Name).HasColumnName("name");
        });
    }

    private static void ConfigureDisputes(ModelBuilder model)
    {
        model.Entity<Dispute>(e =>
        {
            e.ToTable("disputes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.CaseId).HasColumnName("case_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.DisputedAmount).HasColumnName("disputed_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            e.Property(x => x.CustomerClaim).HasColumnName("customer_claim");
            e.Property(x => x.RaisedAt).HasColumnName("raised_at");
            e.Property(x => x.RaisedBy).HasColumnName("raised_by");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.AssignedTo).HasColumnName("assigned_to");
            e.Property(x => x.FirstResponseDueAt).HasColumnName("first_response_due_at");
            e.Property(x => x.FirstResponseAt).HasColumnName("first_response_at");
            e.Property(x => x.ResolutionDueAt).HasColumnName("resolution_due_at");
            e.Property(x => x.PendingSince).HasColumnName("pending_since");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
            e.Property(x => x.ResolutionAmount).HasColumnName("resolution_amount").HasColumnType("numeric(19,3)");
            e.Property(x => x.ResolutionNote).HasColumnName("resolution_note");
            e.Property(x => x.CreditNoteId).HasColumnName("credit_note_id");
            e.Property(x => x.CloseReason).HasColumnName("close_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        model.Entity<DisputeEvidence>(e =>
        {
            e.ToTable("dispute_evidence");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.DisputeId).HasColumnName("dispute_id");
            e.Property(x => x.FileName).HasColumnName("file_name");
            e.Property(x => x.ContentType).HasColumnName("content_type");
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Sha256).HasColumnName("sha256");
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
        });

        model.Entity<PaymentVerificationTask>(e =>
        {
            e.ToTable("payment_verification_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.DisputeId).HasColumnName("dispute_id");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Claim).HasColumnName("claim");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.PaymentId).HasColumnName("payment_id");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
        });
    }

    private static void ConfigureMessaging(ModelBuilder model)
    {
        model.Entity<MessageTemplate>(e =>
        {
            e.ToTable("message_templates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.Language).HasColumnName("language").HasColumnType("char(2)");
            e.Property(x => x.Tone).HasColumnName("tone");
            e.Property(x => x.Subject).HasColumnName("subject");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.IsSystem).HasColumnName("is_system");
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by");
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.Ignore(x => x.IsApproved);
        });

        model.Entity<OutboundMessage>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CaseId).HasColumnName("case_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.ContactId).HasColumnName("contact_id");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.Direction).HasColumnName("direction");
            e.Property(x => x.Language).HasColumnName("language").HasColumnType("char(2)");
            e.Property(x => x.TemplateId).HasColumnName("template_id");
            e.Property(x => x.TemplateKey).HasColumnName("template_key");
            e.Property(x => x.TemplateVersion).HasColumnName("template_version");
            e.Property(x => x.ToAddress).HasColumnName("to_address");
            e.Property(x => x.Subject).HasColumnName("subject");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.InvoiceIds).HasColumnName("invoice_ids");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.ApprovalRequired).HasColumnName("approval_required");
            e.Property(x => x.ApprovalReasons).HasColumnName("approval_reasons");
            e.Property(x => x.ApprovalKind).HasColumnName("approval_kind");
            e.Property(x => x.AiDrafted).HasColumnName("ai_drafted");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.DraftedBy).HasColumnName("drafted_by");
            e.Property(x => x.ApprovedBy).HasColumnName("approved_by");
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.QueuedAt).HasColumnName("queued_at");
            e.Property(x => x.SentBy).HasColumnName("sent_by");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason");
            e.Property(x => x.CancelReason).HasColumnName("cancel_reason");
            e.Property(x => x.WhatsappLinkAt).HasColumnName("whatsapp_link_at");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });
    }

    private static void ConfigureAi(ModelBuilder model)
    {
        model.Entity<InboundMessage>(e =>
        {
            e.ToTable("inbound_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.CaseId).HasColumnName("case_id");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.FromAddress).HasColumnName("from_address");
            e.Property(x => x.Subject).HasColumnName("subject");
            e.Property(x => x.BodyRaw).HasColumnName("body_raw");
            e.Property(x => x.BodyNormalized).HasColumnName("body_normalized");
            e.Property(x => x.DetectedLanguage).HasColumnName("detected_language");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.Property(x => x.InReplyToMessageId).HasColumnName("in_reply_to_message_id");
            e.Property(x => x.MatchConfidence).HasColumnName("match_confidence");
            e.Property(x => x.MatchMethod).HasColumnName("match_method");
            e.Property(x => x.MatchedBy).HasColumnName("matched_by");
            e.Property(x => x.ClassificationStatus).HasColumnName("classification_status").HasConversion<string>();
            e.Property(x => x.Classification).HasColumnName("classification");
            e.Property(x => x.HumanClassification).HasColumnName("human_classification");
            e.Property(x => x.HumanClassifiedBy).HasColumnName("human_classified_by");
            e.Property(x => x.HumanClassifiedAt).HasColumnName("human_classified_at");
            e.Property(x => x.LastSuggestionId).HasColumnName("last_suggestion_id");
            e.Property(x => x.HasAttachments).HasColumnName("has_attachments");
            e.Property(x => x.TruncatedForAi).HasColumnName("truncated_for_ai");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });

        model.Entity<AiSuggestion>(e =>
        {
            e.ToTable("ai_suggestions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Operation).HasColumnName("operation");
            e.Property(x => x.SubjectType).HasColumnName("subject_type");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.ModelName).HasColumnName("model_name");
            e.Property(x => x.ModelDigest).HasColumnName("model_digest");
            e.Property(x => x.PromptVersion).HasColumnName("prompt_version");
            e.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            e.Property(x => x.InputRef).HasColumnName("input_ref").HasColumnType("jsonb");
            e.Property(x => x.InputHash).HasColumnName("input_hash");
            e.Property(x => x.OutputJson).HasColumnName("output_json").HasColumnType("jsonb");
            e.Property(x => x.Confidence).HasColumnName("confidence");
            e.Property(x => x.Classification).HasColumnName("classification");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.ValidationStatus).HasColumnName("validation_status");
            e.Property(x => x.RequiresHumanReview).HasColumnName("requires_human_review");
            e.Property(x => x.Suspicious).HasColumnName("suspicious");
            e.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            e.Property(x => x.OutcomeType).HasColumnName("outcome_type");
            e.Property(x => x.OutcomeId).HasColumnName("outcome_id");
            e.Property(x => x.GuardReason).HasColumnName("guard_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.HumanDecision).HasColumnName("human_decision");
            e.Property(x => x.DecidedBy).HasColumnName("decided_by");
            e.Property(x => x.DecidedAt).HasColumnName("decided_at");
            e.Property(x => x.DecisionReason).HasColumnName("decision_reason");
            e.Property(x => x.HumanCorrection).HasColumnName("human_correction").HasColumnType("jsonb");
        });
    }

    private static void ConfigureBriefings(ModelBuilder model)
    {
        model.Entity<DailyBriefing>(e =>
        {
            e.ToTable("daily_briefings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.BriefingDate).HasColumnName("briefing_date");
            e.Property(x => x.Language).HasColumnName("language").HasColumnType("char(2)");
            e.Property(x => x.MetricsJson).HasColumnName("metrics").HasColumnType("jsonb");
            e.Property(x => x.Narrative).HasColumnName("narrative");
            e.Property(x => x.HighlightsJson).HasColumnName("highlights").HasColumnType("jsonb");
            e.Property(x => x.NarrativeStatusValue).HasColumnName("narrative_status");
            e.Property(x => x.AiSuggestionId).HasColumnName("ai_suggestion_id");
            e.Property(x => x.GeneratedAt).HasColumnName("generated_at");
            e.Property(x => x.GeneratedBy).HasColumnName("generated_by");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.Property(x => x.SentToCount).HasColumnName("sent_to_count");
            e.Property(x => x.TemplateId).HasColumnName("template_id");
            e.Property(x => x.TemplateVersion).HasColumnName("template_version");
            e.Property(x => x.DeliveryStatus).HasColumnName("delivery_status");
            e.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        });
    }

    private static void ConfigureInvitations(ModelBuilder model)
    {
        model.Entity<MemberInvitation>(e =>
        {
            e.ToTable("member_invitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
            e.Property(x => x.Role).HasColumnName("role").HasConversion<string>();
            e.Property(x => x.Locale).HasColumnName("locale");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.InvitedBy).HasColumnName("invited_by");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
            e.Property(x => x.AcceptedUserId).HasColumnName("accepted_user_id");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokedBy).HasColumnName("revoked_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}
