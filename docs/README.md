# Documentation Index — AR & Collections Assistant

Status: **Approved for implementation.** These documents are the source of truth; where the build
has had to diverge from them, the divergence is recorded in the slice's own document under
[`slices/`](slices/) **and** amended here in the same commit (DM-34).

Doc 04 has been amended by slice 1 (DM-06, DM-06a, DM-06b), slice 2 (DM-20a), slice 3 (DM-23a), slice 3b (DM-24a), slice 4 (DM-31a), slice 5 (DM-25a), slice 6 (DM-24a), slice 7 (DM-26a) and slice 8 (DM-27a). Doc 03 §2.1 / FIN-20 were amended by slice 3b (F-1: withholding is a fourth balance instrument).

| Slice | Document | Status |
|-------|----------|--------|
| 1 | [slices/slice-01-organization-and-auth.md](slices/slice-01-organization-and-auth.md) | Implemented and tested |
| 2 | [slices/slice-02-customers.md](slices/slice-02-customers.md) · [review](slices/slice-02-review.md) | Implemented and tested; merge and statement deferred |
| 3a | [slices/slice-03-invoice-import.md](slices/slice-03-invoice-import.md) · [review](slices/slice-03-review.md) | Implemented and tested |
| 3b | [slices/slice-03b-payments-and-allocation.md](slices/slice-03b-payments-and-allocation.md) · [review](slices/slice-03b-review.md) | Implemented and tested; re-auth on write-off approval deferred with MFA (D-6) |
| 4 | [slices/slice-04-aging.md](slices/slice-04-aging.md) · [review](slices/slice-04-review.md) | Implemented and tested; disputed column is a placeholder until slice 7 |
| 5 | [slices/slice-05-collection-queue.md](slices/slice-05-collection-queue.md) · [review](slices/slice-05-review.md) | Implemented and tested; the daily sweep is an endpoint until a scheduler exists (D-2) |
| 6 | [slices/slice-06-promise-to-pay.md](slices/slice-06-promise-to-pay.md) · [review](slices/slice-06-review.md) | Implemented and tested; `ptp_kept` added to the case machine (F-1) |
| 7 | [slices/slice-07-disputes.md](slices/slice-07-disputes.md) · [review](slices/slice-07-review.md) | Implemented and tested; SLA lengths are constants (D-1); the dunning guard is enforced by slice 8's send path |
| 8 | [slices/slice-08-email-reminders.md](slices/slice-08-email-reminders.md) · [review](slices/slice-08-review.md) | Implemented and tested; SMTP is the `.env` host for every tenant (D-1); the review names the four send paths |
| 9 | [slices/slice-09-ai-reply-classification.md](slices/slice-09-ai-reply-classification.md) · [review](slices/slice-09-review.md) · [evaluation](decisions/0006-ai-evaluation-2026-09-11-qwen3-4b-classify-v1.md) | Implemented and tested; the corpus is author-written and small (D-5); the model is steerable by injection, the backend is not |
| 10 | [slices/slice-10-daily-briefing.md](slices/slice-10-daily-briefing.md) · [review](slices/slice-10-review.md) | Implemented and tested; metrics reuse the aging and queue computations; the guard drops any untraceable numeral; Arabic prose quality is flagged |
| 11 | [slices/slice-11-v1-acceptance.md](slices/slice-11-v1-acceptance.md) · [review](slices/slice-11-review.md) | The E2E suite (T-121…T-132, en + ar) and the CI workflow; the fake-model mode of the AI service is test-only |
| 12 | [slices/slice-12-members-and-restore-drill.md](slices/slice-12-members-and-restore-drill.md) · [review](slices/slice-12-review.md) | Invitations, role change, deactivation; T-121 walks the real flow; the T-150 restore drill runs in CI |
| 13 | [slices/slice-13-auth-completion.md](slices/slice-13-auth-completion.md) · [review](slices/slice-13-review.md) | TOTP MFA (required for Owner/Admin after a 7-day grace), password reset, re-authentication for ownership transfer and write-off approval |
| 14 | [slices/slice-14-deployment.md](slices/slice-14-deployment.md) · [review](slices/slice-14-review.md) | Podman Compose stack from a clean clone (A-15, SEC-69), the operations runbook (SEC-103), dependency audits in CI (SEC-68) — see [ops/runbook.md](ops/runbook.md) |
| 15 | [slices/slice-15-ops-invariants-alerts.md](slices/slice-15-ops-invariants-alerts.md) · [review](slices/slice-15-review.md) | The invariant job per tenant (doc 03 §7) in the sweep, the SEC-102 alert path (email, webhook, `alerts` rows, T-153), `infrastructure/alert.sh`, the Audit screen (doc 06 §6.11) |
| 16 | [slices/slice-16-ci-gates.md](slices/slice-16-ci-gates.md) · [review](slices/slice-16-review.md) | OpenAPI snapshot (`docs/api/openapi.json`, API-14), gitleaks in CI and pre-commit (SEC-67), the notices/licence checker (PRD-26), the nightly performance workflow (T-140/141) |
| 17 | [slices/slice-17-key-rotation.md](slices/slice-17-key-rotation.md) · [review](slices/slice-17-review.md) | Rotating the MFA KEK (versioned envelopes, rotate on use, `rotate-mfa-kek`) and the JWT signing key (a previous key honoured only for tokens that predate the process) — runbook §4 |
| 18 | [slices/slice-18-typed-contract.md](slices/slice-18-typed-contract.md) · [review](slices/slice-18-review.md) | Every operation's success and failure responses in `docs/api/openapi.json` (the `Problem` component, 401/403/404/400/422/429 per operation); money is a string everywhere in the contract |
| 19 | [slices/slice-19-audit-values.md](slices/slice-19-audit-values.md) · [review](slices/slice-19-review.md) | Before/after values, note and the AI suggestion on the audit viewer (doc 06 §6.11); the coverage baseline and the proposed floor ([decisions/0007](decisions/0007-coverage-floor.md)) |
| 20 | [slices/slice-20-supply-chain-integrity.md](slices/slice-20-supply-chain-integrity.md) · [review](slices/slice-20-review.md) | The whole dependency tree checked for copyleft (`check-notices.py --transitive`, inventory artifact); the `ai_suggestions` subject trigger (migration 0015); CI-side alerts through `alert.sh`; `.env` never overrides the environment in scripts |
| 21 | [slices/slice-21-acceptance-tooling.md](slices/slice-21-acceptance-tooling.md) · [review](slices/slice-21-review.md) | The pilot corpus importer (redaction, provenance, T-90/T-91 readiness), the generated Arabic review pack, and [ops/acceptance-pass.md](ops/acceptance-pass.md) — the procedure for "First Product done" |
| 22 | [slices/slice-22-deferred-items.md](slices/slice-22-deferred-items.md) · [review](slices/slice-22-review.md) | Typed handler results (the compiler checks the contract; found one bare 404), `Location` on every 201, Owners emailed on critical alerts (`PATCH /organization/alert-settings`) |
| 23 | [slices/slice-23-contract-completion.md](slices/slice-23-contract-completion.md) · [review](slices/slice-23-review.md) | The five ledger/organization rows of doc 05 that were never built: holidays (FIN-73), manual invoice (A-11), invoice edits, the invoice trail, the two-step customer merge (DM-21, migration 0017) |
| 24 | [slices/slice-24-email-completion.md](slices/slice-24-email-completion.md) · [review](slices/slice-24-review.md) | The last three doc 05 rows: email verification before the first sign-in, tenant SMTP with write-only secrets (SEC-09/66/67), the signed MTA webhook (Sent → Delivered/Bounced); migration 0018 |
| 25 | [slices/slice-25-password-hygiene-and-bounces.md](slices/slice-25-password-hygiene-and-bounces.md) · [review](slices/slice-25-review.md) | The SEC-01 breached-password check (offline, SecLists ≥ 12 chars, bundled) and bounced contacts (guard `contact_email_bounced`, cleared on edit; migration 0019) |
| 26 | [slices/slice-26-ai-per-operation.md](slices/slice-26-ai-per-operation.md) · [review](slices/slice-26-review.md) | The per-operation AI switches doc 05 names (`aiClassificationEnabled`, `aiBriefingEnabled`, migration 0020); `aiEnabled` stays the kill switch |
| 27 | [slices/slice-27-performance-completion.md](slices/slice-27-performance-completion.md) · [review](slices/slice-27-review.md) | Doc 09 §7 complete: single-entity and 5,000-row-import budgets, T-142 twenty-user load profile (ten minutes on the nightly) |

**Open questions still outstanding.** Q-01, Q-02, Q-04 and Q-06 in document 00 remain unanswered.
Slices 1–10 proceeded on the documented defaults (A-01…A-05, A-11, A-12). **Slice 3b built E1 on a
placeholder 5% withholding rate (Q-01)**; the product computes nothing from the rate, so a real
example changes only the fixture. Q-02 (the pilot's invoice source) still shapes the first import
mapping a pilot will need.

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
| [0006](decisions/0006-ai-evaluation-2026-09-11-qwen3-4b-classify-v1.md) | Evaluation report: qwen3:4b, `classify_reply` prompt v1, on the author-written corpus (AI-111/112) |
| [0007](decisions/0007-coverage-floor.md) | Coverage floor: Domain 95 / Infrastructure 90 / Api 85, enforced in the `api` CI job |
| [0008](decisions/0008-no-langgraph-until-a-multi-step-flow-exists.md) | LangGraph is not a dependency until a slice specifies a multi-step flow (A-22) |

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
