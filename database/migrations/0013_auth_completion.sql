-- 0013 — Auth completion (slice 13): TOTP MFA, recovery codes, password reset, the MFA grace period.
--
-- users, user_recovery_codes and password_reset_tokens are platform tables (DM-06): a person's second
-- factor is theirs, not a tenant's. Their policies follow users: the identity flows (platform scope) and the
-- person themself (app_current_user()) — never a co-member.

ALTER TABLE users
  ADD COLUMN mfa_pending_secret_enc bytea NULL,     -- enrolled but not yet verified; replaced by a new enrolment
  ADD COLUMN mfa_enabled_at        timestamptz NULL; -- set by /auth/mfa/verify; mfa_secret_enc holds the active secret

CREATE TABLE user_recovery_codes (
  id         uuid PRIMARY KEY,
  user_id    uuid NOT NULL REFERENCES users(id),
  code_hash  text NOT NULL CHECK (code_hash ~ '^[0-9a-f]{64}$'),
  used_at    timestamptz NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX user_recovery_codes_user_idx ON user_recovery_codes (user_id) WHERE used_at IS NULL;

CREATE TABLE password_reset_tokens (
  id         uuid PRIMARY KEY,
  user_id    uuid NOT NULL REFERENCES users(id),
  token_hash text NOT NULL UNIQUE CHECK (token_hash ~ '^[0-9a-f]{64}$'),
  expires_at timestamptz NOT NULL,
  used_at    timestamptz NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE user_recovery_codes   ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_recovery_codes   FORCE  ROW LEVEL SECURITY;
ALTER TABLE password_reset_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE password_reset_tokens FORCE  ROW LEVEL SECURITY;

CREATE POLICY own_or_platform ON user_recovery_codes
  USING      (app_platform_scope() OR user_id = app_current_user())
  WITH CHECK (app_platform_scope() OR user_id = app_current_user());
-- Reset tokens are touched only by the anonymous identity flows.
CREATE POLICY platform_only ON password_reset_tokens
  USING (app_platform_scope()) WITH CHECK (app_platform_scope());

GRANT SELECT, INSERT, UPDATE ON user_recovery_codes, password_reset_tokens TO finance_app;

-- SEC-02 with a grace period (slice 13 D-1): Owner and Admin memberships must enrol within seven days.
ALTER TABLE tenant_memberships ADD COLUMN mfa_grace_until timestamptz NULL;
UPDATE tenant_memberships SET mfa_grace_until = now() + interval '7 days' WHERE role IN ('Owner','Admin');

-- A password reset ends every session the user has (slice 13 S4).
ALTER TABLE refresh_tokens DROP CONSTRAINT refresh_tokens_revoked_reason_check;
ALTER TABLE refresh_tokens ADD CONSTRAINT refresh_tokens_revoked_reason_check
  CHECK (revoked_reason IN ('rotated','logout','reuse_detected','superseded','membership_deactivated','password_reset'));
