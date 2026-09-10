# Slice 2 — Customers: self-review

Per `CLAUDE.md` → Slice Self-Review. Each answer is for every change in the slice, and every
"yes" names the test or file that makes it so. Where the honest answer is "yes, with a caveat",
the caveat is stated rather than smoothed over.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all ten. None is anonymous.**

| Endpoint | Declaration | Why |
|----------|-------------|-----|
| `GET /customers`, `GET /customers/{id}`, `GET /customers/duplicates`, `GET /customers/{id}/contacts` | `RequiresPermission(customers.read)` | Reads of tenant business data. |
| `POST /customers`, `PATCH /customers/{id}`, `DELETE /customers/{id}`, `POST/PATCH/DELETE …/contacts` | `RequiresPermission(customers.write)` | Mutations; Viewer and Collector lack the permission (doc 01 §5.1). |

Evidence: `EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at
23 and the anonymous set at exactly `register`, `login`, `refresh`. The 401 and 403 sweeps
(`EveryProtectedEndpoint_Returns401WithoutToken`, `…Returns403ForRoleWithoutPermission`) are
generated from the route table and covered all ten new routes without any per-endpoint code.
`customers.read` joined `tenant.read` in the pinned universal-permission list, because doc 01
§5.1 grants it to every role; its boundary is tenancy (question 2), not role.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, both tables — and the targeted tests the checklist asks for exist.**

| Table | `tenant_id NOT NULL` | RLS enabled | RLS forced | Policy | `(tenant_id, id)` key | FK to a tenant-scoped table |
|-------|:--:|:--:|:--:|:--:|:--:|---|
| `customers` | ✅ | ✅ | ✅ | `tenant_isolation` (no platform clause) | ✅ | none — references only `tenants` (platform) |
| `customer_contacts` | ✅ | ✅ | ✅ | `tenant_isolation` (no platform clause) | ✅ | **composite** `(tenant_id, customer_id) → customers (tenant_id, id)` |

Enumeration-based coverage: `EveryTenantScopedTable_HasTenantIdRlsForcedPolicyAndCompositeKeys`
now walks 8 tables; `NoSingleColumnForeignKey_JoinsTwoTenantScopedTables` confirms the contact FK
is composite; `ConnectionWithoutTenantGuc_ReturnsZeroRows` confirms both new tables fail closed.

**Non-standard access patterns, with their targeted tests** (`CustomerIsolationTests`):

- **Search** is raw SQL (`FromSql`) so it can call `app_normalize_arabic` and `similarity`. Layer 1
  is restated by hand (`WHERE tenant_id = @currentTenant`) and EF composes its tenant and
  soft-delete filters on top; layer 2 governs the statement regardless.
  `SearchAndDuplicates_NeverReturnAnotherTenantsCustomers` seeds the *same* Arabic name in two
  organizations and asserts each sees one.
- **Duplicate detection** is a pairwise self-join, also raw SQL with an explicit tenant predicate.
  Same test: B has a near-duplicate pair, A's view is empty.
- **Composite FK proved alone:** `CrossTenantContact_IsRejectedByCompositeForeignKey` inserts as
  superuser (RLS bypassed) a contact in A pointing at a customer of B — refused by
  `fk_contact_customer`.
- **Contacts addressed through a foreign customer:** `Contacts_OfAnotherTenantsCustomer_AreUnreachable`
  — list, add and delete all 404 with B's real ids, and B's data is unchanged.
- **Soft delete composes with, and does not weaken, the tenant filter:** EF 10 named filters
  (`"Tenant"` and `"SoftDelete"`) are ANDed; nothing in the codebase ignores `"Tenant"` by name
  (the static test `QueryFilters_AreBypassedOnlyWhereDocumented` still confines
  `IgnoreQueryFilters` to the two slice-1 files).
- **`ExecuteUpdateAsync`** (demoting the previous primary contact) runs through the EF filter, so
  it is tenant-scoped at layer 1 and under RLS at layer 2.

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing was added to `PlatformIdentityStore`; `PlatformScope_IsOpenedOnlyByPlatformIdentityStore`
still passes. Every new handler runs after `TenantScopeMiddleware` has bound the tenant from the
token and opened the transaction.

One ordering point worth stating because it is about information disclosure rather than timing:
`POST …/contacts`, `PATCH /customers/{id}` and `PATCH …/contacts/{contactId}` **check existence
before validating the body**. A foreign or missing id therefore answers 404 whatever the request
looks like; a request's shape can never be used to distinguish "does not exist" from "exists but
is not yours". The 404 sweep is what caught the original order (it sent an empty body and got 400).

## 4. Any new money-related field or calculation?

**One field, no calculation.**

- `customers.credit_limit_amount` is `numeric(19,3)`, mapped to C# `decimal?`, paired with
  `credit_limit_currency` under a CHECK that both are null or neither (`credit_limit_pair`).
- On the wire it is `{ "amount": "12500.500", "currency": "JOD" }` — a string with exactly three
  decimals (`MoneyDto.From` uses `F3`, invariant culture). A JSON number is refused at the
  boundary; more than three decimals is refused rather than rounded.
- **Rounding: nowhere.** Nothing is split, allocated or converted in this slice. Refusing over-precise
  input is deliberate: rounding is a financial rule (FIN-05), not a form-field convenience.
- In the audit trail the amount is serialized as a string (`Snapshot`), per DM-28.
- The frontend carries the amount as the string the user typed and never parses it to a number
  (`toInput`); `NoAssemblyType_HasFloatingPointMoneyMember` still passes.

Evidence: `Money_IsDecimalAndSerializedAsString`, `CreditLimit_RequiresCurrency`,
`CustomerMutations_AreAudited`.

`balances` in the response is an **empty list**, not a set of zeros (slice doc D-2).

## 5. Any AI-touching code?

**None.** No call to the AI service, no prompt, no model output is read anywhere in this slice.

## 6. Any new dependency?

**No new packages.** Two PostgreSQL contrib extensions were enabled in the bootstrap script:
`pg_trgm` (Arabic-aware search and duplicate detection, DM-20) and `btree_gin` (so the trigram
index can lead with `tenant_id`, doc 04 §7). Both ship with PostgreSQL under the PostgreSQL License
and are recorded in `THIRD-PARTY-NOTICES.md`.

## Not sure / flagged

Nothing in the six questions is "not sure". Two things are worth the reviewer's eye anyway:

- **Trigram thresholds** (`0.3` for search hits, `0.6` for duplicate candidates) are engineering
  guesses, not product decisions. They are constants at the top of `CustomerEndpoints.cs`; the
  pilot's real customer list should set them.
- **`FromSql` with a computed `%` pattern**: the search term is passed as a parameter and
  `LIKE`-escaped (`EscapeLike`); it is never concatenated into the SQL text. Worth a second look
  precisely because it is the one place user text meets raw SQL.
