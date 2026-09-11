# finance-ai

Bilingual (Arabic/English) AI financial operations platform for Jordanian SMEs.

**First and only product in scope:** the AI Accounts Receivable & Collections Assistant.
No other module is designed or built until this one passes its acceptance tests
(see `CLAUDE.md`).

## Current phase

**Building, from the approved specification.** Slices 1 (Organization & Authentication),
2 (Customers) and 3a (Invoice Import) are implemented and tested; see `docs/slices/`, starting with
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

podman compose -f infrastructure/compose.yml up -d            # PostgreSQL 16 + pgvector, Mailpit

dotnet run --project apps/api/FinanceAi.Migrator -- up        # least-privilege roles, then migrations
dotnet run --project apps/api/FinanceAi.Api                   # http://127.0.0.1:5080
npm --prefix apps/web install && npm --prefix apps/web run dev # http://127.0.0.1:5173
```

`.env` is git-ignored and no credential is ever written into source (SEC-67). The migrator's
`bootstrap` step is the only thing that uses the administrative connection; the application connects
as `finance_app`, which owns nothing and cannot bypass row-level security.

## Running the tests

```bash
dotnet format --verify-no-changes     # formatting
dotnet build --configuration Release  # build
dotnet test  --configuration Release  # unit + integration + tenant-isolation, against real PostgreSQL
npm --prefix apps/web run test        # web units: i18n parity, RTL, permission-filtered navigation
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
services/ai       Python + FastAPI, Qwen3 via Ollama; prompts, schemas, evaluations — not yet built
database          bootstrap (roles) and forward-only SQL migrations (PostgreSQL 16 + pgvector)
infrastructure    Podman Compose and deployment
tests             unit · integration · security · support (e2e and ai-evaluation not yet built)
docs              The specification, plus a per-slice acceptance record under docs/slices
```

## Ground rules

- No paid APIs, no proprietary production runtime dependencies. Every dependency's
  license is recorded in `THIRD-PARTY-NOTICES.md` in the commit that adds it.
- All monetary math in C# `decimal`; LLMs never compute authoritative totals.
- Every business table carries `TenantId`; isolation is enforced server-side.
- See `CLAUDE.md` for the full engineering contract.
