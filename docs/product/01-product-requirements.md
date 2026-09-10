# 01 — Product Requirements: AI AR & Collections Assistant

Status: DRAFT — awaiting approval. Requirement IDs are stable and citable from tests.

---

## 1. Problem

A Jordanian SME with 50–500 open invoices has no reliable answer to three questions:

1. **Who owes me what, right now, and how late is it?** The answer lives in an
   accounting package nobody opens, an Excel file that is a week stale, and the
   owner's memory.
2. **Who do I chase today, and what do I say?** Chasing is emotional labour in two
   languages, and it is the first thing dropped when the week gets busy.
3. **What did the customer actually promise?** Promises arrive as WhatsApp voice
   notes and email replies, and evaporate.

The consequence is cash trapped in receivables: invoices that would have been paid
on a reminder go 90+ days, and the owner discovers it during a cash crunch.

## 2. Product thesis

**PRD-01** The product is a *collections operating loop*, not a reporting tool. Its
output is a short, ranked, defensible list of actions to take today, and a record of
what happened.

**PRD-02** AI's role is to remove reading and drafting work — classifying replies,
drafting bilingual messages, summarising a day — never to decide money. Every
monetary figure a user sees is computed in C# from stored `decimal` values
(see doc 03 and `CLAUDE.md` → Financial Safety).

**PRD-03** The product is **Arabic-first**. Arabic RTL is a first-class layout, not a
mirrored afterthought, and Arabic message drafts are written in Arabic, not translated
from English at send time.

**PRD-04** Trust is the feature. A user must always be able to answer "why does it say
that?" — every balance traces to invoices and payments; every AI suggestion shows its
model, confidence, reason code, and whether a human approved it.

## 3. Success criteria (product-level, measured in pilot)

| ID | Criterion | Target |
|----|-----------|--------|
| PRD-05 | A new tenant reaches "aging report I believe" from signup | ≤ 30 minutes, unaided |
| PRD-06 | Daily collection queue is worked to empty by the primary user | ≥ 4 days out of 5 |
| PRD-07 | Reduction in DSO after 90 days of use vs. tenant's own baseline | ≥ 10% |
| PRD-08 | Share of overdue invoices with a *recorded* next action (PTP, dispute, or scheduled follow-up) | ≥ 90% |
| PRD-09 | AI reply classifications overturned by a human | ≤ 10% of classified replies |
| PRD-10 | Monetary discrepancies vs. tenant's own books found in pilot | 0 (any single one is a P1 defect) |

## 4. Personas

### P1 — Rana, the Owner-Operator (primary buyer, secondary daily user)
Runs a 25-person trading or services company in Amman. Bilingual, prefers Arabic for
customer communication and English for numbers. Checks the phone at 8am and at 9pm.
Cares about cash this week, and about not damaging relationships with customers who
are also friends. **Will abandon the product** if it makes her look sloppy in front of
a customer (chasing an invoice already paid, wrong amount, wrong language, wrong name).
Needs: the daily briefing, approve/reject, the escalation decision.

### P2 — Ahmad, the Accountant / Office Administrator (primary daily user)
Does invoicing, payments, payroll and the bank run. Lives in Excel and in the
accounting package. Sceptical of anything that claims to "do accounting". Types
Arabic and English, sometimes Arabizi. **Will abandon the product** if it double-keys
work he already does elsewhere, or if its numbers disagree with his ledger and he
cannot see why. Needs: import, aging, the queue, recording payments and cheques,
allocation.

### P3 — Sami, the Collections Agent / Sales rep doing collections (heaviest user, larger tenants only)
Works a list. Wants speed: open case, see history, send message, log outcome, next.
Judged on collected amount. **Will game the product** if PTPs are a vanity metric —
so PTP quality (kept vs. broken) must be visible, not just PTP count.

### P4 — Layla, the External Accountant / Auditor (occasional, read-only)
Comes in at month-end or year-end. Needs an export and an audit trail that shows no
one silently changed a balance. **Must never be able to write.**

