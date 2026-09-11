-- 0004 — Payments, cheques, allocations, withholding, credit notes, write-offs (slice 3b).
--
-- Same pattern as 0001–0003. Beyond isolation, this migration carries the financial invariants the
-- database can hold on its own, so they hold even for a writer that bypasses the application:
--   FIN-21 / INV-02  Σ active allocations ≤ payment.amount; currencies match
--   FIN-41 / INV-03  Σ active applications ≤ credit_note.amount; currencies match
--   PRD-11 / FIN-31  four eyes on write-off approval
-- What the database cannot hold — "≤ the invoice's open balance at the time of the write" — is
-- done in C# under SELECT … FOR UPDATE (FIN-22), and proved by the concurrency test.

-- ---------------------------------------------------------------------------------------
-- cheques (A-05, SM-51): a promise until it clears
-- ---------------------------------------------------------------------------------------

CREATE TABLE cheques (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  customer_id    uuid NOT NULL,
  cheque_number  text NOT NULL CHECK (length(btrim(cheque_number)) BETWEEN 1 AND 50),
  bank_name      text NULL,
  amount         numeric(19,3) NOT NULL CHECK (amount > 0),
  currency       char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  cheque_date    date NOT NULL,                          -- may be in the future (post-dated)
  received_date  date NOT NULL,
  status         text NOT NULL DEFAULT 'Received'
                 CHECK (status IN ('Received','Deposited','Cleared','Bounced','Returned','Cancelled')),
  bounced_reason text NULL,
  cleared_date   date NULL,
  ptp_id         uuid NULL,                              -- slice 6: the PTP auto-created for a PDC
  payment_id     uuid NULL,                              -- set when Cleared creates the payment
  notes          text NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  created_by     uuid NULL,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  row_version    bigint NOT NULL DEFAULT 1,
  CONSTRAINT cheques_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_cheque_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  UNIQUE (tenant_id, customer_id, cheque_number)
);

CREATE INDEX cheques_status_idx ON cheques (tenant_id, status, cheque_date);

-- ---------------------------------------------------------------------------------------
-- payments
-- ---------------------------------------------------------------------------------------

CREATE TABLE payments (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES tenants(id),
  customer_id     uuid NOT NULL,
  amount          numeric(19,3) NOT NULL CHECK (amount > 0),
  currency        char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  method          text NOT NULL CHECK (method IN ('BankTransfer','Cheque','Cash','CliQ','Card','Other')),
  received_date   date NOT NULL,
  effective_date  date NOT NULL,                         -- as-of aging (FIN-57)
  reference       text NULL,                             -- bank ref / narrative
  status          text NOT NULL DEFAULT 'Confirmed' CHECK (status IN ('Pending','Confirmed','Reversed')),
  cheque_id       uuid NULL,
  notes           text NULL,
  idempotency_key text NULL,                             -- API-08 (D-2)
  request_hash    text NULL,
  reversed_at     timestamptz NULL,
  reversal_reason text NULL,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid NULL,
  updated_at      timestamptz NOT NULL DEFAULT now(),
  row_version     bigint NOT NULL DEFAULT 1,
  CONSTRAINT payments_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_payment_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_payment_cheque FOREIGN KEY (tenant_id, cheque_id) REFERENCES cheques (tenant_id, id)
);

