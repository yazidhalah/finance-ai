# Slice 3 — Invoice Import: acceptance criteria and test plan

Status: **Implemented (3a).** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 04 §5.2–5.3 (tables, DM-22, DM-23) · doc 05 slice 3 (contracts) · doc 02 §1
(invoice lifecycle I1–I3) · doc 03 FIN-01…07, FIN-10, FIN-72 · doc 06 §6.4 (wizard) · doc 08
SEC-44, SEC-46, SEC-48 · doc 10 slice 3 · doc 00 A-01…A-05, A-11, A-12.

---

## 1. Scope — and the split

Doc 10 sizes slice 3 as **L** and permits splitting it at **3a** (import → `Open` invoices,
read-only balances) and **3b** (payments, cheques, allocation, credit notes, withholding,
write-off), while noting that *3a alone is not shippable to a pilot tenant*. This slice is **3a**,
as the product owner's prompt scoped it. 3b is the next slice and is required before pilot.

| # | Capability |
|---|-----------|
| S1 | `invoices` table per doc 04 §5.2 (composite FK to `customers`), lifecycle `Imported → Open` (I1, I2) and `Imported → Void` (I3, via row skip) |
| S2 | `import_batches`, `import_rows`, `import_mappings` per doc 04 §5.3; raw rows stored as data only (SEC-48) |
| S3 | CSV and XLSX upload: extension **and** content sniffed, 10 MB cap, no external entities, no formula evaluation, zip-bomb limits (SEC-44, SEC-46); **no new dependency** — both formats are parsed in-house |
| S4 | Column mapping with a date-format and decimal-separator choice; mappings saved per tenant and reusable |
| S5 | Validation per row: required fields · amounts parse at ≤ 3 decimals, ≥ 0 · **net + tax = total** · due ≥ issue · customer resolved by code, then by normalized name · duplicate invoice number against existing invoices and within the batch · foreign currency requires a rate |
| S6 | Control totals per currency before commit — computed in C# `decimal`, never across currencies (FIN-04) |
| S7 | Exception resolution: `assign_customer`, `create_customer`, `skip` |
| S8 | Transactional commit: all accepted rows become `Open` invoices or none do; duplicate file refused with an explicit, audited override (DM-23) |
| S9 | Import history with per-batch drill-down to every row and its outcome; rejected rows kept, never dropped |
| S10 | `GET /invoices`, `GET /invoices/{id}`; customer detail gains per-currency `openBalance`; the customer delete guard becomes real |
| S11 | UI: four-step wizard (upload → map → preview → commit), import history, invoice list — bilingual, RTL |

**Deferred to 3b:** payments, cheques, allocations, credit notes, withholding, write-off, the
short-payment resolver, `POST /invoices` (manual), `PATCH /invoices/{id}`, void, invoice lines
(DM-22 line-sum check), `override_duplicate`.

---

## 2. Currency and money — stated explicitly

