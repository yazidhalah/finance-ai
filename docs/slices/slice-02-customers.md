# Slice 2 — Customers: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 04 §5.1 (tables, DM-20, DM-21) · doc 05 slice 2 (contracts) · doc 06 §6.3
(screens) · doc 10 slice 2 (acceptance) · doc 03 FIN-04, FIN-15, FIN-72 · slice 1 §5 (the
isolation pattern this slice copies).

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `customers` table: names in both languages, legal name, tax number, preferred language, payment terms, credit limit (decimal + currency pair), default currency, risk flag, status, notes, soft delete |
| S2 | `customer_contacts` table with a composite `(tenant_id, customer_id)` foreign key and one primary contact per customer, enforced by the database |
| S3 | Arabic-aware name normalization as a generated column, with a `pg_trgm` index (DM-20) |
| S4 | Create, read, update (with `If-Match`), list with search and cursor pagination, soft delete |
| S5 | Contacts: list, add, update, remove; setting a primary demotes the previous one atomically |
| S6 | Duplicate detection: candidate pairs with a similarity score, read-only |
| S7 | Open-balance guard on delete, behind an interface with a stub until invoices exist (doc 10 §2.4) |
| S8 | Bilingual RTL-correct UI: list with search, detail/edit with side-by-side names each in its own `dir`, contacts |

**Deferred** (§7): merge (DM-21), statement of account, balance blocks (FIN-15), `hasOverdue`
filter, promise reliability, open case — every one of them reads rows that slice 3+ creates.

---

## 2. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | Searching `شركه الامل` finds `شركة الأمل` and vice versa; `Al Amal` finds it too | doc 10 §2.1, DM-20 | `Search_IsArabicAware` |
| AC-02 | Normalization strips tatweel and diacritics and unifies alef, teh marbuta and yeh forms | DM-20 | `Normalize_UnifiesArabicForms` (unit, via SQL) |
| AC-03 | Duplicate detection surfaces the pair above with a similarity score | doc 10 §2.2 | `Duplicates_SurfaceNearIdenticalNames` |
| AC-04 | A customer needs at least one name; the database enforces it too | doc 04 | `Create_WithoutAnyName_Returns400`, `Database_RejectsNamelessCustomer` |
| AC-05 | The credit limit is `numeric(19,3)` with a mandatory currency sibling; amount and currency are set together or not at all | DM-09, FIN-01 | `CreditLimit_RequiresCurrency`, `Money_IsDecimalAndSerializedAsString` |
| AC-06 | Money in JSON is `{ "amount": "1250.500", "currency": "JOD" }` — a string with three decimals, never a number | API-05 | `Money_IsDecimalAndSerializedAsString` |
| AC-07 | A customer code is unique within a tenant, case-insensitively, among non-deleted rows; the same code may exist in another tenant | doc 04 | `Code_IsUniquePerTenant_CaseInsensitive` |
| AC-08 | `PATCH` requires `If-Match`; a stale version returns `409 concurrency_conflict` | API-09 | `Update_WithStaleIfMatch_Returns409` |
| AC-09 | Soft delete sets `deleted_at`; the row disappears from list and detail but survives in the database | DM-08 | `Delete_IsSoft` |
| AC-10 | A customer with an open balance cannot be deleted (`422 has_open_balance`) — guard unit-tested against a stub until slice 3 | doc 10 §2.4 | `Delete_WithOpenBalance_Returns422` |
| AC-11 | List is cursor-paginated: `limit`, `cursor`, `nextCursor`, `totalCount` | API-07 | `List_IsCursorPaginated` |
| AC-12 | Exactly one primary contact per customer; promoting a contact demotes the previous one in the same transaction, and the database rejects two primaries | doc 04 | `Contacts_ExactlyOnePrimary`, `Database_RejectsTwoPrimaryContacts` |
| AC-13 | **Cross-tenant customer id → 404** on every `{id}` endpoint; contacts of another tenant's customer are unreachable | T-71, SEC-13 | sweep `EveryEndpointWithId_Returns404ForCrossTenantId` (picks the new routes up automatically) + `Contacts_OfAnotherTenantsCustomer_AreUnreachable` |
| AC-14 | **A contact cannot reference a customer of another tenant** — rejected by the composite FK with RLS bypassed (layer 3 alone) | DM-10, T-75 | `CrossTenantContact_IsRejectedByCompositeForeignKey` |
| AC-15 | Both new tables pass the schema assertions automatically: `tenant_id NOT NULL`, RLS enabled and forced, a policy, `(tenant_id, id)` key, no single-column FK to a tenant-scoped table | DM-01/02/10 | `EveryTenantScopedTable_…` (existing, now covers 8 tables) |
| AC-16 | A connection without `app.tenant_id` sees zero customers and zero contacts | DM-05 | `ConnectionWithoutTenantGuc_ReturnsZeroRows` (existing) |
| AC-17 | Every new endpoint has 401 and 403 tests, generated from the route table | SEC-14 | existing sweeps |
| AC-18 | A body carrying `tenantId`, `status`… on create/update is rejected `400 unexpected_field` | SEC-17 | `Body_WithUnknownField_Returns400` (customers) |
| AC-19 | Create and update write an audit row naming the actor and the changed fields | SEC-50 | `CustomerMutations_AreAudited` |
| AC-20 | Arabic and English name fields render side by side, each with its own `dir`; the list falls back across languages with a visible marker | doc 10 §2.5, UI-24 | web: `customer name rendering` |
| AC-21 | Every new UI string exists in both catalogues | UI-11 | web: i18n parity (existing) |

