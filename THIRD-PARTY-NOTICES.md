# Third-Party Notices

Every dependency and model this product uses is recorded here, in the same commit that adds it
(`CLAUDE.md` → Cost Rules, PRD-26, SEC-68).

**Policy.** Permissive licenses only — MIT, Apache-2.0, BSD, PostgreSQL License, SIL OFL. No paid
API, no proprietary production runtime dependency, ever. A dependency under a copyleft or
source-available license is a blocking review finding, not a discussion.

Versions are pinned; lockfiles (`package-lock.json`, `packages.lock.json`) are committed.

Last updated: slice 11 — v1 acceptance (Playwright for the E2E suite, test-only). Slice 10 added no dependency (LangGraph deliberately not adopted, slice 10 D-1). Slice 9 added the Python AI service and its dependency tree, the Qwen3 model weights and the Ollama runtime; see the flagged `certifi` entry.

---

## Backend — .NET 10 (`apps/api`)

| Package | Version | License | Why it is here |
|---------|---------|---------|----------------|
| `Npgsql` | 10.0.3 | PostgreSQL License | PostgreSQL driver. |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 | PostgreSQL License | EF Core provider for PostgreSQL. |
| `Microsoft.EntityFrameworkCore` | 10.0.4 | MIT | ORM; supplies the global query filter that is isolation layer 1 (ADR-0001). |
| `Konscious.Security.Cryptography.Argon2` | 1.3.1 | MIT | Argon2id password hashing at the parameters SEC-01 mandates. |
| `Konscious.Security.Cryptography.Blake2` | 1.1.1 | MIT | Transitive dependency of the above (Argon2 is built on BLAKE2). |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.19.2 | MIT | Issues the RS256 access token of SEC-03. |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.12 | MIT | Validates it. |

ASP.NET Core, the .NET runtime and the base class libraries ship with the .NET 10 SDK
(pinned in `global.json`) and are licensed **MIT** by Microsoft.

## Frontend — React + Vite (`apps/web`)

| Package | Version | License |
|---------|---------|---------|
| `react` | 19.3.0 | MIT |
| `react-dom` | 19.3.0 | MIT |
| `vite` | 8.3.0 | MIT |
| `@vitejs/plugin-react` | 6.1.1 | MIT |
| `tailwindcss` | 4.3.3 | MIT |
| `@tailwindcss/vite` | 4.3.3 | MIT |
| `typescript` | 7.0.2 | Apache-2.0 |

### Fonts

| Font | License | Note |
|------|---------|------|
| IBM Plex Sans Arabic / IBM Plex Sans | SIL Open Font License 1.1 | UI-25. Referenced by family name in the CSS font stack with a full system fallback; **not bundled and not fetched from a third-party CDN**, so no runtime dependency on an external host. Bundling the files (and shipping their OFL notice alongside) is a task for the first slice that needs guaranteed glyph coverage. |

## Test tooling

| Package | Version | License | Scope |
|---------|---------|---------|-------|
| `xunit` | 2.9.3 | Apache-2.0 | .NET tests |
| `xunit.runner.visualstudio` | 3.1.4 | Apache-2.0 | .NET tests |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | .NET tests |
| `coverlet.collector` | 6.0.4 | MIT | .NET coverage |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | MIT | Hosts the real API in-process |
| `vitest` | 5.0.0 | MIT | Web tests |
| `jsdom` | 30.0.1 | MIT | Web tests |
| `@testing-library/react` | 16.3.3 | MIT | Web tests |
| `@testing-library/jest-dom` | 7.0.1 | MIT | Web tests |
| `@testing-library/user-event` | 14.6.7 | MIT | Web tests |
| `@types/react` | 19.3.0 | MIT | Types only |
| `@types/react-dom` | 19.3.0 | MIT | Types only |
| `@types/node` | 22.20.2 | MIT | Types only |
| `@playwright/test` | 1.63.0 | Apache-2.0 | E2E journeys (`tests/e2e`, slice 11); pulls `playwright` and `playwright-core` (Apache-2.0) and `undici-types` (MIT). Downloads Chromium headless shell (BSD-3-Clause, Chromium) into the user cache at install time — a test browser, never shipped. |

Test-only dependencies do not ship, but they are recorded: a license that forbids commercial use
would still be a problem in CI.

## PostgreSQL extensions

Bundled with PostgreSQL (contrib), enabled by the bootstrap script. No separate download.

