# 03 — Financial Rules and Invariants

Status: DRAFT. **Normative and testable.** Every rule here is a unit test in
`tests/unit` (doc 09 §2.2). This document is the authority the AI service is never
allowed to override.

> `CLAUDE.md`: *LLMs never calculate authoritative monetary totals. All monetary math
> happens in C# using `decimal`, never float/double.* Everything below is C# and SQL.

---

## 1. Money representation

| ID | Rule |
|----|------|
| FIN-01 | Money is `decimal` in C#, `numeric(19,3)` in PostgreSQL. `float`/`double`/`real`/`double precision` MUST NOT appear in any type that touches money. A build-time analyzer/test enforces this (doc 09 §2.2). |
| FIN-02 | **Scale is 3** — the Jordanian Dinar's minor unit is the fils (1 JOD = 1000 fils), A-01. A 2-decimal assumption anywhere is a bug. Non-JOD currencies with 2 decimals are stored at scale 3 with a trailing zero, and their *rounding* uses their own scale (FIN-05). |
| FIN-03 | Every money column is accompanied by a `currency_code` (ISO 4217, `char(3)`). There is no bare amount anywhere in the schema, the API, or the UI. |
| FIN-04 | **Amounts in different currencies are never summed.** Any aggregate is either per-currency, or converted to the tenant base currency using a stored rate on the source document (FIN-06). Attempting to sum mixed currencies MUST throw, not coerce. |
| FIN-05 | Rounding is **MidpointRounding.AwayFromZero** at the currency's own scale (`decimal.Round(value, scale, MidpointRounding.AwayFromZero)`). Banker's rounding is not what a Jordanian SME's accountant expects to see. Rounding happens **once, at the last step** of a computation, never intermediate. |
| FIN-06 | Each invoice stores `fx_rate_to_base` and `base_currency_code`, captured at import from the tenant's rate table for `issue_date`. The rate is **frozen on the document**. We do not revalue; there is no FX gain/loss in v1 (A-02). Base-currency figures are labelled "indicative" in the UI. |
| FIN-07 | Negative amounts are only legal where explicitly stated (credit notes are stored positive and applied negatively; adjustments carry a sign). `invoices.total_amount` MUST be ≥ 0; a "negative invoice" is a credit note. |

### 1.1 Display

**FIN-08** Formatting is a presentation concern and MUST NOT round the stored value.
Display uses the user's locale: `ar-JO` (with a setting for Arabic-Indic vs Western
digits, default **Western**, doc 06 §3) or `en-JO`. Currency shows as a 3-decimal
amount plus code (`1,250.500 JOD` / `١٬٢٥٠٫٥٠٠ د.أ`). Amounts always render LTR-isolated
inside RTL text (doc 06 §3.4).

---

## 2. The balance model

### 2.1 Definitions (single source of truth)

For an invoice `I`:

```
gross_amount          = sum of line net + tax_amount               (imported, not computed by us)
total_amount          = gross_amount                                 (what the customer owes at issue)
allocated_payments(I) = Σ allocations.amount where target = I and allocation is active
                        and the source payment/cheque is in a settled state
allocated_credits(I)  = Σ credit_note_applications.amount where target = I and active
written_off(I)        = Σ write_offs.amount where target = I and status = Approved
open_balance(I)       = total_amount - allocated_payments(I) - allocated_credits(I) - written_off(I)
```

