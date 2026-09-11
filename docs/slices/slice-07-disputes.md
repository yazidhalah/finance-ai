# Slice 7 — Disputes: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 02 §4 (SM-40…48), §2.3 C6/C7, SM-25, §5 (SM-52, SM-54) · doc 03 FIN-20, FIN-56,
E3 · doc 04 §5.5 (`disputes`) · doc 05 slice 7 · doc 06 §6.9 · doc 08 SEC-40, SEC-44 · doc 09
T-10, T-47, T-60, T-127 · doc 10 slice 7 · doc 01 §5.1 (`disputes.write`, `disputes.resolve`).

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `disputes`, `dispute_evidence`, `payment_verification_tasks` on the isolation pattern; closed reason set as a CHECK (SM-43) |
| S2 | **The dispute machine** as one exhaustive table (`DisputeMachine`); every transition one method, `FOR UPDATE`, guard, write, audit row, case activity (SM-02/03); `resolve` requires `disputes.resolve` and a named user (SM-47) |
| S3 | Raise on one invoice (SM-40): `disputed_amount ≤ open balance`, `case_id` = the customer's active case when one exists; C6 on the case (SM-52: from `PromiseActive` too, the promise stays `Active`) |
| S4 | **Resolution atomic with the credit note** (SM-45): accept / partially accept creates a `dispute_resolution` credit note for the resolution amount and applies it to the invoice in the same transaction; SM-46 refuses more than the open balance; reject needs a reason |
| S5 | **SLA clocks** (SM-48): `first_response_due_at` = raised + 2 business days, `resolution_due_at` = raised + 10 business days; `PendingCustomer` pauses the resolution clock; breaches surface on the dispute, the queue row and the summary; the sweep times a stale `PendingCustomer` back to `UnderReview` |
| S6 | **`already_paid` opens a payment-verification task** (SM-44) with its own queue; resolving a task never marks anything paid — `payment_found` points at a payment recorded through the ordinary path |
| S7 | **Dunning guard** (SM-25 / `allow_split_dunning_during_dispute`): `GET /cases/{id}/dunning-eligibility` answers per invoice with `dispute_blocks_send`; slice 8's send path must call it |
| S8 | Aging: `disputedAmount` per bucket and section is real (FIN-56, `disputedAvailable: true`); queue and case rows carry the open-dispute count and the SLA state |
| S9 | Evidence: a file on a dispute (SEC-40/44 — allowlisted types, sniffed, capped, stored inside the RLS boundary, served as an attachment, never rendered) |
| S10 | UI: raise dispute (closed reasons with plain-language descriptions, amount ≤ open balance, claim text quoted as data), dispute list with SLA indicators (text, never colour alone), dispute detail with clocks and evidence, resolution panel that shows the credit note **before** confirming, the payment-verification queue with no "mark as paid" anywhere; dispute flags on the case, queue and aging |

