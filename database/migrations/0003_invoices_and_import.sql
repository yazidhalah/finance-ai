-- 0003 — Invoices and import (slice 3a).
--
-- Same pattern as 0001/0002: tenant_id NOT NULL, RLS ENABLE + FORCE, fail-closed policy with no
-- platform clause, (tenant_id, id) key, composite foreign keys between tenant-scoped tables.

-- ---------------------------------------------------------------------------------------
-- invoices (doc 04 §5.2). Lifecycle is stored; settlement, overdue-ness and dispute are derived
-- (SM-10) — there is no column for any of them, so there is nothing an endpoint could set.
-- ---------------------------------------------------------------------------------------

CREATE TABLE invoices (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL REFERENCES tenants(id),
  customer_id      uuid NOT NULL,
  invoice_number   text NOT NULL CHECK (length(btrim(invoice_number)) BETWEEN 1 AND 100),
  status           text NOT NULL DEFAULT 'Imported'
                   CHECK (status IN ('Imported','Open','Settled','WrittenOff','Void')),
  issue_date       date NOT NULL,
  due_date         date NOT NULL,
  currency         char(3) NOT NULL CHECK (currency ~ '^[A-Z]{3}$'),
  net_amount       numeric(19,3) NOT NULL CHECK (net_amount >= 0),
  tax_amount       numeric(19,3) NOT NULL DEFAULT 0 CHECK (tax_amount >= 0),   -- imported, never computed (A-03)
  total_amount     numeric(19,3) NOT NULL CHECK (total_amount >= 0),           -- FIN-07
  balance_cache    numeric(19,3) NOT NULL DEFAULT 0,                           -- FIN-10: written by one function only
  fx_rate_to_base  numeric(18,8) NOT NULL DEFAULT 1 CHECK (fx_rate_to_base > 0), -- FIN-06: frozen on the document
  base_currency    char(3) NOT NULL CHECK (base_currency ~ '^[A-Z]{3}$'),
  po_reference     text NULL,
  notes            text NULL,
  source           text NOT NULL DEFAULT 'import' CHECK (source IN ('import','manual','api')),
  import_batch_id  uuid NULL,
  external_id      text NULL,                                                  -- id in the source system
  settled_at       timestamptz NULL,
  created_at       timestamptz NOT NULL DEFAULT now(),
  created_by       uuid NULL,
  updated_at       timestamptz NOT NULL DEFAULT now(),
  updated_by       uuid NULL,
  row_version      bigint NOT NULL DEFAULT 1,
  CONSTRAINT invoices_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT due_after_issue CHECK (due_date >= issue_date),
  CONSTRAINT totals_reconcile CHECK (total_amount = net_amount + tax_amount),  -- we do not fix the customer's arithmetic
  CONSTRAINT balance_in_range CHECK (balance_cache >= 0 AND balance_cache <= total_amount),  -- INV-01
  CONSTRAINT fk_invoice_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id)
);

-- One live number per customer per tenant, case-insensitively. Voided invoices free their number.
CREATE UNIQUE INDEX invoices_number_uq
  ON invoices (tenant_id, customer_id, upper(invoice_number)) WHERE status <> 'Void';
CREATE INDEX invoices_aging_idx
  ON invoices (tenant_id, status, due_date) WHERE status = 'Open' AND balance_cache > 0;
CREATE INDEX invoices_customer_idx ON invoices (tenant_id, customer_id, status);
CREATE INDEX invoices_batch_idx ON invoices (tenant_id, import_batch_id) WHERE import_batch_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------
-- import (doc 04 §5.3)
-- ---------------------------------------------------------------------------------------

