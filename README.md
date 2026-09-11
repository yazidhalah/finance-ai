# finance-ai

Bilingual (Arabic/English) AI financial operations platform for Jordanian SMEs.

**First and only product in scope:** the AI Accounts Receivable & Collections Assistant.
No other module is designed or built until this one passes its acceptance tests
(see `CLAUDE.md`).

## Current phase

**Building, from the approved specification.** Slices 1 (Organization & Authentication),
2 (Customers), 3a (Invoice Import), 3b (Payments & Allocation), 4 (Aging), 5 (Collection Queue),
6 (Promise-to-Pay), 7 (Disputes) and 8 (Email Templates & Reminders) are implemented and tested; see
`docs/slices/`, starting with
[`docs/slices/slice-01-organization-and-auth.md`](docs/slices/slice-01-organization-and-auth.md)
for its acceptance criteria, what it deliberately defers, and what its tests found.

The specification under [`/docs`](docs/README.md) remains the source of truth. Start there:
**[docs/README.md](docs/README.md)** — the index and the recommended reading order.

## Running it locally

Prerequisites: .NET 10 SDK (pinned in `global.json`), Node 20+, Podman.

```bash
cp .env.example .env          # then set the passwords and generate a signing key:
#   openssl rand -base64 24                                   -> POSTGRES_*_PASSWORD
#   openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 | base64 -w0
#                                                             -> JWT_SIGNING_KEY_PEM_BASE64
#   openssl rand -base64 32                                   -> MFA_KEK_BASE64 (TOTP secrets at rest, slice 13)

podman compose -f infrastructure/compose.yml up -d            # PostgreSQL 16 + pgvector, Mailpit

dotnet run --project apps/api/FinanceAi.Migrator -- up        # least-privilege roles, then migrations
dotnet run --project apps/api/FinanceAi.Api                   # http://127.0.0.1:5080
npm --prefix apps/web install && npm --prefix apps/web run dev # http://127.0.0.1:5173

# Local AI (slice 9): Ollama with Qwen3 4B, and the FastAPI service on 127.0.0.1:8090.
ollama pull qwen3:4b                                          # Apache-2.0 weights, ~2.5 GB
python3 -m venv services/ai/.venv && services/ai/.venv/bin/pip install -r services/ai/requirements.txt
services/ai/run.sh                                            # needs AI_SERVICE_TOKEN in .env (openssl rand -hex 24)
```

Without the AI service the product still works: replies wait in the inbox and are labelled by hand;
`/ai/health` drives the degraded banner.

`.env` is git-ignored and no credential is ever written into source (SEC-67). The migrator's
`bootstrap` step is the only thing that uses the administrative connection; the application connects
as `finance_app`, which owns nothing and cannot bypass row-level security.

## Running the tests

```bash
dotnet format --verify-no-changes     # formatting
dotnet build --configuration Release  # build
dotnet test  --configuration Release  # unit + integration + tenant-isolation, against real PostgreSQL
npm --prefix apps/web run test        # web units: i18n parity, RTL, permission-filtered navigation
npm --prefix tests/e2e test           # Playwright journeys T-121…T-132 in en and ar (see tests/e2e/README.md)
infrastructure/restore-drill.sh       # backup → restore into a fresh database → row counts match (T-150, PRD-23)
services/ai/.venv/bin/python -m pytest -c services/ai/pytest.ini   # the AI service, with a fake model
AI_LIVE_TESTS=1 services/ai/.venv/bin/python -m pytest -c services/ai/pytest.ini   # + the injection corpus against Ollama (slow)
```

The .NET suites provision their own throwaway database per run, apply the real migrations to it, and
drop it afterwards. Every test creates its own tenants (T-04).

| Area | Document |
|------|----------|
| What I assumed and what I need answered | [docs/assumptions/00-assumptions-and-open-questions.md](docs/assumptions/00-assumptions-and-open-questions.md) |
| Personas, roles, permissions | [docs/product/01-product-requirements.md](docs/product/01-product-requirements.md) |
| State machines | [docs/product/02-state-machines.md](docs/product/02-state-machines.md) |
| Financial rules and invariants | [docs/product/03-financial-rules.md](docs/product/03-financial-rules.md) |
| Data model | [docs/architecture/04-data-model.md](docs/architecture/04-data-model.md) |
| API contracts | [docs/architecture/05-api-contracts.md](docs/architecture/05-api-contracts.md) |
| Screens and RTL behaviour | [docs/product/06-frontend-screens.md](docs/product/06-frontend-screens.md) |
| AI schemas | [docs/architecture/07-ai-service-contracts.md](docs/architecture/07-ai-service-contracts.md) |
| Security and tenancy | [docs/security/08-security-and-tenancy.md](docs/security/08-security-and-tenancy.md) |
| Test strategy | [docs/architecture/09-test-strategy.md](docs/architecture/09-test-strategy.md) |
| Implementation backlog | [docs/product/10-implementation-backlog.md](docs/product/10-implementation-backlog.md) |
| Decisions | [docs/decisions/](docs/decisions/) |

## Repository layout

```
apps/api          ASP.NET Core modular monolith (.NET 10, pinned in global.json)
  FinanceAi.Domain          entities, roles, the permission catalogue
  FinanceAi.Infrastructure  EF Core, tenant scope, Argon2id, tokens, audit, migration runner
  FinanceAi.Api             endpoints, authorization, middleware, structured logging
  FinanceAi.Migrator        the migration job — the only thing that performs DDL
apps/web          React + TypeScript + Vite + Tailwind (bilingual, RTL-first)
apps/worker       Scheduled jobs (case creation, PTP evaluation, invariants, briefings) — not yet built
services/ai       Python + FastAPI, Qwen3 via Ollama; prompts, schemas, evaluations (classify_customer_reply, daily_briefing)
database          bootstrap (roles) and forward-only SQL migrations (PostgreSQL 16 + pgvector)
infrastructure    Podman Compose and deployment
tests             unit · integration · security · e2e (Playwright, en + ar) · support; the AI evaluation lives in services/ai/evaluations
docs              The specification, plus a per-slice acceptance record under docs/slices
```

## Ground rules

- No paid APIs, no proprietary production runtime dependencies. Every dependency's
  license is recorded in `THIRD-PARTY-NOTICES.md` in the commit that adds it.
- All monetary math in C# `decimal`; LLMs never compute authoritative totals.
- Every business table carries `TenantId`; isolation is enforced server-side.
- See `CLAUDE.md` for the full engineering contract.