### P5 — Khalid, the Customer (non-user, receives our output)
Does not log in. Receives emails and WhatsApp messages. Judges the tenant by them.
Every message must be correct, correctly addressed, in his preferred language, and
must never contain another customer's data. He is also the source of the untrusted
text our AI reads (see doc 08 → prompt injection).

### P6 — Platform Operator (us)
Runs the deployment. **Must not** be able to read tenant business data as a routine
capability; support access is break-glass, time-boxed and audited (SEC-30).

## 5. Roles and permissions

Roles are **per tenant**. A user account is global (one identity, one email) and holds
one membership row per tenant with exactly one role. There is no cross-tenant role.

| Role | Intended persona | One-line definition |
|------|------------------|---------------------|
| `Owner` | P1 | Full control including billing, users, and deletion. Exactly one per tenant, always. |
| `Admin` | P1/P2 | Everything except transferring ownership and deleting the tenant. |
| `Accountant` | P2 | All AR data operations: import, payments, allocation, credit notes, write-off *proposals*. |
| `Collector` | P3 | Works cases and messages. Cannot import, cannot alter invoices or payments. |
| `Viewer` | P4 | Read + export. No writes of any kind, including notes. |
| `PlatformSupport` | P6 | No tenant data by default. Break-glass elevation only (SEC-30). |

### 5.1 Permission matrix

Permissions are the authorization primitive; roles are bundles of them. The API
authorizes on **permission**, never on role name (SEC-12).

| Permission | Owner | Admin | Accountant | Collector | Viewer |
|---|:--:|:--:|:--:|:--:|:--:|
| `tenant.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `tenant.settings.write` | ✅ | ✅ | — | — | — |
| `tenant.transfer_ownership` | ✅ | — | — | — | — |
| `tenant.delete` | ✅ | — | — | — | — |
| `users.read` | ✅ | ✅ | ✅ | — | — |
| `users.invite` | ✅ | ✅ | — | — | — |
| `users.role.write` | ✅ | ✅ | — | — | — |
| `users.deactivate` | ✅ | ✅ | — | — | — |
| `customers.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `customers.write` | ✅ | ✅ | ✅ | — | — |
| `customers.merge` | ✅ | ✅ | ✅ | — | — |
| `invoices.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `invoices.import` | ✅ | ✅ | ✅ | — | — |
| `invoices.write` | ✅ | ✅ | ✅ | — | — |
| `invoices.void` | ✅ | ✅ | ✅ | — | — |
| `payments.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `payments.write` | ✅ | ✅ | ✅ | — | — |
| `payments.allocate` | ✅ | ✅ | ✅ | — | — |
| `credit_notes.write` | ✅ | ✅ | ✅ | — | — |
| `writeoff.propose` | ✅ | ✅ | ✅ | — | — |
| `writeoff.approve` | ✅ | ✅ | — | — | — |
| `aging.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `cases.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `cases.write` | ✅ | ✅ | ✅ | ✅ | — |
| `cases.assign` | ✅ | ✅ | ✅ | — | — |
| `cases.escalate` | ✅ | ✅ | — | — | — |
| `ptp.write` | ✅ | ✅ | ✅ | ✅ | — |
| `disputes.write` | ✅ | ✅ | ✅ | ✅ | — |
| `disputes.resolve` | ✅ | ✅ | ✅ | — | — |
| `messages.draft` | ✅ | ✅ | ✅ | ✅ | — |
| `messages.send` | ✅ | ✅ | ✅ | ✅ | — |
| `templates.write` | ✅ | ✅ | ✅ | — | — |
| `ai.suggestions.read` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `ai.suggestions.approve` | ✅ | ✅ | ✅ | ✅ | — |
| `ai.settings.write` | ✅ | ✅ | — | — | — |
| `audit.read` | ✅ | ✅ | ✅ | — | ✅ |
| `export.run` | ✅ | ✅ | ✅ | — | ✅ |

**PRD-11** `writeoff.approve` MUST NOT be held by the same role that holds
`writeoff.propose` alone — write-off requires two distinct human users
(four-eyes, FIN-31). An Owner acting alone in a one-person tenant MAY self-approve,
and the audit record MUST record `self_approved = true`.