| Extension | License | Why |
|-----------|---------|-----|
| `citext` | PostgreSQL License | Case-insensitive email columns (slice 1). |
| `pg_trgm` | PostgreSQL License | Arabic-aware customer search and duplicate detection, DM-20 (slice 2). |
| `btree_gin` | PostgreSQL License | Lets the trigram index lead with `tenant_id`, doc 04 §7 (slice 2). |

## Infrastructure (container images)

| Image | Version | License |
|-------|---------|---------|
| `docker.io/pgvector/pgvector` | pg16 | PostgreSQL License (PostgreSQL); PostgreSQL License (pgvector extension) |
| `docker.io/axllent/mailpit` | latest | MIT — development SMTP/IMAP only; never deployed |

## AI service — Python (`services/ai`)

Direct dependencies are pinned in `services/ai/requirements.txt`; the transitive tree below is what
`pip` resolved on Python 3.14 (slice 9). Nothing here ships to a browser or a customer.

| Package | Version | License | Scope |
|---------|---------|---------|-------|
| `fastapi` | 0.141.1 | MIT | HTTP framework for the internal AI endpoints |
| `starlette` | 1.6.0 | BSD-3-Clause | via fastapi |
| `uvicorn` | 0.52.4 | BSD-3-Clause | ASGI server, bound to 127.0.0.1 |
| `httpx` | 0.28.1 | BSD-3-Clause | The only outbound call: Ollama on the local network |
| `httpcore` | 1.0.9 | BSD-3-Clause | via httpx |
| `h11` | 0.16.0 | MIT | via httpcore |
| `anyio` | 4.15.1 | MIT | via starlette / httpx |
| `sniffio` | — | MIT / Apache-2.0 | via anyio (not installed separately on 3.14; listed for completeness) |
| `idna` | 3.19 | BSD-3-Clause | via httpx |
| `certifi` | 2026.7.22 | **MPL-2.0** — see note | via httpx: Mozilla's CA bundle for TLS. **Flagged:** MPL-2.0 is file-scoped weak copyleft, not on the permissive list. Used unmodified, never linked into our code, and the service makes no TLS connection (Ollama is plain HTTP on localhost). Left for the review to accept or to replace httpx. |
| `jsonschema` | 4.26.0 | MIT | Validates every request and response against `schemas/` (AI-04) |
| `jsonschema-specifications` | 2025.9.1 | MIT | via jsonschema |
| `referencing` | 0.37.0 | MIT | via jsonschema |
| `rpds-py` | 2026.6.3 | MIT | via jsonschema |
| `attrs` | 26.1.0 | MIT | via jsonschema |
| `pydantic` | 2.13.5 | MIT | Pulled by fastapi; the service's own validation is `jsonschema` |
| `pydantic_core` | 2.46.5 | MIT | via pydantic |
| `annotated-types` | 0.8.0 | MIT | via pydantic |
| `typing_extensions` | 4.16.0 | PSF-2.0 | via pydantic |
| `typing-inspection` | 0.4.4 | MIT | via pydantic |
| `click` | 8.5.0 | BSD-3-Clause | via uvicorn |
| `pytest` | 9.1.1 | MIT | Tests only |
| `pluggy` | 1.6.0 | MIT | via pytest |
| `iniconfig` | 2.3.0 | MIT | via pytest |
| `packaging` | 26.3 | Apache-2.0 OR BSD-2-Clause | via pytest |
| `Pygments` | 2.21.0 | BSD-2-Clause | via pytest |

## AI models and runtime

| Component | Version / digest | License | Notes |
|-----------|------------------|---------|-------|
| Qwen3 4B (open weights, GGUF Q4_K_M as packaged by Ollama) | `qwen3:4b`, digest `359d7dd4bcda…` | Apache-2.0 | The only model in the product. Pinned by digest in every `ai_suggestions` row (AI-06, AI-112). Re-evaluated on every prompt or model change (doc 09 §5). |
| Ollama | 0.34.0 | MIT | Local inference runtime on 127.0.0.1:11434; never published on the host network. |

No hosted model, no paid inference API, no telemetry to a model vendor.

## Not used, deliberately

Recorded so the decision is not re-litigated:

- **No Claude API or Claude Agent SDK** in production code (`CLAUDE.md` → Cost Rules).
- **No unofficial WhatsApp library** of any kind (SEC-85, ADR-0004): click-to-chat only.
- **No paid API** anywhere in the runtime path.
- **No shadcn/ui generator** in slice 1 — the four primitives this slice needs are hand-rolled to
  the same API surface, so nothing is recorded here for components that are not used (slice doc D-5).
- **No routing library** in slice 1 — one authenticated destination does not justify one.
- **No assertion or mocking library** beyond what xUnit and Vitest provide.
