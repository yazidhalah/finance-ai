# Slice 5 — Collection queue & cases: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **medium**. No money moves; the slice reads balances and writes case state.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all eleven. None is anonymous.**

| Endpoints | Declaration |
|-----------|-------------|
| `GET /queue`, `GET /queue/summary`, `GET /cases`, `GET /cases/{id}`, `GET /cases/{id}/timeline` | `RequiresPermission(cases.read)` |
| `POST /cases`, `POST /cases/sweep`, `POST /cases/{id}/activities`, `POST /cases/{id}/snooze` | `RequiresPermission(cases.write)` |
| `POST /cases/{id}/transitions` | `RequiresPermission(cases.write)`; `escalate` / `abandon` additionally check `cases.escalate` in the handler → 403 (`Guards_HoldEscalateAbandon_AndManualCreation`) |
| `POST /cases/{id}/assign` | `RequiresPermission(cases.assign)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at **76** and
the anonymous set at exactly `register`, `login`, `refresh`. `cases.read` is held by every role
(doc 01 §5.1) and joined the pinned universal list (now six). The generated 404 sweep covers every
`{id}` route with a real case id of tenant B (`ForeignIds.CaseId`, created through `POST /cases`
on an overdue invoice). Ordering inside handlers is existence → body → permission, so a foreign
id is 404 before any validation message can leak what a valid body looks like (API-03).

**PRD-14** is a filter, not an authorization: for a scoped Collector every read (queue, summary,
list, detail, timeline) is narrowed to `assigned_to = me` whatever the query says; a foreign case
id is 404 rather than 403. `CollectorScoping_IsServerSide`. The role test lives in
`RolePermissions.IsAssignmentScoped` so SEC-12's static rule (no role names in API authorization)
still holds (D-9).

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, all three.**

| Table | `tenant_id NOT NULL` | RLS forced | Policy (no platform clause) | `(tenant_id, id)` key | Composite FKs |
|-------|:--:|:--:|:--:|:--:|---|
| `collection_cases` | ✅ | ✅ | ✅ | ✅ | `→ customers` |
| `case_invoices` | ✅ | ✅ | ✅ | ✅ (D-7) | `→ collection_cases`, `→ invoices` |
| `case_activities` | ✅ | ✅ | ✅ | ✅ | `→ collection_cases` |

The enumeration test now walks **22** tables; `NoSingleColumnForeignKey_JoinsTwoTenantScopedTables`
holds. No DELETE grant on any of the three.

**Targeted tests for the non-standard patterns** (`CaseIsolationTests`, `CaseTests`):

- **Layer 3 alone** — `CrossTenantCase_IsRejectedByCompositeForeignKey`: as superuser, a scope
  row in A for B's invoice (`fk_ci_invoice`), the same row under B (`fk_ci_case`), a case in A for
  B's customer (`fk_case_customer`), an activity on A's case under B (`fk_activity_case`) — all
  refused. There is no tenant value that makes a cross-tenant row fit.
- **INV-06 at the database** — `OneOpenCasePerCustomer_IsEnforcedByTheIndex`: a second
  non-terminal case inserted directly is refused by `one_open_case_per_customer`; once the first
  is terminal a new one is allowed (SM-05).
- **The sweep is the first code that writes rows for many customers at once.**
  `Sweep_IsTenantBound`: run in A it creates nothing in B; B's own sweep numbers from 1; A
  cannot read, assign or escalate B's case by id. The sweep runs under a per-tenant advisory lock
  keyed on the tenant id taken from the bound context, never from input.
- **Raw SQL under RLS** — three statements: the two advisory locks and `SELECT 1 … FOR UPDATE`
  on the case; all inside the request transaction with the tenant GUC set.

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`; the static test still confines it. All
handlers run after tenant binding; the sweep is a tenant-scoped request like any other.

One SM-50 note: `LedgerService.RecomputeAsync` and `VoidInvoiceAsync` now call
`ICaseHooks.InvoiceChangedAsync` in the same transaction. The hook looks the customer's active case
up under the tenant filter and never takes a tenant from the invoice row — the row is already
tenant-filtered.

