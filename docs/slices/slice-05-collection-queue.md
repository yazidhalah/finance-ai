# Slice 5 — Collection queue & cases: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 02 §0 (SM-01…07), §2 (SM-20…27, C1–C11), §5 (SM-50, SM-51, SM-53) · doc 03 §8
(FIN-80…82) · doc 01 PRD-14 · doc 04 §5.5 (`collection_cases`, `case_invoices`,
`case_activities`), `tenant_settings.grace_days_before_case / collector_sees_only_assigned /
dunning_cadence_days / priority_weights_version` · doc 05 slice 5 · doc 06 §6.7 · doc 09 T-10,
T-11, T-12, T-13, T-31, T-133 · doc 10 slice 5.

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `collection_cases`, `case_invoices`, `case_activities` on the isolation pattern; `one_open_case_per_customer` partial unique index (INV-06); per-tenant `case_number` |
| S2 | **The case machine, C1–C11**, as one exhaustive transition table in C# (`CaseMachine.Next`); every transition is one method, `FOR UPDATE`, guard, write, audit row, activity row (SM-02/03) |
| S3 | **The daily sweep** (`CaseService.SweepAsync`, idempotent, SM-06): C1 creation past the tenant's grace days · scope refresh (invoices age in, settled ones leave) · `hold_expired` · `follow_up_due` · C10 `balance_zero` · nightly rescoring. Exposed as `POST /cases/sweep` until a scheduler exists |
| S4 | **Priority score** (FIN-80/81): integer 0–100 from stored decimals, versioned weights, factor breakdown stored with the case; recomputed on every transition and by the sweep (SM-27) |
| S5 | SM-50's case half: settling, writing off or voiding an invoice notifies the case (scope refresh → C10 or rescore) in the same transaction |
| S6 | Assignment (`cases.assign`), snooze, hold (reason + until), escalate (`cases.escalate`, permanent automation stop — SM-26), abandon (`cases.escalate`, reason; **not** a write-off — FIN-33), contact logging (C2), manual creation (`409 duplicate`) |
| S7 | Queue with suppression, filters (`assignedTo`, `bucket`, `minAmount`), PRD-14 scoping, rule-based `suggestedAction`; queue summary; case list; case detail with factor breakdown; merged timeline |
| S8 | UI: queue (ranked, expandable breakdown, filters, `j`/`k`/`Enter`/`s`, celebration empty state) and case detail (snapshot, invoices, timeline, action rail with the SM-26 hard confirmation) |

