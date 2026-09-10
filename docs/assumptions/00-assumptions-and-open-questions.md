# 00 — Assumptions and Open Questions

Status: DRAFT. **Read this first.** Everything below is something I decided on your
behalf in order to produce a complete design. Each item is either an **assumption**
(A-xx, I picked a default, change it freely) or a **blocking question** (Q-xx, the
design has a hole until you answer).

Legend: 🔴 blocking for slice 1 · 🟡 blocking for a later slice · ⚪ safe default.

---

## A. Market, legal and fiscal context (Jordan)

| ID | Assumption | Impact if wrong | Flag |
|----|------------|-----------------|------|
| A-01 | Base currency is **JOD with 3 decimal places** (1 JOD = 1000 fils). Storage is `numeric(19,3)`. | Every money column, every rounding rule, every test fixture. | 🔴 |
| A-02 | Tenants may issue invoices in **JOD, USD, EUR, SAR, AED**; the tenant has exactly one base currency and FX gain/loss is **out of scope for v1** (we report in invoice currency and in base currency at the invoice-date rate, and never post FX differences). | Multi-currency aging and the `payments` model. | 🔴 |
| A-03 | Jordan's **General Sales Tax is charged on the invoice by the tenant's own system**, not by us. We **import** tax amounts; we never compute tax. | If we must compute GST, that is a new module and violates "AI/product never calculates authoritative tax". | 🔴 |
| A-04 | **Withholding tax** is common on local B2B service invoices: the customer pays the invoice net of a withheld percentage and gives the supplier a withholding certificate. Our product MUST be able to record a short-payment as `WithholdingTax` rather than as a shortfall/dispute. I have modelled this. I have **not** hard-coded any rate. | Collections will chase phantom balances on every service invoice — the single most likely reason an SME abandons the product. | 🔴 |
| A-05 | **Post-dated cheques (PDCs)** are a primary payment instrument in Jordan. A PDC is a *promise*, not a payment, until it clears. I have modelled cheques as a first-class instrument with its own lifecycle (received → deposited → cleared / bounced). | Balances will be wrong in the most commercially painful direction. | 🔴 |
| A-06 | Bank rails we reference are **CliQ** and conventional bank transfer. v1 does **not** integrate with any bank or payment API (cost rules). Payments are entered by a human or imported from a bank CSV in a later slice. | Slice ordering only. | ⚪ |
| A-07 | **JoFotara** (national e-invoicing) is **out of scope for v1**. We do not submit anything to any regulator. AI never touches it (`CLAUDE.md` forbids submitting regulatory documents). If tenants already invoice via JoFotara, we import the resulting invoice data like any other source. | A whole compliance slice. | 🟡 |
| A-08 | Business week is **Sunday–Thursday**; Friday and Saturday are the weekend. Jordanian public holidays (fixed + Islamic calendar) affect *when we send messages and when a PTP is "due"* — not how aging is computed. Aging uses calendar days. | Follow-up scheduling, PTP breach timing. | ⚪ |
| A-09 | Legal escalation in Jordan (protest of a bounced cheque, legal notice via a lawyer) is **entirely outside the product**. We record that a human decided to escalate and freeze automation. `CLAUDE.md` forbids AI initiating it. | Scope. | ⚪ |
| A-10 | Timezone is **Asia/Amman** for all business-day logic; all timestamps are stored in UTC (`timestamptz`) and rendered per tenant timezone. | Aging boundaries, "today's queue", briefing send time. | ⚪ |

**Q-01 🔴** What is the withholding-tax rate (or rates) your pilot customers actually
experience, and is it deducted from the gross or the pre-tax amount? I need one real
worked example (invoice, GST, WHT, cash received) to write the allocation tests.

**Q-02 🔴** Do pilot tenants currently issue invoices from an existing system
(which one — e.g. an ERP, Excel, JoFotara portal), and can they export CSV/Excel?
The invoice-import slice depends entirely on this answer.

**Q-03 🟡** Does any pilot tenant need Hijri dates displayed alongside Gregorian?
I have designed for Gregorian-only with a Hijri display option deferred.

---

