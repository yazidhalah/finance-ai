# Slice 6 — Promise-to-Pay: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **high** (per the slice plan: it touches escalation behaviour and the case machine).

## What happens automatically on a broken promise — and what needs a human

The slice prompt asked for this to be explicit. The whole automatic path is
`PromiseService.EvaluateAsync → MarkBrokenAsync → ReleaseCaseAsync`, and it does exactly this:

| Automatic | Where | Test |
|-----------|-------|------|
| The promise becomes `Broken` (or `PartiallyKept` at/above the threshold), with `received_in_window` and a plain-language `evaluation_note` stored | `EvaluateAsync` | `BrokenPromise_ReopensTheCase_AndNothingElse`, `Evaluation_KeptAndPartiallyKept` |
| `customers.broken_promise_count_12m` += 1 (also for `PartiallyKept`, SM-37) | `MarkBrokenAsync` | same |
| The case leaves `PromiseActive` → `InProgress` (C5), its queue suppression is lifted, and its priority is **recomputed** — the broken-promise factor and the customer's count raise it | `ReleaseCaseAsync` → `CaseService.FireAsync` | same (`priorityScore` strictly higher than while `PromiseActive`) |
| One audit row and one `ptp` case activity per transition | `ApplyAsync` | `PromiseTransitions_AreAudited_OneToOne` |
| The queue row shows the count in the factor breakdown; the customer page shows "n of m kept" | UI | `promises.test.tsx` |

**Never automatic — asserted, not assumed** (`BrokenPromise_ReopensTheCase_AndNothingElse`):
no write-off (`write_offs` count 0), no credit note (`credit_notes` count 0), the invoice stays
`Open` with its balance untouched, the case is not escalated and not put on hold, and no message
exists to send (messaging is slice 8; when it arrives, SM-53 already forbids it from an escalated
case, and a broken promise does not escalate). Credit terms and limits are never touched by any
code in this slice. A human reads the badge and decides.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all seven. None is anonymous.**

| Endpoints | Declaration |
|-----------|-------------|
| `POST /cases/{id}/promises`, `POST /promises/{id}/confirm`, `…/reject`, `…/cancel` | `RequiresPermission(ptp.write)` |
| `GET /promises`, `GET /promises/{id}`, `GET /customers/{id}/promise-history` | `RequiresPermission(cases.read)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at **83**.
The 404 sweep covers every `{id}` route with a real promise id of tenant B (`ForeignIds.PromiseId`).
Existence is checked before the body on every write. **There is no route to `Kept`, `PartiallyKept`
or `Broken`** — `NoManualKeptOrBroken` posts to `/kept`, `/broken`, `/transitions`, `/evaluate` and
gets 404 for each; `PtpMachine.IsUserEvent` names confirm / reject / cancel and nothing else.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, all three.**

| Table | `tenant_id NOT NULL` | RLS forced | Policy (no platform clause) | `(tenant_id, id)` key | Composite FKs |
|-------|:--:|:--:|:--:|:--:|---|
| `promises_to_pay` | ✅ | ✅ | ✅ | ✅ | `→ collection_cases`, `→ customers`, `→ cheques`, `→ promises_to_pay` (superseded_by) |
| `ptp_invoices` | ✅ | ✅ | ✅ | ✅ | `→ promises_to_pay`, `→ invoices` |
| `tenant_holidays` | ✅ | ✅ | ✅ | ✅ | — |

The enumeration test now walks **25** tables. No DELETE on promises or covers; `tenant_holidays`
allows DELETE (a holiday can be removed; it is configuration, not a record).

**Targeted tests for the non-standard patterns:**

- **Layer 3 alone** — `CrossTenantPromise_IsRejectedByCompositeForeignKey_AndEvaluationStaysHome`:
  as superuser, a cover row in A for B's invoice (`fk_pi_invoice`) and a promise in B on A's case
  (`fk_ptp_case`) are refused.
- **The INV-07 trigger (DM-24)** spans two tables and runs as the caller under RLS. `ptp_active_clash`
  carries an explicit `tenant_id` predicate on every table it touches; `Supersede_…_AndTheTriggerHolds`
  proves a direct second `Active` cover is refused with `one_active_ptp_per_invoice`.
- **The evaluation job** is the second thing (after the case sweep) that writes for many customers
  at once; it runs inside `POST /cases/sweep` under the same tenant advisory lock. B's sweep run
  past A's deadline leaves A's promise `Active` (same test).
- **`INV-13`** — `active_requires_human`: `ProposedPromise_NeedsAHuman` updates a `Proposed` row to
  `Active` by hand and is refused by the CHECK.

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`. `CaseService` reaches `PromiseService` through
`IPromiseHooks` resolved from the scoped provider at call time (D-6) — the same request, the same
bound tenant, the same transaction.

