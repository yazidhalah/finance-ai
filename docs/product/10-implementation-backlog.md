# 10 — Implementation Backlog

Status: DRAFT. Slices are ordered; each is **independently completable and shippable**
(migration + API + UI + tests + docs, per `CLAUDE.md` step 3). Do not start a slice
until the previous one's definition of done (doc 09 §9) is met.

Sizing is relative (S ≈ 1–2 days, M ≈ 3–5 days, L ≈ 1–2 weeks) for one focused engineer
with AI assistance. They are planning aids, not commitments.

**Blocking answers required before slice 1:** Q-01, Q-02, Q-04, Q-06 (doc 00).

---

## Slice 0 — Foundation *(size: M)*

**Goal:** an empty but real system: it builds, runs, tests, and proves the tenancy
machinery before a single business row exists.

**Scope**
- Solution structure: `apps/api` (modular monolith, module folders matching this
  document's slices), `apps/web`, `services/ai`, `database/migrations`.
- Podman Compose: PostgreSQL 16 + pgvector, API, web, AI service, Ollama, MailHog (dev
  SMTP/IMAP). **No published port for PostgreSQL or the AI service** (SEC-69).
- Migration tooling; the `app` / `migrator` / `readonly_reporting` roles (SEC-100).
- `tenants`, `users`, `tenant_memberships`, `tenant_settings`, `audit_events` tables
  with RLS enabled and forced.
- The tenant-context middleware: token → `app.tenant_id` via `set_config(..., true)`.
- The **schema-isolation test harness** (T-70) and the no-GUC / pool-leakage tests
  (T-72, T-73) — written now so every later table is checked automatically.
- CI: format, build, test, coverage, dependency vulnerability + license check, OpenAPI
  snapshot, secret scanning.
- i18n scaffolding: `ar.json`/`en.json`, RTL-aware Tailwind config, the missing-key
  build failure (UI-11), the physical-property lint rule (UI-20).
- `THIRD-PARTY-NOTICES.md` populated for every dependency added here (PRD-26).

**Acceptance**
- `podman compose up` yields a working stack from a clean clone, documented in README.
- A connection without `app.tenant_id` returns zero rows from a seeded table (T-72).
- CI is green and fails deliberately when a test table is added without RLS.
- A backup + **restore drill** is executed and documented (PRD-23, T-150).

**Out of scope:** any business entity, any UI beyond a shell.

---

## Slice 1 — Organization & Auth *(size: L)*

**Goal:** real users, real tenants, real sessions, isolation proven end-to-end.

**Scope:** registration (user + tenant + Owner atomically) · email verification ·
login with Argon2id · TOTP MFA for Owner/Admin · refresh rotation with reuse detection ·
password reset · invitations · membership and role management · tenant switching ·
organization profile and settings · holiday calendar · permission model
(role→permission table, `[RequiresPermission]` with the boot-time assertion, SEC-10) ·
audit log write path and viewer · the auth and settings screens (doc 06 §6.1–6.2).

**Acceptance criteria** (each becomes a test name)
1. A registered user is Owner of exactly one new tenant, with `one_owner_per_tenant` enforced.
2. Every endpoint has 401 / 403 / 404-cross-tenant tests (SEC-14, T-40).
3. An endpoint without a declared permission prevents application startup (T-61).
4. Refresh-token reuse revokes the family and alerts (T-85).
5. Owner/Admin cannot complete login without TOTP once enrolled.
6. Role changes take effect within 60 s and are audited (SEC-08).
7. The last Owner cannot be demoted or deactivated.
8. Audit rows are append-only; `UPDATE`/`DELETE` raise; the hash chain verifies (T-87).
9. E2E T-121 passes in both locales.
10. The permission matrix in doc 01 §5.1 is the tested source of truth (T-60).

**Dependencies:** slice 0. **Risks:** MFA and email-verification flows always cost more
than estimated; MailHog keeps this self-contained.

---

## Slice 2 — Customers *(size: M)*

**Goal:** the party you chase, searchable in Arabic, without duplicates.

**Scope:** `customers`, `customer_contacts` (+ composite FKs, RLS) · Arabic name
normalization and the `pg_trgm` index (DM-20) · CRUD · contacts with one primary ·
duplicate detection and merge (two-step, audited, DM-21) · customer list and detail
screens with the per-currency balance blocks scaffolded (all zero until slice 3) ·
statement-of-account screen shell.

**Acceptance**
1. Searching `شركه الامل` finds `شركة الأمل` and vice versa; `Al Amal` finds it too.
2. Duplicate detection surfaces the pair above with a similarity score.
3. Merge moves every child row in one transaction, retains the merged row, and writes a
   high-severity audit event; a merge cannot cross tenants (composite FK test).
4. A customer with an open balance cannot be soft-deleted (`422 has_open_balance`) —
   testable once slice 3 exists; until then the guard is unit-tested against a stub.
5. Arabic and English name fields each render with their own `dir`; the list falls back
   across languages with a visible marker.
6. Cross-tenant customer id → 404 (T-71).

**Dependencies:** slice 1.

---

## Slice 3 — Invoice import (+ payments, cheques, credit notes) *(size: L — the largest)*

**Goal:** real receivables in the system, and every way money can move against them.

> This slice is deliberately large because an invoice without payments cannot be
> validated: you cannot prove a balance is right if nothing can reduce it. If it must be
> split, split at **3a (import → `Open` invoices, read-only balances)** and
> **3b (payments, cheques, allocation, credit notes, withholding, write-off)** — but 3a
> alone is not shippable to a pilot tenant.

**Scope:** `invoices`, `invoice_lines`, `import_batches`, `import_rows`,
`import_mappings`, `payments`, `cheques`, `payment_allocations`,
`withholding_deductions`, `credit_notes`, `credit_note_applications`, `write_offs` ·
CSV/XLSX parsing with configurable date format and decimal separator · column mapping
with saved mappings · duplicate-file and duplicate-invoice detection · the exception
resolution flow · transactional commit · invoice lifecycle I1–I8 · the balance
recompute function and `balance_cache` with the nightly reconciliation (FIN-10) ·
allocation with FIFO proposal, row-locking, reversal · short-payment resolver ·
cheque lifecycle including bounce → reopen · withholding · credit notes · write-off with
four-eyes · the import wizard, invoice list/detail, allocation and short-payment screens
(doc 06 §6.4–6.5).

**Acceptance**
1. **Worked examples E1–E6 (doc 03 §9) pass as fixture tests** (T-20). This is the gate.
2. Property tests hold all financial invariants over randomized workloads (T-21).
3. Import is transactional: an induced mid-commit failure leaves zero invoices (T-41).
4. Re-uploading the same file is refused; the override is explicit and audited (DM-23).
5. Concurrent allocations to one invoice: exactly one wins; invariants hold (T-42).
6. No endpoint anywhere can set an invoice to `Settled` directly (SM-10, PRD-12).
7. A short payment cannot be silently left as a residual — the resolver is mandatory
   (FIN-28).
8. `balance_cache` == derived balance for every invoice after the randomized workload,
   and a corrupted cache is detected by the nightly job (T-45).
9. Write-off requires two distinct users, or explicit self-approval, recorded (T-29).
10. Every money column is `numeric(19,3)`; no float anywhere (T-23).
11. E2E T-122, T-123, T-124, T-125 pass in both locales.

**Dependencies:** slice 2. **Risks:** real customer files are messier than any fixture —
budget time for the exception taxonomy; this is where Q-02's answer changes the estimate.

---

## Slice 4 — Aging *(size: M)*

**Goal:** the report the tenant believes, which is the moment the product earns trust.

**Scope:** `fn_aging` and the as-of computation from `effective_date` (FIN-57) ·
per-currency sections · disputed column (placeholder until slice 7) · unapplied cash and
credit lines · DSO and average-days-to-pay with sample-size gating (FIN-60/61) ·
aging screen with drill-through, `asOf` picker, basis indicator, "how this is
calculated" explainer · XLSX/CSV export with RTL-correct output and formula-injection
escaping (SEC-45) · the reconciliation report endpoint.

**Acceptance**
1. Bucket boundary tests at dpd 0/1/30/31/60/61/90/91 (T-24).
2. Bucket sums equal total AR per currency for randomized data (INV-08).
3. As-of aging is reproducible after later activity (T-26).
4. Timezone boundary correctness (T-25).
5. Every cell drills through to the invoices composing it (UI-44).
6. Export opens correctly in Excel with Arabic text and RTL layout intact; a customer
   named `=cmd|…` exports as a literal (T-80).
7. P95 < 800 ms at 50k invoices (T-140).
8. Zero-state and single-currency states render correctly.

**Dependencies:** slice 3.

---

## Slice 5 — Collection queue & cases *(size: L)*

**Goal:** the daily loop — a ranked list of who to chase, and a place to record what
happened.

**Scope:** `collection_cases`, `case_invoices`, `case_activities` · the daily
case-creation job (C1) with grace days · the case state machine C1–C11 · priority
scoring with stored `weights_version` and the factor breakdown (FIN-80) · assignment ·
snooze/hold · escalation with the permanent-automation-stop semantics (SM-26) ·
queue and case-detail screens with the merged timeline and keyboard navigation
(doc 06 §6.7).

**Acceptance**
1. Exhaustive case transition matrix, legal and illegal (T-10).
2. One non-terminal case per customer, enforced by the partial index (INV-06).
3. Suppression works: snoozed, on-hold and promise-covered cases leave the queue and
   return on schedule; the job is idempotent (T-13).
4. Escalation permanently disables automated sending for the case, asserted at the API
   level, not just the UI (SM-26).
5. Priority score is deterministic and reproducible from `weights_version` (T-31).
6. The factor breakdown shown in the UI matches the computed contributions.
7. Settling all invoices resolves the case automatically (C10, SM-50).
8. Queue P95 < 800 ms; keyboard-only operation passes; both locales (T-133).

**Dependencies:** slice 4.

---

## Slice 6 — Promise-to-Pay *(size: M)*

**Scope:** `promises_to_pay`, `ptp_invoices` · PTP state machine · the nightly
evaluation job (SM-33/34/35) including the cheque-clearance rule · supersession
(SM-36) · reliability metric with denominator (SM-37) · auto-PTP from a post-dated
cheque (SM-51) · promises screens (doc 06 §6.8).

**Acceptance**
1. Kept / partially kept / broken at threshold boundaries, including the exact-threshold
   case (T-30).
2. A post-dated cheque creates an `Active` PTP; clearance → `Kept`; bounce → `Broken`
   plus case reopen (E2, T-125).
3. A promise exceeding the covered balance is rejected (SM-32).
4. Recording an overlapping promise cancels the prior one as `superseded` (SM-36).
5. There is **no** API path to set `Kept`/`Broken` manually.
6. Reliability never displays a bare percentage on a sample below 3 (SM-37).
7. E2E T-126 passes in both locales.

**Dependencies:** slice 5.

---

## Slice 7 — Dispute *(size: M)*

**Scope:** `disputes` · dispute state machine · closed reason-code set · SLA clocks with
the `PendingCustomer` pause (SM-48) · resolution creating a credit note atomically
(SM-45) · payment-verification tasks (SM-44) · dunning suppression (SM-25) · dispute
screens and the verification task queue (doc 06 §6.9).

**Acceptance**
1. Exhaustive dispute transition matrix (T-10).
2. Accept/partially-accept creates the credit note in the same transaction; a forced
   failure rolls back both (SM-45).
3. Accepted amount cannot exceed open balance (SM-46).
4. An open dispute blocks sends: API returns `dispute_blocks_send` **and** the UI
   disables the control (T-47, T-127).
5. `Collector` cannot resolve a dispute (403); `Accountant` can (T-60).
6. SLA breach appears in the queue; `PendingCustomer` pauses the resolution clock.
7. `already_paid` creates a payment-verification task and **never** changes invoice
   status (SM-44, T-130).
8. E2E T-127 passes in both locales.

**Dependencies:** slice 5 (and 3 for credit notes).

---

## Slice 8 — Email templates & sending *(size: L)*

**Scope:** `message_templates`, `messages` · template versioning · the closed placeholder
set and rendering · bilingual seeded system templates for the dunning cadence (before
due, due today, +7, +30, +60, +90, PTP confirmation, PTP reminder, dispute
acknowledgement, thank-you-for-payment) — **written in Arabic and English independently**
(UI-13) · outbound queue with retry · SMTP per tenant with write-only secrets · bounce
handling · all send guards server-side · approval workflow (PRD-15) · WhatsApp
click-to-chat link generation (ADR-0004) · the outbound kill switch (SEC-103) ·
per-tenant send cap and anomaly alert (SEC-86) · template, compose, and message-history
screens (doc 06 §6.10).

**Acceptance**
1. Each send guard returns its specific error code and is enforced server-side (T-47).
2. A body referencing another customer's invoice is rejected (SEC-81, T-48).
3. The sent body is frozen; editing the template afterwards does not change history
   (DM-25).
4. Approval is required when configured, the first message to a customer always requires
   it, and the approver is recorded (AI-61 applies from slice 9).
5. SMTP credentials are never returned by any endpoint; changing them requires
   re-authentication (SEC-09, SEC-67).
6. WhatsApp produces only a `wa.me` link; **no dependency on any unofficial WhatsApp
   library exists in the lockfiles** — asserted by a test that greps the dependency
   manifests (`CLAUDE.md`).
7. The kill switch halts all sending within one job cycle (T-152).
8. Arabic templates render RTL correctly in the preview and in a real email client
   (manual check documented, plus a visual-regression snapshot).
9. E2E T-128 passes in both locales.

**Dependencies:** slices 5–7. **Risks:** email deliverability depends on Q-05.

---

## Slice 9 — Local AI integration: reply classification *(size: L)*

**Scope:** the FastAPI service with `classify_customer_reply` and `extract_promise` ·
Ollama + Qwen3 4B pinned by digest · strict schema validation both sides (AI-04) ·
redaction projection (AI-30/31) · prompt-injection framing (AI-20..AI-27) · confidence
thresholding · `ai_suggestions` with full provenance · `inbound_messages` + the IMAP
poller and customer matching · the suggestion review UI (doc 06 §6.10) · manual
classification fallback · `/ai/health` and degraded-mode banners · the evaluation corpus
and harness (doc 09 §5) · the AI settings screen.

**Acceptance**
1. **Evaluation gates T-93 … T-104 pass**, and the report is committed to
   `docs/decisions/`.
2. **Injection suite T-110 passes with zero state changes** from injected text.
3. `payment_claimed` creates a payment-verification task in 100% of cases and never
   changes an invoice (T-96, T-130).
4. `promise_to_pay` creates a `Proposed` PTP only; the `Active` transition always
   records a human `confirmedBy` (SM-31, INV-13).
5. Schema-invalid output after one repair retry is handed to a human, never used
   (AI-04).
6. With Ollama stopped, every non-AI endpoint and screen works; the UI shows the
   degraded banner (T-49, PRD-28).
7. No prompt body, customer PII, or secret appears in application logs (T-82, SEC-41).
8. `ai_suggestions` records model name, digest, prompt version, input hash, output,
   confidence, and the human decision for every call (SEC-56).
9. E2E T-129 passes in both locales.

**Dependencies:** slice 8. **Risks:** the highest-uncertainty slice. If gates fail, ship
with the operation disabled by default (T-105) and iterate on prompts — the product
still works. Q-06 and Q-07 directly affect this slice's feasibility.

---

## Slice 10 — Daily briefing *(size: M)*

**Scope:** metric computation in C# · `daily_briefings` · the LangGraph briefing flow ·
the **numeric-fidelity guard** (AI-81) · per-language generation (AI-83) · scheduled
precomputation before `briefing_send_at` · email delivery of the briefing · the Today
screen (doc 06 §6.7).

**Acceptance**
1. Every metric is computed in C#; a test asserts the AI service receives only
   pre-computed values (FIN-62, AI-80).
2. A narrative containing an untraceable numeral is discarded and logged as
   `rejected_by_guard`; `narrative` is null and the screen still renders (T-103, API-22).
3. Arabic and English briefings are generated independently and are both idiomatic
   (reviewed by a native speaker before release, recorded).
4. Briefings are precomputed; the Today screen loads without waiting for the model.
5. Historical briefings are immutable.
6. E2E T-131 passes, including the AI-down path.

**Dependencies:** slice 9.

---

## After v1 — explicitly deferred

Do not start any of these until the acceptance tests above pass (`CLAUDE.md` → First
Product): PDF/scan ingestion with PaddleOCR + Docling (A-23) · bank statement import and
auto-matching · customer self-service portal · WhatsApp Business Platform (when paid) ·
semantic search over history with pgvector (DM-26) · payment plans as first-class
objects · credit limits and holds · multi-entity tenants · and **any second module**
(Expenses, Reconciliation, Cash Flow, Budget-vs-Actual).

---

## Suggested sequencing note

Slices 0–4 produce a tenant-ready **AR ledger view with a trustworthy aging report** —
that alone is worth putting in front of a pilot user for feedback, and it is the natural
first demo. Slices 5–8 produce the **collections loop**, which is the actual product.
Slices 9–10 add the **assistant**. If time pressure appears, cut scope from 9–10 first;
never from 3 or 4, where correctness lives.
