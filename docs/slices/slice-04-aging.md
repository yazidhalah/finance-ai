# Slice 4 — Aging: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 03 §5 (FIN-50…62), §6 (FIN-70, FIN-71), §7 (INV-08, INV-09) · doc 04 §4
(`tenant_settings.aging_basis`, `aging_bucket_days`), DM-30, DM-31, DM-33, §7 · doc 05 slice 4 ·
doc 06 §6.6, UI-44 · doc 08 SEC-45 · doc 09 T-24, T-25, T-26, T-80, T-140, T-141 · doc 10 slice 4.

This is a **read/derivation slice**. No money moves. `invoices` gains no column; the schema change
is a SQL function (DM-31), a view (DM-30) — both pure set-based derivation (DM-33) — and three
history indexes on the 3b instrument tables (D-7).

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `fn_aging(tenant, as_of, basis, tz)` — one row per invoice that had an open balance **as of** the date, computed from history (FIN-57), and `v_invoice_balances` (DM-30) |
| S2 | Bucketing in C# from `tenant_settings.aging_bucket_days` (never hardcoded); `Current` + one bucket per boundary + a final open-ended bucket; half-open and exhaustive (FIN-51/52) |
| S3 | `GET /reports/aging` — per-currency sections, buckets with counts, "of which disputed" (placeholder zero until slice 7), unapplied cash and credit below the table (FIN-59), indicative base-currency total (FIN-55), grouped by bucket or by customer |
| S4 | `GET /reports/aging/customers/{id}` — the invoices behind every cell (UI-44 drill-through), plus `averageDaysToPay` with its sample size (FIN-61) |
| S5 | `GET /reports/dso` — FIN-60 with the 90-day history gate |
| S6 | `GET /reports/aging/export?format=csv\|xlsx` — localized headers, SEC-45 escaping, RTL sheet for Arabic; both writers in-house |
| S7 | `GET /reports/reconciliation` — INV-09 cache-vs-derived for the tenant |
| S8 | UI: aging report with `asOf` picker, basis indicator, group-by, per-currency sections, drill-through, "how this is calculated", export; nav `aging` available |

**Deferred, and to which slice:** the disputed column's real values (slice 7) · collections
effectiveness index and promise reliability (FIN-61, need cases/PTPs → slices 5, 6) · the
nightly reconciliation job and the "stale data" warning it drives (the check exists as an endpoint
and a test; the scheduler comes with the first background job in slice 8) · virtualized tables
beyond 200 rows (UI-60, when a tenant has 200 customers with balances) · T-141 plan assertion
as a test (the index is in place; asserted by hand in the review) · T-142 load profile.

---

## 2. Rules, stated explicitly (the edge cases the slice prompt names)

