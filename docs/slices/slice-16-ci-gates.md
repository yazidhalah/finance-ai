# Slice 16 — The remaining CI gates: OpenAPI snapshot, secret scanning, license check, nightly performance

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 10 slice 0 ("CI: format, build, test, coverage, dependency vulnerability + license check, OpenAPI
snapshot, secret scanning") · API-14 / T-51 (OpenAPI 3.1 generated from the implementation, diffed against a
checked-in snapshot; an unreviewed contract change fails the build) · SEC-67 (a pre-commit secret scanner runs in
CI) · SEC-68 / PRD-26 (every dependency's license recorded in `THIRD-PARTY-NOTICES.md`; a license check in CI) ·
doc 09 §4.4 · T-140 / T-141 (the seeded performance dataset; query-plan assertions) · slice 14 (vulnerability audits
already in CI).

The one rule this slice exists to keep: **a change to the API contract, a secret in a commit, an unrecorded
dependency, or a slow query cannot reach `main` unnoticed.**

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **OpenAPI snapshot** (API-14, T-51): the document is generated in-process from the real endpoint metadata (`Microsoft.AspNetCore.OpenApi`'s `IOpenApiDocumentProvider`; no route is mapped — the document is a build artefact, not a public endpoint) and compared with `docs/api/openapi.json`. A difference fails the security suite with the diff and the one-line way to accept it (`UPDATE_OPENAPI=1`). Every route carries its permission (`x-permission`) or its access level (`x-access`), so a change to *who may call what* is a contract change too. |
| S2 | **Secret scanning** (SEC-67): gitleaks over the full history in CI (pinned version, checksum-verified download) with `.gitleaks.toml` allowlisting only what is known non-secret (`.env.example` placeholders, the test password, fixture hashes); a `.githooks/pre-commit` hook runs `gitleaks protect --staged` locally when gitleaks is installed, enabled with one `git config core.hooksPath .githooks`. |
| S3 | **License and notices check** (SEC-68, PRD-26): `infrastructure/check-notices.py` reads every direct dependency — NuGet `PackageReference`s, `package.json` dependencies (web and e2e), `requirements.txt`, the container images in the Containerfiles and compose — and fails when one is missing from `THIRD-PARTY-NOTICES.md`, or when its declared license (from the installed package metadata) is outside the permissive allowlist and the notices do not mark it **Flagged**. Runs in CI after the installs. |
| S4 | **Nightly performance** (T-140, T-141): `.github/workflows/nightly.yml` on a schedule and by hand runs the `Performance` category (aging and queue P95 at 50k invoices) and the new query-plan assertions: the aging function and the queue query use the `(tenant_id, status, due_date)` index — no sequential scan on `invoices` at 50k rows. |

**Deferred:** coverage thresholds (doc 10 lists "coverage"; `coverlet` is already collected — a threshold needs a
baseline the team agrees on; flagged) · T-142 (a sustained load profile needs a host that is not a shared runner) ·
classification P95 (needs the real model; measured by the evaluation harness) · import-of-5,000-rows timing (runs
in the nightly job as an assertion only if the seeded import path is exercised — it is not yet; flagged).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **Is the OpenAPI document served?** | No. `AddOpenApi()` registers the generator; nothing calls `MapOpenApi()`. The route pin stays 147 and the anonymous set stays six. | Doc 05 lists no `/openapi` route; the document is for review, not discovery. |
| **What makes the snapshot deterministic?** | Paths, operations, parameters and schemas are emitted in the generator's stable order; the test serialises with indentation and compares the exact text. `x-permission` / `x-access` are added by a document transformer from the same metadata the middleware enforces. | T-51 needs a diff a reviewer can read. |
| **What does gitleaks scan?** | The whole git history on every run (`detect` on the repository), not only the diff — a secret committed and later removed is still a leak. | SEC-67. |
| **What is a "recorded" dependency?** | Its package id (or image name) appears in `THIRD-PARTY-NOTICES.md`. Transitive packages are not required individually; the notices say so where a transitive licence matters (certifi). | PRD-26 as practised since slice 1. |
| **What licences pass?** | MIT, Apache-2.0, BSD-2/3-Clause, ISC, 0BSD, PostgreSQL, PSF-2.0 / Python-2.0, Unlicense, CC0-1.0, BlueOak-1.0.0, MPL-2.0 only when the notices mark the package **Flagged**. Anything else, or an unknown licence, fails. | `CLAUDE.md` → Cost Rules. |
| **Does the nightly job block merges?** | No — it is a schedule, not a PR check; a failure is visible on the Actions page and in the runbook's alert list as a page-worthy item once the webhook exists for CI (flagged). | T-140 at 50k rows takes minutes; PR CI stays fast. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test / check |
|----|-----------|------|--------------|
| AC-01 | The generated OpenAPI document equals `docs/api/openapi.json`; changing an endpoint's route, permission, parameter or response type changes the document and fails the test | API-14, T-51 | `OpenApiSnapshotTests.Document_MatchesTheCheckedInSnapshot` |
| AC-02 | Every operation in the document carries `x-permission` or `x-access`; the anonymous set equals the middleware's anonymous set | SEC-10 | `OpenApiSnapshotTests.EveryOperation_DeclaresItsAccess` |
| AC-03 | gitleaks finds nothing in the repository history; a planted key in a scratch commit is detected (proved once locally, recorded in the review) | SEC-67 | CI job `secrets` |
| AC-04 | `check-notices.py` passes on the repository; removing a line from the notices makes it fail naming the package; a dependency with a GPL licence fails | PRD-26, SEC-68 | CI job `notices` + a self-test mode (`--self-test`) |
| AC-05 | The nightly workflow runs the Performance category and the query-plan assertions on a schedule and by hand | T-140, T-141 | `.github/workflows/nightly.yml`; `QueryPlanTests` |

---

## 4. Endpoints, with declared permission

None added. Route pin stays **147**.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | The snapshot is a test, not a build step | It runs where the app already runs in-process (the security suite), needs no extra tooling, and fails with a diff. |
| D-2 | gitleaks from a pinned release with a checksum, not the marketplace action | The action needs a licence key for organization repositories; the binary is MIT and one `curl`. |
| D-3 | The notices checker is Python without dependencies | It runs in every job that has the installs; the AI service already requires Python. |
| D-4 | Performance is nightly, not per PR | 50k-row seeding on a shared runner is minutes; PR feedback should stay under fifteen. |
