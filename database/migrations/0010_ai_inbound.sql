-- 0010 — Local AI: reply classification (slice 9): inbound_messages and ai_suggestions.
--
-- Same pattern as 0001–0009. What the database holds on its own here is provenance: an
-- ai_suggestions row cannot exist without model name, digest, prompt version, schema version,
-- input hash, validated output and confidence (CLAUDE.md → Financial Safety, AI-06). Nothing in
-- this migration lets an AI output change an invoice, a payment or a case status: the tables
-- reference those, never the other way round.

-- ---------------------------------------------------------------------------------------
-- inbound_messages (doc 04 §5.6). body_raw is UNTRUSTED (SEC-40): stored verbatim, never
-- interpreted, rendered as quoted text only (SEC-42).
-- ---------------------------------------------------------------------------------------

CREATE TABLE inbound_messages (
  id                      uuid PRIMARY KEY,
  tenant_id               uuid NOT NULL REFERENCES tenants(id),
  customer_id             uuid NULL,                 -- NULL until matched
  case_id                 uuid NULL,
  channel                 text NOT NULL CHECK (channel IN ('email','whatsapp_pasted','manual')),
  from_address            text NULL CHECK (from_address IS NULL OR length(from_address) <= 320),
  subject                 text NULL CHECK (subject IS NULL OR length(subject) <= 500),
  body_raw                text NOT NULL CHECK (length(body_raw) BETWEEN 1 AND 100000),
  body_normalized         text NULL,
  detected_language       text NULL CHECK (detected_language IS NULL OR detected_language IN ('ar','en','ar_latin','mixed','other')),
  received_at             timestamptz NOT NULL,
  in_reply_to_message_id  uuid NULL,
  match_confidence        numeric(4,3) NULL CHECK (match_confidence IS NULL OR match_confidence BETWEEN 0 AND 1),
  match_method            text NULL CHECK (match_method IS NULL OR match_method IN ('contact_email','manual','reply_to')),
  matched_by              uuid NULL,
  classification_status   text NOT NULL DEFAULT 'Unprocessed' CHECK (classification_status IN
                          ('Unprocessed','Classified','Unclassified','HumanClassified','Ignored')),
  classification          text NULL,                 -- the closed set of doc 07 §4.2; mirrored from the latest suggestion or the human
  human_classification    text NULL,
  human_classified_by     uuid NULL,
  human_classified_at     timestamptz NULL,
  last_suggestion_id      uuid NULL,
  has_attachments         boolean NOT NULL DEFAULT false,
  truncated_for_ai        boolean NOT NULL DEFAULT false,
  created_by              uuid NULL,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  row_version             bigint NOT NULL DEFAULT 1,
  CONSTRAINT inbound_messages_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_inbound_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_inbound_case     FOREIGN KEY (tenant_id, case_id)     REFERENCES collection_cases (tenant_id, id),
  CONSTRAINT fk_inbound_reply_to FOREIGN KEY (tenant_id, in_reply_to_message_id) REFERENCES messages (tenant_id, id),
  CONSTRAINT human_classified_has_human CHECK (classification_status <> 'HumanClassified' OR (human_classification IS NOT NULL AND human_classified_by IS NOT NULL))
);
CREATE INDEX inbound_unprocessed_idx ON inbound_messages (tenant_id, classification_status, received_at);
CREATE INDEX inbound_customer_idx ON inbound_messages (tenant_id, customer_id, received_at DESC) WHERE customer_id IS NOT NULL;
CREATE INDEX inbound_case_idx ON inbound_messages (tenant_id, case_id) WHERE case_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------
-- ai_suggestions (doc 04 §5.7). One row per model call; the human decision lives on the same row.
-- ---------------------------------------------------------------------------------------

CREATE TABLE ai_suggestions (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES tenants(id),
  operation         text NOT NULL CHECK (operation IN
                    ('classify_customer_reply','extract_promise','draft_message',
                     'summarize_case','daily_briefing','match_remittance')),
  subject_type      text NOT NULL CHECK (subject_type IN ('inbound_message','case','tenant')),
  subject_id        uuid NOT NULL,
  model_name        text NOT NULL CHECK (length(model_name) BETWEEN 1 AND 200),
  model_digest      text NOT NULL CHECK (length(model_digest) BETWEEN 1 AND 128),
  prompt_version    text NOT NULL CHECK (length(prompt_version) BETWEEN 1 AND 100),
  schema_version    text NOT NULL CHECK (length(schema_version) BETWEEN 1 AND 100),
  input_ref         jsonb NOT NULL,            -- references to inputs, never the text (SEC-41, AI-32)
  input_hash        text NOT NULL CHECK (input_hash ~ '^[0-9a-f]{64}$'),
  output_json       jsonb NOT NULL,            -- validated against the response schema before insert (AI-04)
  confidence        numeric(4,3) NOT NULL CHECK (confidence BETWEEN 0 AND 1),
  classification    text NULL,
  reason_code       text NULL,
  validation_status text NOT NULL CHECK (validation_status IN ('valid','schema_invalid','below_threshold','rejected_by_guard')),
  requires_human_review boolean NOT NULL DEFAULT true,
  suspicious        boolean NOT NULL DEFAULT false,   -- AI-25 signal (model ∨ heuristic)
  latency_ms        int NOT NULL CHECK (latency_ms >= 0),
  -- What the backend did with it, deterministically, per the safety table (doc 07 §4.3):
  outcome_type      text NULL CHECK (outcome_type IS NULL OR outcome_type IN ('verification_task','promise_proposed','dispute_open','activity','none')),
  outcome_id        uuid NULL,
  guard_reason      text NULL,                 -- why an outcome was withheld (amount_invalid, date_relative, invoice_ambiguous, …)
  created_at        timestamptz NOT NULL DEFAULT now(),
  human_decision    text NOT NULL DEFAULT 'pending' CHECK (human_decision IN ('pending','approved','edited','rejected','expired')),
  decided_by        uuid NULL,
  decided_at        timestamptz NULL,
  decision_reason   text NULL,
  human_correction  jsonb NULL,                -- feeds the evaluation set (doc 09 §5.4)
  CONSTRAINT ai_suggestions_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT decided_has_human CHECK (human_decision IN ('pending','expired') OR (decided_by IS NOT NULL AND decided_at IS NOT NULL))
);
CREATE INDEX ai_suggestions_subject_idx ON ai_suggestions (tenant_id, subject_type, subject_id);
CREATE INDEX ai_suggestions_pending_idx ON ai_suggestions (tenant_id, human_decision) WHERE human_decision = 'pending';
CREATE INDEX ai_suggestions_created_idx ON ai_suggestions (tenant_id, created_at DESC);

-- subject_id is polymorphic (inbound_message | case | tenant) so it cannot carry a composite FK;
-- the inbound side is closed by this FK from the message to its latest suggestion instead.
ALTER TABLE inbound_messages
  ADD CONSTRAINT fk_inbound_last_suggestion FOREIGN KEY (tenant_id, last_suggestion_id) REFERENCES ai_suggestions (tenant_id, id);

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE inbound_messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE inbound_messages FORCE  ROW LEVEL SECURITY;
ALTER TABLE ai_suggestions   ENABLE ROW LEVEL SECURITY;
ALTER TABLE ai_suggestions   FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON inbound_messages USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON ai_suggestions   USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. Suggestions are decided, never deleted; a message is Ignored, never deleted.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE ON inbound_messages, ai_suggestions TO finance_app;
GRANT SELECT ON inbound_messages, ai_suggestions TO finance_reporting;