CREATE UNIQUE INDEX payments_idempotency_uq ON payments (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX payments_customer_idx ON payments (tenant_id, customer_id, received_date DESC);

ALTER TABLE cheques ADD CONSTRAINT fk_cheque_payment FOREIGN KEY (tenant_id, payment_id) REFERENCES payments (tenant_id, id);

-- ---------------------------------------------------------------------------------------
-- payment_allocations (FIN-20..FIN-24). Reversible, never deleted (FIN-23).
-- ---------------------------------------------------------------------------------------

CREATE TABLE payment_allocations (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  payment_id     uuid NOT NULL,
  invoice_id     uuid NOT NULL,
  amount         numeric(19,3) NOT NULL CHECK (amount > 0),     -- FIN-22: ≥ 0.001 by scale
  currency       char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  effective_date date NOT NULL,
  is_active      boolean NOT NULL DEFAULT true,
  reversal_of_id uuid NULL,                                     -- FIN-23: the row this one undoes
  reversal_reason text NULL,
  allocated_by   uuid NULL,
  method         text NOT NULL DEFAULT 'manual' CHECK (method IN ('manual','auto_exact_match','proposed_fifo')),
  created_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT payment_allocations_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_alloc_payment FOREIGN KEY (tenant_id, payment_id) REFERENCES payments (tenant_id, id),
  CONSTRAINT fk_alloc_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id),
  CONSTRAINT fk_alloc_reversal FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES payment_allocations (tenant_id, id),
  -- A reversal is the only row allowed to be inactive from birth; an original starts active.
  CONSTRAINT reversal_is_inactive CHECK (reversal_of_id IS NULL OR NOT is_active)
);

CREATE INDEX alloc_invoice_idx ON payment_allocations (tenant_id, invoice_id) WHERE is_active;
CREATE INDEX alloc_payment_idx ON payment_allocations (tenant_id, payment_id) WHERE is_active;
CREATE UNIQUE INDEX alloc_one_reversal ON payment_allocations (tenant_id, reversal_of_id) WHERE reversal_of_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------
-- withholding_deductions (FIN-29): the rate is entered, never computed.
-- ---------------------------------------------------------------------------------------

CREATE TABLE withholding_deductions (
  id                    uuid PRIMARY KEY,
  tenant_id             uuid NOT NULL REFERENCES tenants(id),
  invoice_id            uuid NOT NULL,
  payment_id            uuid NULL,
  base_amount           numeric(19,3) NOT NULL CHECK (base_amount > 0),
  rate_pct              numeric(5,2) NOT NULL CHECK (rate_pct > 0 AND rate_pct < 100),
  withheld_amount       numeric(19,3) NOT NULL CHECK (withheld_amount > 0),
  currency              char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  certificate_reference text NULL,
  certificate_received  boolean NOT NULL DEFAULT false,
  is_active             boolean NOT NULL DEFAULT true,
  reversal_of_id        uuid NULL,
  created_at            timestamptz NOT NULL DEFAULT now(),
  created_by            uuid NULL,
  CONSTRAINT withholding_deductions_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_wht_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id),
  CONSTRAINT fk_wht_payment FOREIGN KEY (tenant_id, payment_id) REFERENCES payments (tenant_id, id),
  CONSTRAINT fk_wht_reversal FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES withholding_deductions (tenant_id, id),
  CONSTRAINT wht_reversal_is_inactive CHECK (reversal_of_id IS NULL OR NOT is_active)
);

CREATE INDEX wht_invoice_idx ON withholding_deductions (tenant_id, invoice_id) WHERE is_active;
CREATE INDEX wht_certificate_chase_idx ON withholding_deductions (tenant_id) WHERE is_active AND NOT certificate_received;

-- ---------------------------------------------------------------------------------------
-- credit_notes (FIN-40..43): stored positive, applied negatively
-- ---------------------------------------------------------------------------------------

CREATE TABLE credit_notes (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  customer_id  uuid NOT NULL,
  note_number  text NULL,
  amount       numeric(19,3) NOT NULL CHECK (amount > 0),
  currency     char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  issue_date   date NOT NULL,
  reason_code  text NOT NULL CHECK (reason_code IN
               ('dispute_resolution','agreed_discount','goods_returned','service_credit',
                'billing_error','bank_charges','rounding_adjustment','other')),
  dispute_id   uuid NULL,                                -- slice 7
  status       text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Void')),
  approved_by  uuid NULL,
  notes        text NULL,
  voided_at    timestamptz NULL,
  void_reason  text NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  created_by   uuid NULL,
  row_version  bigint NOT NULL DEFAULT 1,
  CONSTRAINT credit_notes_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_credit_note_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id)
);

