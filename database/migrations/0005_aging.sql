-- 0005 — Aging (slice 4): DM-30 v_invoice_balances and DM-31 fn_aging.
--
-- Read-only. No table changes, no new columns on invoices. Both objects are pure set-based
-- derivation (DM-33): no transition, no money mutation, nothing a C# unit test could not repeat.
-- Neither is SECURITY DEFINER: they run as the caller, so RLS on every table they read applies
-- exactly as it does to a plain SELECT. The explicit tenant predicate is layer 1 in the raw text,
-- the same pattern as every other raw query in the codebase; it never widens what RLS allows.

-- ---------------------------------------------------------------------------------------
-- DM-30: the derived balance per invoice, today. The FIN-10 authority; balance_cache is checked
-- against it (INV-09), never the other way round.
-- ---------------------------------------------------------------------------------------

CREATE OR REPLACE VIEW v_invoice_balances AS
SELECT i.tenant_id,
       i.id AS invoice_id,
       i.total_amount,
       i.total_amount
         - coalesce((SELECT sum(a.amount) FROM payment_allocations a
                     WHERE a.tenant_id = i.tenant_id AND a.invoice_id = i.id AND a.is_active), 0)
         - coalesce((SELECT sum(c.amount) FROM credit_note_applications c
                     WHERE c.tenant_id = i.tenant_id AND c.invoice_id = i.id AND c.is_active), 0)
         - coalesce((SELECT sum(w.amount) FROM write_offs w
                     WHERE w.tenant_id = i.tenant_id AND w.invoice_id = i.id AND w.status = 'Approved'), 0)
         - coalesce((SELECT sum(h.withheld_amount) FROM withholding_deductions h
                     WHERE h.tenant_id = i.tenant_id AND h.invoice_id = i.id AND h.is_active), 0)
         AS open_balance,
       i.balance_cache
FROM invoices i;

-- ---------------------------------------------------------------------------------------
-- DM-31: fn_aging. One row per invoice that had an open balance as of p_as_of (FIN-57).
--
-- The balance as of a date is re-derived from history, never read from balance_cache:
--   allocations and credit applications: an original row counts from its effective_date; its
--     reversal row (reversal_of_id IS NOT NULL) counts back from *its* effective_date;
--   write-offs: count from the approval date until the reversal date, in tenant time;
--   withholding: counts from the day it was recorded, in tenant time (slice 4 D-5).
-- An invoice is included when it existed (issue_date ≤ as_of), was ever opened (status not
-- Imported), was not voided (SM-55: void means never valid), and still owed something on the date.
-- Days past due are calendar days (FIN-71) from the basis date (FIN-50). Bucketing is the
-- caller's (C#) job, from tenant settings.
-- ---------------------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION fn_aging(p_tenant uuid, p_as_of date, p_basis text, p_tz text)
RETURNS TABLE (
  invoice_id       uuid,
  customer_id      uuid,
  invoice_number   text,
  currency         char(3),
  issue_date       date,
  due_date         date,
  total_amount     numeric(19,3),
  open_balance     numeric(19,3),
  days_past_due    integer,
  fx_rate_to_base  numeric(18,8),
  base_currency    char(3)
)
LANGUAGE sql STABLE
AS $$
  WITH inv AS (
    SELECT i.id, i.tenant_id, i.customer_id, i.invoice_number, i.currency, i.issue_date, i.due_date,
           i.total_amount, i.fx_rate_to_base, i.base_currency
    FROM invoices i
    WHERE i.tenant_id = p_tenant
      AND i.status <> 'Imported' AND i.status <> 'Void'
      AND i.issue_date <= p_as_of
  ),
  alloc AS (
    SELECT a.invoice_id, sum(CASE WHEN a.reversal_of_id IS NULL THEN a.amount ELSE -a.amount END) AS amount
    FROM payment_allocations a
    WHERE a.tenant_id = p_tenant AND a.effective_date <= p_as_of
    GROUP BY a.invoice_id
  ),
  credit AS (
    SELECT c.invoice_id, sum(CASE WHEN c.reversal_of_id IS NULL THEN c.amount ELSE -c.amount END) AS amount
    FROM credit_note_applications c
    WHERE c.tenant_id = p_tenant AND c.effective_date <= p_as_of
    GROUP BY c.invoice_id
  ),
  wo AS (
    SELECT w.invoice_id, sum(w.amount) AS amount
    FROM write_offs w
    WHERE w.tenant_id = p_tenant
      AND w.status IN ('Approved', 'Reversed')
      AND (w.approved_at AT TIME ZONE p_tz)::date <= p_as_of
      AND (w.reversed_at IS NULL OR (w.reversed_at AT TIME ZONE p_tz)::date > p_as_of)
    GROUP BY w.invoice_id
  ),
  wht AS (
    SELECT h.invoice_id, sum(CASE WHEN h.reversal_of_id IS NULL THEN h.withheld_amount ELSE -h.withheld_amount END) AS amount
    FROM withholding_deductions h
    WHERE h.tenant_id = p_tenant AND (h.created_at AT TIME ZONE p_tz)::date <= p_as_of
    GROUP BY h.invoice_id
  ),
  balances AS (
    SELECT inv.*,
           inv.total_amount - coalesce(alloc.amount, 0) - coalesce(credit.amount, 0)
                            - coalesce(wo.amount, 0) - coalesce(wht.amount, 0) AS open_balance
    FROM inv
    LEFT JOIN alloc  ON alloc.invoice_id  = inv.id
    LEFT JOIN credit ON credit.invoice_id = inv.id
    LEFT JOIN wo     ON wo.invoice_id     = inv.id
    LEFT JOIN wht    ON wht.invoice_id    = inv.id
  )
  SELECT b.id, b.customer_id, b.invoice_number, b.currency, b.issue_date, b.due_date,
         b.total_amount, b.open_balance,
         (p_as_of - CASE WHEN p_basis = 'issue_date' THEN b.issue_date ELSE b.due_date END)::integer,
         b.fx_rate_to_base, b.base_currency
  FROM balances b
  WHERE b.open_balance > 0
$$;

-- History reads are not filtered on is_active (a reversed row is part of the past), so the partial
-- "active only" indexes of 0004 do not serve them. One tenant-leading index per instrument table.
CREATE INDEX alloc_history_idx ON payment_allocations (tenant_id, invoice_id, effective_date);
CREATE INDEX cna_history_idx   ON credit_note_applications (tenant_id, invoice_id, effective_date);
CREATE INDEX wht_history_idx   ON withholding_deductions (tenant_id, invoice_id);

GRANT SELECT ON v_invoice_balances TO finance_app, finance_reporting;
GRANT EXECUTE ON FUNCTION fn_aging(uuid, date, text, text) TO finance_app, finance_reporting;