| Question | Answer | Why |
|----------|--------|-----|
| **Which invoices appear?** | Those with `open_balance_as_of > 0`, `issue_date ≤ asOf`, lifecycle not `Imported` and not `Void`. | FIN-53 — but evaluated **as of the date**. A `Settled` invoice that was still open on the as-of date appears there (FIN-57); a `WrittenOff` invoice appears if the write-off was approved after the date. `Void` never appears: void requires zero financial history (SM-55), so the invoice is treated as never having existed. `Imported` has not been opened. |
| **Zero-balance invoice** | **Disappears.** It is not shown as "fully settled" in the aging report. | Aging answers "what is owed and how late is it" (FIN-54). A settled invoice owes nothing; showing it would put a zero in a bucket and inflate counts. Its settled state is visible on the invoice list and detail, where it belongs. |
| **Partially paid invoice** | Contributes its **open balance as of the date**, not its total. | FIN-54, worked in `AC-04`. |
| **Exactly on a boundary** | `dpd ≤ 0` → `Current`; `dpd = 30` → `Days1To30`; `dpd = 31` → `Days31To60`; `dpd = 90` → `Days61To90`; `dpd = 91` → `Days90Plus`. Boundary `b` belongs to the bucket that ends at `b`. | FIN-51 table, read literally: `1 ≤ dpd ≤ 30`. Tested at every edge (T-24). |
| **Negative balance / credit note** | Cannot occur on an invoice (`INV-01`, `balance_in_range`). Unapplied credit is a **separate line below the table**, per currency, never a negative bucket. Same for unapplied cash. | FIN-59, FIN-42. |
| **Days past due** | `asOf − due_date` (or `issue_date` when `aging_basis = issue_date`) in **calendar days**, where `asOf` is a calendar date in the tenant timezone. Default `asOf` is *today in the tenant's timezone*, not UTC. | FIN-50, FIN-58, FIN-71. |
| **As-of a past date** | Balance is re-derived: `total − Σ allocations effective ≤ asOf + Σ allocation reversals effective ≤ asOf − (same for credit applications) − Σ withholding recorded ≤ asOf − Σ write-offs approved ≤ asOf and not reversed by asOf`. The `balance_cache` is **not** consulted for any date, including today — one code path, reproducible by construction. A test asserts the history result for today equals the cache (INV-08 ∧ INV-09). | FIN-57. |
| **Buckets** | Boundaries come from `tenant_settings.aging_bucket_days` (default `{30,60,90}`), validated ascending and positive. `N` boundaries → `N + 2` buckets named `Current`, `Days1To30`, `Days31To60`, …, `Days90Plus`. | Slice prompt: never hardcode. |
| **Disputed** | Column present, always `0.000` this slice, and the response says so (`disputedAvailable: false`). | FIN-56; disputes are slice 7. |
| **Currencies** | One section per currency; nothing is summed across them. The base-currency total is `Σ round(open_as_of × fx_rate_to_base, 3)` per invoice, labelled `indicative: true`. | FIN-04, FIN-06, FIN-55. |
| **Rounding — the first in the product** | Exactly one place: the indicative base-currency conversion, per invoice, `Math.Round(x, 3, MidpointRounding.AwayFromZero)` before summing. DSO and average-days-to-pay are **days**, not money: rounded to one decimal, same mode, stated in the response. Nothing else rounds. | FIN-05 requires the place to be stated. |
| **DSO** | Period = the 90 calendar days ending on `asOf`. `credit_sales` = Σ `total_amount` of non-void invoices with `issue_date` in the period, per currency. `AR` = Σ open-as-of at `asOf`. `DSO = AR / credit_sales × 90`. `null` + `insufficientHistory: true` when the earliest invoice is younger than 90 days or sales are zero. Labelled "based on invoices imported into this system". | FIN-60. |
| **Average days to pay** | Per customer: mean of `(settlement_date − due_date)` over invoices settled in the trailing 12 months, where `settlement_date` is the effective date of the last balance-reducing row. Reported with `sampleSize`; `null` when the sample is empty. | FIN-61. |
| **Export** | Any text cell beginning with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'` in CSV and flagged `quotePrefix` in XLSX (so Excel stores it as text without executing it). Amounts are numeric cells in XLSX and plain `F3` strings in CSV; they are never text-prefixed because a balance is never negative. Arabic export sets the sheet `rightToLeft`. CSV carries a UTF-8 BOM so Excel reads Arabic. | SEC-45, T-80. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | With default boundaries, `dpd` = 0, 1, 30, 31, 60, 61, 90, 91 land in `Current`, `Days1To30`, `Days1To30`, `Days31To60`, `Days31To60`, `Days61To90`, `Days61To90`, `Days90Plus` | FIN-51, T-24 | `Buckets_BoundariesAreInclusiveOnTheRight` (unit) |
| AC-02 | Custom boundaries `{15, 45}` produce `Current`, `Days1To15`, `Days16To45`, `Days45Plus`; unsorted or non-positive boundaries are refused | slice prompt | `Buckets_ComeFromSettings` (unit) |
| AC-03 | Randomized data: for every currency, Σ buckets == Σ open balance of included invoices; every included invoice is in exactly one bucket | INV-08, FIN-52 | `BucketSums_EqualTotalAr` (unit, 500 rounds) + the INV-08 assertion inside `Aging_UsesOpenBalance_AndDropsSettled` (integration) |
| AC-04 | A 10,000 invoice with 9,000 allocated contributes 1,000 to its bucket; a settled invoice contributes nothing and is absent | FIN-53, FIN-54 | `Aging_UsesOpenBalance_AndDropsSettled` |
| AC-05 | Timezone: invoice due 2026-09-10; at 2026-09-10T20:59Z (23:59 Amman) it is `Current`; at 2026-09-10T21:01Z (00:01 Amman) it is `Days1To30`. Repeated for a DST-observing tenant (`Europe/London`) across a DST transition | FIN-58, T-25 | `Aging_DayBoundaryIsTenantLocal` |
| AC-06 | As-of reproducibility: aging for date D; then a payment allocated with effective date after D, a reversal, and a write-off approval; aging for D again → byte-identical | FIN-57, T-26 | `Aging_AsOf_IsReproducible` |
| AC-07 | As-of history for today equals the `balance_cache` path for every invoice after a randomized ledger walk | INV-09 ∧ FIN-57 | `Aging_HistoryMatchesCache_Today` |
| AC-08 | Unapplied cash and unapplied credit appear per currency below the table and are not in any bucket | FIN-59 | `Aging_UnappliedLinesAreSeparate` |
| AC-09 | Two currencies → two sections; no field sums them; the indicative base total is present with `indicative: true` and equals Σ per-invoice rounded conversions | FIN-04, FIN-55, E6 | `Aging_PerCurrency_WithIndicativeBase` |
| AC-10 | `groupBy=customer` returns one row per customer per currency whose bucket cells sum to the bucket totals; the customer drill-through lists exactly the invoices behind a cell | UI-44, doc 05 | `Aging_ByCustomer_DrillsThrough` |
| AC-11 | `aging_basis = issue_date` ages from issue date and the response header says so; `aging_bucket_days` drives the keys | FIN-50 | `Aging_BasisAndBucketsFromSettings` |
| AC-12 | DSO: < 90 days of history → `null`, `insufficientHistory: true`; with history, the documented formula, one decimal | FIN-60 | `Dso_IsGatedAndComputed` |
| AC-13 | Average days to pay: two settled invoices paid 5 and 15 days late → `10.0`, `sampleSize: 2`; none → `null`, `0` | FIN-61 | `AverageDaysToPay_StatesSampleSize` |
| AC-14 | Export: a customer named `=cmd\|' /C calc'!A0` exports as a prefixed literal in CSV and a `quotePrefix` text cell in XLSX; Arabic headers in `ar-JO`; the XLSX sheet is `rightToLeft` for Arabic; the CSV starts with a BOM | SEC-45, T-80 | `Export_EscapesFormulas_AndIsRtlSafe` |
| AC-15 | Reconciliation endpoint reports zero mismatches on a clean ledger and names the invoice after a hand-corrupted cache | INV-09 | `Reconciliation_ReportsMismatch` |
| AC-16 | 50,000 invoices with 100,000 allocation rows in one tenant: `GET /reports/aging` P95 < 800 ms over 20 calls; `EXPLAIN` shows no sequential scan on `invoices` | T-140, T-141, PRD-22 | `Aging_P95_Under800ms_At50k` (integration, `[Trait("Category","Performance")]`) — measured P95 297 ms; the plan assertion is deferred (review item 2) |
| AC-17 | Cross-tenant: the report never includes another tenant's invoices even when `fn_aging` is called with the other tenant's id; `/reports/aging/customers/{id}` with a foreign id → 404 | INV-05, T-71 | `Aging_IsTenantBound` + sweep |
| AC-18 | Every report endpoint declares its permission; `export.run` is required for export and denied to a Collector | doc 01 §5.1 | sweep |
| AC-19 | UI: cells drill through; `asOf` and basis are shown in the header; the indicative disclaimer renders; the browser computes no total | UI-30, UI-44 | web test |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/reports/aging` | `aging.read` |
| GET | `/reports/aging/customers/{id}` | `aging.read` |
| GET | `/reports/aging/export` | `export.run` |
| GET | `/reports/dso` | `aging.read` |
| GET | `/reports/reconciliation` | `audit.read` |

All through `TenantScopeMiddleware`; none anonymous. The route count pin moves from 60 to 65.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | `fn_aging` takes the tenant timezone as a fourth parameter | The function converts `timestamptz` columns (write-off approval, withholding creation) to tenant-calendar dates. The `tenants` row is readable under RLS, but passing the value keeps the function pure and testable with any timezone (T-25). |
| D-2 | History, not cache, for every as-of date including today | One code path; reproducibility by construction; the cache is validated *against* it (AC-07) rather than trusted by it. At A-16 scale the difference is milliseconds (AC-16). |
| D-3 | Bucket keys are generated from the boundaries (`Days1To30`), not stored | The keys are what the UI translates; a tenant changing boundaries gets new keys and the UI falls back to a formatted range. |
| D-4 | The XLSX writer is in-house (one sheet, inline strings, `quotePrefix` style) | No dependency to record; the reader already is. Excel and LibreOffice open it. |
| D-5 | Withholding as-of uses `created_at` in tenant time | The table has no `effective_date`; adding one would be a write-side change to a 3b table for no observable difference (a deduction is recorded when the certificate arrives). Noted for the reviewer. |
| D-6 | Allocation reversal rows are dated with the UTC date at reversal time (3b behaviour) | Unchanged here; a reversal at 23:30 Amman lands on the previous UTC day. Flagged rather than fixed in a read-only slice. |
| D-7 | Three plain `(tenant_id, invoice_id[, effective_date])` indexes on the 3b instrument tables | The 0004 indexes are partial on `is_active`; history reads must include reversed rows, so they were unusable and the first 50k run timed out at the 5 s statement limit. Indexes only — no column, no constraint. |
| D-8 | `fn_aging` pre-aggregates each instrument per invoice and joins, rather than correlated subqueries | Same result, one pass per table; P95 297 ms at 50k invoices through the full HTTP stack (AC-16). |