## 4. Any new money-related field or calculation?

**One stored field, `overdue_balance_base numeric(19,3)`, and no new arithmetic on money.**

| Item | As built |
|------|----------|
| `overdue_balance_base` | `Σ AgingRules.ToBaseIndicative(balance_cache, fx_rate_to_base)` over in-scope `Open` invoices — the same per-invoice rounding as the aging report, reused, not a second rounding rule. It is a **ranking input** (FIN-80: "not a monetary figure"): the API never returns it; the queue shows per-currency open balances (`overdueBalances`, `GROUP BY currency`, FIN-04). `EveryMoneyColumn_IsNumeric19_3` covers it. |
| Priority score | Integer 0–100 from stored decimals and ints; each weight × normalized factor is rounded to an integer *before* summing (D-3), so `Σ contributions == score` by construction; then clamped. `PriorityScore_IsDeterministic_AndExplainable` (2,000 random inputs), `Weights_SaturateAndDampen_AsDocumented`, `CaseDetail_FactorsSumToScore` (through the API: 29 + 16 + 0 + 5 + 5 − 10 = 45). No AI input anywhere (FIN-82). |
| Weights | `PriorityWeights.Version1`, selected by `tenant_settings.priority_weights_version`; the case stores `weights_version` (FIN-81). Unknown version → refused. |
| Days past due | `DayNumber` differences in the tenant's calendar (`AgingRules.TodayIn`). |
| Frontend | `Queue.tsx` / `CaseDetail.tsx` render the server's contributions and balances; `queue.test.tsx` greps for arithmetic on amounts, scores and contributions and for `reduce(`. |

**Nothing rounds beyond the reused `ToBaseIndicative`.**

## 5. Any AI-touching code?

**None.** The `suggestedAction` is rule-based (`SuggestedActions.For`, unit-tested) and the API
shape leaves room for a separate `aiSuggestion` field (slice 9). `case_activities.actor_kind`
admits `ai_assisted` only with an `ai_suggestion_id` (CHECK), and nothing writes it yet (SM-04).

## 6. Any new dependency?

**None.** `THIRD-PARTY-NOTICES.md` is unchanged apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Six things deserve the reviewer's attention:

1. **Weights are code, not tenant data (D-1).** FIN-81 says "default weights live in tenant
   settings"; the schema has only `priority_weights_version`. A tenant chooses a version, not a
   weight. If per-tenant values are wanted, it is a table and an editor — not built.
2. **The sweep is an endpoint (D-2).** `POST /cases/sweep` (`cases.write`) runs the daily job for
   the caller's tenant; it is idempotent (`Sweep_CreatesCases_PastGrace_Idempotently`) so a cron
   hitting it is safe. The scheduler and the "run for every tenant" loop (SEC-22) come with the
   first background worker.
3. **Scope vs. grace.** Grace gates *creation* (dpd > `grace_days_before_case`); once a case
   exists, *every* past-due Open invoice of the customer is in scope, including ones within grace.
   Stated in the slice doc §2; the alternative (grace per invoice) would leave a 2-day-late
   invoice out of the conversation with a customer we are already calling.
4. **C10 also fires when the only remaining Open invoices are not yet due.** Scope is past-due
   invoices; if all of those settle, the case resolves even though the customer still owes
   something not yet due. A new case opens when that invoice passes grace. This reads the spec's
   "everything in scope settled" literally.
5. **Four machine rows go beyond the diagram** but follow the table's "any active" wording:
   `Open → PromiseActive`, `Open → Disputed`, `Open → OnHold`, `Open → Escalated`. The exhaustive
   matrix (`CaseMachine_Matrix_IsExhaustive`, 9 × 17 pairs) is transcribed independently in the
   test, so a disagreement with the reviewer is one row to delete on both sides.
6. **`AwaitingCustomer` has no user-facing entry until slice 8** (`message_sent`). The
   `follow_up_due` return path is built and tested by setting the state directly
   (`Suppression_LeavesAndReturnsOnSchedule`).
