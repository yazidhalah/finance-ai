-- 0006 — Collection cases (slice 5): collection_cases, case_invoices, case_activities.
--
-- Same pattern as 0001–0005. The one invariant the database holds on its own is INV-06 — at most
-- one non-terminal case per customer — as a partial unique index. Every state write happens in
-- C# (SM-02); the CHECK on status keeps the string enum honest (SM-01). No DELETE anywhere:
-- a case is closed, an invoice leaves scope with removed_at, an activity is history.

-- ---------------------------------------------------------------------------------------
-- collection_cases (SM-20): one unit of work per customer
-- ---------------------------------------------------------------------------------------

CREATE TABLE collection_cases (
  id                 uuid PRIMARY KEY,
  tenant_id          uuid NOT NULL REFERENCES tenants(id),
  customer_id        uuid NOT NULL,
  case_number        bigint NOT NULL,                       -- per-tenant, human-friendly
  status             text NOT NULL DEFAULT 'Open' CHECK (status IN
                     ('Open','InProgress','AwaitingCustomer','PromiseActive','Disputed',
                      'OnHold','Escalated','Resolved','Abandoned')),                  -- SM §2.1
  priority_score     int NOT NULL DEFAULT 0 CHECK (priority_score BETWEEN 0 AND 100),  -- FIN-80
  weights_version    int NOT NULL DEFAULT 1,                                            -- FIN-81
  priority_factors   jsonb NOT NULL DEFAULT '[]',           -- the breakdown that produced the score
  scored_at          timestamptz NULL,
  overdue_balance_base numeric(19,3) NOT NULL DEFAULT 0,    -- ranking input, never shown as money
  max_days_past_due  int NOT NULL DEFAULT 0,
  invoice_count      int NOT NULL DEFAULT 0,
  assigned_to        uuid NULL,
  opened_at          timestamptz NOT NULL DEFAULT now(),
  next_action_at     timestamptz NULL,                      -- queue suppression (C3 / C4 / snooze)
  next_action_reason text NULL,
  hold_until         date NULL,
  hold_reason        text NULL,
  escalated_at       timestamptz NULL,                      -- SM-26: set once, never cleared
  escalated_by       uuid NULL,
  escalation_reason  text NULL,
  closed_at          timestamptz NULL,
  close_reason       text NULL,
  last_contact_at    timestamptz NULL,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  row_version        bigint NOT NULL DEFAULT 1,
  CONSTRAINT collection_cases_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_case_customer FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id),
  CONSTRAINT hold_has_reason CHECK (status <> 'OnHold' OR (hold_until IS NOT NULL AND hold_reason IS NOT NULL)),
  CONSTRAINT escalated_has_reason CHECK (escalated_at IS NULL OR escalation_reason IS NOT NULL),
  CONSTRAINT closed_is_terminal CHECK ((closed_at IS NULL) = (status NOT IN ('Resolved','Abandoned')))
);

-- INV-06 / SM-21: the database, not the service, guarantees one open case per customer.
CREATE UNIQUE INDEX one_open_case_per_customer
  ON collection_cases (tenant_id, customer_id)
  WHERE status NOT IN ('Resolved','Abandoned');
CREATE UNIQUE INDEX case_number_uq ON collection_cases (tenant_id, case_number);
CREATE INDEX case_queue_idx
  ON collection_cases (tenant_id, status, priority_score DESC, next_action_at);
CREATE INDEX case_assignee_idx ON collection_cases (tenant_id, assigned_to) WHERE status NOT IN ('Resolved','Abandoned');

-- ---------------------------------------------------------------------------------------
-- case_invoices: what is in scope, and since / until when
-- ---------------------------------------------------------------------------------------

CREATE TABLE case_invoices (
  id         uuid NOT NULL DEFAULT gen_random_uuid(),         -- DM-10: tenant-qualified key for future children
  tenant_id  uuid NOT NULL REFERENCES tenants(id),
  case_id    uuid NOT NULL,
  invoice_id uuid NOT NULL,
  added_at   timestamptz NOT NULL DEFAULT now(),
  removed_at timestamptz NULL,
  removed_reason text NULL,
  PRIMARY KEY (tenant_id, case_id, invoice_id),
  CONSTRAINT case_invoices_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_ci_case    FOREIGN KEY (tenant_id, case_id)    REFERENCES collection_cases (tenant_id, id),
  CONSTRAINT fk_ci_invoice FOREIGN KEY (tenant_id, invoice_id) REFERENCES invoices (tenant_id, id)
);
CREATE INDEX case_invoice_idx ON case_invoices (tenant_id, invoice_id) WHERE removed_at IS NULL;

-- ---------------------------------------------------------------------------------------
-- case_activities: the timeline. actor_kind is never the model (SM-04).
-- ---------------------------------------------------------------------------------------

CREATE TABLE case_activities (
  id               uuid PRIMARY KEY,
  tenant_id        uuid NOT NULL REFERENCES tenants(id),
  case_id          uuid NOT NULL,
  kind             text NOT NULL CHECK (kind IN
                   ('note','call','email_sent','email_received','whatsapp_prepared',
                    'meeting','status_change','ptp','dispute','payment','system')),
  occurred_at      timestamptz NOT NULL DEFAULT now(),
  actor_user_id    uuid NULL,
  actor_kind       text NOT NULL DEFAULT 'user' CHECK (actor_kind IN ('user','system','ai_assisted')),
  ai_suggestion_id uuid NULL,                                -- SM-04: an input, never an actor
  summary          text NOT NULL CHECK (length(summary) BETWEEN 1 AND 2000),
  detail           jsonb NULL,
  CONSTRAINT case_activities_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT fk_activity_case FOREIGN KEY (tenant_id, case_id) REFERENCES collection_cases (tenant_id, id),
  CONSTRAINT ai_assisted_has_suggestion CHECK (actor_kind <> 'ai_assisted' OR ai_suggestion_id IS NOT NULL)
);
CREATE INDEX case_activity_idx ON case_activities (tenant_id, case_id, occurred_at DESC);

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE collection_cases ENABLE ROW LEVEL SECURITY;
ALTER TABLE collection_cases FORCE  ROW LEVEL SECURITY;
ALTER TABLE case_invoices    ENABLE ROW LEVEL SECURITY;
ALTER TABLE case_invoices    FORCE  ROW LEVEL SECURITY;
ALTER TABLE case_activities  ENABLE ROW LEVEL SECURITY;
ALTER TABLE case_activities  FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON collection_cases USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON case_invoices    USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());
CREATE POLICY tenant_isolation ON case_activities  USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. Cases close, invoices leave scope, activities are history. No DELETE.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE ON collection_cases, case_invoices, case_activities TO finance_app;
GRANT SELECT ON collection_cases, case_invoices, case_activities TO finance_reporting;