## B. Product scope

| ID | Assumption | Flag |
|----|------------|------|
| A-11 | The product **does not issue invoices**. It ingests them. Creating invoices is an AR-adjacent module we are explicitly not building. Manual single-invoice entry exists only as a convenience for gap-filling. | 🔴 |
| A-12 | The product **does not do accounting**. There is no general ledger, no journal entries, no chart of accounts. It maintains a *collections view* of receivables that must reconcile to the tenant's own books, not replace them. | 🔴 |
| A-13 | Outbound channels for v1: **email** (owned, automated with approval) and **WhatsApp click-to-chat** (a human presses send in WhatsApp; we only prepare text and log the intent). SMS and voice are out of scope. See ADR-0004. | ⚪ |
| A-14 | Inbound customer replies arrive by **email only** in v1 (IMAP polling of a tenant-configured mailbox). WhatsApp replies are pasted in by a human. This is what `classify_customer_reply` operates on. | 🟡 |
| A-15 | v1 is **single-region, self-hosted via Podman Compose**, one PostgreSQL instance, shared-schema multi-tenancy. | ⚪ |
| A-16 | Expected pilot scale: ≤ 20 tenants, ≤ 50k invoices and ≤ 200k AR events per tenant. This justifies shared-schema + RLS over per-tenant databases, and justifies computing aging on read rather than materializing it. | ⚪ |
| A-17 | Users are **not** accountants in the majority case; the primary daily user is an owner or an office administrator. UI must be explanation-first, jargon-light, and Arabic-first. | ⚪ |

**Q-04 🔴** Confirm A-11 and A-12. If the pilot expects to *create* invoices in this
product, the entity model and the whole backlog change shape.

**Q-05 🟡** Who owns the sending mailbox — the tenant's own domain (we need SMTP
credentials and SPF/DKIM alignment) or ours (deliverability risk, looks like spam)?
I have designed for tenant-owned SMTP/IMAP.

---

## C. AI

| ID | Assumption | Flag |
|----|------------|------|
| A-18 | **Qwen3 4B via Ollama, running locally.** Small models are unreliable at Arabic financial nuance and at Jordanian dialect. Every AI operation is therefore designed as a **constrained classification/extraction with a confidence floor and a human gate** — never free-form judgement, never arithmetic. | 🔴 |
| A-19 | Arabic input will be **mixed MSA, Jordanian dialect, and Arabizi (Arabic in Latin letters, e.g. "b3atlak el 7awaleh bokra")**. The evaluation set MUST include Arabizi or the reply classifier will silently fail on real traffic. | 🔴 |
| A-20 | AI never sees full customer PII or amounts it does not need. Prompts are built from a **redacted projection** of the record (see doc 08). | 🔴 |
| A-21 | Below the confidence floor, the correct behaviour is **"unclassified → human queue"**, never a guess. A wrong "customer promised to pay" is more expensive than an unclassified message. | 🔴 |
| A-22 | LangGraph is used for the **daily briefing** and reply-handling flow only. Classification and extraction are single-shot calls, not agents. Adding an agent loop to a 4B model multiplies failure modes. | ⚪ |
| A-23 | OCR/Docling (PaddleOCR) is **deferred to a later slice** — invoice import is CSV/Excel first. PDF/scan ingestion is a large, low-certainty slice and must not block the core loop. | ⚪ |

**Q-06 🔴** Is a GPU available on the deployment target? A 4B model on CPU changes
the daily-briefing latency budget (and therefore whether briefings are precomputed
overnight — which is what I assumed).

**Q-07 🟡** Do you have any corpus of real customer replies (Arabic + English) we can
label for the evaluation set? Without ~300 labelled replies, doc 09's AI acceptance
gates cannot be measured, and slice 9 cannot be accepted.

---

## D. Deliberate non-goals for v1

Recorded so they are not re-litigated mid-build: invoice creation · general ledger ·
FX revaluation · bank/payment API integration · JoFotara submission · customer
self-service portal · SMS/voice · WhatsApp Business Platform API · credit scoring ·
mobile app · offline mode · automated legal escalation · any second module.
