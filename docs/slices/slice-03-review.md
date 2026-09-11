# Slice 3 — Invoice Import (3a): self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all thirteen. None is anonymous.**

| Endpoints | Declaration |
|-----------|-------------|
| `POST/GET /imports`, `GET /imports/{id}`, `GET …/rows`, `POST …/mapping`, `POST …/rows/{rowId}/resolve`, `POST …/commit`, `POST …/cancel` | `RequiresPermission(invoices.import)` |
| `GET/POST /import-mappings`, `DELETE /import-mappings/{id}` | `RequiresPermission(invoices.import)` |
| `GET /invoices`, `GET /invoices/{id}` | `RequiresPermission(invoices.read)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at 36 and
the anonymous set at exactly `register`, `login`, `refresh`. The generated 401/403/404 sweeps
covered all thirteen; `invoices.read` joined the pinned universal-permission list (held by every
role in doc 01 §5.1), so its boundary is tenancy, tested in question 2.

Two things about the upload endpoint specifically:

- **Antiforgery is disabled on it** (`DisableAntiforgery()`). Authentication is a bearer header,
  not a cookie, so a cross-site form post carries no credential (SEC-63). Stated here rather than
  left as a runtime surprise.
- **Routing answers 415 to a non-multipart body before any middleware runs.** An authenticated
  Collector posting JSON gets 415, not 403. This discloses nothing (the content-type rule is the
  same for everyone), but the sweep now speaks multipart to that endpoint so the 403 is actually
  exercised rather than masked.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, all four — and the composite keys go in every direction.**

| Table | `tenant_id NOT NULL` | RLS forced | Policy (no platform clause) | `(tenant_id, id)` key | Composite FKs |
|-------|:--:|:--:|:--:|:--:|---|
| `invoices` | ✅ | ✅ | ✅ | ✅ | `→ customers`, `→ import_batches` |
| `import_mappings` | ✅ | ✅ | ✅ | ✅ | — |
| `import_batches` | ✅ | ✅ | ✅ | ✅ | `→ import_mappings` |
| `import_rows` | ✅ | ✅ | ✅ | ✅ | `→ import_batches`, `→ customers`, `→ invoices` |

The enumeration test now walks **12** tables. `NoSingleColumnForeignKey_JoinsTwoTenantScopedTables`
confirms every FK between tenant-scoped tables carries `tenant_id`.

**Targeted tests for the non-standard patterns** (`ImportIsolationTests`, `ImportTests`):

- **Layer 3 alone**: `CrossTenantImportRow_IsRejectedByCompositeForeignKey` inserts, as superuser
  with RLS bypassed, a row in A pointing at B's batch (`fk_row_batch`) and an invoice in A billed
  to B's customer (`fk_invoice_customer`). Both refused.
- **Two raw-SQL paths** in `ImportService.ResolveCustomersAsync`: normalizing the file's customer
  names (`unnest`, no table touched) and matching them against `customers.name_ar / name_en`
  through `app_normalize_arabic`. The second carries an explicit `tenant_id = @currentTenant`
  predicate under the RLS policy — the slice 2 pattern. Covered by `Customers_ResolveByCodeThenName…`
  in one tenant and by the fact that `resolve … assign_customer` with another tenant's customer id
  answers 404 (same test).
- **Immutability of committed batches**: `CommittedImportRows_AreImmutable_EvenForTheOwner`
  proves the `import_rows_frozen_after_commit` trigger binds the table owner. The app role does
  hold DELETE on `import_rows` (re-mapping replaces preview rows); the trigger, not the grant, is
  what protects the record.
- **The file itself** lives in `import_batches.file_content` (bytea), inside the RLS boundary,
  never on disk; the sanitized filename is display metadata and is never used as a path
  (`Upload_RejectsHostileFiles` sends `../../etc/passwd.csv`).

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`; the static test still confines it. All
handlers run after the tenant is bound.

One ordering note, the same shape as slice 2: `resolve` looks the batch and row up before reading
the body, so a foreign id answers 404 regardless of what is sent.

## 4. Any new money-related field or calculation?

**Fields: five columns on `invoices`. Calculations: two, both exact.**

| Item | As built |
|------|----------|
| `net_amount`, `tax_amount`, `total_amount`, `balance_cache` | `numeric(19,3)` / `decimal`; `EveryMoneyColumn_IsNumeric19_3` enumerates `information_schema` and fails on any float/double/money type or wrong scale. |
| `fx_rate_to_base` | `numeric(18,8)`, a rate not an amount; `> 0` enforced. |
| **Parsing** | `ImportValueParser.TryParseMoney`: `decimal.TryParse` invariant, `Scale > 3` → rejected (`too_many_decimals`), negative → rejected. **Never rounded.** A JSON number as an amount is refused at the API boundary. |
| **Reconciliation** | `net + tax == total` compared exactly; mismatch rejects the row. Also a database CHECK (`totals_reconcile`), so no other writer can store a non-reconciling invoice either. |
| **Derivation** | net-only → `total = net + tax(0)`; total-only → `net = total − tax(0)`. Scale-3 ± scale-3 is exact; no rounding. |
| **Control totals** | `MoneyTotals`: one running sum per currency; `Single()` throws on more than one (FIN-04). `MoneyTotals_RefuseMixedCurrencies`, `ControlTotals_ArePerCurrency…`. |
| **Balance** | `InvoiceBalance.Recompute` is the only writer of `balance_cache` (a private setter, `internal` mutator); with no allocations it equals total. `INV-01` range is asserted in code and by the `balance_in_range` CHECK. |
| **Customer open balance** | `GROUP BY currency` in SQL, `SUM(balance_cache)` per group; never across currencies. |
| **Rounding** | **Nowhere in this slice.** Nothing is split, allocated or converted. Stated as D-2 in the slice doc; 3b introduces the first rounding (FIN-05) and must say where. |
| **Frontend** | `formatAmount` groups digits on the *string*; a Number() round-trip is asserted absent (`import.test.tsx`, including a value beyond double precision). |

## 5. Any AI-touching code?

**None.**

## 6. Any new dependency?

**None.** CSV and XLSX are parsed in-house (`CsvTableReader`, `XlsxTableReader`), which is what lets
SEC-46 be *tested* rather than trusted: `Xlsx_IgnoresFormulasAndEntities` feeds a formula cell and
a DTD/external-entity payload; `Rejects_TooManyEntries` a 300-entry archive. `THIRD-PARTY-NOTICES.md`
is unchanged apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Four things deserve the reviewer's attention regardless:

1. **FX rate source (D-3).** There is no tenant rate table. A foreign-currency row must carry its
   own `fx_rate_to_base` or it is rejected. That is the honest choice today, but it means a USD
   invoice from a system that does not export rates cannot be imported until a rate table exists.
2. **XLSX date detection** relies on the cell's number-format id (built-ins 14–22, 45–47, or a
   custom format containing day/month/year tokens). A workbook that stores dates as plain numbers
   with no date style will present them as serial numbers and the row will be rejected as
   `invalid_date` — visible, not silent.
3. **The intermittent bootstrap race.** The first full run of this slice failed the entire security
   suite at fixture setup because two assemblies bootstrapped roles concurrently. `BootstrapAsync`
   now runs in one transaction under a cluster-wide advisory lock. Three consecutive full runs
   since were green. Worth a glance at `MigrationRunner.BootstrapAsync`.
4. **3a is not shippable to a pilot** (doc 10). Invoices can be imported and viewed; nothing can
   reduce a balance until 3b.
