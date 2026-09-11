# Slice 19 — Audit values on the viewer, and the coverage baseline

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 06 §6.11 ("each row expands to show before/after values, reason code, and the AI suggestion that
influenced it, if any") — flagged open in the slice 15 review · DM-28 (`changes` as `{field: {old, new}}`, money as
strings) · doc 10 slice 0 "coverage" — the last unenforced CI item after slice 16.

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `AuditEventDto` gains `changes` (the stored JSON, verbatim), `note` and `aiSuggestionId`; `GET /audit` returns them. No new endpoint. |
| S2 | The Audit screen expands a row with recorded values into a before/after table (or a flat snapshot when the event stored one state), the note, and the suggestion id. Rows without values do not expand. |
| S3 | `infrastructure/coverage-report.py` merges coverlet's cobertura output per assembly (generated code excluded) and can enforce floors; `docs/decisions/0007-coverage-floor.md` records today's numbers; the owner accepted Domain 95 / Infrastructure 90 / Api 85 and the `api` job enforces it. |

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | After an organization update, `GET /audit?eventType=tenant.updated` returns the row with `changes.legalName.old` null and `.new` the new value | `OrganizationUpdate_IsAuditedWithTheFieldsThatChanged` (extended) |
| AC-02 | The Audit screen expands a row with changes to a table with the field, before and after; a row without values does not expand; the note is shown | `audit.test.tsx` |
| AC-03 | The OpenAPI snapshot changed only by the three new properties on `AuditEventDto`; route pin 147 | `OpenApiSnapshotTests` |
| AC-04 | `coverage-report.py` reports per-assembly line coverage and exits 1 below a floor | run recorded in decision 0007 |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | `changes` is returned as stored, not reshaped | DM-28 already fixes the shape; the viewer handles both the diff form and the flat snapshot some events write. Money stays a string. |
| D-2 | The floor was proposed first, then enforced on acceptance | A floor is a team commitment; the owner accepted 95/90/85 the same day and the `api` job now fails below it. |
