# Documentation Index — AR & Collections Assistant

Status: **Approved for implementation.** These documents are the source of truth; where the build
has had to diverge from them, the divergence is recorded in the slice's own document under
[`slices/`](slices/) **and** amended here in the same commit (DM-34).

Doc 04 has been amended by slice 1 (DM-06, DM-06a, DM-06b).

| Slice | Document | Status |
|-------|----------|--------|
| 1 | [slices/slice-01-organization-and-auth.md](slices/slice-01-organization-and-auth.md) | Implemented and tested |

**Open questions still outstanding.** Q-01, Q-02, Q-04 and Q-06 in document 00 are marked as
blocking for slice 1. None of them touches organization, identity, roles or sessions — they concern
withholding tax, invoice sources, product scope and GPU availability — so slice 1 proceeded without
them. **They do block slice 3 (invoice import), and Q-04 could reshape the entity model, so they
should be answered before slice 2 starts.**

Scope of this document set: **one product only** — the bilingual (Arabic/English)
AI Accounts Receivable and Collections Assistant for Jordanian SMEs. No other
module (Expenses, Reconciliation, Cash Flow, Budget-vs-Actual) is designed here.

## Read in this order

| # | Document | What it decides |
|---|----------|-----------------|
| 00 | [assumptions/00-assumptions-and-open-questions.md](assumptions/00-assumptions-and-open-questions.md) | Everything I assumed and must be confirmed before code |
| 01 | [product/01-product-requirements.md](product/01-product-requirements.md) | Personas, jobs, roles, permission matrix, scope boundaries |
| 02 | [product/02-state-machines.md](product/02-state-machines.md) | Invoice, collection case, promise-to-pay, dispute |
| 03 | [product/03-financial-rules.md](product/03-financial-rules.md) | Aging, balances, allocation, rounding, currency, tax |
| 04 | [architecture/04-data-model.md](architecture/04-data-model.md) | PostgreSQL entity model, tenant isolation from the ground up |
| 05 | [architecture/05-api-contracts.md](architecture/05-api-contracts.md) | HTTP contracts per vertical slice, in build order |
| 06 | [product/06-frontend-screens.md](product/06-frontend-screens.md) | Every screen, every state, RTL/LTR behaviour |
| 07 | [architecture/07-ai-service-contracts.md](architecture/07-ai-service-contracts.md) | AI operations, strict JSON schemas, confidence & reason codes |
| 08 | [security/08-security-and-tenancy.md](security/08-security-and-tenancy.md) | AuthN/AuthZ, tenant isolation, audit, prompt-injection defence |
| 09 | [architecture/09-test-strategy.md](architecture/09-test-strategy.md) | Unit, integration, E2E, security, AI-evaluation plans |
| 10 | [product/10-implementation-backlog.md](product/10-implementation-backlog.md) | Vertical slices with acceptance criteria, in order |

## Decision records

| ADR | Title |
|-----|-------|
| [0001](decisions/0001-tenant-isolation-strategy.md) | Tenant isolation: shared schema + RLS + application filter |
| [0002](decisions/0002-money-representation.md) | Money as `numeric(19,3)` / C# `decimal`, minor unit = fils |
| [0003](decisions/0003-ai-advisory-only-boundary.md) | AI is advisory-only; the human/state machine acts |
| [0004](decisions/0004-messaging-channels.md) | Email owned in-product; WhatsApp via click-to-chat only |
| [0005](decisions/0005-bilingual-content-strategy.md) | Bilingual content, RTL, and locale strategy |

## Glossary (used consistently across all documents)

| Term | Meaning |
|------|---------|
| **Tenant** | One paying SME. The isolation boundary for all business data. |
| **Organization** | Synonym for tenant at the UI level; `tenants` is the table. |
| **AR** | Accounts Receivable — money customers owe the tenant. |
| **Aging bucket** | Current / 1–30 / 31–60 / 61–90 / 90+ days past due. |
| **DSO** | Days Sales Outstanding. |
| **Collection case** | The workflow object tracking recovery of one customer's overdue balance. |
| **PTP** | Promise-to-Pay — a customer commitment to pay an amount by a date. |
| **Dispute** | A customer's assertion that some or all of an invoice is not owed. |
| **Allocation** | Assignment of a receipt amount to specific invoices. |
| **Fils** | Minor unit of the Jordanian Dinar. 1 JOD = 1000 fils (three decimals). |
| **JoFotara** | Jordan's national e-invoicing platform (ISTD). Read-only interest for v1. |
| **Advisory output** | Any AI output. Never authoritative, never self-acting. |

## Conventions

- **RFC 2119 keywords** (MUST / MUST NOT / SHOULD / MAY) are normative.
- Every requirement has a stable ID (`PRD-xx`, `FIN-xx`, `SEC-xx`, `AI-xx`, …) so
  tests and code can cite it.
- Anything not confirmed by the product owner appears in document 00 as an
  assumption with an ID (`A-xx`) and is referenced inline as `[A-xx]`.
