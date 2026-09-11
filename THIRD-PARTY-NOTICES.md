# Third-Party Notices

Every dependency and model this product uses is recorded here, in the same commit that adds it
(`CLAUDE.md` → Cost Rules, PRD-26, SEC-68).

**Policy.** Permissive licenses only — MIT, Apache-2.0, BSD, PostgreSQL License, SIL OFL. No paid
API, no proprietary production runtime dependency, ever. A dependency under a copyleft or
source-available license is a blocking review finding, not a discussion.

Versions are pinned; lockfiles (`package-lock.json`, `packages.lock.json`) are committed.

Last updated: slice 4 — Aging (no dependency added: the XLSX/CSV export writers are in-house, like the readers).

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

## AI models

**None yet.** Slice 1 contains no AI. Qwen3 via Ollama arrives in a later slice; its model licence
(Apache-2.0 for the Qwen3 open weights) and Ollama's own licence (MIT) must be recorded here in the
commit that introduces them, together with the pinned model digest that doc 09 §5.3 requires.

## Not used, deliberately

Recorded so the decision is not re-litigated:

- **No Claude API or Claude Agent SDK** in production code (`CLAUDE.md` → Cost Rules).
- **No unofficial WhatsApp library** of any kind (SEC-85, ADR-0004): click-to-chat only.
- **No paid API** anywhere in the runtime path.
- **No shadcn/ui generator** in slice 1 — the four primitives this slice needs are hand-rolled to
  the same API surface, so nothing is recorded here for components that are not used (slice doc D-5).
- **No routing library** in slice 1 — one authenticated destination does not justify one.
- **No assertion or mocking library** beyond what xUnit and Vitest provide.
