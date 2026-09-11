-- 0008 — Disputes (slice 7): disputes, dispute_evidence, payment_verification_tasks.
--
-- Same pattern as 0001–0007. A dispute never changes a balance (SM-41): the only money in this
-- migration is disputed_amount / resolution_amount, both descriptive. The balance moves through
-- a credit note (0004) created in the same transaction as an acceptance (SM-45).

-- ---------------------------------------------------------------------------------------
-- disputes (SM-40: one invoice each)
-- ---------------------------------------------------------------------------------------

CREATE TABLE disputes (
  id                    uuid PRIMARY KEY,
  tenant_id             uuid NOT NULL REFERENCES tenants(id),
  invoice_id            uuid NOT NULL,
  customer_id           uuid NOT NULL,
  case_id               uuid NULL,
  status                text NOT NULL DEFAULT 'Open' CHECK (status IN
                        ('Open','UnderReview','PendingCustomer','Accepted','PartiallyAccepted',
                         'Rejected','Withdrawn','Cancelled')),
  reason_code           text NOT NULL CHECK (reason_code IN
                        ('wrong_amount','wrong_quantity','price_mismatch','goods_not_received',
                         'goods_damaged','service_not_delivered','duplicate_invoice','already_paid',
                         'missing_po_reference','wrong_tax_treatment','wrong_entity_billed',
                         'contract_terms','other')),                                   -- SM-43
  disputed_amount       numeric(19,3) NOT NULL CHECK (disputed_amount > 0),
  currency              char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  customer_claim        text NULL CHECK (customer_claim IS NULL OR length(customer_claim) <= 4000),
  raised_at             timestamptz NOT NULL DEFAULT now(),
  raised_by             uuid NULL,
  source                text NOT NULL DEFAULT 'user' CHECK (source IN ('user','customer_email','ai_suggested')),
  ai_suggestion_id      uuid NULL,
  assigned_to           uuid NULL,
  first_response_due_at timestamptz NOT NULL,                                          -- SM-48
  first_response_at     timestamptz NULL,
  resolution_due_at     timestamptz NOT NULL,
  pending_since         timestamptz NULL,                                              -- SLA pause
  resolved_at           timestamptz NULL,
  resolved_by           uuid NULL,
  resolution_amount     numeric(19,3) NULL CHECK (resolution_amount IS NULL OR resolution_amount > 0),
  resolution_note       text NULL,
  credit_note_id        uuid NULL,                                                     -- SM-45
  close_reason          text NULL,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  row_version           bigint NOT NULL DEFAULT 1,
  CONSTRAINT disputes_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_dispute_invoice  FOREIGN KEY (tenant_id, invoice_id)     REFERENCES invoices (tenant_id, id),
  CONSTRAINT fk_dispute_customer FOREIGN KEY (tenant_id, customer_id)    REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_dispute_case     FOREIGN KEY (tenant_id, case_id)        REFERENCES collection_cases (tenant_id, id),
  CONSTRAINT fk_dispute_credit   FOREIGN KEY (tenant_id, credit_note_id) REFERENCES credit_notes (tenant_id, id),
  -- SM-45: an acceptance carries its credit note; SM-47: every terminal state names a human, except the
  -- customer's own withdrawal and our cancellation, which still name who recorded them.
  CONSTRAINT accepted_has_credit_note CHECK (status NOT IN ('Accepted','PartiallyAccepted') OR (credit_note_id IS NOT NULL AND resolution_amount IS NOT NULL)),
  CONSTRAINT resolved_by_a_human CHECK (status NOT IN ('Accepted','PartiallyAccepted','Rejected') OR (resolved_by IS NOT NULL AND resolved_at IS NOT NULL)),
  CONSTRAINT pending_has_since CHECK (status <> 'PendingCustomer' OR pending_since IS NOT NULL)
);

