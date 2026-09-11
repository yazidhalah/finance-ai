-- 0011 — Daily briefing (slice 10): daily_briefings and the briefing settings.
--
-- Same pattern as 0001–0010. What the database holds on its own: one row per tenant, date and
-- language; metrics are a JSON document written by C# (FIN-62) — the model never writes here, and
-- the narrative column may be NULL because the metrics are the briefing (PRD-28).

-- ---------------------------------------------------------------------------------------
-- tenant_settings: how and to whom the briefing is delivered (doc 05 slice 10)
-- ---------------------------------------------------------------------------------------

ALTER TABLE tenant_settings
  ADD COLUMN briefing_language char(2) NOT NULL DEFAULT 'ar' CHECK (briefing_language IN ('ar','en')),
  ADD COLUMN briefing_email_enabled boolean NOT NULL DEFAULT false,
  ADD COLUMN briefing_recipient_user_ids uuid[] NOT NULL DEFAULT '{}';

-- ---------------------------------------------------------------------------------------
-- daily_briefings (doc 04 §5.7)
-- ---------------------------------------------------------------------------------------

CREATE TABLE daily_briefings (
  id                 uuid PRIMARY KEY,
  tenant_id          uuid NOT NULL REFERENCES tenants(id),
  briefing_date      date NOT NULL,
  language           char(2) NOT NULL CHECK (language IN ('ar','en')),
  metrics            jsonb NOT NULL,             -- computed in C#; the ONLY numbers allowed (FIN-62)
  narrative          text NULL CHECK (narrative IS NULL OR length(narrative) <= 1200),   -- LLM prose, NULL when unavailable (PRD-28)
  highlights         jsonb NOT NULL DEFAULT '[]',
  narrative_status   text NOT NULL DEFAULT 'unavailable' CHECK (narrative_status IN ('available','unavailable','rejected_by_guard','schema_invalid','disabled')),
  ai_suggestion_id   uuid NULL,
  generated_at       timestamptz NOT NULL DEFAULT now(),
  generated_by       uuid NULL,                  -- NULL when the sweep generated it
  sent_at            timestamptz NULL,
  sent_to_count      int NOT NULL DEFAULT 0,
  template_id        uuid NULL,
  template_version   int NULL,
  delivery_status    text NULL CHECK (delivery_status IS NULL OR delivery_status IN ('sent','not_enabled','no_recipients','template_not_approved','outbound_disabled','failed')),
  row_version        bigint NOT NULL DEFAULT 1,
  CONSTRAINT daily_briefings_tenant_id_key UNIQUE (tenant_id, id),
  CONSTRAINT daily_briefings_one_per_day UNIQUE (tenant_id, briefing_date, language),
  CONSTRAINT fk_briefing_suggestion FOREIGN KEY (tenant_id, ai_suggestion_id) REFERENCES ai_suggestions (tenant_id, id),
  CONSTRAINT fk_briefing_template   FOREIGN KEY (tenant_id, template_id)      REFERENCES message_templates (tenant_id, id),
  CONSTRAINT narrative_matches_status CHECK ((narrative_status = 'available') = (narrative IS NOT NULL))
);
CREATE INDEX daily_briefings_date_idx ON daily_briefings (tenant_id, briefing_date DESC);

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform clause.
-- ---------------------------------------------------------------------------------------

ALTER TABLE daily_briefings ENABLE ROW LEVEL SECURITY;
ALTER TABLE daily_briefings FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON daily_briefings USING (tenant_id = app_current_tenant()) WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. Today's row may be regenerated (UPDATE); a past row is never touched by the
-- application (enforced in code and by a trigger). No DELETE.
-- ---------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION daily_briefings_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.briefing_date < (now() AT TIME ZONE 'UTC')::date - 1 THEN
    RAISE EXCEPTION 'daily_briefings: a past briefing is immutable' USING ERRCODE = 'check_violation', CONSTRAINT = 'daily_briefings_immutable';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER daily_briefings_immutable BEFORE UPDATE ON daily_briefings FOR EACH ROW EXECUTE FUNCTION daily_briefings_immutable();

GRANT SELECT, INSERT, UPDATE ON daily_briefings TO finance_app;
GRANT SELECT ON daily_briefings TO finance_reporting;
