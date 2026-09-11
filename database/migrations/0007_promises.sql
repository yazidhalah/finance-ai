-- 0007 — Promise-to-Pay (slice 6): promises_to_pay, ptp_invoices, tenant_holidays.
--
-- Same pattern as 0001–0006. A promise is a commitment, not money: nothing here touches a balance.
-- Two invariants live in the database on their own:
--   INV-13  an Active promise has a human confirmer (CHECK active_requires_human)
--   INV-07  one Active promise per invoice (trigger, DM-24 — the rule spans two tables)

-- ---------------------------------------------------------------------------------------
-- tenant_holidays (FIN-73): announced, never computed; missing means "a business day".
-- ---------------------------------------------------------------------------------------

CREATE TABLE tenant_holidays (
  id        uuid NOT NULL DEFAULT gen_random_uuid(),          -- DM-10
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  date      date NOT NULL,
  name      text NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
  PRIMARY KEY (tenant_id, date),
  CONSTRAINT tenant_holidays_tenant_id_key UNIQUE (tenant_id, id)
);

-- ---------------------------------------------------------------------------------------
-- promises_to_pay (SM-30)
-- ---------------------------------------------------------------------------------------

CREATE TABLE promises_to_pay (
  id                 uuid PRIMARY KEY,
  tenant_id          uuid NOT NULL REFERENCES tenants(id),
  case_id            uuid NOT NULL,
  customer_id        uuid NOT NULL,
  status             text NOT NULL DEFAULT 'Proposed' CHECK (status IN
                     ('Proposed','Active','Kept','PartiallyKept','Broken','Cancelled','Rejected')),
  promised_amount    numeric(19,3) NOT NULL CHECK (promised_amount > 0),
  currency           char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  promised_date      date NOT NULL,
  deadline_date      date NOT NULL CHECK (deadline_date >= promised_date),   -- promised + grace business days (SM-33)
  source             text NOT NULL CHECK (source IN ('call','email','whatsapp','in_person','cheque','ai_suggested')),
  captured_by        uuid NULL,
  confirmed_by       uuid NULL,                              -- SM-31: required to reach Active
  ai_suggestion_id   uuid NULL,
  cheque_id          uuid NULL,                              -- A-05: the PDC this promise stands for
  superseded_by_id   uuid NULL,                              -- SM-36
  cancel_reason      text NULL,
  evaluated_at       timestamptz NULL,
  received_in_window numeric(19,3) NULL,                     -- computed at evaluation, for explainability
  evaluation_note    text NULL,
  notes              text NULL,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  row_version        bigint NOT NULL DEFAULT 1,
  CONSTRAINT ptp_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_ptp_case       FOREIGN KEY (tenant_id, case_id)          REFERENCES collection_cases (tenant_id, id),
  CONSTRAINT fk_ptp_customer   FOREIGN KEY (tenant_id, customer_id)      REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_ptp_cheque     FOREIGN KEY (tenant_id, cheque_id)        REFERENCES cheques (tenant_id, id),
  CONSTRAINT fk_ptp_superseded FOREIGN KEY (tenant_id, superseded_by_id) REFERENCES promises_to_pay (tenant_id, id),
  CONSTRAINT active_requires_human CHECK (status <> 'Active' OR confirmed_by IS NOT NULL),      -- INV-13
  CONSTRAINT evaluated_has_amount CHECK (status NOT IN ('Kept','PartiallyKept','Broken') OR received_in_window IS NOT NULL)
);

CREATE INDEX ptp_case_idx     ON promises_to_pay (tenant_id, case_id, status);
CREATE INDEX ptp_customer_idx ON promises_to_pay (tenant_id, customer_id, status);
CREATE INDEX ptp_due_idx      ON promises_to_pay (tenant_id, deadline_date) WHERE status = 'Active';

CREATE TABLE ptp_invoices (
  id         uuid NOT NULL DEFAULT gen_random_uuid(),         -- DM-10
  tenant_id  uuid NOT NULL REFERENCES tenants(id),
  ptp_id     uuid NOT NULL,
  invoice_id uuid NOT NULL,
  PRIMARY KEY (tenant_id, ptp_id, invoice_id),
  CONSTRAINT ptp_invoices_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_pi_ptp     FOREIGN KEY (tenant_id, ptp_id)     REFERENCES promises_to_pay (tenant_id, id),
  CONSTRAINT fk_pi_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id)
);
CREATE INDEX ptp_invoice_idx ON ptp_invoices (tenant_id, invoice_id);

