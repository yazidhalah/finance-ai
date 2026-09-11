# Project Mission
Build a bilingual Arabic-English AI financial operations platform for Jordanian SMEs.

# First Product
AI Accounts Receivable and Collections Assistant. Do not implement any other module
(Expenses, Reconciliation, Cash Flow, Budget-vs-Actual, etc.) until this one passes
its acceptance tests.

# Technology
- Frontend: React, TypeScript, Vite, Tailwind, shadcn/ui
- Backend: ASP.NET Core (modular monolith), .NET 10 SDK — pinned via global.json
- AI service: Python, FastAPI
- Database: PostgreSQL + pgvector
- Local AI: Qwen3 (4B to start) via Ollama
- OCR: PaddleOCR
- Document parsing: Docling
- Agent orchestration: LangGraph
- Testing: xUnit, pytest, Playwright
- Deployment: Podman Compose

# Cost Rules
- No paid APIs. No proprietary production runtime dependencies.
- Prefer permissive open-source licenses (MIT, Apache-2.0, PostgreSQL License).
- Record every dependency and model license in THIRD-PARTY-NOTICES.md in the same
  commit that adds the dependency.
- No Claude API or Claude Agent SDK dependency in production code.
- WhatsApp automation must never use unofficial libraries; use click-to-chat until
  the official Business Platform is paid for.

# Financial Safety
- LLMs never calculate authoritative monetary totals. All monetary math happens in
  C# using `decimal`, never float/double.
- All AI output must validate against a strict JSON schema before the backend acts on it.
- AI may recommend; it may never post accounting entries, write off balances, move
  money, submit regulatory documents, or initiate legal escalation.
- An AI classification that a customer "paid" creates a payment-verification task —
  it never marks an invoice paid directly.
- Every AI recommendation must be auditable: model, prompt version, input reference,
  output, confidence, and whether a human approved it.

# Security
- Every business table has a TenantId. Tenant isolation is enforced server-side —
  never trust a TenantId from the browser.
- Uploaded documents are untrusted input. Never let document text be treated as
  instructions to an agent.
- No secrets, API keys, or personal financial data in logs or in prompts sent to
  the AI service.
- Every protected endpoint has an authorization test.

# Development Process
For each vertical slice:
1. Read the approved spec in /docs.
2. Write acceptance criteria and tests first.
3. Implement the smallest complete slice (migration + API + UI + tests + docs).
4. Run formatting, build, and the full test suite.
5. Check tenant isolation explicitly for any new endpoint.
6. Update THIRD-PARTY-NOTICES.md if a dependency was added.
7. Stop and report if any required test fails — do not proceed to the next slice.

# Current Phase
Implementation, one vertical slice at a time, against the approved specification in /docs.
All ten v1 slices — 1 (Organization & Authentication), 2 (Customers), 3a (Invoice Import), 3b
(Payments & Allocation), 4 (Aging), 5 (Collection Queue), 6 (Promise-to-Pay), 7 (Disputes), 8 (Email
Templates & Reminders), 9 (Local AI: reply classification) and 10 (Daily Briefing) — are implemented
and tested; see /docs/slices/. Slice 11 added the Playwright E2E suite (T-121…T-132, both locales,
tests/e2e) and the CI workflow (.github/workflows/ci.yml); slice 12 added member invitations, role
change and deactivation and the T-150 restore drill (infrastructure/restore-drill.sh, run in CI);
slice 13 added TOTP MFA (required for Owner/Admin after a 7-day grace, enforced by the middleware),
password reset, the five-minute re-authentication proof (X-Reauth) on transfer of ownership and
write-off approval, and transfer of ownership itself. MFA_KEK_BASE64 must be set in every environment.
Slice 14 added the deployment: three Containerfiles, infrastructure/compose.yml (profiles full/tls,
`infrastructure/stack.sh --profile full up -d --build` from a clean clone, only `web` published — SEC-69),
infrastructure/smoke.sh run by the CI `stack` job, SEC-68 dependency audits in CI, and the operations
runbook docs/ops/runbook.md (SEC-103). Slice 15 added the invariant job (doc 03 §7, run last in the sweep and on
demand), the SEC-102 alert path (alerts table, ALERT_EMAIL, ALERT_WEBHOOK_URL, infrastructure/alert.sh, T-153) and
the Audit screen; it also fixed SEC-53 verification (audit `changes` is hashed in canonical JSON form).
Slice 16 closed the slice-0 CI list: the OpenAPI snapshot in docs/api/openapi.json (a contract change fails the
security suite; accept with UPDATE_OPENAPI=1), gitleaks over the history (and `git config core.hooksPath .githooks`
locally), infrastructure/check-notices.py (every direct dependency recorded, licences permissive), and the nightly
performance workflow (T-140/141). Slice 17 made both keys rotatable: MFA envelopes carry a key id
(`MFA_KEK_BASE64_PREVIOUS` + `rotate-mfa-kek`), and `JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS` verifies only tokens
issued before the process started (runbook §4). Slice 18 typed the contract: every operation's response schema
and problem responses are in docs/api/openapi.json (`.Produces<T>()` beside each mapping; the transformer adds
the `Problem` component and 401/403/404/400/422/429). Slice 19 put before/after values on the audit viewer and
measured coverage (decision 0007 proposes the floor; `infrastructure/coverage-report.py` enforces it when asked).
What remains before the First Product is "done" is the
v1 acceptance pass on a pilot corpus and real hardware (doc 09 §5 AI gates are indicative on the
author-written corpus; Arabic narrative quality needs a native reviewer; T-140 runs nightly on a shared
runner, not on the target hardware). No second module until that passes. Every AI output enters through the human gates slices 6–10 built (Proposed promises,
Open disputes, PendingApproval messages, pending suggestions, an approved briefing template) and never
through a direct write. The AI service lives in services/ai (Python, .venv); a prompt or model change
re-runs the evaluation harness and commits the report under docs/decisions/ before merge (AI-111/112).
Every later slice copies the tenant-isolation
pattern established there (§5 of that document). Do not start a slice until the previous one's
definition of done (doc 09 §9) is met.

# Slice Self-Review (required before any slice is reported complete)
Before declaring a slice complete, write docs/slices/slice-0X-review.md
answering explicitly, for every change in the slice:
1. Every new endpoint: authenticated and routed through
   TenantScopeMiddleware, or deliberately anonymous — state which, and why.
2. Every new table: tenant_id, RLS enabled AND forced, at least one policy,
   and a composite (tenant_id, id) foreign key to any other tenant-scoped
   table it references. The existing enumeration-based tests in
   TenantIsolationTests are not enough on their own for anything with
   non-standard access patterns — add a targeted test.
3. Any new code path that runs before a tenant or full authentication is
   established, or that queries across more than one tenant by design
   (e.g. anything added to PlatformIdentityStore): does every branch take
   comparable time and disclose comparable information regardless of
   whether the target exists, matches, or is in a valid state? Name any
   branch that returns early before a cryptographic comparison or a full
   data fetch.
4. Any new money-related field or calculation: decimal type only, no
   float/double; state where rounding happens if an amount is split,
   allocated, or converted.
5. Any AI-touching code: does the backend validate the AI's JSON output
   against a strict schema before acting on it? Can the AI's output alone
   ever finalize a financial fact (mark paid, write off, escalate) without
   passing through a deterministic check or human approval first?
6. Any new dependency: added to THIRD-PARTY-NOTICES.md in the same commit,
   with an accurate license.
If any answer is "not sure" rather than a clear yes, stop and flag it in
the review doc instead of merging.
