# Slice 6 — Promise-to-Pay: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 02 §3 (SM-30…37), §2.3 C4/C5, §5 (SM-50, SM-51, SM-52, SM-53, SM-54) · doc 03
§6 (FIN-71, FIN-73), §8, E2 · doc 04 §5.5 (`promises_to_pay`, `ptp_invoices`, `tenant_holidays`),
DM-24 · doc 05 slice 6 · doc 06 §6.8 · doc 09 T-10, T-13, T-30, T-125, T-126 · doc 10 slice 6 ·
assumptions A-05, A-08.

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `promises_to_pay`, `ptp_invoices`, `tenant_holidays` on the isolation pattern; INV-07 (one `Active` promise per invoice) as a trigger (DM-24); `active_requires_human` CHECK (INV-13) |
| S2 | **The PTP machine** as one exhaustive table (`PtpMachine`); every transition one method, `FOR UPDATE`, guard, write, audit row, case activity (SM-02/03) |
| S3 | Record a promise (`Active`, `confirmed_by` = caller) with SM-32 (never above the covered open balance) and SM-36 (supersede overlapping active promises, linked); AI-sourced promises are `Proposed` only (SM-31) and `confirm` is the only path to `Active` |
| S4 | **Automatic evaluation** (SM-33/34/35): at `promised_date + grace` in **business days** (Fri/Sat weekend, tenant holidays) the system decides `Kept` / `PartiallyKept` / `Broken` from allocations received in the window; a promise is also `Kept` the moment payments cover it. **No API path sets these** |
| S5 | Case coupling: C4 on record (queue suppressed until the deadline), C5 on broken / cancelled, `ptp_kept_and_balance_zero`; a kept partial promise returns the case to work (F-1) |
| S6 | Post-dated cheque → `Active` promise on the cheque date (A-05, SM-51, E2); clearance → `Kept` through the ordinary payment path; bounce → `Broken` at once, case reopens at raised priority |
| S7 | Reliability (SM-37): kept / (kept + partially kept + broken) over 12 months with the denominator; no percentage below a sample of 3 |
| S8 | UI: record-promise dialog on the case (invoices with balances, amount, date with business-day hint, source, supersede warning), promises list grouped (due today · overdue to evaluate · active · recently broken/kept), a broken-promises view, promise detail with the evaluation arithmetic, promise history + reliability badge on the customer |

**Deferred, and to which slice:** AI-extracted promises (the `Proposed` state, `confirm` and
`reject` are built and tested through the API with `source = ai_suggested`, but nothing extracts
them until slice 9) · `human_correction` feedback to the AI evaluation set (slice 9) · a tenant
holiday editor (the table exists; seeded by SQL until settings UI grows) · SM-52 dispute-wins-over-
promise and SM-54's PTP guard on write-off approval (slice 7 introduces disputes; the write-off
guard is added here — see §2) · notifying a manager on a broken promise (messaging, slice 8) ·
E2E T-126 in Playwright (the same walk is an integration test).

---

## 2. Rules, stated explicitly — including what happens automatically on a broken promise