-- ---------------------------------------------------------------------------------------
-- DM-24 / INV-07: one Active promise per invoice, whoever writes.
-- Checked when a cover row is added and when a promise becomes Active.
-- ---------------------------------------------------------------------------------------

-- Shared check: does any *other* Active promise cover any invoice of p_ptp (or the extra invoice)?
CREATE OR REPLACE FUNCTION ptp_active_clash(p_tenant uuid, p_ptp uuid, p_extra_invoice uuid) RETURNS uuid
  LANGUAGE sql STABLE AS $$
  SELECT other.invoice_id
  FROM ptp_invoices other
  JOIN promises_to_pay p ON p.tenant_id = other.tenant_id AND p.id = other.ptp_id AND p.status = 'Active'
  WHERE other.tenant_id = p_tenant AND other.ptp_id <> p_ptp
    AND (other.invoice_id = p_extra_invoice
         OR other.invoice_id IN (SELECT mine.invoice_id FROM ptp_invoices mine WHERE mine.tenant_id = p_tenant AND mine.ptp_id = p_ptp))
  LIMIT 1
$$;

-- On a new cover row: refuse if the promise is Active and another Active promise covers the invoice.
CREATE OR REPLACE FUNCTION one_active_ptp_per_invoice_cover() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  v_status text;
  v_clash uuid;
BEGIN
  SELECT status INTO v_status FROM promises_to_pay WHERE tenant_id = NEW.tenant_id AND id = NEW.ptp_id;
  IF v_status IS DISTINCT FROM 'Active' THEN
    RETURN NEW;
  END IF;

  v_clash := ptp_active_clash(NEW.tenant_id, NEW.ptp_id, NEW.invoice_id);
  IF v_clash IS NOT NULL THEN
    RAISE EXCEPTION 'one_active_ptp_per_invoice: invoice % already has an active promise (INV-07)', v_clash
      USING ERRCODE = 'unique_violation', CONSTRAINT = 'one_active_ptp_per_invoice';
  END IF;

  RETURN NEW;
END $$;

-- On a promise becoming Active: refuse if any of its covered invoices is under another Active promise.
CREATE OR REPLACE FUNCTION one_active_ptp_per_invoice_status() RETURNS trigger
  LANGUAGE plpgsql AS $$
DECLARE
  v_clash uuid;
BEGIN
  v_clash := ptp_active_clash(NEW.tenant_id, NEW.id, NULL);
  IF v_clash IS NOT NULL THEN
    RAISE EXCEPTION 'one_active_ptp_per_invoice: invoice % already has an active promise (INV-07)', v_clash
      USING ERRCODE = 'unique_violation', CONSTRAINT = 'one_active_ptp_per_invoice';
  END IF;

  RETURN NEW;
END $$;

CREATE TRIGGER one_active_ptp_per_invoice_cover_trg
  BEFORE INSERT ON ptp_invoices
  FOR EACH ROW EXECUTE FUNCTION one_active_ptp_per_invoice_cover();

CREATE TRIGGER one_active_ptp_per_invoice_status_trg
  BEFORE INSERT OR UPDATE OF status ON promises_to_pay
  FOR EACH ROW WHEN (NEW.status = 'Active') EXECUTE FUNCTION one_active_ptp_per_invoice_status();

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE tenant_holidays  ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenant_holidays  FORCE  ROW LEVEL SECURITY;
ALTER TABLE promises_to_pay  ENABLE ROW LEVEL SECURITY;
ALTER TABLE promises_to_pay  FORCE  ROW LEVEL SECURITY;
ALTER TABLE ptp_invoices     ENABLE ROW LEVEL SECURITY;
ALTER TABLE ptp_invoices     FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON tenant_holidays USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON promises_to_pay USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON ptp_invoices    USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. A promise is cancelled, never deleted; a holiday may be removed.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE ON promises_to_pay, ptp_invoices TO finance_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_holidays TO finance_app;
GRANT SELECT ON promises_to_pay, ptp_invoices, tenant_holidays TO finance_reporting;
