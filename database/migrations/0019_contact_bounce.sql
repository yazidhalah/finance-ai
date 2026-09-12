-- 0019 — Bounced contacts (slice 25): the MTA's bounce lands on the contact, so the send guard can refuse the address
-- until someone corrects it. Cleared when the email changes.
ALTER TABLE customer_contacts ADD COLUMN bounced_at timestamptz NULL;
ALTER TABLE customer_contacts ADD COLUMN bounce_reason text NULL CHECK (bounce_reason IS NULL OR length(bounce_reason) <= 500);
