-- 0009 — Email templates & reminders (slice 8): message_templates, messages, and two settings.
--
-- Same pattern as 0001–0008. The invariant the database holds on its own is INV-13 for messages:
-- nothing reaches Queued / Sent without a human's id in approved_by (sent_requires_approval).
-- The body is frozen text (DM-25): it is never re-rendered from the template that produced it.

-- ---------------------------------------------------------------------------------------
-- tenant_settings: the kill switch (SEC-103) and the daily cap (SEC-86)
-- ---------------------------------------------------------------------------------------

ALTER TABLE tenant_settings
  ADD COLUMN outbound_sending_enabled boolean NOT NULL DEFAULT true,
  ADD COLUMN daily_send_cap int NOT NULL DEFAULT 200 CHECK (daily_send_cap >= 0);

-- ---------------------------------------------------------------------------------------
-- message_templates (UI-13): one row per language, a new version on every content change
-- ---------------------------------------------------------------------------------------

CREATE TABLE message_templates (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  key           text NOT NULL CHECK (key ~ '^[a-z][a-z0-9_]{1,60}$'),
  channel       text NOT NULL CHECK (channel IN ('email','whatsapp')),
  language      char(2) NOT NULL CHECK (language IN ('ar','en')),
  tone          text NOT NULL DEFAULT 'polite' CHECK (tone IN ('polite','neutral','firm','final')),
  subject       text NULL CHECK (subject IS NULL OR length(subject) <= 300),
  body          text NOT NULL CHECK (length(body) BETWEEN 1 AND 10000),
  version       int NOT NULL DEFAULT 1 CHECK (version >= 1),
  status        text NOT NULL DEFAULT 'Draft' CHECK (status IN ('Draft','Approved')),
  is_active     boolean NOT NULL DEFAULT true,
  is_system     boolean NOT NULL DEFAULT false,
  approved_by   uuid NULL,
  approved_at   timestamptz NULL,
  created_at    timestamptz NOT NULL DEFAULT now(),
  created_by    uuid NULL,
  deleted_at    timestamptz NULL,
  CONSTRAINT message_templates_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT template_version_uq UNIQUE (tenant_id, key, channel, language, version),
  CONSTRAINT approved_has_approver CHECK (status <> 'Approved' OR (approved_by IS NOT NULL AND approved_at IS NOT NULL))
);
CREATE INDEX message_templates_key_idx ON message_templates (tenant_id, key, channel, language) WHERE deleted_at IS NULL;

-- ---------------------------------------------------------------------------------------
-- messages (outbound). direction is fixed here; inbound_messages arrive with slice 9.
-- ---------------------------------------------------------------------------------------

CREATE TABLE messages (
  id                  uuid PRIMARY KEY,
  tenant_id           uuid NOT NULL REFERENCES tenants(id),
  case_id             uuid NULL,
  customer_id         uuid NOT NULL,
  contact_id          uuid NULL,
  channel             text NOT NULL CHECK (channel IN ('email','whatsapp_click_to_chat')),
  direction           text NOT NULL DEFAULT 'outbound' CHECK (direction = 'outbound'),
  language            char(2) NOT NULL CHECK (language IN ('ar','en')),
  template_id         uuid NULL,
  template_key        text NULL,
  template_version    int NULL,
  to_address          text NULL,                          -- the email or E.164 number the message went to
  subject             text NULL,
  body                text NOT NULL,                      -- final rendered text, frozen (DM-25, SEC-52)
  invoice_ids         uuid[] NOT NULL DEFAULT '{}',
  status              text NOT NULL DEFAULT 'Draft' CHECK (status IN
                      ('Draft','PendingApproval','Approved','Queued','Sent','Delivered',
                       'Bounced','Failed','Cancelled','PreparedForManualSend')),
  approval_required   boolean NOT NULL DEFAULT true,
  approval_reasons    text[] NOT NULL DEFAULT '{}',        -- why a human must approve: tenant_setting, first_message, free_text, final_tone, ai_drafted
  approval_kind       text NULL CHECK (approval_kind IS NULL OR approval_kind IN ('message','sender','template')),
  ai_drafted          boolean NOT NULL DEFAULT false,
  ai_suggestion_id    uuid NULL,
  drafted_by          uuid NULL,                          -- NULL when the cadence drafted it
  approved_by         uuid NULL,                          -- PRD-15
  approved_at         timestamptz NULL,
  queued_at           timestamptz NULL,
  sent_by             uuid NULL,
  sent_at             timestamptz NULL,
  attempts            int NOT NULL DEFAULT 0,
  next_attempt_at     timestamptz NULL,
  provider_message_id text NULL,
  failure_reason      text NULL,
  cancel_reason       text NULL,
  whatsapp_link_at    timestamptz NULL,
  idempotency_key     text NULL,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  row_version         bigint NOT NULL DEFAULT 1,
  CONSTRAINT messages_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_message_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_message_case     FOREIGN KEY (tenant_id, case_id)     REFERENCES collection_cases (tenant_id, id),
  CONSTRAINT fk_message_contact  FOREIGN KEY (tenant_id, contact_id)  REFERENCES customer_contacts (tenant_id, id),
  CONSTRAINT fk_message_template FOREIGN KEY (tenant_id, template_id) REFERENCES message_templates (tenant_id, id),
  -- INV-13: nothing leaves without a human's id on it.
  CONSTRAINT sent_requires_approval CHECK (status NOT IN ('Queued','Sent','Delivered') OR (approved_by IS NOT NULL AND approval_kind IS NOT NULL)),
  CONSTRAINT ai_drafted_has_suggestion CHECK (NOT ai_drafted OR ai_suggestion_id IS NOT NULL)
);
CREATE INDEX messages_customer_idx ON messages (tenant_id, customer_id, created_at DESC);
CREATE INDEX messages_case_idx ON messages (tenant_id, case_id) WHERE case_id IS NOT NULL;
CREATE INDEX messages_queue_idx ON messages (tenant_id, next_attempt_at) WHERE status = 'Queued';
CREATE INDEX messages_sent_idx ON messages (tenant_id, sent_at) WHERE sent_at IS NOT NULL;
CREATE UNIQUE INDEX messages_idempotency_uq ON messages (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE message_templates ENABLE ROW LEVEL SECURITY;
ALTER TABLE message_templates FORCE  ROW LEVEL SECURITY;
ALTER TABLE messages          ENABLE ROW LEVEL SECURITY;
ALTER TABLE messages          FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON message_templates USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON messages          USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. A template is versioned or soft-deleted; a message is cancelled. No DELETE.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE ON message_templates, messages TO finance_app;
GRANT SELECT ON message_templates, messages TO finance_reporting;
