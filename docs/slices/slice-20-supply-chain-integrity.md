# Slice 20 — Supply-chain integrity: the whole dependency tree, the AI subject trigger, CI-side alerts

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: SEC-68 / PRD-26 (licence check; slice 16 flagged "transitive licences are not enumerated") · `CLAUDE.md`
→ Cost Rules · INV-05 / doc 04 §3.3 (slice 9 flagged `ai_suggestions.subject_id` has no foreign key because the subject
is polymorphic) · SEC-102 (slice 16 flagged "the nightly job does not page").

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `check-notices.py --transitive`: every package in the three lock files with the licence read from the installed metadata — **copyleft anywhere in the tree fails** (GPL/AGPL/LGPL/SSPL/BUSL/EUPL/CC-BY-NC…); weak copyleft (MPL, EPL, CDDL) passes only with a **Flagged** row in the notices; a package with no recognisable licence fails until it is added to `KNOWN` with the licence read from the package. `--inventory` writes the full table; CI uploads it as an artifact (90 days). |
| S2 | Migration 0015: a trigger on `ai_suggestions` refuses a row whose `subject_type`/`subject_id` does not name an existing row of the same tenant (`inbound_messages`, `collection_cases`) or the tenant itself — the composite key the polymorphic column could not have. |
| S3 | The nightly performance job and the restore drill in CI call `infrastructure/alert.sh` on failure through the repository's `ALERT_WEBHOOK_URL` secret (when set). The operator scripts now load `.env` **without overriding the environment** (`infrastructure/load-env.sh`), so an injected secret survives an empty `.env` line. |

## 2. Findings recorded

- The whole tree today: 252 installed packages of 317 in the lock files (the rest are optional platform binaries for
  other OSes); **no copyleft**; one weak-copyleft family — `lightningcss` (MPL-2.0), a build-time CSS transformer under
  `vite`/`@tailwindcss/vite`, never bundled into the served SPA — now a Flagged row in the notices.
- `xunit.abstractions` carries no SPDX expression (a licence URL); recorded as Apache-2.0 from that file in `KNOWN`.

## 3. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | `--transitive` passes on the repository; `--self-test` proves copyleft-in-tree, unflagged-MPL and uninstalled-optional cases | CI `notices` job |
| AC-02 | A suggestion in tenant B whose subject is A's inbound message, a non-existent message, A's case, or A's tenant is refused with `ai_suggestions_subject_exists`; the product's own writes are unaffected | `AiIsolationTests.CrossTenantInbound_IsRejected` (extended); the full suite |
| AC-03 | `load-env.sh` keeps an environment value over an `.env` line; the drill and backup still run | local run recorded in the review |

## 4. Endpoints, with declared permission

None added. Route pin stays **147**.

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | A trigger, not three nullable foreign-key columns | The subject is one column by design (doc 04); a trigger enforces the same "same tenant, exists" rule without reshaping every reader. It runs as the app role under RLS, so a cross-tenant subject is invisible to it — refused. |
| D-2 | The inventory is an artifact, not a committed file | It changes on every dependency bump; the notices file is the reviewed record, the inventory is evidence. |
| D-3 | Weak copyleft needs a Flagged row even when transitive | The rule is the same as for direct dependencies; `lightningcss` got its row the day it was found. |
