# Slice 14 — Deployment: acceptance criteria and test plan

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: A-15 (single-region, self-hosted via Podman Compose) · doc 10 slice 0 ("`podman compose up` yields a
working stack from a clean clone, documented in README"; "no published port for PostgreSQL or the AI service") ·
SEC-60, SEC-61 (TLS, security headers, CSP without `unsafe-inline`) · SEC-67 (secrets by environment) · SEC-68
(vulnerability audits in CI) · SEC-69 (container hardening and published ports) · SEC-101 (structured logs) ·
SEC-103 (the incident-response runbook) · AI-101 (the AI service on the internal network only) · PRD-23 / T-150
(backups and the drill, slice 12) · `CLAUDE.md` → Technology: "Deployment: Podman Compose".

The one rule this slice exists to keep: **from a clean clone and a filled-in `.env`, one command produces the
product with the same isolation the code assumes — nothing but the web tier reachable, no secret in an image.**

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Images** built from the repository: `apps/api/Containerfile` (SDK build stage with `dotnet restore --locked-mode`; ASP.NET runtime; one image, `migrate` entrypoint for the migrator), `apps/web/Containerfile` (Vite build → `nginx-unprivileged` serving `dist/` with the SPA fallback and the `/api` proxy), `services/ai/Containerfile` (`python:3.13-slim`, uvicorn on 8090). All three run as a non-root uid; `api` and `ai` on a read-only root filesystem with `/tmp` as tmpfs; `cap_drop: ALL`, `no-new-privileges`, memory limits (SEC-69). |
| S2 | **`infrastructure/compose.yml`** with profiles: default (PostgreSQL + Mailpit — the dev stack the README already documents, unchanged for developers), `full` (+ `migrate` one-shot → `api` → `web`, `ai`, `ollama` + `ollama-pull` one-shot for `AI_MODEL`), `tls` (+ Caddy, automatic HTTPS for `DOMAIN`). Health checks on every long-running service; `api` waits for `migrate` to exit 0; `web` waits for `api` healthy. |
| S3 | **Ports**: `web` is the only published service (`WEB_PORT`, default 8080). PostgreSQL binds to `127.0.0.1` on the host for `backup.sh` and the restore drill; `api`, `ai`, `ollama` publish nothing (SEC-69). |
| S4 | **Configuration**: the repo `.env` is the single source (`env_file`), with the container-network overrides (`POSTGRES_HOST=postgres`, `AI_SERVICE_URL=http://ai:8090`, `OLLAMA_URL=http://ollama:11434`) in the compose file. `infrastructure/stack.sh` runs Podman Compose with that `.env` from any directory. No secret is baked into an image (`.containerignore` excludes `.env`). |
| S5 | **Web tier**: nginx adds the SEC-61 headers (CSP with `script-src 'self'; style-src 'self'` — no `unsafe-inline`, `frame-ancestors 'none'`, nosniff, DENY, strict referrer, minimal Permissions-Policy), proxies `/api/` to `api:5080` with `X-Forwarded-*` and a 12 MB body cap for imports, re-resolves `api` through the network DNS so a recreated API container is picked up, caches hashed assets immutably. |
| S6 | **Production logging**: the API's content root is its own directory so `appsettings.json` applies — ASP.NET, EF Core at Warning, Data Protection at Error (it is explicitly ephemeral: nothing in the product uses it); one JSON line per event with `request_id`, redacted (SEC-101). |
| S7 | **CI** (`.github/workflows/ci.yml`): `dotnet list package --vulnerable`, `npm audit --audit-level=high`, `pip-audit` (SEC-68) in the existing jobs; a new `stack` job builds the three images with Podman, starts the `full` profile with the CI overlay (`compose.ci.yml`: `AI_FAKE_MODEL=1`, no Ollama) and runs `infrastructure/smoke.sh`. |
| S8 | **Runbook** `docs/ops/runbook.md` (SEC-103): who is called; the global and tenant kill switches in one action each; revoking one member's, one user's, or everyone's sessions; rotating each key (and the honest limitation on `MFA_KEK_BASE64`); backup and restore; symptom → first look. |

**Deferred, and to which slice:** GPU passthrough for Ollama (a commented `devices:` line; Q-06) · log shipping
and retention (SEC-101 "shipped to a local store, retained 90 days" — the stack writes JSON to stdout; the
operator's log driver is the store for now) · alerting (SEC-102, T-153 — no alert path exists yet; flagged since
slice 12) · a dual-key grace window for signing-key rotation · an image registry and signed images (the stack
builds from the clone) · edge rate limiting per IP (SEC-70 — the API's own limiter applies; Caddy adds none).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **What is reachable from outside?** | `web:8080` (or Caddy's 80/443). Nothing else. PostgreSQL on the host loopback only. | SEC-69, doc 10 slice 0. `smoke.sh` step 5 fails if `api`, `ai` or `ollama` publish anything, or if PostgreSQL publishes off loopback. |
| **Where do secrets live?** | `.env` on the host, injected as environment; never in an image layer, never in compose `environment:` literals, never in a log line (the JSON logger redacts; §6). | SEC-67. |
| **Does the SPA need `unsafe-inline`?** | No. Vite emits module scripts and a CSS file; React styles go through the CSSOM. Verified by driving the built SPA through nginx in headless Chromium: sign-in, RTL navigation, three screens, zero CSP violations. | SEC-61. |
| **Why is the refresh cookie `Secure` when the stack speaks HTTP?** | Because browsers treat `localhost` as a secure context and every real deployment sits behind TLS (`tls` profile or the operator's edge). The stack never downgrades the cookie to make plain HTTP work on a public host. | SEC-04, SEC-60. |
| **Who runs migrations?** | `migrate` — the same image as the API, `FinanceAi.Migrator up` under the migrator credentials — as a one-shot the API depends on. The API never migrates. | SEC-100. |
| **What if the model is not there yet?** | `ai` starts anyway, `/health` is 200, `/ready` is 503; the API reports `reachable: true, ready: false` and the product marks AI unavailable rather than failing. `ollama-pull` fetches `AI_MODEL` into the `ollama-models` volume once. | PRD-28, AI-11. |
| **Can the kill switch be flipped without a redeploy?** | Yes, twice over: the global `OUTBOUND_SENDING_ENABLED` needs only the `api` container recreated (`up -d --no-build api`, seconds); the tenant switch is an API call. | SEC-103. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Verified by |
|----|-----------|------|-------------|
| AC-01 | The three images build from a clean checkout with no network access to anything but the package registries and base images; `--locked-mode` restore fails on a lockfile drift | SEC-68 | CI `stack` job (`up --build`); local `podman build` ×3 |
| AC-02 | `stack.sh --profile full up -d` from a clean clone with a filled `.env` reaches: `migrate` exited 0, `postgres`/`ai`/`api`/`web` healthy | doc 10 slice 0 | `smoke.sh` step 1 + the CI job's `ps`; locally on 2026-09-11 |
| AC-03 | `GET /` on the published port serves the SPA with the SEC-61 headers and a CSP without `unsafe-inline`; deep links fall back to `index.html` | SEC-61 | `smoke.sh` step 2 |
| AC-04 | `/api` through nginx reaches the API and the database: register → login → `GET /organization` returns the organization | doc 10 | `smoke.sh` step 3 |
| AC-05 | The API reaches the AI service on the compose network with the shared token: `GET /ai/health` reports `reachable: true` | AI-101 | `smoke.sh` step 4 |
| AC-06 | Only `web` publishes a port; PostgreSQL only on `127.0.0.1`; `api`/`ai`/`ollama` publish nothing | SEC-69 | `smoke.sh` step 5 |
| AC-07 | Containers run as non-root; `api` and `ai` on a read-only rootfs with all capabilities dropped and memory limits | SEC-69 | compose file; `podman exec api id` = 10001; the API serves under `read_only: true` (smoke passes) |
| AC-08 | The built SPA works under the CSP: sign-in, Arabic RTL navigation, Customers, Today, Security screens with no console CSP violation | SEC-61, UI | headless-Chromium walk (recorded in the review, §3) |
| AC-09 | A recreated `api` container is reachable through nginx without restarting `web` | ops | `podman rm -f api` + `up -d api` → login probe 401 through the proxy |
| AC-10 | Production logs: one JSON line per event, no per-request ASP.NET or EF chatter, no Data Protection warnings; secrets absent | SEC-101 | container logs after a smoke run: 4 lifetime lines + application events |
| AC-11 | Known-vulnerable dependencies fail CI in all three ecosystems | SEC-68 | the three audit steps (all clean on 2026-09-11) |
| AC-12 | The runbook's actions exist: the global switch is honoured on every send (`KillSwitch_AndQuietHours_HoldTheMail`), the tenant switch is an endpoint, deactivation revokes refresh tokens (`RoleChange_AndDeactivate_Guards_AndCutAccess`, slice 12), password reset revokes them (slice 13), key rotation is one env change | SEC-103 | existing tests named in the runbook; the runbook itself |
| AC-13 | With the real model: `ollama-pull` fetches `qwen3:4b` into the volume and `ai` reports `ready` with the pinned digest | AI-112 | local run on 2026-09-11 (`docs/slices/slice-14-review.md` §5) |

---

## 4. Endpoints, with declared permission

No new API endpoints. `GET /health` (anonymous, root, outside `/api/v1`) already existed and is what the container
health check calls; nginx only proxies `/api/`, so `/health` is not reachable from outside. Route pin stays **143**.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | nginx proxies `/api` rather than the API being published beside the SPA | One origin: the refresh cookie is `SameSite=Strict` (SEC-63) and CORS stays a non-question. The API still validates `WEB_ORIGINS`. |
| D-2 | One API image for the migrator and the API | The migrator is the same build; a second image doubles the surface for nothing. The entrypoint switches on `migrate`. |
| D-3 | `ai` does not depend on `ollama-pull` | A 2.5 GB pull must not hold the product's start; the service's `/ready` is the truth, and the API already degrades gracefully. CI runs `ai` with `AI_FAKE_MODEL=1` and no Ollama at all. |
| D-4 | The compose project name stays `infrastructure` | Renaming it would orphan every developer's `infrastructure_postgres-data` volume. |
| D-5 | Caddy as an optional profile, not a required edge | Operators with their own TLS edge keep it; those without get automatic certificates. Either way `web` is what they front. |
| D-6 | `global.json` rolls forward to `latestFeature` | The SDK image ships 10.0.4xx; `latestPatch` refused it. The pin is still "the 10.0 SDK", enforced by the base image tag and `--locked-mode`. |
| D-7 | Data Protection is declared ephemeral | Nothing in the product uses it; a read-only container otherwise logs three key-ring warnings at each start. Saying so in `Program.cs` beats silencing the category alone. |
| D-8 | `podman-compose` is a CI tool, not a dependency | It runs the compose file; nothing of it ships. Recorded in THIRD-PARTY-NOTICES with that reasoning (GPL-2.0 tool, same footing as `bash`). |
