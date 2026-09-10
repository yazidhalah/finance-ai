# finance-ai

Bilingual (Arabic/English) AI financial operations platform for Jordanian SMEs.

**First and only product in scope:** the AI Accounts Receivable & Collections Assistant.
No other module is designed or built until this one passes its acceptance tests
(see `CLAUDE.md`).

## Current phase

**Requirements and architecture.** No application code is written yet. The full
specification lives under [`/docs`](docs/README.md) and is awaiting review and approval.

Start here: **[docs/README.md](docs/README.md)** — the index and the recommended reading
order.

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
apps/web          React + TypeScript + Vite + Tailwind + shadcn/ui
apps/worker       Scheduled jobs (case creation, PTP evaluation, invariants, briefings)
services/ai       Python + FastAPI, Qwen3 via Ollama; prompts, schemas, evaluations
database          Migrations and seed data (PostgreSQL 16 + pgvector)
infrastructure    Podman Compose and deployment
tests             unit · integration · e2e · security · ai-evaluation
docs              The specification (this is the current deliverable)
```

## Ground rules

- No paid APIs, no proprietary production runtime dependencies. Every dependency's
  license is recorded in `THIRD-PARTY-NOTICES.md` in the commit that adds it.
- All monetary math in C# `decimal`; LLMs never compute authoritative totals.
- Every business table carries `TenantId`; isolation is enforced server-side.
- See `CLAUDE.md` for the full engineering contract.