| ID | Rule |
|----|------|
| FIN-10 | `open_balance` is **always derived**, never stored as the authority. It MAY be maintained in a `balance_cache` column for query performance, but (a) the cache is written only by the single recompute function, (b) a nightly job asserts cache == derived for every invoice, and (c) any mismatch is a P1 alert (doc 09 §3.5). |
| FIN-11 | `0 ≤ open_balance(I) ≤ total_amount(I)` at all times. Over-allocation MUST be rejected at write time, not corrected later. |
| FIN-12 | Settlement status is a pure function: `open_balance == 0 → Paid`; `0 < open_balance < total → PartiallyPaid`; `open_balance == total → Unpaid`. Nothing else may set it (SM-10). |
| FIN-13 | **Exact zero, not epsilon.** With `decimal` there is no float dust. `open_balance == 0m` is the settlement test. If a comparison needs a tolerance, the arithmetic upstream is wrong. |
| FIN-14 | Tiny residual balances (e.g. 0.005 JOD from a customer's rounding) are cleared by an explicit **`RoundingAdjustment`** record (a typed credit note reason), not by a tolerance in the comparison. Tenant setting `auto_clear_residual_below` (default `0.100` JOD) may automate *proposing* it; a human approves. |

### 2.2 Customer and tenant balances

```
customer_open_balance(C, currency) = Σ open_balance(I) for I in invoices(C)
                                     where status = 'Open' and currency matches
unapplied_cash(C, currency)        = Σ (payment.amount - Σ its allocations) for active payments
customer_net_position(C, cur)      = customer_open_balance - unapplied_cash - unapplied_credit_notes
```

**FIN-15** The UI shows **open balance** and **unapplied cash** as two separate
numbers, never netted silently. "You owe 5,000 and we're holding 1,200 unapplied" is
a different conversation from "you owe 3,800", and the second one loses arguments.

**FIN-16** `unapplied_cash ≥ 0` always. A payment may not be allocated beyond its own
amount.

---

## 3. Allocation

Allocation assigns money (a payment, a cheque that cleared, or a credit note) to
specific invoices.

| ID | Rule |
|----|------|
| FIN-20 | **Only three instruments change an invoice balance:** an allocation of a payment, an application of a credit note, and an approved write-off. Nothing else. Not a dispute, not a PTP, not an AI output, not a status change. |
| FIN-21 | `Σ allocations(P).amount ≤ P.amount` for every payment, enforced by a transactional check plus a database constraint trigger. |
| FIN-22 | Every allocation line MUST be ≥ 0.001 and ≤ the target invoice's `open_balance` at the time of the write, evaluated under `SELECT … FOR UPDATE` on the invoice row. |
| FIN-23 | Allocations are **reversible, never deleted**. Reversal writes a compensating row (`reversal_of_id`) and both rows remain. The audit trail must show that money was moved and then moved back. |
| FIN-24 | Currency of payment and target invoice MUST match. Cross-currency settlement is out of scope (A-02) and MUST be rejected with `currency_mismatch`. |

### 3.1 Default allocation strategy

**FIN-25** When the user does not specify, the proposed allocation is
**oldest-due-date-first (FIFO by `due_date`, then by `invoice_number`)**, restricted
to `Open` invoices of the same customer and currency, **skipping**:

- invoices with an open dispute,
- invoices in `Escalated` cases (a human decides where that money lands).

**FIN-26** The proposal is *always shown for confirmation*. Auto-allocation without a
human look is only permitted when the payment references exactly one invoice number
that matches unambiguously and the amount equals that invoice's open balance
(`exact_match_auto_allocation`, tenant setting, default **on**).

**FIN-27** A remittance advice or bank narrative referencing invoice numbers MAY be
parsed to propose targeted allocation. If AI does this parsing, the extraction is a
*suggestion* with confidence; the amounts are re-validated in C# against stored
invoice balances before any proposal is shown (doc 07 §6).

### 3.2 Short payments — the important case

A customer pays **less** than the invoice. There are five distinct causes, and
collapsing them is the single biggest source of wrong chasing (A-04):

| Cause | Correct handling | Result on balance |
|-------|------------------|-------------------|
| **Withholding tax deducted** | Record a `WithholdingTax` deduction with rate/amount and a certificate reference | Invoice is fully settled: `payment + withholding = total`. Withholding is tracked as a receivable **from the tax authority**, out of scope for AR chasing |
| **Agreed discount / settlement discount** | Credit note, reason `agreed_discount`, approved by `credit_notes.write` | Balance reduced legitimately |
| **Bank charges deducted** | Credit note, reason `bank_charges`, or expensed per tenant policy | Balance reduced |
| **Dispute over part of the invoice** | Open a dispute for the shortfall | Balance unchanged; chasing suppressed for the disputed part |
| **Partial payment (customer is short of cash)** | Normal partial allocation | Balance reduced by the paid amount; remainder still chased |

| ID | Rule |
|----|------|
| FIN-28 | On a short payment the system MUST ask which of the five it is. It MUST NOT silently leave a residual balance and keep dunning, and MUST NOT silently write off the difference. |
| FIN-29 | `WithholdingTax` deductions are recorded with `rate`, `base_amount`, `withheld_amount`, `certificate_reference` (nullable, chaseable). We never compute the rate — it is entered or imported (A-03, Q-01). |
| FIN-30 | A pending withholding certificate MAY generate a **document-chase task**, which is explicitly *not* a collection case and MUST NOT use dunning templates. |

### 3.3 Write-off

| ID | Rule |
|----|------|
| FIN-31 | Write-off requires **two distinct humans**: `writeoff.propose` then `writeoff.approve` (PRD-11). A single-user tenant self-approves with `self_approved = true` recorded. |
| FIN-32 | AI MUST NOT propose a write-off amount as an actionable value. It may flag "this looks uncollectible" as an *observation with a reason code*; the amount is always the system-computed open balance and the decision is human (`CLAUDE.md`: AI may never write off balances). |
| FIN-33 | Abandoning a collection case (C11) is **not** a write-off. The balance stays in AR until someone writes it off explicitly. Conflating the two would understate receivables. |
| FIN-34 | Write-offs are reported separately (write-off register: date, invoice, amount, reason code, proposer, approver) and are excluded from aging. |

---

## 4. Credit notes

| ID | Rule |
|----|------|
| FIN-40 | A credit note is stored with a **positive** amount and a `reason_code` from a closed set: `dispute_resolution`, `agreed_discount`, `goods_returned`, `service_credit`, `billing_error`, `bank_charges`, `rounding_adjustment`, `other`. |
| FIN-41 | A credit note may be applied across several invoices of the same customer and currency; `Σ applications ≤ credit_note.amount` (mirrors FIN-21). |
| FIN-42 | Unapplied credit reduces the *customer's* net position but not any invoice balance until applied. It MUST be visible on the customer screen and in the dunning message context so we never chase a customer who is holding our credit. |
| FIN-43 | Voiding a credit note reverses its applications (compensating rows, FIN-23) and MAY reopen invoices (I7). |

---

## 5. Aging

### 5.1 Definition

**FIN-50** Aging is computed on **days past due**, from `due_date`, not from
`issue_date`. (Invoice-date aging is a valid alternative some accountants prefer; it
is a tenant setting `aging_basis` = `due_date` (default) | `issue_date`, and the
report header MUST state which is in use.)

```
days_past_due(I, asOf) = (asOf_date_in_tenant_tz - I.due_date).Days     // calendar days
```

**FIN-51** Buckets (tenant-configurable boundaries, these are the defaults):

| Bucket | Condition |
|--------|-----------|
| `Current` | `days_past_due ≤ 0` |
| `Days1To30` | `1 ≤ dpd ≤ 30` |
| `Days31To60` | `31 ≤ dpd ≤ 60` |
| `Days61To90` | `61 ≤ dpd ≤ 90` |
| `Days90Plus` | `dpd > 90` |

| ID | Rule |
|----|------|
| FIN-52 | Buckets are **half-open and exhaustive**: every `Open` invoice with `open_balance > 0` lands in exactly one bucket. A test asserts bucket sums == total AR for randomized data (doc 09 §2.2). |
| FIN-53 | Aging includes **only** invoices in lifecycle `Open` with `open_balance > 0`. It excludes `Imported`, `Void`, `WrittenOff`, and `Settled`. |
| FIN-54 | Aging buckets the **open balance**, not the total amount. A 10,000 invoice with 9,000 paid contributes 1,000. |
| FIN-55 | Aging is computed **per currency**, with a per-currency subtotal, plus an optional indicative base-currency total using frozen document rates (FIN-06), clearly labelled. |
| FIN-56 | `disputed_amount` is shown as a **separate column** and is *also* included in the bucket total (the money is still owed until credited, SM-41). The report MUST show "of which disputed". |
| FIN-57 | `as_of` date is a parameter, defaulting to today in tenant timezone. Aging as of a past date MUST recompute from history (allocations carry `effective_date`), not from current balances — otherwise the report is not reproducible. This is an explicit requirement of the aging slice, not a later feature. |
| FIN-58 | Day boundaries use the tenant timezone (A-10). An invoice due 2026-09-10 is not overdue at 23:00 Amman on 2026-09-10, and is overdue at 00:01 on 2026-09-11. |
| FIN-59 | Unapplied cash and unapplied credit notes are reported **below** the aging table as separate lines, never distributed into buckets. |

### 5.2 Derived metrics (advisory, not accounting)

**FIN-60** DSO (Days Sales Outstanding), simple average method:

```
DSO(period) = (total_AR_at_period_end / credit_sales_in_period) * days_in_period
```
Requires sales in the period, which we only know from imported invoices — so DSO is
labelled **"based on invoices imported into this system"** and MUST NOT be presented
as an audited figure. If < 90 days of history exists, it is not shown at all.

**FIN-61** Additional advisory metrics: `average_days_to_pay` per customer (mean of
`payment_date - due_date` over settled invoices, trailing 12 months), collections
effectiveness index, and promise reliability (SM-37). All are advisory, all state
their sample size.

**FIN-62** No advisory metric may be produced by the LLM. They are SQL/C# computations
the AI may *narrate* in the daily briefing using values passed to it (doc 07 §7).

---

## 6. Dates and calendars

| ID | Rule |
|----|------|
| FIN-70 | All timestamps stored `timestamptz` (UTC). All *business dates* (`issue_date`, `due_date`, `promised_date`, `received_date`) stored as `date` in the tenant timezone's calendar — a due date is a calendar fact, not an instant. |
| FIN-71 | Aging arithmetic uses **calendar days**. Follow-up scheduling, PTP grace, and dispute SLAs use **business days** with weekend = Friday+Saturday and a tenant holiday calendar (A-08). |
| FIN-72 | Payment terms are stored on the customer (`payment_terms_days`, default 30) and MAY be overridden per invoice. `due_date = issue_date + terms` when not supplied by the import; when supplied, the imported `due_date` wins and any mismatch is surfaced as an import warning, not silently corrected. |
| FIN-73 | The tenant holiday calendar is a table of dates, seeded manually. Islamic-calendar holidays MUST NOT be computed by us (they are announced, not calculated). Missing holidays degrade to "it's a business day" — never block sending. |

---

## 7. Invariants (asserted continuously)

These run as (a) unit tests over generated data, (b) a nightly job per tenant, and (c)
an integration test after every mutation-heavy test scenario. Any violation is P1.

| ID | Invariant |
|----|-----------|
| INV-01 | For every invoice: `open_balance = total - allocated_payments - allocated_credits - written_off` and `0 ≤ open_balance ≤ total`. |
| INV-02 | For every payment: `Σ allocations ≤ payment.amount`, and every allocation's currency == payment currency == invoice currency. |
| INV-03 | For every credit note: `Σ applications ≤ credit_note.amount`. |
| INV-04 | Settlement status of every invoice equals the pure function of its balance (FIN-12). |
| INV-05 | Every row of every business table has a non-null `tenant_id`, and **no** allocation, application, case, PTP, dispute or message references an entity from a different tenant (SEC-11). Enforced by composite foreign keys (doc 04 §3.3) — not by hope. |
| INV-06 | At most one non-terminal collection case per (tenant, customer) (SM-21). |
| INV-07 | At most one `Active` PTP per invoice (SM-36). |
| INV-08 | Sum of aging buckets == total AR open balance for each currency, for any `as_of` date. |
| INV-09 | `balance_cache == derived open_balance` for every invoice (FIN-10). |
| INV-10 | No invoice in `Settled` has `open_balance > 0`; no invoice in `Open` has `open_balance = 0` (both would mean SM-12 failed to run). |
| INV-11 | Every money column in the database has type `numeric(19,3)`; a schema test enumerates `information_schema.columns` and fails on any `float`/`double precision`/`money` type or wrong scale. |
| INV-12 | Every state transition in the four machines has a corresponding audit row; a test asserts `count(transitions) == count(audit rows of type transition)` for a scripted scenario. |
| INV-13 | No AI-sourced row is in a state that required human confirmation without a recorded `approved_by_user_id` (PTP `Active`, dispute beyond `Open`, any sent message). |

---

## 8. Case priority score (product heuristic — explicitly not money)

**FIN-80** Priority is an integer 0–100 computed in C# and stored on the case. It is
a *ranking* device; it MUST NOT be displayed as a monetary or risk percentage, and it
MUST be explainable — the UI shows the contributing factors.

```
score = w1 * normalized(open_balance_in_base)      // size of the problem
      + w2 * normalized(max_days_past_due)          // how late
      + w3 * broken_promise_penalty                 // trust signal (SM-37)
      + w4 * bounced_cheque_penalty                 // A-05
      + w5 * customer_value_factor                  // don't burn a good customer
      - w6 * recent_contact_dampener                // don't chase daily
      - w7 * dispute_dampener                       // SM-25
```

**FIN-81** Default weights live in tenant settings, are versioned, and every stored
score records the `weights_version` that produced it, so a queue ordering can be
reproduced after the fact.

**FIN-82** The LLM MUST NOT produce or adjust the score. It may explain the ranking in
words using the factors given to it.

---

## 9. Worked examples (become fixture tests verbatim)

**E1 — Withholding tax.** Invoice 10,000.000 JOD (services). Customer withholds 5%
[Q-01] and pays 9,500.000. Correct: payment allocation 9,500.000 + withholding
deduction 500.000 → `open_balance = 0` → `Settled`. A withholding certificate chase
task is created. **Incorrect and forbidden:** leaving 500.000 open and dunning it.

**E2 — Post-dated cheque.** Invoice 4,000.000 due 2026-09-01. On 2026-09-05 the
customer hands over a cheque dated 2026-10-15. Correct: cheque recorded `Received`
(no allocation, balance unchanged, still overdue in aging), **plus** an `Active` PTP
for 4,000.000 on 2026-10-15 → case `PromiseActive`, chasing suppressed. On 2026-10-16
cheque `Cleared` → allocation → `Settled` → PTP `Kept`. If instead `Bounced` → no
allocation, PTP `Broken`, case reopens at raised priority, `bounced_cheque` recorded.

**E3 — Partial payment plus dispute.** Invoice 6,000.000. Customer pays 4,000.000 and
disputes 2,000.000 as `goods_damaged`. Correct: allocation 4,000.000 →
`PartiallyPaid`, `open_balance = 2,000.000`, dispute `Open` with
`disputed_amount = 2,000.000` → case `Disputed`, dunning on this invoice blocked.
Aging shows 2,000.000 in its bucket with "of which disputed 2,000.000". Resolution
`Accepted` → credit note 2,000.000 → `open_balance = 0` → `Settled`.

**E4 — Overpayment.** Invoice 1,000.000; customer pays 1,500.000. Correct: allocate
1,000.000 → `Settled`; 500.000 remains **unapplied cash** on the customer, visible,
allocatable to future invoices or refundable outside the system. **Forbidden:**
negative balance on the invoice, or auto-allocating to an unrelated disputed invoice.

**E5 — Rounding residual.** Invoice 333.335 JOD; customer pays 333.330. Residual
0.005 < `auto_clear_residual_below` → system **proposes** a `rounding_adjustment`
credit note; a human approves; invoice `Settled`. **Forbidden:** treating 0.005 as
zero by tolerance (FIN-13/FIN-14).

**E6 — Multi-currency customer.** Customer has 5,000.000 JOD and 2,000.00 USD open.
The customer screen shows two balance blocks; the aging report shows two currency
sections; nothing anywhere sums them without an explicit, labelled base-currency
conversion using frozen document rates (FIN-04, FIN-55).
