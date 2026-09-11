-- 0014 — Operations (slice 15): the invariant job's records and the alert path (doc 03 §7, SEC-102).
--
-- Same pattern as 0001–0013. Both tables are tenant-scoped: every run checks one tenant's rows and every
-- alert is about one tenant's data. The operator's cross-tenant view is their inbox and webhook, never a
-- platform-scoped query.

-- ---------------------------------------------------------------------------------------
-- invariant_runs: one row per run, immutable. `checks` is the per-check outcome written by C#.
-- ---------------------------------------------------------------------------------------

CREATE TABLE invariant_runs (
  id            uuid PRIMARY KEY,
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  ran_at        timestamptz NOT NULL DEFAULT now(),
  trigger       text NOT NULL CHECK (trigger IN ('sweep','manual')),
  actor_user_id uuid NULL,                                   -- NULL when the sweep ran it
  status        text NOT NULL CHECK (status IN ('ok','violations')),
  checks        jsonb NOT NULL,                              -- [{id, violations, samples[]}] — counts and ids only, never amounts
  duration_ms   int  NOT NULL CHECK (duration_ms >= 0),
  CONSTRAINT invariant_runs_tenant_id_key UNIQUE (tenant_id, id)
);
CREATE INDEX invariant_runs_latest_idx ON invariant_runs (tenant_id, ran_at DESC);

ALTER TABLE invariant_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE invariant_runs FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON invariant_runs USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

GRANT SELECT, INSERT ON invariant_runs TO finance_app;      -- no UPDATE, no DELETE: a run is a record
GRANT SELECT ON invariant_runs TO finance_reporting;

-- ---------------------------------------------------------------------------------------
-- alerts: one per (tenant, kind, UTC day). Acknowledgement is the only update.
-- ---------------------------------------------------------------------------------------

CREATE TABLE alerts (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES tenants(id),
  kind            text NOT NULL CHECK (kind IN ('invariant_violation','audit_chain_break','ai_guard_rejection_spike','send_volume_anomaly')),
  severity        text NOT NULL CHECK (severity IN ('critical','warning')),
  summary         text NOT NULL CHECK (length(summary) <= 500),
  details         jsonb NOT NULL DEFAULT '{}',               -- counts, ids, thresholds — never a customer, address or amount (SEC-41)
  dedupe_key      text NOT NULL,                             -- '<kind>:<yyyy-mm-dd>'
  raised_at       timestamptz NOT NULL DEFAULT now(),
  email_delivery  text NOT NULL DEFAULT 'skipped' CHECK (email_delivery   IN ('sent','skipped','failed')),
  webhook_delivery text NOT NULL DEFAULT 'skipped' CHECK (webhook_delivery IN ('sent','skipped','failed')),
  acknowledged_at timestamptz NULL,
  acknowledged_by uuid NULL,
  row_version     bigint NOT NULL DEFAULT 1,
  CONSTRAINT alerts_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT alerts_one_per_day UNIQUE (tenant_id, dedupe_key),
  CONSTRAINT ack_pair CHECK ((acknowledged_at IS NULL) = (acknowledged_by IS NULL))
);
CREATE INDEX alerts_open_idx ON alerts (tenant_id, raised_at DESC) WHERE acknowledged_at IS NULL;

ALTER TABLE alerts ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerts FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON alerts USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

GRANT SELECT, INSERT, UPDATE ON alerts TO finance_app;      -- no DELETE
GRANT SELECT ON alerts TO finance_reporting;
