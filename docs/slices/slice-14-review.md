# Slice 14 — Deployment: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test, script or file that makes it so.
Risk tier: **high** — this slice decides what is reachable from the network and where secrets live.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**No new API endpoints.** Route pin stays 143 (`EndpointAuthorizationSweepTests`); the anonymous set is unchanged
at six. Two things that look like endpoints and are not:

| Surface | Status | Why |
|---------|--------|-----|
| `GET /health` (API root) | pre-existing, anonymous, `ExcludeFromDescription` | Called by the container health check (`dotnet FinanceAi.Api.dll --health`) from inside the container. nginx proxies only `/api/`, so it is not reachable from the published port — `smoke.sh` reaches the API only through `/api/v1/...`. |
| `GET /healthz` (nginx) | static `200 ok` from nginx itself | Answers the `web` health check; touches no application code. |

The `--health` mode in `Program.cs` runs before the host builder and issues one loopback GET; it cannot be reached
over the network and starts nothing.

## 2. Every new table: `tenant_id`, RLS enabled and forced, a policy, composite FKs?

**No new tables and no migration.** The migrator runs unchanged inside the `migrate` container under the same
`finance_migrator` credentials; the API connects as `finance_app` as before (SEC-100). `TenantIsolationTests` and the
rest of the suite are the same 643 · 192 · 38 as slice 13 (unit · integration · security) and pass against the
same schema.

## 3. Code paths before a tenant / full authentication, or across tenants by design?

Three new pre-authentication surfaces, none of them application logic:

| Path | Comparable time and disclosure? |
|------|--------------------------------|
| nginx `/api/` proxy | Forwards every request identically; adds `X-Request-Id` and `X-Forwarded-*`. It neither inspects bodies nor short-circuits on paths other than `/healthz`. |
| nginx static serving + SPA fallback | Any unknown path returns `index.html` with 200 — nothing about the application's state is disclosed by a path. |
| `Program.cs --health` | One GET to `/health`, exit 0/1. No data, no credentials, loopback only. |

The `smoke.sh` register → login flow exercises the real anonymous endpoints through the proxy; their uniform
behaviour (slice 1, 12, 13 reviews) is unchanged because nothing in them changed. **No branch returns early before a
cryptographic comparison** — this slice contains no cryptographic comparison.

Verification beyond the scripts: the built SPA was driven through nginx in headless Chromium (register via the
API, sign in through the form, Arabic locale, navigate to Customers, Today and Security): `dir="rtl"`, zero console
errors except the expected `401` from the pre-sign-in silent refresh, and **no CSP violation** under
`script-src 'self'; style-src 'self'`.

## 4. Money-related fields or calculations?

None. No code in this slice touches an amount.

## 5. AI-touching code?

The compose wiring only. `ai` receives `AI_SERVICE_TOKEN` from `.env` and is reachable solely from `api` on the
compose network (`smoke.sh` step 5 fails if it publishes a port). Nothing about validation changed: the backend's
schema check, `AiPolicy`, and the human gates are exactly slice 9–10's. The AI's output still cannot finalize a
financial fact.

With the real model, on this machine on 2026-09-11: `ollama-pull` fetched `qwen3:4b` (2.5 GB at ~20 MB/s) into
Ollama's model store and exited 0; `ai` then answered `/ready` with the pinned digest `359d7dd4bcda…`. Recorded
here because CI runs the fake model and never sees this path.

## 6. New dependencies?

Recorded in `THIRD-PARTY-NOTICES.md` in this commit:

| Dependency | License | Note |
|-----------|---------|------|
| `mcr.microsoft.com/dotnet/sdk:10.0`, `dotnet/aspnet:10.0` | MIT (.NET) on Debian 12 | build stage / runtime |
| `node:22-alpine` | MIT | build stage only |
| `nginxinc/nginx-unprivileged:1.27-alpine` | BSD-2-Clause | web runtime |
| `python:3.13-slim` | PSF-2.0 | AI runtime |
| `ollama/ollama:0.34.0` | MIT | already listed as the runtime; now also the image |
| `caddy:2-alpine` | Apache-2.0 | optional `tls` profile |
| `pip-audit` | Apache-2.0 | CI only |
| `podman-compose` 1.5.0 | **GPL-2.0** — a tool the CI runner installs, on the same footing as `bash`; nothing of it is linked into or shipped with the product. Flagged for the reviewer to accept the reasoning. | CI only |

No NuGet, npm or Python **runtime** package was added. `libgssapi-krb5-2` (MIT-style, Debian) is installed into
the API image because Npgsql probes it and logs an error without it — an OS package, not a product dependency.

## Flagged, in one place

- **`MFA_KEK_BASE64` cannot be rotated in place** (runbook §4). Ciphertexts carry no key id. A rotation means
  re-enrolment. Carried from slice 13; now written into the runbook as the procedure.
- **No dual-key window for `JWT_SIGNING_KEY_PEM_BASE64`**: rotation invalidates every access token at once (a
  15-minute blip for signed-in users, who refresh transparently). Acceptable at pilot scale; flagged.
- **`podman-compose` is GPL-2.0** — tool, not dependency (§6). If the reviewer disagrees, the compose file is
  standard and runs under Docker Compose unchanged.
- **Alerting (SEC-102, T-153) still has no path.** The runbook names what is page-worthy; nothing pages.
- **Logs are stdout only.** SEC-101's local store and 90-day retention are the operator's log driver for now.
- **`postgres` binds `127.0.0.1:5432` on the host** so `backup.sh` and the drill work from the host. SEC-69 forbids
  a *published* port for PostgreSQL; loopback is not the network, but a reviewer may prefer `podman exec pg_dump`
  and no binding at all — a one-line change in `compose.yml`.
- **The CI stack job uses the fake model.** The real-model path (`ollama-pull` → `ai` ready) was verified locally
  (§5), not in CI, because a 2.5 GB pull per run is not reasonable on a shared runner.
- **Visual snapshots and performance tests** remain local-only, as recorded in slices 11 and 13.
