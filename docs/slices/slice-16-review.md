# Slice 16 — CI gates: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test, script or file that makes it so.
Risk tier: **low for the product, high for the process** — nothing here runs in production; everything here decides
what may reach `main`.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**No new endpoints.** `AddOpenApi()` registers the document generator only; `MapOpenApi()` is never called, so the
document is not served. Route pin stays 147 and `OpenApiSnapshotTests.EveryOperation_DeclaresItsAccess` proves the
document's anonymous set is exactly the middleware's six.

## 2. Every new table?

None. No migration.

## 3. Code paths before a tenant / full authentication, or across tenants by design?

None in the product. The CI jobs read the repository (`gitleaks`, `check-notices.py`) and the installed package
metadata; neither touches a database or a tenant.

## 4. Money-related fields or calculations?

None.

## 5. AI-touching code?

None.

## 6. New dependencies?

- `Microsoft.AspNetCore.OpenApi` 10.0.12 (MIT) — **had been referenced since slice 1 and never recorded**; the new
  checker found it on its first run. Recorded now. `Microsoft.OpenApi` arrives with it transitively (MIT).
- `gitleaks` 8.24.3 (MIT) — a CI tool downloaded from a pinned release with a checksum, and an optional local hook;
  not linked into or shipped with the product. Recorded under test tooling.
- No npm or Python package added; `check-notices.py` is stdlib only.

## What the gates actually catch

| Gate | Catches | Proof |
|------|---------|-------|
| OpenAPI snapshot | a new/removed route, a changed route template or parameter, a changed request body type, a changed permission or access level, MFA/re-auth metadata | `Document_MatchesTheCheckedInSnapshot` (diff on mismatch, `UPDATE_OPENAPI=1` to accept) |
| gitleaks | any secret in any commit of the history; staged secrets locally | clean run over 30 commits; a planted GitHub token and a private key detected (AC-03, 2026-09-11) |
| notices check | a direct dependency (NuGet, npm, pip, container image) absent from the notices; a declared licence outside the allowlist; MPL-2.0 without a **Flagged** row | `--self-test` runs all three failure modes in CI before the real check |
| nightly | aging and queue P95 at 50k rows; invoice scans that are not tenant-scoped | `Aging_P95_Under800ms_At50k` (267 ms locally), `Queue_P95_Under800ms` (34 ms); T-141 assertions |

## Flagged, in one place

- **Response shapes are not in the OpenAPI document.** Handlers return `IResult`, so every operation shows
  `200 OK` without a schema; the snapshot pins routes, parameters, request bodies and access, not response types.
  Typed results (`Results<Ok<T>, …>`) would close this across ~140 handlers — a slice of its own.
- **T-141's "no sequential scan" is conditional.** With one 50k tenant in the test database, the aging read covers
  91 % of the table and a tenant-filtered sequential scan is the planner's correct choice; the test requires an
  index scan only when the tenant is under 20 % of the table, and always for the selective sweep query. A
  three-tenant seed (as T-140 literally says) would make the aging case index-bound too; deferred with the seeding cost.
- **Coverage thresholds** (doc 10 slice 0 "coverage") are still not enforced; `coverlet` collects, nobody reads.
- **The nightly job does not page** — a failure is on the Actions page only. `alert.sh` could be called from the
  workflow once a CI-side `ALERT_WEBHOOK_URL` secret exists.
- **Transitive licences are not enumerated** — the notices record direct dependencies and the transitive packages
  that matter (certifi); the checker reads only direct metadata. `dotnet list package --include-transitive` and
  `npm ls --all` could feed it later.
- **The pre-commit hook is opt-in** (`git config core.hooksPath .githooks`); git offers no way to install it for a
  clone automatically. CI is the backstop.
