# Slice 4 — Aging: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **medium**. A read-only slice: no money moves, `invoices` gains no column.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all five. None is anonymous.**

| Endpoint | Declaration |
|----------|-------------|
| `GET /reports/aging`, `GET /reports/aging/customers/{id}`, `GET /reports/dso` | `RequiresPermission(aging.read)` |
| `GET /reports/aging/export` | `RequiresPermission(export.run)` |
| `GET /reports/reconciliation` | `RequiresPermission(audit.read)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at **65** and
the anonymous set at exactly `register`, `login`, `refresh`. `aging.read` is held by every role in
doc 01 §5.1, so it joined the pinned universal-permission list (now five entries); its boundary is
tenancy, tested in question 2. `export.run` and `audit.read` are denied to a Collector, and the
generated 403 sweep proves both.

`/reports/aging/customers/{id}` is exercised by the 404 sweep with a real customer id of tenant B
(the `customers` mapping in `IdFor`), and by `Aging_IsTenantBound` directly.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**No new table.** Migration `0005` adds a view, a function and three indexes:

| Object | What it is | Isolation |
|--------|-----------|-----------|
| `v_invoice_balances` (DM-30) | derived balance per invoice, today | plain view, not `security_barrier`-relevant: it runs as the caller, so RLS on `invoices` and the four instrument tables applies inside it exactly as to a plain `SELECT`; carries `tenant_id` and the reconciliation joins on it |
| `fn_aging(tenant, as_of, basis, tz)` (DM-31) | one row per invoice with an open balance as of the date | `LANGUAGE sql STABLE`, **not** `SECURITY DEFINER`; every table it reads is RLS-forced; the explicit `tenant_id = p_tenant` predicate is layer 1 in the text and can only narrow |
| `alloc_history_idx`, `cna_history_idx`, `wht_history_idx` | tenant-leading indexes on the 3b instrument tables (D-7) | indexes; doc 04 §7's "tenant-leading or review failure" rule holds |

**Targeted tests for the non-standard access pattern** — a report is a wide read, and a function
is a new place a tenant id is passed by value:

- `Aging_IsTenantBound` (security): tenant A's report contains none of B's figures or names; then,
  **as the app role with A's RLS scope set**, `fn_aging(B's id, …)` returns **0 rows** and
  `fn_aging(A's id, …)` returns A's row — the parameter cannot widen what RLS allows. Drill-through
  with B's customer id is 404; `?customerId=` with B's id is an empty report, not a leak.
- `EveryMoneyColumn_IsNumeric19_3` is unchanged (no new column). The 19-table enumeration is
  unchanged; the view is not a table and holds no data.
- The reconciliation endpoint reads only through the view under the caller's scope
  (`Reconciliation_ReportsMismatch`).

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`; the static test still confines
`EnterPlatformAsync` / `IgnoreQueryFilters` to that file (an earlier draft of `AgingService` used
`IgnoreQueryFilters` to name soft-deleted customers; it was removed — a deleted customer's row is
shown without a name rather than by bypassing a filter).

Every handler runs after the tenant is bound. The `{id}` route loads the customer under the tenant
filter first and answers 404 before anything else is computed. The export reads the user's
preferred locale from `users` under the co-member policy, after tenant binding.

## 4. Any new money-related field or calculation?

**No new field. Several calculations — all `decimal`, and the slice contains the product's first
rounding, in exactly one place.**

| Item | As built |
|------|----------|
| Open balance as of a date | `fn_aging`: `total − Σ allocations − Σ credit applications − Σ write-offs − Σ withholding`, each instrument counted from its dated rows on or before the date, with a reversal row counting back from *its* date. `numeric(19,3)` throughout; sums of scale-3 values are exact. `Aging_AsOf_IsReproducible` (T-26), `Aging_HistoryMatchesCache_Today` (INV-09 ∧ FIN-57, at the SQL level *and* through the endpoint). |
| Bucketing | `AgingBuckets.FromBoundaries(tenant_settings.aging_bucket_days)`; `Classify` is exhaustive and exclusive (`BucketSums_EqualTotalAr`, 500 random boundary sets); the eight documented edges (`Buckets_BoundariesAreInclusiveOnTheRight`, T-24). Sums are `decimal` additions of open balances (`Bucketize`). |
| Per currency | `GroupBy(currency)` before any sum; `E6`-style test `Aging_PerCurrency_WithIndicativeBase` asserts no cross-currency figure appears. |
| **Rounding #1 — indicative base total** | `AgingRules.ToBaseIndicative(open, fx_rate_to_base) = Math.Round(open × rate, 3, MidpointRounding.AwayFromZero)` **per invoice, then summed**. Labelled `indicative: true` in the API and with the FIN-06 disclaimer in the UI and the export footer. `ToBaseIndicative_RoundsOncePerInvoice`; the integration test checks `0.001 × 0.709 → 0.001`. |
| DSO (FIN-60) | `AR / credit_sales × 90`, **days not money**, one decimal, `AwayFromZero`; `null` + `insufficientHistory` below 90 days of history or with no sales. `Dso_IsTheDocumentedFormula`, `Dso_IsGatedAndComputed`. The response carries the "based on invoices imported" disclaimer key. |
| Average days to pay (FIN-61) | mean of `(settlement date − due date)` over settled invoices in the trailing 12 months; one decimal; `sampleSize` alongside; `null` for an empty sample. `AverageDaysToPay_StatesSampleSize`. |
| Unapplied cash / credit (FIN-59) | as-of aware; per currency; never in a bucket. `Aging_UnappliedLinesAreSeparate`. |
| Day arithmetic | `DateOnly.DayNumber` differences (calendar days, FIN-71); "today" is `TimeZoneInfo.ConvertTime(utcNow, tenant tz)` (FIN-58). `TodayIn_IsTheTenantCalendarDate` (unit, incl. a DST transition), `Aging_DayBoundaryIsTenantLocal` (integration, Amman / London BST / London GMT, with the host clock pinned). |
| Export | amounts are emitted as `F3` strings (CSV) or numeric cells (XLSX) from `decimal`; never parsed back. |
| Frontend | `Aging.tsx` computes nothing (`aging.test.tsx` greps for arithmetic and for `reduce(`); totals, unapplied lines, the indicative figure and the disputed column are all the server's. |

**Nothing else rounds.** Withholding, allocation and credit figures are read as stored.

## 5. Any AI-touching code?

**None.** FIN-62 is upheld by construction: every advisory metric is SQL/C#.

## 6. Any new dependency?

**None.** The XLSX writer is in-house (`AgingExport`, one sheet, inline strings, two styles), so
SEC-45 is *tested* (`Xlsx_KeepsFormulaTextAsQuotePrefixedString_AndSetsRightToLeft`,
`Export_EscapesFormulas_AndIsRtlSafe`) rather than delegated. `THIRD-PARTY-NOTICES.md` is unchanged
apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Six things deserve the reviewer's attention:

1. **The slice touches 3b's tables after all — indexes only (D-7).** The first 50k-invoice run hit
   the 5-second statement timeout because the history read cannot use the `is_active` partial
   indexes. Three tenant-leading indexes fixed it; no column or constraint changed. The
   performance test (`Aging_P95_Under800ms_At50k`, 50k invoices / 100k allocation rows, 20 calls
   through the HTTP stack) measured **P95 297 ms**.
2. **T-141's plan assertion is not a test.** In a single-tenant test database a sequential scan of
   `invoices` is the *correct* plan, so "no seq scan" would be asserting the wrong thing. The
   tenant-leading indexes exist; the assertion belongs to a multi-tenant seeded environment.
3. **Two 3b dating choices surface here, unchanged (D-5, D-6):** withholding is dated by
   `created_at` in tenant time (no `effective_date` column), and allocation reversal rows carry the
   UTC date at reversal. Both only matter for an as-of report that straddles a reversal made late
   in the evening. Worth a one-line fix in 3b's code if the reviewer prefers tenant-local dating.
4. **`fn_aging` has a fourth parameter (`p_tz`)** beyond DM-31's three, so the function converts
   `timestamptz` columns without reading `tenants` itself. Doc 04 amended (DM-31a).
5. **The disputed column is present and always zero** with `disputedAvailable: false` until slice
   7, and the UI shows a dash rather than `0.000` so nobody reads "no disputes" into it.
6. **The "stale data" warning of doc 06 §6.6 is not built** — there is no nightly job yet to be
   stale. The reconciliation check is an endpoint and a test; the job comes with the first
   background worker.
