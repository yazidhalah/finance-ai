-- 0020 — Per-operation AI enablement (slice 26, doc 05 /organization/ai-settings, T-105): ai_enabled stays the
-- tenant's kill switch; each operation also has its own switch so a pilot can keep the briefing while the
-- classifier is off (or the reverse). An operation runs only when both are true.
ALTER TABLE tenant_settings ADD COLUMN ai_classification_enabled boolean NOT NULL DEFAULT true;
ALTER TABLE tenant_settings ADD COLUMN ai_briefing_enabled       boolean NOT NULL DEFAULT true;
