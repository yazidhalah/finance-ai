-- 0018 — Email completion (slice 24): verification tokens, tenant SMTP settings, bounce reasons.

-- Verification links: a platform table shaped like password_reset_tokens (DM-06), touched only by the anonymous
-- identity flows.
CREATE TABLE email_verification_tokens (
  id         uuid PRIMARY KEY,
  user_id    uuid NOT NULL REFERENCES users(id),
  token_hash text NOT NULL UNIQUE CHECK (token_hash ~ '^[0-9a-f]{64}$'),
  expires_at timestamptz NOT NULL,
  used_at    timestamptz NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE email_verification_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE email_verification_tokens FORCE  ROW LEVEL SECURITY;
CREATE POLICY platform_only ON email_verification_tokens USING (app_platform_scope()) WITH CHECK (app_platform_scope());
GRANT SELECT, INSERT, UPDATE ON email_verification_tokens TO finance_app;

-- Tenant SMTP (doc 05 /organization/email-settings; SEC-67): the password is sealed by the MFA KEK envelope and is
-- write-only through the API. One row per tenant.
CREATE TABLE tenant_email_settings (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  smtp_host      text NOT NULL CHECK (length(smtp_host) BETWEEN 1 AND 253),
  smtp_port      int  NOT NULL CHECK (smtp_port BETWEEN 1 AND 65535),
  smtp_tls       boolean NOT NULL DEFAULT true,
  smtp_username  text NULL CHECK (smtp_username IS NULL OR length(smtp_username) <= 200),
  smtp_password_enc bytea NULL,
  from_address   text NOT NULL CHECK (length(from_address) BETWEEN 3 AND 254),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     uuid NULL,
  row_version    bigint NOT NULL DEFAULT 1,
  CONSTRAINT tenant_email_settings_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT tenant_email_settings_one_per_tenant UNIQUE (tenant_id)
);
ALTER TABLE tenant_email_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_email_settings FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON tenant_email_settings USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
GRANT SELECT, INSERT, UPDATE ON tenant_email_settings TO finance_app;   -- no DELETE: settings are replaced, never removed
-- Reporting never sees credentials, sealed or not: no grant.

-- The MTA's verdict (slice 8 D-6, now live): why a message bounced.
ALTER TABLE messages ADD COLUMN bounce_reason text NULL CHECK (bounce_reason IS NULL OR length(bounce_reason) <= 500);
ALTER TABLE messages ADD COLUMN delivered_at timestamptz NULL;
