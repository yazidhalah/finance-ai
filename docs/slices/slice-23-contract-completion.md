# Slice 23 — Contract completion I: holidays, manual invoice, invoice edits, invoice audit, customer merge

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source: a diff of doc 05's endpoint table against `docs/api/openapi.json` (slice 22) found seven contract rows never
built. This slice builds the five that belong to the ledger and the organization; the two email ones
(`/auth/verify-email`, `/webhooks/email-events`) and tenant email settings are slice 24.

| Row | Spec |
|-----|------|
| `GET/POST/DELETE /organization/holidays` | doc 05 line 107 · FIN-71/FIN-73 (business days use the tenant calendar; Islamic holidays are announced, never computed; a missing calendar degrades to "it's a business day") · slice 1 D-4 deferred it |
| `POST /invoices` | doc 05 line 210 · A-11 ("manual single-invoice entry exists only as a convenience for gap-filling") · FIN-72 (due = issue + the customer's terms when not supplied) |
| `PATCH /invoices/{id}` | doc 05 line 211: only `dueDate`, `poReference`, `notes`; **amounts are never editable** |
| `GET /invoices/{id}/audit` | doc 05 line 213 · doc 06 §6.11 ("an invoice's audit trail is reachable in one click") |
| `POST /customers/{id}/merge` | doc 05 line 167 (two-step: preview then confirm; irreversible; high-severity audit) · DM-21 (`merged_into_id`, the merged row retained, every child row re-pointed in one transaction) · doc 10 slice 2 acceptance 3 |

## 1. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **What can a manual invoice do that an import cannot?** | Nothing. It runs the same reconciliation (net + tax = total, or two of the three), the same currency/fx rule (fx required when the currency is not the base; frozen on the document, FIN-06), the same duplicate rule (number unique per customer among non-void invoices), the same I2 transition (`Imported → Open`, audited with reason `manual_entry`), the same `InvoiceBalance.Recompute`, and the same case hook. `source = manual`. | A-11: a convenience, not a second path. |
| **What can be edited on an invoice?** | `dueDate` (≥ `issueDate`), `poReference`, `notes`. Any other field in the body is `400 unexpected_field` (the middleware already refuses unmapped members). A `Void` invoice is immutable (`422 invoice_void`). Audited with `{field: {old, new}}`. | Doc 05: amounts are a credit note or a void-and-reimport. |
| **What does a merge move?** | Every row with a `customer_id` (twelve tables: contacts, invoices, import rows, cheques, payments, credit notes, cases, promises, disputes, verification tasks, messages, inbound messages) from the source to the target, in one transaction; the source row stays with `merged_into_id` set and status `Inactive`. | DM-21. |
| **When is a merge refused?** | `422 same_customer`; `422 invoice_number_conflict` (both have a non-void invoice with the same number — the unique index would refuse it halfway; the preview names the numbers); `422 both_have_open_cases` (INV-06: at most one non-terminal case per customer — resolve one first); `422 already_merged` (either side already merged away); `404` across tenants. | Invariants first; a merge that would need to "fix" a case or an invoice is not a merge. |
| **Two-step** | `POST /customers/{id}/merge {sourceCustomerId}` → `200` preview with per-table counts, conflicts and a `confirmToken` (HMAC over target, source and the counts; ten minutes). `POST … {sourceCustomerId, confirmToken}` → `200` result, `merged: true`. A stale token (the counts changed) is `422 confirm_token_stale`. | Doc 05: preview then confirm; irreversible. |
| **Contacts' primary flag** | If both customers have a primary contact, the source's primary is demoted (one primary per customer). | Constraint. |
| **Holidays** | A date + name; unique per tenant and date (`409 holiday_exists`); `DELETE` by id. Used by the business-day arithmetic already in `PromiseService` and `DisputeService`. All three verbs need `tenant.settings.write` (doc 05). | FIN-73. |

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | Holidays: create, list (sorted by date), delete; a duplicate date is 409; a Collector is 403; a holiday on the promise deadline pushes the deadline the way `PromiseService` already computes it | `HolidayTests` |
| AC-02 | Manual invoice: `net + tax = total` reconciliation (all three, net-only, total-only, mismatch → 422); due defaults to issue + the customer's terms; due before issue → 422; foreign currency without fx → 422; duplicate number → 422; the created invoice is `Open`, `source = manual`, balance = total, audited `Imported → Open / manual_entry`; it appears in aging and the customer's position | `ManualInvoiceTests` |
| AC-03 | PATCH: due/po/notes change and are audited with old/new; `amount`/`totalAmount` in the body → 400 `unexpected_field`; due before issue → 422; a void invoice → 422 | `InvoiceEditTests` |
| AC-04 | `GET /invoices/{id}/audit` lists that invoice's events newest first with `changes`; 404 across tenants | `InvoiceEditTests` |
| AC-05 | Merge: preview counts equal the rows moved; confirm moves every table (asserted per table), retains the source with `merged_into_id`, writes the audit event; the source's invoices now aggregate on the target's position; a second merge of the same source → 422; the three refusals; a tampered/stale token → 422; cross-tenant → 404 | `CustomerMergeTests` |
| AC-06 | Route pin 148 → 154; every new endpoint through the sweep; the OpenAPI snapshot updated | security suite |

## 3. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/organization/holidays` | `tenant.settings.write` |
| POST | `/organization/holidays` | `tenant.settings.write` |
| DELETE | `/organization/holidays/{id}` | `tenant.settings.write` |
| POST | `/invoices` | `invoices.write` |
| PATCH | `/invoices/{id}` | `invoices.write` |
| GET | `/invoices/{id}/audit` | `audit.read` |
| POST | `/customers/{id}/merge` | `customers.merge` |

## 4. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Manual invoices reuse the import's reconciliation and the ledger's balance function rather than a new validator | One arithmetic path (FIN-10, doc 03). |
| D-2 | Merge refuses conflicts instead of resolving them | A merge that voids an invoice or abandons a case is two operations; each already has its own audited path. |
| D-3 | The confirm token is an HMAC under a per-process key, ten minutes | A-15 (single instance); it binds the confirmation to the exact preview the human saw. Multi-instance would need a shared key (flagged). |
| D-4 | `GET /invoices/{id}/audit` is a filter over the audit log, not a new table | The log already carries every transition; the endpoint is the one-click path doc 06 asks for. |
