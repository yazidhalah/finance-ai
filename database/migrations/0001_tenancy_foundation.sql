-- 0001 — Tenancy foundation.
--
-- Establishes the three-layer isolation machinery of ADR-0001 before any table exists that
-- needs it. Layer 2 (RLS) and layer 3 (composite foreign keys) live here; layer 1 (the EF
-- Core global query filter) lives in TenantDbContext.
--
-- Forward-only (DM-34). Applied as finance_migrator, which owns everything it creates.

-- ---------------------------------------------------------------------------------------
-- The tenant context (doc 04 §1.1)
-- ---------------------------------------------------------------------------------------

-- Reads the transaction-local GUC set by the request pipeline. Returns NULL when unset, so
-- every policy built on it evaluates to NULL -> not true -> zero rows. Fail closed, never
-- fail open (DM-05, AC-30).
--
-- Deliberately NOT security definer: it must run with the caller's privileges.
CREATE OR REPLACE FUNCTION app_current_tenant() RETURNS uuid
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT nullif(current_setting('app.tenant_id', true), '')::uuid $$;

CREATE OR REPLACE FUNCTION app_current_user() RETURNS uuid
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT nullif(current_setting('app.user_id', true), '')::uuid $$;

-- The one documented escape from tenant scope, for identity flows that necessarily run
-- before a tenant is known: login, refresh, register, "which organizations do I belong to".
-- It is set transaction-locally by PlatformScope and by nothing else; a static test asserts
-- that only PlatformIdentityStore may open it (AC-35).
--
-- It grants no access to any tenant-scoped business table — audit_events and tenant_settings
-- have no platform policy, so even an identity flow cannot read them without a real tenant.
CREATE OR REPLACE FUNCTION app_platform_scope() RETURNS boolean
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT coalesce(nullif(current_setting('app.platform_scope', true), ''), 'off') = 'on' $$;

-- ---------------------------------------------------------------------------------------
-- Platform tables (DM-06): no tenant_id of their own
-- ---------------------------------------------------------------------------------------

CREATE TABLE tenants (
  id                  uuid PRIMARY KEY,
  name                text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
  legal_name          text NULL,
  tax_registration_no text NULL,                 -- Jordanian TIN, free text, not validated in v1
  base_currency       char(3) NOT NULL DEFAULT 'JOD' CHECK (base_currency ~ '^[A-Z]{3}$'),
  timezone            text NOT NULL DEFAULT 'Asia/Amman',
  default_locale      text NOT NULL DEFAULT 'ar-JO' CHECK (default_locale IN ('ar-JO','en-JO')),
  status              text NOT NULL DEFAULT 'Active'
                      CHECK (status IN ('Active','Suspended','Closed')),
  created_at          timestamptz NOT NULL DEFAULT now(),
  row_version         bigint NOT NULL DEFAULT 1    -- optimistic concurrency (API-09)
);

CREATE TABLE users (                              -- global identity, not tenant-scoped
  id                 uuid PRIMARY KEY,
  email              citext NOT NULL UNIQUE,
  email_verified_at  timestamptz NULL,
  password_hash      text NULL,                   -- Argon2id (SEC-01); NULL if SSO-only later
  full_name          text NOT NULL CHECK (length(btrim(full_name)) BETWEEN 1 AND 200),
  preferred_locale   text NOT NULL DEFAULT 'ar-JO' CHECK (preferred_locale IN ('ar-JO','en-JO')),
  mfa_secret_enc     bytea NULL,                  -- reserved for TOTP (SEC-02), slice 1b
  status             text NOT NULL DEFAULT 'Active'
                     CHECK (status IN ('Invited','Active','Disabled')),
  failed_login_count int NOT NULL DEFAULT 0 CHECK (failed_login_count >= 0),
  locked_until       timestamptz NULL,
  created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tenant_memberships (
  id         uuid PRIMARY KEY,
  tenant_id  uuid NOT NULL REFERENCES tenants(id),
  user_id    uuid NOT NULL REFERENCES users(id),
  role       text NOT NULL CHECK (role IN ('Owner','Admin','Accountant','Collector','Viewer')),
  status     text NOT NULL DEFAULT 'Active' CHECK (status IN ('Invited','Active','Disabled')),
  invited_by uuid NULL REFERENCES users(id),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, user_id),
  CONSTRAINT tenant_memberships_tenant_id_key UNIQUE (tenant_id, id)
);