**Deferred, and to which slice:** `source = customer_email` / `ai_suggested` disputes (slice 9; the
columns and the human-only resolution are built) · the send-time enforcement of the guard (slice 8;
the guard and its code exist now) · SLA breaches in the daily briefing (slice 10) · lines on a dispute
(SM-40's "optionally specific lines" — no invoice lines exist) · tenant-configurable SLA days
(constants, flagged) · virus scanning of evidence (SEC-44's scanner is infrastructure not present).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **Balance** | Raising, reviewing, rejecting or withdrawing a dispute never changes a balance. Accepting does — through a credit note, the ordinary instrument, applied in the same transaction. | SM-41, FIN-20, SM-45. |
| **Amounts** | `disputed_amount ≤ invoice open balance` at raise. Resolution: `accept` → `resolution_amount = disputed_amount`, must be `≤ open balance` **now** (SM-46) — a payment since raising can make an accept impossible at the old figure, so the API returns `exceeds_open_balance` with the live figure; `partially_accept` → `0 < resolution_amount < disputed_amount` and `≤ open balance`; `reject` → no amount, reason required. | SM-45, SM-46. |
| **Who resolves** | Only `disputes.resolve` (Owner, Admin, Accountant). A Collector raises and updates. `resolved_by` is always a user id — there is no system or AI path to a terminal state except `timeout`, which only returns a pending dispute to review. | SM-47, the slice prompt. |
| **Case coupling** | Raise: if the customer has a non-terminal case, the dispute joins it and C6 fires when legal (`Open`, `InProgress`, `AwaitingCustomer`, `PromiseActive`). A case on hold or escalated keeps its state; the dispute still counts. Resolve/withdraw/cancel: when no open dispute remains in the case, C7 (`Disputed → InProgress`); the priority is recomputed either way (the dispute dampener). | C6, C7, SM-52, FIN-80. |
| **Dunning guard** | For an invoice with an open dispute: `dispute_blocks_send`. For another invoice of a case whose customer has any open dispute: blocked unless `allow_split_dunning_during_dispute`. No dispute: allowed. There are no messages yet; the guard is the contract slice 8 must call before sending. | SM-25. |
| **SLA** | Business days (Sun–Thu, tenant holidays). `first_response_due_at` = raised + 2; satisfied by `assign`. `resolution_due_at` = raised + 10; paused while `PendingCustomer` — on leaving that state the due moment moves forward by the time spent pending. `PendingCustomer` older than 5 business days without `info_received` is timed out to `UnderReview` by the sweep (constant, flagged). | SM-48. |
| **`already_paid`** | The dispute is raised as usual **and** a payment-verification task is opened on the invoice. Resolving the task with `payment_found` requires the id of a payment already recorded through `/payments`; the dispute is then resolved by a human like any other (typically `accept` for the amount now covered, or `reject`). The task screen has no "mark as paid". | SM-44, SM-10. |
| **Evidence** | `pdf`, `png`, `jpg`, `jpeg` only, sniffed by magic bytes, ≤ 10 MB, filename sanitised, stored as `bytea` inside the RLS boundary, served with `Content-Disposition: attachment` and `nosniff`. Never parsed, never shown to an AI. | SEC-40, SEC-44. |
| **Visible everywhere** | Aging: `disputedAmount` = Σ open `disputed_amount` per invoice capped at its open balance (FIN-56: still in the bucket, shown as "of which disputed"). Queue/case: `openDisputes`, `disputeSlaBreached`, status `Disputed`. Invoice detail: the dispute list. | The slice prompt; FIN-56. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | Exhaustive matrix: 8 states × 9 events, legal and illegal | T-10 | `DisputeMachine_Matrix_IsExhaustive` (unit) |
| AC-02 | Raise: reason outside the closed set → 400; amount > open balance → 422; a second open dispute on the same invoice → 409; a settled invoice → 422; the response carries both SLA moments as business days from now | SM-40, SM-43, SM-48 | `Raise_Guards_AndSlaClocks` |
| AC-03 | Raising joins the customer's active case and moves it to `Disputed` (also from `PromiseActive`, promise stays `Active`); the queue row shows `openDisputes: 1`; the dampener lowers the score | C6, SM-52, FIN-80 | `Raise_MovesTheCaseToDisputed` |
| AC-04 | Accept: a `dispute_resolution` credit note for the amount is created and applied in the same request; the invoice balance falls; `creditNoteId` is on the dispute; E3 end to end (6,000 invoice, 4,000 paid, 2,000 disputed → accepted → `Settled`) | SM-45, E3 | `Accept_CreatesAndAppliesTheCreditNote_E3` |
| AC-05 | Forced failure: accept after a payment leaves less open than the disputed amount → 422 `exceeds_open_balance`, the dispute is still `UnderReview`, no credit note exists — one transaction rolled back both | SM-45, SM-46 | `Accept_BeyondOpenBalance_RollsBackEverything` |
| AC-06 | Partially accept: `0 < amount < disputed` → credit note for that amount, dispute `PartiallyAccepted`; amount ≥ disputed → 422; reject without reason → 400; reject → no credit note | SM-45 | `PartialAndReject` |
| AC-07 | Collector: raise 201, assign 200, resolve 403; Accountant: resolve 200 | SM-47, T-60 | `Collector_CannotResolve` + sweep |
| AC-08 | Dunning guard: invoice with an open dispute → `dispute_blocks_send`; a sibling invoice → blocked with `allow_split_dunning_during_dispute` off, allowed with it on; after resolution → allowed | SM-25, T-47 | `DunningGuard_FollowsTheSetting` |
| AC-09 | Resolving the last open dispute returns the case `Disputed → InProgress`; a second open dispute keeps it `Disputed` | C7 | `Resolution_ReturnsTheCase` |
| AC-10 | SLA: `request_info` pauses; with the clock pinned 3 business days later, `info_received` moves `resolutionDueAt` forward by the pause; `slaBreached` is true past the due moment; a pending dispute is timed out by the sweep after 5 business days; breach shows on the queue row and summary | SM-48 | `Sla_PausesAndBreaches` |
| AC-11 | `already_paid` raises a dispute and a verification task; the task queue lists it; `payment_found` needs an existing payment id and never changes the invoice; `no_payment_found` closes it; the API has no route that marks an invoice paid | SM-44, SM-10 | `AlreadyPaid_OpensAVerificationTask` |
| AC-12 | Aging shows `disputedAmount` in the bucket and section (capped at the open balance) with `disputedAvailable: true`; the customer drill-through and the export carry it | FIN-56 | `Aging_ShowsDisputedAmounts` |
| AC-13 | Evidence: a PDF uploads and downloads as an attachment with `nosniff`; an `.exe`, a renamed HTML file and an oversize file are refused; the file is invisible to another tenant | SEC-40, SEC-44 | `Evidence_IsAllowlistedAndIsolated` |
| AC-14 | Every transition writes one audit row with a user actor for every terminal state (timeout is `system` and non-terminal) and one `dispute` case activity | SM-03, SM-47 | `DisputeTransitions_AreAudited_OneToOne` |
| AC-15 | Cross-tenant: every `{id}` route → 404 with B's ids; a dispute in A on B's invoice is refused by the composite key; B's queue and aging know nothing of A's dispute | INV-05 | sweep + `CrossTenantDispute_IsRejected` |
| AC-16 | UI: the resolution panel shows the credit note amount before confirming; the send-message control is disabled with the dispute reason; the verification queue has no "mark as paid"; SLA state is text; the browser computes no amount | UI-30, doc 06 §6.9 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| POST | `/invoices/{id}/disputes` | `disputes.write` |
| GET | `/disputes`, `/disputes/{id}`, `/disputes/{id}/evidence/{evidenceId}`, `/cases/{id}/dunning-eligibility` | `cases.read` |
| POST | `/disputes/{id}/transitions` (`assign`, `request_info`, `info_received`, `withdraw`, `cancel`), `/disputes/{id}/evidence` | `disputes.write` |
| POST | `/disputes/{id}/resolve` (`accepted`, `partially_accepted`, `rejected`) | `disputes.resolve` |
| GET | `/tasks/payment-verification` | `payments.read` |
| POST | `/tasks/payment-verification/{id}/resolve` | `payments.write` |

All through `TenantScopeMiddleware`; none anonymous. The route count pin moves from 83 to 93.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | SLA days (2 / 10) and the pending timeout (5) are constants in `DisputeSla` | SM-48 says tenant-configurable; `tenant_settings` has no columns for them and no persona has asked for an editor. Flagged. |
| D-2 | `payment_verification_tasks` is its own table | Doc 04 sketches no table for SM-44; the task needs a status, an outcome and a payment link of its own, and slice 9 will open the same tasks from AI classifications. |
| D-3 | The resolution writes the dispute first, then the credit note, then the application | So SM-46's refusal inside the ledger unwinds the dispute write too — the middleware's single transaction is the atomicity, and AC-05 proves it rather than trusting it. |
| D-4 | The dunning guard is an endpoint and a service method now, enforced by slice 8's send path | Acceptance 4 wants the API code `dispute_blocks_send` and a disabled control; both exist before any message can be sent. |
| D-5 | Evidence lives in `bytea` inside RLS, like import files | No storage path, no cleanup job, no cross-tenant path traversal to get wrong. |
| D-6 | `resolution_amount` for `accept` is the disputed amount, not a request field | "Accept" means the claim as made; a different figure is `partially_accept`. |
