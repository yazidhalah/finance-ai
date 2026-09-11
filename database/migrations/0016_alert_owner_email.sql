-- 0016 — Per-tenant alert routing (slice 22): an Owner may ask to be emailed on the organization's critical alerts
-- (SEC-102). Off by default — the operator's ALERT_EMAIL is the standing route; this adds the tenant's Owners.

ALTER TABLE tenant_settings ADD COLUMN alert_owner_email_enabled boolean NOT NULL DEFAULT false;