**Deferred, and to which slice:** `ptp_recorded` / `ptp_broken` / `ptp_cancelled` / `ptp_kept_and_balance_zero`
(C4, C5 — slice 6; the transitions exist in the machine and are unit-tested, no endpoint fires
them) · `dispute_opened` / `all_disputes_resolved` (C6, C7 — slice 7, same) · `message_sent` /
`reply_received` (C3 — slice 8, same) · the `dispute_dampener` input is the case being `Disputed`
until disputes exist · a scheduler for the sweep (first background worker) · Owner notification on
escalation (C9's "notifies Owner" → messaging, slice 8) · the AI `aiSuggestion` field (slice 9) ·
axe scans (T-133's tooling; keyboard operation is built and tested by hand).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **When is a case created?** | By the sweep, for a customer with ≥ 1 `Open` invoice whose `due_date + grace_days_before_case < today` (tenant calendar) and no non-terminal case. Manual creation requires ≥ 1 past-due `Open` invoice. | C1. Grace gates *creation*; scope is wider (below). |
| **What is in scope?** | Every `Open` invoice of the customer with `due_date < today`, at creation and on every sweep. An invoice leaves scope (`removed_at`) when it is no longer `Open`. A not-yet-due invoice is never in scope. | SM-20: one case per customer covering all overdue invoices. |
| **When does a case resolve itself?** | When no in-scope invoice is `Open` any more (all `Settled`, `WrittenOff` or `Void`) — checked in the same transaction as the settling allocation, write-off approval or void (SM-50), and by the sweep. | C10. |
| **Reopening** | Never in place. An invoice that reopens (I7) after its case resolved gets a **new** case at the next sweep. | SM-05. |
| **Escalated (SM-26)** | `escalated_at` is set once and never cleared; `automationDisabled: true` in every response; `next_action_at` is cleared and the sweep never sets it again; the only outgoing edges are `balance_zero` and `abandon`. No endpoint in this or any later slice may send from an escalated case. | The one-way door. Asserted at the API (AC-04). |
| **Abandoned** | Terminal; invoices stay `Open` and keep aging. | C11, FIN-33. |
| **Queue eligibility** | Status ∉ {`OnHold`, `Escalated`, `Resolved`, `Abandoned`} and (`next_action_at` is null or ≤ now). Ordered `priority_score DESC, max_days_past_due DESC`. | Doc 05 slice 5. |
| **PRD-14** | When `collector_sees_only_assigned` is on and the caller's role is `Collector`, the queue, case list and summary are filtered to `assigned_to = me` server-side, whatever `assignedTo` says. It is a filter, not tenancy. | PRD-14. |
| **Priority score** | `score = Σ round(w_i × factor_i)` with each contribution rounded to an integer *first*, so the breakdown always sums to the score, then clamped to 0–100. Inputs are stored decimals/ints; nothing from an AI (FIN-82). | FIN-80; acceptance 6 by construction. |
| **Weights (FIN-81)** | Versioned in code (`PriorityWeights.Version(n)`); the case stores `weights_version`; `tenant_settings.priority_weights_version` selects the version. v1: amount 35 (saturates at 10,000 base), days past due 30 (saturates at 120), broken promises 15 (at 3), bounced cheques 10 (at 2), customer value 10 (`RiskFlag`: None 0, Watch 0.5, HighRisk/Legal 1), recent contact −10 (linear over 7 days), dispute −20 (case `Disputed`). | The spec puts the weights "in tenant settings"; only the version column exists there, and per-tenant weight *values* would need a table this slice does not need. Flagged. |
| **Amount factor** | `open_balance_in_base = Σ ToBaseIndicative(balance_cache, fx_rate_to_base)` over in-scope `Open` invoices — the same per-invoice rounding as the aging report, used only for ranking, never shown as money. | FIN-80: "not a monetary figure". |
| **Suggested action** | Rule-based: `Disputed` → `resolve_dispute`; `PromiseActive` → `await_promise`; `Escalated` → `manual_follow_up`; max dpd ≥ last cadence step and no contact in 7 days → `call`; else `send_reminder` with `dunning_{stage}` where stage is the last `dunning_cadence_days` entry ≤ max dpd. Language = customer's `preferred_language`. | Doc 05: rule-based until slice 9. |
| **Timeline** | `case_activities` (notes, calls, status changes — written by the service) merged with allocations to in-scope invoices (`payment`), ordered by time. Messages, PTPs, disputes and AI suggestions join as their slices ship. | Doc 05 `/timeline`. |
| **Snooze vs hold** | Snooze sets `next_action_at` (queue suppression only, any active status, `cases.write`). Hold is a state (`OnHold`), needs reason + `hold_until`, stops the sweep's automation, `cases.write`; expiry resumes it. | C3/C8. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | **Exhaustive matrix**: every (state, event) pair — 9 × 17 — is enumerated; legal pairs yield the documented state, illegal ones throw `InvalidTransitionException`; adding a state or event without extending the table fails the test | T-10 | `CaseMachine_Matrix_IsExhaustive` (unit) |
| AC-02 | Guards: `hold` without reason or `hold_until` → refused; `escalate` / `abandon` without reason → refused; `escalate` / `abandon` by an Accountant → 403; manual creation with an open case → `409 duplicate`; with no overdue invoice → 422 | T-11, C8, C9, C11, INV-06 | `Guards_HoldEscalateAbandon_AndManualCreation` |
| AC-03 | INV-06: a second non-terminal case for the same customer is refused **by the database** when inserted directly | INV-06 | `OneOpenCasePerCustomer_IsEnforcedByTheIndex` |
| AC-04 | Escalation: `automationDisabled: true`; the case leaves the queue; the sweep never re-suppresses or resumes it; every event but `balance_zero` and `abandon` → `409 invalid_transition`; the flag survives resolution | SM-26, T-12 | `Escalation_StopsAutomationPermanently` |
| AC-05 | Sweep: customer with an invoice 2 days overdue and grace 3 → no case; 4 days → case with all past-due invoices in scope, priority computed; **running the sweep twice on the same day creates nothing and writes no second audit row** | C1, T-13 | `Sweep_CreatesCases_PastGrace_Idempotently` |
| AC-06 | Suppression: snoozed until D leaves the queue and returns at D (clock pinned); `hold` leaves and returns at `hold_until` via the sweep (`hold_expired`); `AwaitingCustomer` with a follow-up date returns via `follow_up_due` | C2, C3, C8, T-12 | `Suppression_LeavesAndReturnsOnSchedule` |
| AC-07 | C10 / SM-50: allocating the full balance of every in-scope invoice resolves the case in the same request; a write-off approval or void of the last open invoice does too; a partial payment only rescores | SM-50, C10 | `SettlingEverything_ResolvesTheCase` |
| AC-08 | Score determinism: same inputs + same version → same score and same breakdown; the breakdown's contributions sum to the score; 0 ≤ score ≤ 100 over 2,000 random inputs; a different version can differ | T-31, FIN-80 | `PriorityScore_IsDeterministic_AndExplainable` (unit) |
| AC-09 | The factors returned by the API equal the stored contributions and sum to `priorityScore`; `weightsVersion` is present | FIN-80/81 | `CaseDetail_FactorsSumToScore` |
| AC-10 | PRD-14: with `collector_sees_only_assigned` on, a Collector's queue, list and summary contain only cases assigned to them; an Accountant's are unaffected; with it off a Collector sees all | PRD-14 | `CollectorScoping_IsServerSide` |
| AC-11 | Assignment: to an active member → ok, activity logged; to a user of another tenant → 404; to a disabled member → 422 | doc 05 | `Assign_ValidatesMembership` |
| AC-12 | Every transition writes exactly one audit row (`case.status_changed`) and one `status_change` activity; C1 has `from_state = null` and actor `system` | SM-03, INV-12 | `Transitions_AreAudited_OneToOne` |
| AC-13 | Queue order is `priorityScore DESC, maxDaysPastDue DESC`; `bucket` and `minAmount` filter; `assignedTo=me` filters; summary counts agree with the list | doc 05 | `Queue_OrdersAndFilters` |
| AC-14 | Timeline merges activities and allocations in time order | doc 05 | `Timeline_MergesActivitiesAndPayments` |
| AC-15 | Cross-tenant: every `{id}` route → 404 with real ids of tenant B; a `case_invoices` row in A pointing at B's invoice is refused by the composite key; the sweep run in A creates nothing in B | INV-05, T-71 | sweep + `CrossTenantCase_IsRejectedByCompositeForeignKey`, `Sweep_IsTenantBound` |
| AC-16 | 5,000 cases: `GET /queue` P95 < 800 ms over 20 calls | doc 10 acceptance 8 | `Queue_P95_Under800ms` — measured P95 21 ms once paging and the bucket filter moved into SQL |
| AC-17 | UI: rows show the score with an expandable breakdown whose contributions are the server's; `j`/`k`/`Enter`/`s` work; escalate shows the hard confirmation; the browser computes no total or score | UI-30, doc 06 §6.7 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/queue`, `/queue/summary`, `/cases`, `/cases/{id}`, `/cases/{id}/timeline` | `cases.read` |
| POST | `/cases`, `/cases/{id}/activities`, `/cases/{id}/snooze`, `/cases/sweep` | `cases.write` |
| POST | `/cases/{id}/transitions` | `cases.write`; `escalate` and `abandon` additionally require `cases.escalate` (403 otherwise) |
| POST | `/cases/{id}/assign` | `cases.assign` |

All through `TenantScopeMiddleware`; none anonymous. The route count pin moves from 65 to 76.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Weights are versioned constants in code; the tenant selects a version | FIN-81 needs reproducibility, which a version number gives; per-tenant weight values would need a table and an editor no persona has asked for. |
| D-2 | The sweep is an endpoint (`POST /cases/sweep`) until a scheduler exists | No background worker yet; the job is idempotent so an operator or a cron calling it is safe. |
| D-3 | Each score contribution is rounded to an integer before summing | Makes "breakdown sums to score" true by construction rather than by tolerance. |
| D-4 | `case_number` is allocated under a per-tenant advisory lock and protected by a unique index | Human-friendly, gap-free enough, and a race produces a constraint error, never a duplicate. |
| D-5 | `case_invoices.removed_at` marks an invoice leaving scope; rows are never deleted | The timeline can say "INV-7 settled on the 12th"; no DELETE grant, like the ledger. |
| D-6 | `escalated_at` is the permanent automation flag | One column, set once, never cleared (C9); a separate boolean could drift from it. |
| D-7 | `case_invoices` carries an `id` and a `UNIQUE (tenant_id, id)` beside the doc 04 composite primary key | DM-10 (every tenant-scoped table exposes a tenant-qualified key) is enforced by the schema enumeration test; the doc's DDL had only the three-column key. |
| D-8 | The sweep's transitions are recorded with actor `system`, and the sweep run itself is audited once with the user who triggered it | SM-03 says C1 and the time-based transitions are the system's; the endpoint is only a trigger until a scheduler exists. |
| D-9 | PRD-14's "is this a Collector" test lives in `RolePermissions.IsAssignmentScoped` (Domain), not in an endpoint | SEC-12's static rule forbids role names in API authorization; this is a spec-defined visibility filter, kept in one named place so the rule test still holds. |
