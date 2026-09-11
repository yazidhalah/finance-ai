# Slice 3b — Payments, Cheques, Allocation, Credit Notes, Withholding, Write-off: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 03 §2–§4, §7, §9 (the financial rules and the six worked examples) · doc 02 §1
(I4–I8, SM-12, SM-13), §5 (SM-50, SM-51, SM-54, SM-55) · doc 04 §5.4 · doc 05 slice 3 (payments
block) · doc 06 §6.5 · doc 09 T-20, T-21, T-29, T-42, T-43, T-45 · doc 10 slice 3 acceptance.

This completes doc 10's slice 3. With it, "3a alone is not shippable" no longer applies.

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `payments`, `cheques`, `payment_allocations`, `withholding_deductions`, `credit_notes`, `credit_note_applications`, `write_offs` — all on the isolation pattern, composite FKs in every direction |
| S2 | **One balance function.** `LedgerService.RecomputeAsync(invoice)` is the only writer of `balance_cache`; it derives from allocations, credit applications, write-offs and withholding, then runs I4/I7 (SM-12). Every mutation below ends by calling it. |
| S3 | Record a payment (with `Idempotency-Key`, API-08); FIFO allocation proposal (FIN-25, proposal only); explicit allocation under `SELECT … FOR UPDATE` (FIN-22); allocation and payment reversal by compensating rows (FIN-23) |
| S4 | Cheques: `Received → Deposited → Cleared \| Bounced`, `Cancelled`; only `Cleared` creates money (a payment); `Bounced` after clearing reverses it (I7) and increments the customer's bounced count (SM-51) |
| S5 | Withholding deductions (FIN-29): rate and amount entered, never computed; settles the invoice; certificate chase flag |
| S6 | Credit notes (FIN-40…43): positive amount, closed reason set, apply across invoices, void reverses applications |
| S7 | Write-off (FIN-31…34): propose → approve by a **different** user, or explicit self-approval recorded; amount is the system-computed open balance; rejected/reversed states |
| S8 | Short-payment support (FIN-28): the allocation result names the residual per invoice and, for residuals below `auto_clear_residual_below`, **proposes** a `rounding_adjustment` credit note — a human creates it |
| S9 | Void (I6/SM-55): only with zero financial history |
| S10 | Invoice detail gains the settlement facet (FIN-12) and the **money history** (doc 06 §6.5); customer detail gains unapplied cash and unapplied credit per currency (FIN-15, FIN-42) |
| S11 | UI: record payment + allocation screen with proposal and residual choices, cheque register, credit notes, write-off propose/approve, invoice money history |