| Question | Answer | Why |
|----------|--------|-----|
| **Deadline** | `promised_date + ptp_grace_business_days`, counting Sunday–Thursday and skipping `tenant_holidays`. A promise for Thursday with grace 2 is due Monday. | SM-33, A-08, FIN-71, FIN-73. |
| **What counts as received** | Active allocations to the covered invoices whose payment is `Confirmed` and whose `received_date` is in `[capture date, deadline]`. A cheque contributes only once `Cleared` (its allocation exists only then). Withholding and credit notes are not payments and do not count. | SM-34. |
| **Verdict** | `received ≥ promised` → `Kept` (evaluated immediately on every payment, and at the deadline). At the deadline: `received ≥ ptp_partial_threshold_pct % × promised` → `PartiallyKept`, else `Broken`. Exactly on the threshold is `PartiallyKept`. | SM-35, T-30. |
| **Over-promise** | `promised_amount ≤ Σ open balance of the covered invoices` at capture, exactly (tolerance zero). Covered invoices must be `Open`, in the case's scope, and in one currency. | SM-32. |
| **Overlap** | Recording a promise that covers an invoice with an `Active` promise cancels that promise with reason `superseded` and `superseded_by_id`; the response says so. The trigger refuses any second `Active` cover of an invoice regardless of who writes it. | SM-36, INV-07. |
| **Who confirms** | A user-recorded promise is `Active` with `confirmed_by` = caller. `source = ai_suggested` → `Proposed`; only `POST /promises/{id}/confirm` makes it `Active`. The CHECK `active_requires_human` makes this structural. | SM-31, INV-13. |
| **Automatic on a broken promise** (the slice prompt's question) | **Exactly these, and nothing else:** (1) the promise becomes `Broken` or `PartiallyKept`, with `received_in_window` stored; (2) `customers.broken_promise_count_12m` is incremented; (3) the case moves `PromiseActive → InProgress` (C5), its suppression is cleared, and its priority is **recomputed** — the broken-promise factor and the customer's count raise it; (4) a `ptp` activity and an audit row are written; (5) the queue row shows a broken-promise badge. **Never automatic:** write-off, credit note, credit-term or credit-limit change, escalation, hold, any message to the customer, any change to a balance. A human reads the badge and decides. | CLAUDE.md → Financial Safety; SM-26; FIN-33. |
| **Kept but balance remains** | A partial promise kept in full leaves money owed. The case returns to work (`PromiseActive → InProgress`, event `ptp_kept`) rather than staying suppressed. | Doc 02 has no edge for this; flagged F-1. |
| **Post-dated cheque** | Recording a cheque with `cheque_date > received_date` for a customer with a non-terminal case creates an `Active` promise for the cheque amount on the cheque date, covering the case's in-scope invoices oldest-due first up to the amount, `source = cheque`, linked from `cheques.ptp_id`. No case → no promise (nothing to suppress). Clearance allocates → `Kept`. Bounce → the promise is `Broken` **immediately** (not at the deadline), the customer's bounced and broken counts both rise, the case reopens. | A-05, SM-51, E2, T-125. |
| **Write-off guard (SM-54)** | Approving a write-off of an invoice covered by an `Active` promise is refused (`ptp_active`). The dispute half arrives with slice 7. | SM-54. |
| **Reliability** | `kept / (kept + partiallyKept + broken)` over promises evaluated in the trailing 12 months; the response always carries `kept` and `denominator`; the UI shows "2 of 3 kept" and shows a percentage only when the denominator is ≥ 3. | SM-37. |
| **Evaluation timing** | The daily sweep (`POST /cases/sweep`, idempotent) evaluates every `Active` promise whose deadline ≤ today in the tenant calendar. Running it twice changes nothing. Payments evaluate the covered promises immediately for `Kept`. | SM-33, SM-06, T-13. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | Exhaustive matrix: 7 states × 8 events, legal and illegal (unit) | T-10 | `PtpMachine_Matrix_IsExhaustive` |
| AC-02 | Verdict at the boundaries: promised 1,000, threshold 50%: received 1,000 → Kept; 999.999 at deadline → PartiallyKept; 500.000 → PartiallyKept; 499.999 → Broken; 0 → Broken; threshold 60% moves the edge; before the deadline only Kept is possible | SM-35, T-30 | `Verdict_AtThresholdBoundaries` (unit) |
| AC-03 | Business-day deadline: Thursday + 2 → Monday; a holiday on Sunday pushes to Tuesday; grace 0 → same day | SM-33, A-08 | `Deadline_SkipsWeekendAndHolidays` (unit) |
| AC-04 | Over-promise is refused `exceeds_covered_balance`; a promise on an invoice outside the case's scope, not `Open`, or in another currency is refused; a promise on a terminal case is refused | SM-32 | `Record_GuardsCoverageAndAmount` |
| AC-05 | Recording a promise moves the case to `PromiseActive`, removes it from the queue until the deadline, and the queue returns it after the deadline once evaluated | C4, T-12 | `Promise_SuppressesTheQueue_UntilDeadline` |
| AC-06 | Overlapping promise: the earlier `Active` one becomes `Cancelled/superseded` with `supersededById`; the response lists it; the trigger alone refuses a direct second `Active` cover | SM-36, INV-07, DM-24 | `Supersede_CancelsThePriorPromise`, `OneActivePromisePerInvoice_IsEnforcedByTheTrigger` |
| AC-07 | Evaluation by the sweep: unpaid at deadline → `Broken`, customer count +1, case `InProgress` with a higher score than while `PromiseActive`, activity + audit rows; a second sweep changes nothing; nothing else changed (no write-off, no credit note, invoice still `Open`, no message) | SM-35, C5, T-13 | `BrokenPromise_ReopensTheCase_AndNothingElse` |
| AC-08 | Partial at the exact threshold → `PartiallyKept` (counts as broken for reliability); full payment before the deadline → `Kept` in the same request as the allocation; kept with balance remaining returns the case to `InProgress`; kept with zero balance → case `Resolved` | SM-34, SM-35, SM-50 | `Evaluation_KeptAndPartiallyKept` |
| AC-09 | No endpoint sets `Kept` / `PartiallyKept` / `Broken`: the transitions endpoint set is confirm / reject / cancel only; a body naming `kept` is 400/404 | doc 05 slice 6 | `NoManualKeptOrBroken` |
| AC-10 | AI-sourced (`source = ai_suggested`) → `Proposed`, no case transition, no suppression; `confirm` → `Active` with `confirmedBy`; `reject` requires a reason → `Rejected`; the CHECK refuses a direct `Active` row without `confirmed_by` | SM-31, INV-13 | `ProposedPromise_NeedsAHuman` |
| AC-11 | E2 / T-125: post-dated cheque → `Active` promise on the cheque date linked from the cheque, case `PromiseActive`; clear → allocation → invoice `Settled`, promise `Kept`, case `Resolved`. Alternative: bounce → promise `Broken`, bounced + broken counts 1, case `InProgress` at a higher score | SM-51, E2 | `PostDatedCheque_CreatesPromise_ClearKeeps_BounceBreaks` |
| AC-12 | Reliability: 2 kept + 1 broken → `kept 2, denominator 3, ratio 0.667`; below 3 the ratio is `null`; `PartiallyKept` counts in the denominator; promises older than 12 months do not count | SM-37 | `Reliability_ShowsTheDenominator` |
| AC-13 | SM-54: write-off approval on an invoice with an `Active` promise → 422 `ptp_active` | SM-54 | `WriteOff_IsBlockedByAnActivePromise` |
| AC-14 | Every promise transition writes one audit row and one `ptp` activity on the case | SM-03, INV-12 | `PromiseTransitions_AreAudited_OneToOne` |
| AC-15 | Cross-tenant: every `{id}` route → 404 with B's ids; a `ptp_invoices` row in A pointing at B's invoice is refused by the composite key; the sweep's evaluation touches only the caller's tenant | INV-05, T-71 | sweep + `CrossTenantPromise_IsRejectedByCompositeForeignKey` |
| AC-16 | T-126 walk: open case → log call → record promise → case suppressed → deadline passes unpaid → case back in the queue with a broken-promise badge | T-126 | `WorkTheQueue_T126` |
| AC-17 | UI: the record dialog blocks over-promise before sending (server message shown), shows the business-day deadline hint from the server, warns on supersede; the list groups promises; the badge never shows a percentage below 3; the browser computes no amount | UI-30, doc 06 §6.8 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| POST | `/cases/{id}/promises` | `ptp.write` |
| GET | `/promises`, `/promises/{id}`, `/customers/{id}/promise-history` | `cases.read` |
| POST | `/promises/{id}/confirm`, `/promises/{id}/reject`, `/promises/{id}/cancel` | `ptp.write` |

All through `TenantScopeMiddleware`; none anonymous. Evaluation rides on `POST /cases/sweep`.
The route count pin moves from 76 to 83.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | `ptp_kept` is a new case event (`PromiseActive → InProgress`) | A kept partial promise otherwise leaves the case suppressed forever. Flagged as F-1 for the spec. |
| D-2 | A post-dated cheque's promise covers the case's in-scope invoices oldest-due first up to the cheque amount | The cheque names no invoice; FIFO is the same proposal rule as allocation (FIN-25) and the human sees the coverage on the promise. |
| D-3 | Evaluation is part of the daily sweep, and payments evaluate for `Kept` immediately | One idempotent job, one place; the immediate check is SM-50's ordering. |
| D-4 | `tenant_holidays` is created now, empty | The deadline rule needs it; seeding is SQL until a settings screen exists (FIN-73: never computed). |
| D-5 | The trigger (DM-24) checks covered invoices on insert into `ptp_invoices` and on any status change to `Active` | The rule spans two tables; a partial index cannot express it. |
| D-6 | `PromiseService` is reached from `CaseService` through `IPromiseHooks` resolved at call time | SM-50's order (recompute → PTP → case) runs inside the ledger's hook; a constructor dependency would be circular. |