CREATE INDEX credit_notes_customer_idx ON credit_notes (tenant_id, customer_id, status);

CREATE TABLE credit_note_applications (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  credit_note_id uuid NOT NULL,
  invoice_id     uuid NOT NULL,
  amount         numeric(19,3) NOT NULL CHECK (amount > 0),
  currency       char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  effective_date date NOT NULL,
  is_active      boolean NOT NULL DEFAULT true,
  reversal_of_id uuid NULL,
  reversal_reason text NULL,
  applied_by     uuid NULL,
  created_at     timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT credit_note_applications_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_cna_note FOREIGN KEY (tenant_id, credit_note_id) REFERENCES credit_notes (tenant_id, id),
  CONSTRAINT fk_cna_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id),
  CONSTRAINT fk_cna_reversal FOREIGN KEY (tenant_id, reversal_of_id) REFERENCES credit_note_applications (tenant_id, id),
  CONSTRAINT cna_reversal_is_inactive CHECK (reversal_of_id IS NULL OR NOT is_active)
);

CREATE INDEX cna_invoice_idx ON credit_note_applications (tenant_id, invoice_id) WHERE is_active;
CREATE INDEX cna_note_idx ON credit_note_applications (tenant_id, credit_note_id) WHERE is_active;

-- ---------------------------------------------------------------------------------------
-- write_offs (FIN-31..34): four eyes, in the schema
-- ---------------------------------------------------------------------------------------

CREATE TABLE write_offs (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  invoice_id    uuid NOT NULL,
  amount        numeric(19,3) NOT NULL CHECK (amount > 0),   -- FIN-32: the computed open balance
  currency      char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  reason_code   text NOT NULL CHECK (length(btrim(reason_code)) BETWEEN 1 AND 50),
  note          text NULL,
  status        text NOT NULL DEFAULT 'Proposed' CHECK (status IN ('Proposed','Approved','Rejected','Reversed')),
  proposed_by   uuid NOT NULL,
  proposed_at   timestamptz NOT NULL DEFAULT now(),
  approved_by   uuid NULL,
  self_approved boolean NOT NULL DEFAULT false,            -- PRD-11
  approved_at   timestamptz NULL,
  rejected_by   uuid NULL,
  reversed_by   uuid NULL,
  reversed_at   timestamptz NULL,
  reversal_reason text NULL,
  row_version   bigint NOT NULL DEFAULT 1,
  CONSTRAINT write_offs_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_write_off_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id),
  CONSTRAINT four_eyes CHECK (
    status <> 'Approved' OR self_approved OR (approved_by IS NOT NULL AND approved_by IS DISTINCT FROM proposed_by)),
  CONSTRAINT approved_has_approver CHECK (status <> 'Approved' OR approved_by IS NOT NULL)
);

CREATE INDEX write_offs_invoice_idx ON write_offs (tenant_id, invoice_id, status);
-- At most one open proposal per invoice.
CREATE UNIQUE INDEX write_offs_one_proposal ON write_offs (tenant_id, invoice_id) WHERE status = 'Proposed';

-- ---------------------------------------------------------------------------------------
-- Constraint triggers: the caps and currency rules the database can hold by itself.
-- ---------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION allocation_within_payment() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  payment_amount   numeric(19,3);
  payment_currency char(3);
  invoice_currency char(3);
  allocated        numeric(19,3);
BEGIN
  SELECT p.amount, p.currency INTO payment_amount, payment_currency FROM payments p WHERE p.id = NEW.payment_id;
  SELECT i.currency INTO invoice_currency FROM invoices i WHERE i.id = NEW.invoice_id;

  -- INV-02: allocation, payment and invoice currency all agree.
  IF NEW.currency <> payment_currency OR NEW.currency <> invoice_currency THEN
    RAISE EXCEPTION 'currency_mismatch: allocation %, payment %, invoice %', NEW.currency, payment_currency, invoice_currency
      USING ERRCODE = 'check_violation';
  END IF;

  -- FIN-21: Σ active allocations ≤ payment.amount.
  SELECT coalesce(sum(a.amount), 0) INTO allocated
  FROM payment_allocations a WHERE a.payment_id = NEW.payment_id AND a.is_active;

  IF allocated > payment_amount THEN
    RAISE EXCEPTION 'exceeds_payment: allocated % of %', allocated, payment_amount USING ERRCODE = 'check_violation';
  END IF;

  RETURN NEW;