**Deferred, and to which slice:** PTP auto-created for a post-dated cheque (E2's promise half → slice 6) ·
FIFO skipping disputed/escalated invoices (no disputes or cases exist → slices 5, 7) · the
"dispute" option of the short-payment resolver (slice 7) · case C10 / message cancellation after
settlement (slices 5, 8) · re-authentication on write-off approval (SEC-09 → slice 1b) · SM-54's
"no open dispute / active PTP" guard on approval (slices 6, 7) · invoice lines · remittance parsing
(FIN-27, slice 9) · nightly cache reconciliation job (T-45's scheduled half; the check itself is a
test here) · document-chase task for withholding certificates (FIN-30, slice 5).

---

## 2. Money rules, stated explicitly

| Rule | As built |
|------|----------|
| Balance formula | `open = total − Σ active allocations − Σ active credit applications − Σ approved write-offs − Σ withholding`. **Withholding is the fourth instrument** (doc 03 §2.1 and FIN-20 omit it; E1 and A-04 require it). Flagged as F-1 in the review. |
| Rounding (FIN-05) | **Still none.** Every input is entered at scale ≤ 3 and refused otherwise; every derived figure is a sum or difference of scale-3 decimals. The FIFO proposal allocates exact remainders. Withholding `withheld_amount` is entered, not computed from the rate. The first genuine rounding in the product will be base-currency conversion in slice 4 and must say so. |
| Exact zero (FIN-13) | Settlement is `open == 0m`. No tolerance anywhere. |
| Residuals (FIN-14) | A residual below `auto_clear_residual_below` is *proposed* as a `rounding_adjustment` credit note in the allocation response; nothing is cleared automatically. |
| Currency (FIN-24, INV-02) | Payment, allocation and invoice currency must match, checked in code and by a database trigger. Credit notes likewise. Cross-currency is `422 currency_mismatch`. |
| Caps (FIN-21, FIN-22, FIN-41) | `Σ allocations ≤ payment.amount` and `Σ applications ≤ note.amount` — code **and** database constraint triggers. Each line `≥ 0.001` and `≤ invoice open balance`, evaluated under a row lock. |
| Reversal (FIN-23) | Never a delete: a compensating row with `reversal_of_id`; both rows remain, both `is_active = false`. |
| Write-off amount (FIN-32) | The endpoint takes no amount. It writes the current open balance. |
| Four eyes (FIN-31, PRD-11) | `approved_by ≠ proposed_by`, or `self_approved = true` sent explicitly by the same user and recorded; a database CHECK repeats it. |

---

## 3. Acceptance criteria

Doc 09 T-20: **the six worked examples are implemented verbatim as fixture tests and gate this slice.**

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-E1 | Invoice 10,000.000 JOD; payment 9,500.000 allocated; withholding 500.000 at 5% → open 0, `Settled`, certificate pending | E1, FIN-29 | `E1_WithholdingTax` |
| AC-E2 | Invoice 4,000.000; post-dated cheque `Received` → balance unchanged; `Deposited`; `Cleared` with allocation → `Settled`. Alternative branch: `Bounced` → no allocation, customer bounced count 1. Cleared-then-bounced → allocation reversed, invoice reopens (I7) | E2, SM-51 (money half) | `E2_PostDatedCheque_Clears`, `E2_PostDatedCheque_Bounces`, `E2_ClearedCheque_ThenBounces_Reopens` |
| AC-E3 | Invoice 6,000.000; payment 4,000.000 → `PartiallyPaid`, open 2,000.000; credit note 2,000.000 (`dispute_resolution`) applied → `Settled` | E3 (money half) | `E3_PartialPaymentThenCreditNote` |
| AC-E4 | Invoice 1,000.000; payment 1,500.000; allocate 1,000.000 → `Settled`; 500.000 unapplied cash on the customer; allocating 1,500.000 is refused `exceeds_open_balance` | E4, FIN-11, FIN-16 | `E4_Overpayment_LeavesUnappliedCash` |
| AC-E5 | Invoice 333.335; payment 333.330 → open 0.005; the allocation response **proposes** a `rounding_adjustment` of 0.005; nothing settles until a human creates it; then `Settled` | E5, FIN-13, FIN-14 | `E5_RoundingResidual_IsProposedNotAssumed` |
| AC-E6 | Customer with 5,000.000 JOD and 2,000.00 USD open shows two balance blocks; no endpoint returns a sum across them; a USD payment cannot be allocated to a JOD invoice | E6, FIN-04, FIN-24 | `E6_MultiCurrency_NeverSummed` |
| AC-01 | **Property test**: 2,000 random steps (allocate, reverse, apply credit, void credit, write off, withhold, cheque events) over several invoices; after every step INV-01, INV-02, INV-03, INV-04, INV-09, INV-10 hold | T-21 | `RandomizedLedger_HoldsAllInvariants` (unit, in-memory ledger model) + `RandomizedLedger_AgainstTheDatabase` (integration, 300 steps) |
| AC-02 | Two concurrent allocations of the full balance to one invoice: exactly one succeeds, the other is `422`, invariants hold | T-42, FIN-22 | `ConcurrentAllocations_ExactlyOneWins` |
| AC-03 | Replaying `POST /payments` with the same `Idempotency-Key` creates one payment and returns the original response; a different body under the same key is `409` | T-43, API-08 | `Payment_IsIdempotent` |
| AC-04 | Reversal writes a compensating row; both rows remain; the balance and settlement recompute; `Settled → Open` is audited as I7 | FIN-23, I7 | `Reversal_IsACompensatingRow_AndReopens` |
| AC-05 | Over-allocation, negative, zero and cross-currency lines are rejected with the documented codes and the live `openBalance` in `meta` | FIN-22, FIN-24, T-28 | `Allocation_RejectsBadLines` |
| AC-06 | The FIFO proposal is oldest-due-first, same customer, same currency, exact remainders, never exceeds any balance, and is **not applied** | FIN-25, FIN-26 | `Proposal_IsFifoAndNotApplied` |
| AC-07 | Write-off: proposer cannot approve; a second user can; a single user can only with `selfApproved: true`, recorded; the amount is the open balance; approval → `WrittenOff`; reversal → `Open` audited high-severity | FIN-31…34, T-29, I5, I8 | `WriteOff_FourEyes`, `WriteOff_UsesComputedBalance`, `WriteOff_Reverse` |
| AC-08 | Void: refused once any allocation, application, withholding or write-off ever touched the invoice, even if reversed | I6, SM-55 | `Void_RequiresZeroFinancialHistory` |
| AC-09 | Database triggers alone refuse: allocations summing beyond the payment, applications beyond the note, a currency mismatch — with the application filter bypassed | FIN-21, INV-02, INV-03, T-75 | `DatabaseTriggers_HoldWithoutTheApplication` |
| AC-10 | `balance_cache == derived` for every invoice after every scenario; a cache corrupted by hand is detected by the reconciliation check | INV-09, T-45 | `BalanceCache_MatchesDerived_AndCorruptionIsDetected` |
| AC-11 | Every transition (I4, I5, I6, I7, I8, cheque events, write-off states) writes exactly one audit row; `count(transitions) == count(audit rows)` for a scripted scenario | INV-12 | `Transitions_AreAudited_OneToOne` |
| AC-12 | Cross-tenant: every new `{id}` route → 404 with real ids of another tenant; an allocation in A cannot reference a payment or invoice of B (layer 3 alone) | T-71, T-75, INV-05 | sweep + `CrossTenantAllocation_IsRejectedByCompositeForeignKey` |
| AC-13 | All seven tables pass the schema assertions automatically; every money column is `numeric(19,3)` | DM-01/02/10, INV-11 | existing enumeration (19 tables) |
| AC-14 | Customer detail: open balance, unapplied cash, unapplied credit — per currency, never netted | FIN-15, FIN-42 | `CustomerDetail_ShowsThreeUnnettedFigures` |
| AC-15 | Invoice detail: settlement facet from the pure function; money history lists every instrument with dates and actors and ends in the current balance | FIN-12, doc 06 §6.5 | `InvoiceDetail_ShowsSettlementAndHistory` |
| AC-16 | A Viewer cannot record money; a Collector cannot allocate; an Accountant cannot approve a write-off | doc 01 §5.1 | sweep |
| AC-17 | The allocation UI never computes a total or remainder itself; the remainder shown is the server's | UI-30 | web test |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| POST | `/payments` | `payments.write` |
| GET | `/payments`, `/payments/{id}` | `payments.read` |
| GET | `/payments/{id}/allocation-proposal` | `payments.allocate` |
| POST | `/payments/{id}/allocations` | `payments.allocate` |
| POST | `/payments/{id}/reverse` | `payments.write` |
| POST | `/allocations/{id}/reverse` | `payments.allocate` |
| POST | `/cheques`, GET `/cheques`, `/cheques/{id}` | `payments.write` / `payments.read` |
| POST | `/cheques/{id}/transitions` | `payments.write` |
| POST | `/invoices/{id}/withholding` | `payments.write` |
| POST | `/credit-notes`, GET `/credit-notes`, `/credit-notes/{id}` | `credit_notes.write` / `payments.read` |
| POST | `/credit-notes/{id}/applications`, `/credit-notes/{id}/void` | `credit_notes.write` |
| POST | `/invoices/{id}/write-off` | `writeoff.propose` |
| GET | `/write-offs`, `/write-offs/{id}` | `payments.read` |
| POST | `/write-offs/{id}/approve`, `/write-offs/{id}/reject`, `/write-offs/{id}/reverse` | `writeoff.approve` |
| POST | `/invoices/{id}/void` | `invoices.void` |

All through `TenantScopeMiddleware`; none anonymous.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Withholding reduces the open balance | E1 and A-04. Without it the product chases phantom balances. Doc 03 §2.1 amended. |
| D-2 | Idempotency is a `payments.idempotency_key` column, unique per tenant, plus a stored hash of the request | The minimal honest implementation of API-08 for the one endpoint that needs it now. A general idempotency table can replace it when messages (slice 8) need one. |
| D-3 | A cleared cheque becomes a `Payment` with `method = Cheque`; allocation is a separate, confirmed step unless lines are supplied with the `clear` event | FIN-26: allocation is always confirmed by a human. |
| D-4 | The randomized invariant test is hand-rolled with a seeded `Random`, not FsCheck | No new dependency; the shrinking FsCheck offers is not worth a licence entry for one test. The seed is printed on failure. |
| D-5 | `SELECT … FOR UPDATE` is an explicit statement before the EF load, not a `FromSql` composition | Reads as what it is; RLS applies to it the same way. |
| D-6 | Re-authentication on write-off approval (SEC-09) is deferred with MFA (slice 1b) | The re-auth mechanism does not exist yet; an Owner's fresh session is required instead (token < 15 min old is all we can assert today). Flagged. |