CREATE TABLE import_mappings (                    -- saved column mappings per tenant
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES tenants(id),
  name              text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
  column_map        jsonb NOT NULL,               -- {"Invoice No": "invoice_number", ...}
  date_format       text NOT NULL DEFAULT 'yyyy-MM-dd',
  decimal_separator char(1) NOT NULL DEFAULT '.' CHECK (decimal_separator IN ('.', ',')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT import_mappings_tenant_id_key UNIQUE (tenant_id, id),
  UNIQUE (tenant_id, name)
);

CREATE TABLE import_batches (
  id                uuid PRIMARY KEY,
  tenant_id         uuid NOT NULL REFERENCES tenants(id),
  file_name         text NOT NULL,                -- sanitized: no path separators, bounded length
  file_hash         text NOT NULL,                -- sha256 hex, for duplicate-file detection (DM-23)
  file_size         bigint NOT NULL CHECK (file_size BETWEEN 1 AND 10485760),
  file_kind         text NOT NULL CHECK (file_kind IN ('csv','xlsx')),
  file_content      bytea NOT NULL,               -- D-1: kept so the batch can be re-parsed when the mapping changes
  mapping_id        uuid NULL,
  column_map        jsonb NULL,                   -- the mapping actually applied (a saved one may change later)
  date_format       text NOT NULL DEFAULT 'yyyy-MM-dd',
  decimal_separator char(1) NOT NULL DEFAULT '.' CHECK (decimal_separator IN ('.', ',')),
  headers           jsonb NOT NULL,               -- detected column headers, in file order
  status            text NOT NULL DEFAULT 'Uploaded'
                    CHECK (status IN ('Uploaded','Parsing','Preview','Committing','Committed','Failed','Cancelled')),
  row_count         int NOT NULL DEFAULT 0,
  accepted_count    int NOT NULL DEFAULT 0,
  rejected_count    int NOT NULL DEFAULT 0,
  duplicate_count   int NOT NULL DEFAULT 0,
  warning_count     int NOT NULL DEFAULT 0,
  forced            boolean NOT NULL DEFAULT false, -- DM-23 override, audited
  uploaded_by       uuid NOT NULL,
  uploaded_at       timestamptz NOT NULL DEFAULT now(),
  committed_at      timestamptz NULL,
  row_version       bigint NOT NULL DEFAULT 1,
  CONSTRAINT import_batches_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_batch_mapping FOREIGN KEY (tenant_id, mapping_id) REFERENCES import_mappings (tenant_id, id)
);

-- DM-23: the same file cannot be imported twice by accident. A cancelled or failed batch does not
-- hold the hash; a forced re-import is an explicit, audited action and is exempt.
CREATE UNIQUE INDEX import_batches_file_hash_uq
  ON import_batches (tenant_id, file_hash) WHERE status NOT IN ('Cancelled','Failed') AND NOT forced;
CREATE INDEX import_batches_recent_idx ON import_batches (tenant_id, uploaded_at DESC);

CREATE TABLE import_rows (
  id           uuid PRIMARY KEY,
  tenant_id    uuid NOT NULL REFERENCES tenants(id),
  batch_id     uuid NOT NULL,
  row_no       int NOT NULL CHECK (row_no > 0),
  raw          jsonb NOT NULL,                    -- untrusted input, stored as data only (SEC-40, SEC-48)
  parsed       jsonb NULL,
  outcome      text NOT NULL DEFAULT 'Pending'
               CHECK (outcome IN ('Pending','Accepted','Rejected','Duplicate','Warning','Skipped')),
  error_code   text NULL,
  error_detail text NULL,
  customer_id  uuid NULL,                         -- resolved customer, from matching or from assign/create
  invoice_id   uuid NULL,                         -- set on commit
  CONSTRAINT import_rows_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_row_batch FOREIGN KEY (tenant_id, batch_id) REFERENCES import_batches (tenant_id, id),
  CONSTRAINT fk_row_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT fk_row_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id),
  UNIQUE (tenant_id, batch_id, row_no)
);

CREATE INDEX import_rows_batch_idx ON import_rows (tenant_id, batch_id, outcome);

-- Layer 3 in the other direction: an invoice's batch link is tenant-qualified too.
ALTER TABLE invoices
  ADD CONSTRAINT fk_invoice_batch FOREIGN KEY (tenant_id, import_batch_id) REFERENCES import_batches (tenant_id, id);

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE invoices        ENABLE ROW LEVEL SECURITY;
ALTER TABLE invoices        FORCE  ROW LEVEL SECURITY;
ALTER TABLE import_mappings ENABLE ROW LEVEL SECURITY;
ALTER TABLE import_mappings FORCE  ROW LEVEL SECURITY;
ALTER TABLE import_batches  ENABLE ROW LEVEL SECURITY;
ALTER TABLE import_batches  FORCE  ROW LEVEL SECURITY;
ALTER TABLE import_rows     ENABLE ROW LEVEL SECURITY;
ALTER TABLE import_rows     FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON invoices
  USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON import_mappings
  USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON import_batches
  USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON import_rows
  USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- A committed batch is the permanent record of what happened to every line of a file
-- (doc 06 §6.4). Preview rows are replaced whenever the mapping changes; once the batch is
-- committed, its rows can be neither changed nor removed — by anyone, including the owner.
-- ---------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION import_rows_frozen_after_commit() RETURNS trigger
  LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS (SELECT 1 FROM import_batches b WHERE b.id = OLD.batch_id AND b.status = 'Committed') THEN
    RAISE EXCEPTION 'import_rows of a committed batch are immutable: % is not permitted', TG_OP
      USING ERRCODE = 'restrict_violation';
  END IF;
  RETURN COALESCE(NEW, OLD);
END
$$;

CREATE TRIGGER import_rows_frozen_after_commit_trg
  BEFORE UPDATE OR DELETE ON import_rows
  FOR EACH ROW EXECUTE FUNCTION import_rows_frozen_after_commit();

-- ---------------------------------------------------------------------------------------
-- Privileges. Invoices are never deleted (financial rows are reversed or voided, FIN-23).
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE         ON invoices        TO finance_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON import_mappings TO finance_app;
GRANT SELECT, INSERT, UPDATE         ON import_batches  TO finance_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON import_rows     TO finance_app;
GRANT SELECT ON invoices, import_mappings, import_batches, import_rows TO finance_reporting;