## 4. Any new money-related field or calculation?

**Three `numeric(19,3)` columns and no arithmetic on money beyond exact sums and one exact comparison.**

| Item | As built |
|------|----------|
| `promised_amount`, `received_in_window` | `numeric(19,3)` / `decimal`; `EveryMoneyColumn_IsNumeric19_3` covers them. A promise is never subtracted from anything (SM §3 opening line): the ledger has no knowledge of promises. |
| Over-promise (SM-32) | `amount ≤ Σ balance_cache` of the covered invoices, exactly, tolerance zero; the covered figure travels in `errors[].meta.coveredBalance`. `Record_GuardsCoverageAndAmount`. |
| Received in window (SM-34) | An exact SQL `SUM` of active allocations from `Confirmed` payments with `received_date` in `[capture date, deadline]`; cheques count when their allocation exists, i.e. when `Cleared`. `PostDatedCheque_…` |
| Verdict (SM-35) | `received ≥ promised` → Kept; at the deadline `received ≥ promised × pct / 100` → PartiallyKept else Broken. The product is exact in `decimal`; no rounding. `Verdict_AtThresholdBoundaries` includes `333.335 × 50 % = 166.6675` decided both sides. |
| Reliability (SM-37) | `kept / denominator` rounded to three decimals — a ratio, not money — and **null below three**. `Reliability_ShowsTheDenominator` (unit and API). |
| `ptp_partial_threshold_pct`, `ptp_grace_business_days` | Read from `tenant_settings` on every evaluation; never hardcoded. The 60 % case in the unit test and the `Deadline_SkipsWeekendAndHolidays` cases show both moving. |
| Cheque promise amount | `min(cheque amount, Σ covered balances)` so SM-32 holds for cheques too. |
| Frontend | `Promises.tsx` computes nothing; `promises.test.tsx` greps for arithmetic and for any kept/broken control. |

**Nothing rounds** except the reliability ratio, which is not money.

## 5. Any AI-touching code?

**None runs.** The `Proposed` state, `ai_suggestion_id`, `POST /confirm` (with `human_correction`
recorded) and `POST /reject` exist so slice 9 has a door that already refuses to be anything but
human-gated: `active_requires_human` is a database CHECK, and `PtpRules.Verdict` reads only stored
allocations (SM-31, INV-13, FIN-62). `ProposedPromise_NeedsAHuman` seeds a row the way slice 9 will
and proves it has no effect on the queue until a human confirms.

## 6. Any new dependency?

**None.** `THIRD-PARTY-NOTICES.md` is unchanged apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Seven things deserve the reviewer's attention:

1. **F-1 — a new case event, `ptp_kept`** (`PromiseActive → InProgress`). Doc 02 has no edge for a
   kept *partial* promise that leaves a balance; without it the case stays suppressed forever. The
   case matrix test (now 9 × 18) carries the row; delete it on both sides if you disagree.
2. **Post-dated cheque coverage (D-2).** A cheque names no invoice; its promise covers the case's
   in-scope invoices oldest-due first up to the cheque amount, and only when a case exists. A PDC
   received before the customer passes grace creates no promise — nothing is being chased yet.
3. **SM-34's window is date-based**: a payment received *earlier the same day* on a covered invoice
   counts toward a promise recorded that afternoon. This follows the spec's `[created_at,
   deadline]` on `received_date`; noted because `Reliability_ShowsTheDenominator` had to use one
   invoice per promise to avoid it.
4. **The bounce path breaks the promise immediately** (not at the deadline), and the case's score
   rises through both counters (bounced + broken). E2 says "PTP Broken, case reopens at raised
   priority"; the immediacy is a reading.
5. **SM-52 (dispute wins over promise) and the dispute half of SM-54** wait for slice 7; the
   PTP half of SM-54 (`ptp_active` blocks write-off approval) is built and tested.
6. **`tenant_holidays` is empty** until a settings screen exists (D-4). Deadlines skip Fridays and
   Saturdays today; announced holidays must be seeded by SQL.
7. **Manager notification on a broken promise is not built** — there is nothing to send it with
   until slice 8. The badge, the queue position and the timeline are the notification today.
