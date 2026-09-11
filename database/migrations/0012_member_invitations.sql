-- 0012 — Member invitations (slice 12).
--
-- The row holds the SHA-256 of the token, never the token (D-1). The accept path is the one place a
-- lookup crosses tenants by design (it runs before the invitee has a session); it lives in
-- PlatformIdentityStore like registration and login, and the slice 12 review answers question 3 for it.

CREATE TABLE member_invitations (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES tenants(id),
  email             citext NOT NULL,
  role              text NOT NULL CHECK (role IN ('Admin','Accountant','Collector','Viewer')),   -- never Owner (D-3)
  locale            text NOT NULL CHECK (locale IN ('ar-JO','en-JO')),
  token_hash        text NOT NULL CHECK (token_hash ~ '^[0-9a-f]{64}$'),
  invited_by        uuid NOT NULL REFERENCES users(id),
  expires_at        timestamptz NOT NULL,
  accepted_at       timestamptz NULL,
  accepted_user_id  uuid NULL REFERENCES users(id),
  revoked_at        timestamptz NULL,
  revoked_by        uuid NULL,
  created_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT member_invitations_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT member_invitations_token_uq UNIQUE (token_hash),
  CONSTRAINT accepted_has_user CHECK ((accepted_at IS NULL) = (accepted_user_id IS NULL))
);
CREATE INDEX member_invitations_pending_idx ON member_invitations (tenant_id, email) WHERE accepted_at IS NULL AND revoked_at IS NULL;

ALTER TABLE member_invitations ENABLE ROW LEVEL SECURITY;
ALTER TABLE member_invitations FORCE  ROW LEVEL SECURITY;
-- Like refresh_tokens: the identity flow that accepts an invitation runs before a tenant is known, so the
-- platform scope may read the row by its token hash (and only PlatformIdentityStore may open that scope).
CREATE POLICY tenant_isolation ON member_invitations
  USING      (tenant_id = app_current_tenant() OR app_platform_scope())
  WITH CHECK (tenant_id = app_current_tenant() OR app_platform_scope());
GRANT SELECT, INSERT, UPDATE ON member_invitations TO finance_app;
GRANT SELECT ON member_invitations TO finance_reporting;

-- Deactivating a member ends their sessions in that tenant (slice 12 S5): a fifth revocation reason.
ALTER TABLE refresh_tokens DROP CONSTRAINT refresh_tokens_revoked_reason_check;
ALTER TABLE refresh_tokens ADD CONSTRAINT refresh_tokens_revoked_reason_check
  CHECK (revoked_reason IN ('rotated','logout','reuse_detected','superseded','membership_deactivated'));
