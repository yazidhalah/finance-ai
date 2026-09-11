# Slice 11 — v1 acceptance: the E2E suite and CI

Status: **Implemented.** Not a product slice: this is doc 09 §9 item 2 ("unit, integration, E2E (both
locales), and the tenant-isolation suite pass in CI"), which no slice had satisfied because `tests/e2e`
was empty and there was no CI definition.

Source specs: doc 09 §6 (T-120…T-132), §9 · doc 06 UI-70, UI-23, UI-17 · doc 10 "After each slice".

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `tests/e2e`: Playwright, twelve journeys T-121…T-132, run as two projects (`en`, `ar`); the Arabic run asserts `dir="rtl"`, money isolates (`<bdi data-amount>` with the exact stored string) and keeps three visual snapshots (aging table, empty queue, Arabic message preview) |
| S2 | A deterministic **fake model** in the AI service (`AI_FAKE_MODEL=1`, `services/ai/app/fake_model.py`) so the AI journeys run without a GPU; opt-in only, self-announcing (`/model-info` says `fake: true`, digest `fake`), never the production path |
| S3 | `.github/workflows/ci.yml`: format, Release build, unit, integration (performance category excluded), security, web, AI pytest, then the E2E suite in both locales against pgvector + Mailpit service containers |

**Deferred:** T-140…T-142 in CI (performance runs stay a local `Category=Performance` trait) · T-150 restore
drill (needs a backup target) · T-153 alert path (no alerting infrastructure exists) · visual snapshots
for `en` (the Arabic ones are the doc 09 requirement).

## 2. Rules, stated explicitly

| Question | Answer |
|----------|--------|
| What is driven through the browser? | The human gates: register and sign in, the import wizard's three resolutions and commit, the FIFO proposal and its confirmation, log call and record promise on the case, raise a dispute and see the send button disabled, resolve with the credit-note preview, compose from an Arabic template and approve as a second user, classify → edit-and-approve in the inbox, the Today screen with and without the AI, the not-found screen across tenants. |
| What is set up through the API? | Organizations, customers, contacts, invoices (through the import path — the only way invoices enter), payments and withholding, cheques, the sweep. The same endpoints the browser calls. |
| What touches the database directly? | Seeding a second member (v1 has no invitation flow; slice 1 said so) and moving a promise's dates into the past for T-126 — both done with `psql` and the repo `.env`, both commented in the spec. |
| How is the AI made deterministic? | The fake model classifies by keywords and narrates by echoing the figures; an organization named with `[ai-down]` gets "model unavailable". The real model is exercised by `services/ai` live tests and the evaluation harness, not here. |
| What can the suite not prove? | Prose quality, latency on real hardware, and anything the fake model decides — it proves the gates, the guards and the screens. |

## 3. Acceptance

| ID | Journey | Result |
|----|---------|--------|
| T-121…T-132 | as listed in doc 09 §6 | 12 × 2 locales pass locally (`24 passed`, ~1 min) |
| T-120 | Arabic run: RTL, isolates, three snapshots | asserted in every journey; snapshots committed |
| CI | the workflow file | written; **not executed here** (no runner in this environment) — see the review |
