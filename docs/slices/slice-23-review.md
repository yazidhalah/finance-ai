# Slice 23 — Contract completion I: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **high** — a manual path into AR and an irreversible customer merge.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

Seven, all through the middleware, none anonymous. Route pin 148 → 155.

| Endpoint | Permission | Note |
|----------|-----------|------|
| `GET/POST /organization/holidays`, `DELETE /organization/holidays/{id}` | `tenant.settings.write` | doc 05's row; a Collector is 403 (`Holidays_AreManaged…`) |
| `POST /invoices` | `invoices.write` | a foreign `customerId` is 404 |
| `PATCH /invoices/{id}` | `invoices.write` | the sweep's cross-tenant 404; `unexpected_field` for anything but the three fields |
| `GET /invoices/{id}/audit` | `audit.read` | 404 across tenants |
| `POST /customers/{id}/merge` | `customers.merge` | the route id is looked up **before** the body is validated, so a foreign id is a 404 and never a 400 (SEC-13 — the sweep caught the first version doing it the other way round) |

## 2. Every new table?

None. Migration 0017 adds `customers.merged_into_id` / `merged_at` with a **composite** self-reference `(tenant_id,
merged_into_id) → customers (tenant_id, id)`, a pair check and a not-self check; RLS on `customers` is unchanged.
`TenantIsolationTests` covers the table as before.

## 3. Pre-auth / cross-tenant paths?

None before authentication. The merge re-points twelve tables with `ExecuteUpdate` inside the request's tenant
transaction; every statement is filtered by `customer_id = source` under the tenant's RLS, so a row of another
tenant cannot be touched even if the id were guessed. The confirm token binds the confirmation to the exact preview
(target, source, per-table counts, minute), is compared in constant time, and expires after ten minutes; it is keyed
per process (A-15, flagged).

## 4. Money?

The manual invoice reuses the import's reconciliation (`net + tax = total`, two-of-three), keeps `decimal`
throughout, refuses more than three decimals at the boundary (`400 invalid_amount`), and writes the balance through
`InvoiceBalance.Recompute` (FIN-10). No rounding anywhere. The merge moves rows; it computes nothing — the target's
position afterwards is the same `LedgerService.CustomerPositionAsync` over more rows (`700.000` in the test: the
source's invoice was paid off before the merge).

## 5. AI?

None. Inbound messages and suggestions move with the customer; their content is untouched.

## 6. Dependencies?

None.

## Flagged

- **The confirm token is per process.** Two API instances would refuse each other's previews (`confirm_token_stale`); a shared HMAC key (from the signing key material) is the fix when A-15 changes.
- **Merge refuses rather than resolves** a shared invoice number or two open cases — by design (D-2); the operator resolves one side first.
- **`GET /organization/holidays` requires `tenant.settings.write`** as doc 05 says; a read-only permission would be friendlier for a Collector who wants to see the calendar.
- **Manual invoices are not idempotent by header** (payments are, API-08): the duplicate-number rule is the guard against a double submit.