---

## 3. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/customers` | `customers.read` |
| POST | `/customers` | `customers.write` |
| GET | `/customers/duplicates` | `customers.read` |
| GET | `/customers/{id}` | `customers.read` |
| PATCH | `/customers/{id}` | `customers.write` |
| DELETE | `/customers/{id}` | `customers.write` |
| GET | `/customers/{id}/contacts` | `customers.read` |
| POST | `/customers/{id}/contacts` | `customers.write` |
| PATCH | `/customers/{id}/contacts/{contactId}` | `customers.write` |
| DELETE | `/customers/{id}/contacts/{contactId}` | `customers.write` |

All ten run through `TenantScopeMiddleware`; none is anonymous.

---

## 4. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | `status` column (`Active` / `Inactive`) added to `customers`, distinct from `deleted_at` | Requested scope. Inactive = "we no longer trade with them but the history stands"; deleted = "should never have existed". Doc 04 amended. |
| D-2 | `balances` is returned as an **empty list**, not fabricated zeros | Doc 10 says "scaffolded (all zero)". A zero is a number a user may believe; an empty list says "nothing to show yet" and costs the UI nothing. |
| D-3 | Search is `ILIKE` on the normalized column **or** trigram similarity above 0.3 | Substring for exact-ish matches, similarity for typos. Both go through the same `app_normalize_arabic` function, so the C# side has no second copy of the normalization rules. |
| D-4 | Cursor pagination orders by `id` (UUIDv7 = creation order) | Stable under concurrent inserts; a name-ordered cursor would need a composite cursor for no gain at this size. |
| D-5 | Duplicate detection is an explicit raw SQL query with an explicit `tenant_id = @tenant` predicate | Pairwise self-join is not expressible through the EF filter. Layer 1 is replaced by the explicit predicate; layer 2 still applies. Named in the review doc. |
| D-6 | `pg_trgm` created in bootstrap alongside `citext` | Trusted extension, but creating it needs `CREATE` on the database, which the migrator role deliberately lacks. |
| D-7 | The open-balance guard is `ICustomerBalanceGuard` with `NoInvoicesYetBalanceGuard` | Slice 3 replaces the implementation; the endpoint and its 422 test do not change. |

---

## 5. Deferred, and what it blocks

| Deferred | Reason |
|----------|--------|
| Merge (DM-21) | Irreversible, audited-high-severity, "moves every child row". With only contacts as children it would be re-implemented in slice 3. Better done once, when invoices/payments/cases exist. |
| Statement of account | Reads invoices and payments. |
| Balance blocks, `hasOverdue`, open case, promise reliability | Read slices 3–6. |
| Export / PDF | Slice 3 or later, with CSV-injection protection (SEC-45). |