-- Exactly one Owner per tenant, enforced by the database rather than by application code.
CREATE UNIQUE INDEX one_owner_per_tenant
  ON tenant_memberships (tenant_id) WHERE role = 'Owner' AND status <> 'Disabled';

CREATE INDEX tenant_memberships_user_idx ON tenant_memberships (user_id);

-- Opaque rotating refresh tokens with family revocation (SEC-04). Deviation D-2: not in doc 04.
CREATE TABLE refresh_tokens (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  user_id        uuid NOT NULL REFERENCES users(id),
  family_id      uuid NOT NULL,
  token_hash     text NOT NULL UNIQUE,            -- SHA-256 of the raw token; never the raw value
  issued_at      timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz NOT NULL,
  rotated_at     timestamptz NULL,
  revoked_at     timestamptz NULL,
  revoked_reason text NULL
                 CHECK (revoked_reason IN ('rotated','logout','reuse_detected','superseded')),
  CONSTRAINT refresh_tokens_tenant_id_key UNIQUE (tenant_id, id),
  -- Layer 3: a session can only exist for a real membership of that exact tenant. A token for
  -- tenant B belonging to a user who is only a member of tenant A is unrepresentable (AC-33).
  CONSTRAINT fk_refresh_token_membership
    FOREIGN KEY (tenant_id, user_id) REFERENCES tenant_memberships (tenant_id, user_id)
);

CREATE INDEX refresh_tokens_family_idx ON refresh_tokens (family_id);
CREATE INDEX refresh_tokens_expiry_idx ON refresh_tokens (expires_at) WHERE revoked_at IS NULL;

-- ---------------------------------------------------------------------------------------
-- Tenant-scoped tables
-- ---------------------------------------------------------------------------------------

CREATE TABLE tenant_settings (       -- one row per tenant, typed columns, not a JSON bag
  tenant_id                          uuid PRIMARY KEY REFERENCES tenants(id),
  aging_basis                        text NOT NULL DEFAULT 'due_date'
                                     CHECK (aging_basis IN ('due_date','issue_date')),
  aging_bucket_days                  int[] NOT NULL DEFAULT '{30,60,90}',
  grace_days_before_case             int NOT NULL DEFAULT 3,
  ptp_grace_business_days            int NOT NULL DEFAULT 2,
  ptp_partial_threshold_pct          numeric(5,2) NOT NULL DEFAULT 50.00,
  require_approval_before_send       boolean NOT NULL DEFAULT true,     -- PRD-15
  exact_match_auto_allocation        boolean NOT NULL DEFAULT true,     -- FIN-26
  auto_clear_residual_below          numeric(19,3) NOT NULL DEFAULT 0.100,
  allow_split_dunning_during_dispute boolean NOT NULL DEFAULT false,    -- SM-25
  collector_sees_only_assigned       boolean NOT NULL DEFAULT false,    -- PRD-14
  ai_enabled                         boolean NOT NULL DEFAULT true,
  ai_min_confidence                  numeric(4,3) NOT NULL DEFAULT 0.700,
  dunning_cadence_days               int[] NOT NULL DEFAULT '{0,7,14,30}',
  quiet_hours_start                  time NOT NULL DEFAULT '20:00',
  quiet_hours_end                    time NOT NULL DEFAULT '08:00',
  briefing_send_at                   time NOT NULL DEFAULT '07:30',
  priority_weights_version           int NOT NULL DEFAULT 1
);

CREATE TABLE audit_events (
  id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id        uuid NOT NULL REFERENCES tenants(id),
  occurred_at      timestamptz NOT NULL DEFAULT now(),
  actor_user_id    uuid NULL REFERENCES users(id),
  actor_kind       text NOT NULL CHECK (actor_kind IN ('user','system','ai_assisted','support')),
  actor_ip         inet NULL,
  event_type       text NOT NULL,
  entity_type      text NOT NULL,
  entity_id        uuid NOT NULL,
  from_state       text NULL,
  to_state         text NULL,
  reason_code      text NULL,
  note             text NULL,
  changes          jsonb NULL,            -- {field: {old, new}}, money as strings (DM-28)
  ai_suggestion_id uuid NULL,
  request_id       text NULL,
  prev_hash        text NULL,             -- SEC-53 tamper-evident per-tenant chain
  hash             text NOT NULL,
  CONSTRAINT audit_events_tenant_id_key UNIQUE (tenant_id, id)
);