| Rule | As built |
|------|----------|
| Per-invoice currency (A-02) | Each row carries its own `currency`; if the file has no currency column, the customer's `default_currency` is used. Allowed: the ISO set the tenant already uses; validated as `^[A-Z]{3}$`. |
| Base currency (FIN-06) | `invoices.base_currency` = `tenants.base_currency` at import. |
| Rate (FIN-06) | There is no tenant rate table yet. A row whose currency ≠ base **must carry a mapped `fx_rate_to_base` column** or it is rejected (`missing_fx_rate`). A base-currency row gets rate `1`. *A defaulted rate of 1 on a USD invoice would be a wrong number, and a wrong number is worse than no number (doc 01 §8).* |
| Scale (FIN-02) | All amounts `numeric(19,3)` / `decimal`; input with more than three decimals is **rejected**, not rounded. |
| Rounding (FIN-05) | **None occurs in this slice.** Nothing is split, allocated or converted. Control totals sum 3-scale decimals, which stays exact. |
| Tax (A-03) | `tax_amount` is imported, never computed. If absent it is 0 and `total` must equal `net`. |
| Reconciliation | `net + tax = total`, exactly, or the row is rejected (`totals_do_not_reconcile`). We do not fix the customer's arithmetic. |
| Balance (FIN-10) | `balance_cache` is written only by `InvoiceBalance.Recompute`; with no allocations yet it equals `total_amount`. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | A CSV with three deliberate exceptions is uploaded, mapped, previewed with the three exceptions named, each resolved, and committed; the accepted rows are `Open` invoices with `balance_cache = total_amount` | T-122 (API half) | `Import_EndToEnd_WithThreeExceptions` |
| AC-02 | An XLSX file goes through the same flow; cells with Excel date serials parse as dates | S3 | `Import_Xlsx_ParsesDatesAndNumbers` |
| AC-03 | **Commit is transactional**: an induced failure mid-commit leaves zero invoices and the batch not `Committed` | T-41 | `Commit_WhenAnInsertFails_LeavesNoInvoices` |
| AC-04 | Re-uploading the same file is refused `409 duplicate_file`; `?force=true` is accepted and audited | DM-23 | `DuplicateFile_IsRefused_ThenOverridable` |
| AC-05 | A duplicate invoice number (same tenant, same customer, case-insensitive) against an existing `Open` invoice is a `Duplicate` row, not a second invoice | I2 guard | `DuplicateInvoiceNumber_IsFlagged` |
| AC-06 | Two rows in one batch with the same number for the same customer: the second is `Duplicate` | S5 | `DuplicateWithinBatch_IsFlagged` |
| AC-07 | `net + tax ≠ total` rejects the row with `totals_do_not_reconcile`; net alone (no tax column) sets total = net | S5 | `Totals_MustReconcile` |
| AC-08 | Amounts with more than three decimals are rejected, not rounded; negative totals rejected | FIN-02, FIN-07 | `Amounts_AreExactDecimals` |
| AC-09 | Decimal separator `,` parses `1.250,500` as `1250.500`; separator `.` parses `1,250.500` identically | doc 06 §6.4 | `DecimalSeparator_IsHonoured` |
| AC-10 | Date format is honoured (`dd/MM/yyyy` vs `yyyy-MM-dd`); a missing `due_date` becomes `issue_date + customer.payment_terms_days` (FIN-72); `due < issue` is rejected | FIN-72 | `Dates_AreParsedAndDefaulted` |
| AC-11 | A foreign-currency row without a rate is rejected `missing_fx_rate`; with a rate, the rate is frozen on the invoice; base-currency rows get rate 1 | FIN-06 | `ForeignCurrency_RequiresARate` |
| AC-12 | Customers resolve by code first, then by normalized name; an unmatched row is `Rejected: customer_not_found` and can be resolved by `assign_customer` or `create_customer` | doc 05 | `Customers_ResolveByCodeThenName`, `Resolve_AssignAndCreateCustomer` |
| AC-13 | Control totals are per currency, exact, and never mixed | FIN-04 | `ControlTotals_ArePerCurrency`, `MoneyTotals_RefuseMixedCurrencies` (unit) |
| AC-14 | Commit is idempotent: a second commit of a committed batch returns the same result and creates nothing | API-08 | `Commit_IsIdempotent` |
| AC-15 | Upload rejects: wrong extension, wrong sniffed content, oversized file, a zip bomb, a path-traversal filename (the name is stored sanitized, never used as a path) | SEC-44, SEC-46, T-81 | `Upload_RejectsHostileFiles` |
| AC-16 | XLSX parsing never evaluates formulas or resolves external entities: a sheet with `=CMD()` formulas yields the cached values only; a DTD/entity payload is refused | SEC-46 | `Xlsx_IgnoresFormulasAndEntities` |
| AC-17 | Raw rows are stored verbatim as `jsonb` for audit and never interpolated anywhere; a row containing SQL text imports as data | SEC-48 | `RawRows_AreDataOnly` |
| AC-18 | Every accepted invoice has an audit row for `Imported → Open`; the batch commit has one; an override has one | INV-12, SEC-50 | `Import_IsAudited` |
| AC-19 | Cross-tenant: batch, row, mapping and invoice ids of another tenant → 404; a batch cannot reference another tenant's rows (composite FK, RLS bypassed) | T-71, T-75 | sweep + `CrossTenantImportRow_IsRejectedByCompositeForeignKey` |
| AC-20 | Both new business tables and the three import tables pass the schema assertions automatically | DM-01/02/10 | existing enumeration (now 12 tables) |
| AC-21 | Every money column in the database is `numeric(19,3)`; no `float`/`double precision`/`money` type anywhere | INV-11, T-23 | `EveryMoneyColumn_IsNumeric19_3` |
| AC-22 | A customer with an `Open` invoice can no longer be deleted (the slice-2 guard becomes real) | doc 10 §2.4 | `Customer_WithOpenInvoice_CannotBeDeleted` |
| AC-23 | Customer detail returns `openBalance` per currency, exact, from `balance_cache` | FIN-15 | `CustomerDetail_ShowsOpenBalancePerCurrency` |
| AC-24 | Saved mappings are per tenant; another tenant's mapping is invisible | S4 | sweep + `Mappings_ArePerTenant` |
| AC-25 | The wizard disables Commit while any blocking exception is unresolved; every new string exists in both catalogues; money renders from the API string with the currency, never computed | doc 06 §6.4, UI-30, UI-32 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| POST | `/imports` (multipart, `?force=true`) | `invoices.import` |
| GET | `/imports` | `invoices.import` |
| GET | `/imports/{id}` | `invoices.import` |
| GET | `/imports/{id}/rows` | `invoices.import` |
| POST | `/imports/{id}/mapping` | `invoices.import` |
| POST | `/imports/{id}/rows/{rowId}/resolve` | `invoices.import` |
| POST | `/imports/{id}/commit` | `invoices.import` |
| POST | `/imports/{id}/cancel` | `invoices.import` |
| GET / POST / DELETE | `/import-mappings`, `/import-mappings/{id}` | `invoices.import` |
| GET | `/invoices` | `invoices.read` |
| GET | `/invoices/{id}` | `invoices.read` |