END
$$;

CREATE CONSTRAINT TRIGGER allocation_within_payment_trg
  AFTER INSERT OR UPDATE ON payment_allocations
  DEFERRABLE INITIALLY IMMEDIATE
  FOR EACH ROW EXECUTE FUNCTION allocation_within_payment();

CREATE OR REPLACE FUNCTION application_within_credit_note() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  note_amount      numeric(19,3);
  note_currency    char(3);
  invoice_currency char(3);
  applied          numeric(19,3);
BEGIN
  SELECT n.amount, n.currency INTO note_amount, note_currency FROM credit_notes n WHERE n.id = NEW.credit_note_id;
  SELECT i.currency INTO invoice_currency FROM invoices i WHERE i.id = NEW.invoice_id;

  IF NEW.currency <> note_currency OR NEW.currency <> invoice_currency THEN
    RAISE EXCEPTION 'currency_mismatch: application %, note %, invoice %', NEW.currency, note_currency, invoice_currency
      USING ERRCODE = 'check_violation';
  END IF;

  -- FIN-41 / INV-03: Σ active applications ≤ credit_note.amount.
  SELECT coalesce(sum(a.amount), 0) INTO applied
  FROM credit_note_applications a WHERE a.credit_note_id = NEW.credit_note_id AND a.is_active;

  IF applied > note_amount THEN
    RAISE EXCEPTION 'exceeds_credit_note: applied % of %', applied, note_amount USING ERRCODE = 'check_violation';
  END IF;

  RETURN NEW;
END
$$;

CREATE CONSTRAINT TRIGGER application_within_credit_note_trg
  AFTER INSERT OR UPDATE ON credit_note_applications
  DEFERRABLE INITIALLY IMMEDIATE
  FOR EACH ROW EXECUTE FUNCTION application_within_credit_note();

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE cheques                  ENABLE ROW LEVEL SECURITY;
ALTER TABLE cheques                  FORCE  ROW LEVEL SECURITY;
ALTER TABLE payments                 ENABLE ROW LEVEL SECURITY;
ALTER TABLE payments                 FORCE  ROW LEVEL SECURITY;
ALTER TABLE payment_allocations      ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment_allocations      FORCE  ROW LEVEL SECURITY;
ALTER TABLE withholding_deductions   ENABLE ROW LEVEL SECURITY;
ALTER TABLE withholding_deductions   FORCE  ROW LEVEL SECURITY;
ALTER TABLE credit_notes             ENABLE ROW LEVEL SECURITY;
ALTER TABLE credit_notes             FORCE  ROW LEVEL SECURITY;
ALTER TABLE credit_note_applications ENABLE ROW LEVEL SECURITY;
ALTER TABLE credit_note_applications FORCE  ROW LEVEL SECURITY;
ALTER TABLE write_offs               ENABLE ROW LEVEL SECURITY;
ALTER TABLE write_offs               FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON cheques                  USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON payments                 USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON payment_allocations      USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON withholding_deductions   USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON credit_notes             USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON credit_note_applications USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON write_offs               USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. Financial rows are never deleted: they are reversed (FIN-23). No DELETE anywhere.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE ON cheques, payments, payment_allocations, withholding_deductions,
                                credit_notes, credit_note_applications, write_offs TO finance_app;
GRANT SELECT ON cheques, payments, payment_allocations, withholding_deductions,
                credit_notes, credit_note_applications, write_offs TO finance_reporting;
