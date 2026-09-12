-- 0021 — Customer erasure (slice 31, SEC-93): a contact's personal data is anonymized in place — the row stays so
-- every message that referenced it keeps its financial and audit meaning — and the fact is recorded on the row.
ALTER TABLE customer_contacts ADD COLUMN erased_at timestamptz NULL;
ALTER TABLE customer_contacts ADD COLUMN erased_by uuid NULL;