CREATE INDEX disputes_open_idx ON disputes (tenant_id, status) WHERE status IN ('Open','UnderReview','PendingCustomer');
CREATE INDEX disputes_invoice_idx ON disputes (tenant_id, invoice_id);
CREATE INDEX disputes_case_idx ON disputes (tenant_id, case_id) WHERE case_id IS NOT NULL;
-- One open dispute per invoice: a second claim is an update to the first, not a sibling.
CREATE UNIQUE INDEX one_open_dispute_per_invoice ON disputes (tenant_id, invoice_id) WHERE status IN ('Open','UnderReview','PendingCustomer');

-- ---------------------------------------------------------------------------------------
-- dispute_evidence (SEC-40 / SEC-44): untrusted bytes inside the RLS boundary, never parsed
-- ---------------------------------------------------------------------------------------

CREATE TABLE dispute_evidence (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  dispute_id    uuid NOT NULL,
  file_name     text NOT NULL CHECK (length(file_name) BETWEEN 1 AND 200),
  content_type  text NOT NULL CHECK (content_type IN ('application/pdf','image/png','image/jpeg')),
  size_bytes    int NOT NULL CHECK (size_bytes > 0 AND size_bytes <= 10485760),
  sha256        text NOT NULL,
  content       bytea NOT NULL,
  uploaded_by   uuid NULL,
  uploaded_at   timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT dispute_evidence_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_evidence_dispute FOREIGN KEY (tenant_id, dispute_id) REFERENCES disputes (tenant_id, id)
);
CREATE INDEX dispute_evidence_idx ON dispute_evidence (tenant_id, dispute_id);

-- ---------------------------------------------------------------------------------------
-- payment_verification_tasks (SM-44): "the customer says they paid" is a task, never a paid mark
-- ---------------------------------------------------------------------------------------

CREATE TABLE payment_verification_tasks (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  invoice_id   uuid NOT NULL,
  customer_id  uuid NOT NULL,
  dispute_id   uuid NULL,
  source       text NOT NULL DEFAULT 'dispute' CHECK (source IN ('dispute','ai_classification','user')),
  ai_suggestion_id uuid NULL,
  status       text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Resolved')),
  claim        text NULL CHECK (claim IS NULL OR length(claim) <= 4000),
  outcome      text NULL CHECK (outcome IS NULL OR outcome IN ('payment_found','no_payment_found','partial')),
  payment_id   uuid NULL,
  notes        text NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid NULL,
  resolved_at  timestamptz NULL,
  resolved_by  uuid NULL,
  CONSTRAINT pvt_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_pvt_invoice  FOREIGN KEY (tenant_id, invoice_id)  REFERENCES invoices (tenant_id, id),
  CONSTRAINT fk_pvt_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_pvt_dispute  FOREIGN KEY (tenant_id, dispute_id)  REFERENCES disputes (tenant_id, id),
  CONSTRAINT fk_pvt_payment  FOREIGN KEY (tenant_id, payment_id)  REFERENCES payments (tenant_id, id),
  CONSTRAINT resolved_has_outcome CHECK ((status = 'Resolved') = (outcome IS NOT NULL AND resolved_by IS NOT NULL)),
  CONSTRAINT found_has_payment CHECK (outcome IS DISTINCT FROM 'payment_found' OR payment_id IS NOT NULL)
);
CREATE INDEX pvt_open_idx ON payment_verification_tasks (tenant_id, status, created_at) WHERE status = 'Open';

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE disputes                   ENABLE ROW LEVEL SECURITY;
ALTER TABLE disputes                   FORCE  ROW LEVEL SECURITY;
ALTER TABLE dispute_evidence           ENABLE ROW LEVEL SECURITY;
ALTER TABLE dispute_evidence           FORCE  ROW LEVEL SECURITY;
ALTER TABLE payment_verification_tasks ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment_verification_tasks FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON disputes                   USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON dispute_evidence           USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON payment_verification_tasks USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. Disputes close, evidence is a record, tasks resolve. No DELETE.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE ON disputes, dispute_evidence, payment_verification_tasks TO finance_app;
GRANT SELECT ON disputes, payment_verification_tasks TO finance_reporting;
