-- 0017 — Customer merge (slice 23, DM-21): the merged row is retained, never deleted, and points at the survivor.
-- The composite self-reference keeps a merge inside one tenant by construction.

ALTER TABLE customers ADD COLUMN merged_into_id uuid NULL;
ALTER TABLE customers ADD COLUMN merged_at timestamptz NULL;
ALTER TABLE customers ADD CONSTRAINT fk_customer_merged_into FOREIGN KEY (tenant_id, merged_into_id) REFERENCES customers (tenant_id, id);
ALTER TABLE customers ADD CONSTRAINT merged_pair CHECK ((merged_into_id IS NULL) = (merged_at IS NULL));
ALTER TABLE customers ADD CONSTRAINT merged_not_self CHECK (merged_into_id IS NULL OR merged_into_id <> id);
CREATE INDEX customers_merged_into_idx ON customers (tenant_id, merged_into_id) WHERE merged_into_id IS NOT NULL;
