-- 0002 — Customers and contacts (slice 2).
--
-- Follows the pattern of 0001 exactly: tenant_id NOT NULL, RLS ENABLE + FORCE, a fail-closed
-- policy, a (tenant_id, id) key, and composite foreign keys between tenant-scoped tables.
-- The schema test in TenantIsolationTests enumerates pg_catalog and would fail this build if any
-- of those were missing.

-- ---------------------------------------------------------------------------------------
-- DM-20: Arabic-aware normalization, in one place, as a database function.
-- ---------------------------------------------------------------------------------------
--
-- Search, duplicate detection and the stored generated column all go through this function, so
-- the C# side never has a second copy of the rules to drift. IMMUTABLE is required for use in a
-- generated column and is honest: the output depends only on the input.
--
--   tatweel (U+0640)                 removed
--   diacritics (U+064B..U+0652, U+0670) removed
--   alef forms  أ إ آ ٱ               -> ا
--   teh marbuta ة                    -> ه
--   alef maqsura ى                   -> ي
--   Latin                             lower-cased
--   whitespace                        collapsed
CREATE OR REPLACE FUNCTION app_normalize_arabic(input text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
  AS $$
    SELECT btrim(regexp_replace(
      lower(translate(
        regexp_replace(input, '[ـً-ْٰ]', '', 'g'),
        'أإآٱةى',
        'ااااهي')),
      '\s+', ' ', 'g'))
  $$;

-- ---------------------------------------------------------------------------------------
-- customers
-- ---------------------------------------------------------------------------------------

CREATE TABLE customers (
  id                       uuid PRIMARY KEY,
  tenant_id                uuid NOT NULL REFERENCES tenants(id),
  code                     text NULL,                          -- the tenant's own customer code
  name_ar                  text NULL,
  name_en                  text NULL,
  legal_name               text NULL,
  tax_registration_no      text NULL,
  preferred_language       text NOT NULL DEFAULT 'ar' CHECK (preferred_language IN ('ar','en')),
  payment_terms_days       int NOT NULL DEFAULT 30 CHECK (payment_terms_days BETWEEN 0 AND 365),  -- FIN-72
  credit_limit_amount      numeric(19,3) NULL CHECK (credit_limit_amount >= 0),                 -- DM-09
  credit_limit_currency    char(3) NULL CHECK (credit_limit_currency ~ '^[A-Z]{3}$'),
  default_currency         char(3) NOT NULL DEFAULT 'JOD' CHECK (default_currency ~ '^[A-Z]{3}$'),
  risk_flag                text NOT NULL DEFAULT 'None'
                           CHECK (risk_flag IN ('None','Watch','HighRisk','Legal')),
  status                   text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Inactive')),  -- D-1
  broken_promise_count_12m int NOT NULL DEFAULT 0,             -- maintained by a job, advisory
  bounced_cheque_count_12m int NOT NULL DEFAULT 0,
  notes                    text NULL,
  normalized_name          text GENERATED ALWAYS AS
                           (app_normalize_arabic(coalesce(name_ar, '') || ' ' || coalesce(name_en, ''))) STORED,
  created_at               timestamptz NOT NULL DEFAULT now(),
  created_by               uuid NULL,
  updated_at               timestamptz NOT NULL DEFAULT now(),
  updated_by               uuid NULL,
  row_version              bigint NOT NULL DEFAULT 1,
  deleted_at               timestamptz NULL,
  CONSTRAINT customer_has_a_name CHECK (name_ar IS NOT NULL OR name_en IS NOT NULL),
  CONSTRAINT credit_limit_pair CHECK ((credit_limit_amount IS NULL) = (credit_limit_currency IS NULL)),
  CONSTRAINT customers_tenant_id_key UNIQUE (tenant_id, id)
);

-- Unique among live rows, per tenant, case-insensitively. Deleting a customer frees its code.
CREATE UNIQUE INDEX customers_tenant_code_uq
  ON customers (tenant_id, lower(code)) WHERE code IS NOT NULL AND deleted_at IS NULL;

-- Every index leads with tenant_id (doc 04 §7). The trigram index is what makes "شركه الامل"
-- find "شركة الأمل" in milliseconds rather than a sequential scan.
CREATE INDEX customers_tenant_live_idx ON customers (tenant_id, id) WHERE deleted_at IS NULL;
CREATE INDEX customers_normalized_name_trgm ON customers USING gin (tenant_id, normalized_name gin_trgm_ops);

-- ---------------------------------------------------------------------------------------
-- customer_contacts
-- ---------------------------------------------------------------------------------------

CREATE TABLE customer_contacts (
  id                 uuid PRIMARY KEY,
  tenant_id          uuid NOT NULL REFERENCES tenants(id),
  customer_id        uuid NOT NULL,
  name               text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
  role_title         text NULL,                                -- 'Accounts Payable', 'Owner'
  email              citext NULL,
  phone_e164         text NULL CHECK (phone_e164 IS NULL OR phone_e164 ~ '^\+[1-9][0-9]{6,14}$'),
  is_primary         boolean NOT NULL DEFAULT false,
  is_billing         boolean NOT NULL DEFAULT false,
  preferred_language text NULL CHECK (preferred_language IN ('ar','en')),
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  row_version        bigint NOT NULL DEFAULT 1,
  CONSTRAINT customer_contacts_tenant_id_key UNIQUE (tenant_id, id),
  -- Layer 3: a contact can only belong to a customer of the same tenant. A contact in tenant A
  -- attached to a customer in tenant B is unrepresentable (AC-14).
  CONSTRAINT fk_contact_customer
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers (tenant_id, id)
);

CREATE UNIQUE INDEX one_primary_contact ON customer_contacts (tenant_id, customer_id) WHERE is_primary;
CREATE INDEX customer_contacts_customer_idx ON customer_contacts (tenant_id, customer_id);

-- ---------------------------------------------------------------------------------------
-- Layer 2 — RLS, enabled AND forced, no platform escape: business data needs a real tenant.
-- ---------------------------------------------------------------------------------------

ALTER TABLE customers         ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers         FORCE  ROW LEVEL SECURITY;
ALTER TABLE customer_contacts ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_contacts FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON customers
  USING      (tenant_id = app_current_tenant())
  WITH CHECK (tenant_id = app_current_tenant());

CREATE POLICY tenant_isolation ON customer_contacts
  USING      (tenant_id = app_current_tenant())
  WITH CHECK (tenant_id = app_current_tenant());

-- ---------------------------------------------------------------------------------------
-- Privileges. No DELETE on customers: they are soft-deleted (DM-08), never removed.
-- ---------------------------------------------------------------------------------------

GRANT SELECT, INSERT, UPDATE         ON customers         TO finance_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON customer_contacts TO finance_app;
GRANT SELECT ON customers, customer_contacts TO finance_reporting;