CREATE INDEX audit_entity_idx ON audit_events (tenant_id, entity_type, entity_id, occurred_at DESC);
CREATE INDEX audit_time_idx   ON audit_events (tenant_id, occurred_at DESC);

-- Append-only (SEC-51, DM-28). Privileges are revoked below; the trigger is the second layer
-- and binds the table owner too, so tampering requires disabling it — which is itself visible.
CREATE OR REPLACE FUNCTION audit_events_append_only() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'audit_events is append-only (SEC-51): % is not permitted', TG_OP
    USING ERRCODE = 'restrict_violation';
END
$$;

CREATE TRIGGER audit_events_append_only_trg
  BEFORE UPDATE OR DELETE ON audit_events
  FOR EACH ROW EXECUTE FUNCTION audit_events_append_only();

-- ---------------------------------------------------------------------------------------
-- Layer 2 — Row-level security. ENABLE and FORCE on everything (DM-02).
-- FORCE matters: without it the table owner is exempt and the control is theatre.
-- ---------------------------------------------------------------------------------------

ALTER TABLE tenants             ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants             FORCE  ROW LEVEL SECURITY;
ALTER TABLE users               ENABLE ROW LEVEL SECURITY;
ALTER TABLE users               FORCE  ROW LEVEL SECURITY;
ALTER TABLE tenant_memberships  ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_memberships  FORCE  ROW LEVEL SECURITY;
ALTER TABLE refresh_tokens      ENABLE ROW LEVEL SECURITY;
ALTER TABLE refresh_tokens      FORCE  ROW LEVEL SECURITY;
ALTER TABLE tenant_settings     ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_settings     FORCE  ROW LEVEL SECURITY;
ALTER TABLE audit_events        ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_events        FORCE  ROW LEVEL SECURITY;

-- An organization is visible to a request scoped to it, and to the identity flows.
CREATE POLICY tenant_isolation ON tenants
  USING      (id = app_current_tenant() OR app_platform_scope())
  WITH CHECK (id = app_current_tenant() OR app_platform_scope());

-- A user row is visible to the identity flows, and, inside a tenant-scoped request, only to
-- co-members of that tenant. So "list the members of my organization" cannot reach a stranger.
CREATE POLICY users_visible_to_co_members ON users
  USING (
    app_platform_scope()
    OR EXISTS (SELECT 1 FROM tenant_memberships m
               WHERE m.user_id = users.id AND m.tenant_id = app_current_tenant()))
  WITH CHECK (app_platform_scope());

-- ...and a user may always update their own profile (PATCH /me) inside a tenant-scoped request.
CREATE POLICY users_self_update ON users FOR UPDATE
  USING      (id = app_current_user())
  WITH CHECK (id = app_current_user());

CREATE POLICY tenant_isolation ON tenant_memberships
  USING      (tenant_id = app_current_tenant() OR app_platform_scope())
  WITH CHECK (tenant_id = app_current_tenant() OR app_platform_scope());

CREATE POLICY tenant_isolation ON refresh_tokens
  USING      (tenant_id = app_current_tenant() OR app_platform_scope())
  WITH CHECK (tenant_id = app_current_tenant() OR app_platform_scope());

-- No platform escape below this line: business data requires a real tenant, always.
CREATE POLICY tenant_isolation ON tenant_settings
  USING      (tenant_id = app_current_tenant())
  WITH CHECK (tenant_id = app_current_tenant());

CREATE POLICY tenant_isolation ON audit_events
  USING      (tenant_id = app_current_tenant())
  WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges: the application gets DML on what it needs and nothing more.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE         ON tenants            TO finance_app;
GRANT SELECT, INSERT, UPDATE         ON users              TO finance_app;
GRANT SELECT, INSERT, UPDATE         ON tenant_memberships TO finance_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON refresh_tokens     TO finance_app;
GRANT SELECT, INSERT, UPDATE         ON tenant_settings    TO finance_app;

-- audit_events: INSERT and SELECT only. UPDATE, DELETE and TRUNCATE are never granted (DM-28).
GRANT SELECT, INSERT ON audit_events TO finance_app;
GRANT USAGE ON SEQUENCE audit_events_id_seq TO finance_app;

GRANT SELECT ON tenants, users, tenant_memberships, tenant_settings, audit_events
  TO finance_reporting;
