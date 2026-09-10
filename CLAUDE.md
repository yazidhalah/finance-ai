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
Requirements and architecture only. Do not write application code until the
documents under /docs are reviewed and approved.