All through `TenantScopeMiddleware`; none anonymous. The upload endpoint disables ASP.NET's
antiforgery check: authentication is a bearer header, not a cookie, so there is no CSRF surface
(SEC-63).

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | The uploaded file is stored in `import_batches.file_content bytea` (doc 04 amended) | The batch must be re-parseable when the mapping changes. `bytea` keeps the file inside the tenant's RLS boundary and the transaction, and needs no storage path or cleanup job at pilot scale (≤ 10 MB × batches). SEC-26's path scheme applies when files move to disk. |
| D-2 | CSV and XLSX are parsed in-house | XLSX is a zip of XML; the subset needed (shared strings, one sheet, cell values) is small, and owning the parser is what lets SEC-46's "no formula evaluation, no external entities, zip-bomb limits" be asserted rather than assumed of a library. No licence to record. |
| D-3 | Foreign-currency rows need an explicit rate column | See §2. A rate table is a 3b/4 concern. |
| D-4 | `create_customer` on resolve creates the customer from the row's name/code with tenant defaults | Same path as `POST /customers`, audited the same way. |
| D-5 | Audit rows for a commit are written in one chained batch (`WriteManyAsync`) | 5,000 invoices × one row each through the single-row path would take one advisory lock and one round trip per row. One lock, one chain computation, one save. |
| D-6 | Commit inserts invoices as `Imported` and transitions them to `Open` in the same transaction, with one audit row per invoice for I2 | INV-12 wants one audit row per transition. I1 is implied by the `import_rows.invoice_id` link plus the batch row. |
| D-7 | The `invoices.status` filter on `GET /invoices` and the `Void` on skip are the only lifecycle transitions exposed; `Void` (I6) and manual create wait for 3b | I6's guard is "zero allocations" — untestable before allocations exist. |

---

## 6. Deferred, and what it blocks

| Deferred | Blocks | Note |
|----------|--------|------|
| Payments, cheques, allocation, credit notes, withholding, write-off (3b) | **Pilot.** Doc 10: "3a alone is not shippable." | The `balance_cache` recompute function exists and is the single write path; 3b extends it. |
| Invoice lines, DM-22 | Line-level detail | Header-only import is the common case (doc 04). |
| Manual invoice, PATCH, void | Gap-filling, corrections | Void's guard needs allocations to exist. |
| `overdueOnly`, `hasDispute`, `settlement` filters | Slices 4, 7 | The columns they read do not exist yet. |
| Restore drill (PRD-23) | Still outstanding from slice 1 | Operational. |