**PRD-12** No permission grants "mark invoice paid" as a direct action. Payment status
is *derived* from payments and allocations (FIN-10). There is no endpoint that sets
an invoice to Paid.

**PRD-13** Every permission above MUST have an authorization test that asserts a
403 for at least one role that lacks it, and a cross-tenant test asserting a 404
(SEC-14, doc 09 §4).

### 5.2 Additional guards

**PRD-14 Assignment scoping.** A `Collector` sees all cases by default. A tenant
setting `collector_sees_only_assigned` (default `false`) restricts the queue,
customer list and case list to assigned cases. This is a *filter*, not a security
boundary claim — it is enforced server-side but is not a substitute for tenancy.

**PRD-15 Send guard.** `messages.send` is additionally gated by tenant setting
`require_approval_before_send` (default **`true`**). While true, a message composed by
or with AI assistance MUST be approved by a user with `ai.suggestions.approve` before
dispatch, and the approving user MUST be recorded.

## 6. Functional scope (v1)

In scope, in build order (mirrors doc 10): organization & auth · customers · invoice
import (CSV/Excel) · aging · collection queue · promise-to-pay · dispute · email
templates & sending · local AI reply classification · daily briefing.

Supporting capabilities inside those slices: payments and cheques (inside invoice/
aging slices — a receivables product without payments is fiction), credit notes,
allocation, audit log, export, bilingual content, notification of AI suggestions.

Out of scope: see doc 00 §D.

## 7. Non-functional requirements

| ID | Requirement |
|----|-------------|
| PRD-20 | **Bilingual.** Every user-visible string exists in `ar` and `en`. Arabic is a supported *authoring* language, not only a display language. Untranslated strings MUST fail the build, not fall back silently. |
| PRD-21 | **RTL.** Arabic renders in a genuine RTL layout (`dir="rtl"`), with logical CSS properties throughout. Numbers, currency, invoice numbers and dates use locale-correct formatting and never break bidirectionally (doc 06 §3). |
| PRD-22 | **Performance.** Aging report and collection queue P95 < 800 ms at 50k invoices / tenant. Any single API call P95 < 500 ms excluding AI. AI classification P95 < 8 s; daily briefing is precomputed. |
| PRD-23 | **Availability.** Single-node pilot; RPO ≤ 24h via nightly `pg_dump`, RTO ≤ 4h, and a **restore drill is part of the definition of done for slice 1**, not a later chore. |
| PRD-24 | **Auditability.** Every state transition, money mutation, message send, and AI-influenced action is recorded immutably (doc 08 §5). |
| PRD-25 | **Accessibility.** WCAG 2.2 AA: keyboard-operable queue, visible focus, 4.5:1 contrast, screen-reader labels in both languages, no colour-only status encoding. |
| PRD-26 | **Cost.** No paid API, no proprietary runtime dependency, ever (`CLAUDE.md` → Cost Rules). Any dependency added is recorded in `THIRD-PARTY-NOTICES.md` in the same commit. |
| PRD-27 | **Data residency & retention.** Tenant data stays in the deployed instance. Retention: audit 7 years, messages 3 years, AI inference logs 90 days (then only the decision record survives), soft-deleted rows purged after 30 days. |
| PRD-28 | **Offline degradation.** If Ollama is unreachable, the product MUST remain fully usable: queue, aging, messaging and manual classification all work; AI panels show an explicit degraded state (doc 06 §9). AI is never on the critical path of getting paid. |

## 8. Product principles (tie-breakers for future decisions)

1. **A wrong number is worse than no number.** Prefer showing "unallocated" or
   "needs review" over a confident guess.
2. **The customer relationship is the tenant's asset.** Default to fewer, better
   messages; never automate a send that a human has not seen at least once for a
   given customer.
3. **AI proposes, the state machine disposes.** No AI output ever mutates money or
   status directly (ADR-0003).
4. **Arabic parity is a release blocker,** not a follow-up ticket.
5. **Explain, then act.** Every action screen shows the evidence that motivated it.
