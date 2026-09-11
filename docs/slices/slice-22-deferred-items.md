# Slice 22 — The deferred items: typed handler results, `Location`, per-tenant alert routing

Status: **Implemented and tested.**

Source: the "flagged" sections of the slice 15, 16 and 18 reviews.

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Typed handler results.** Every handler returns `Results<Ok<T> | Created<T> | Accepted<T> | NoContent | FileContentHttpResult, ProblemHttpResult>` instead of `IResult`; the `Rule`/`Invalid`/`ParseQuery`/`ParseLines` helpers return `ProblemHttpResult`. The compiler now checks the response declaration against the code; the `.Produces<T>()` metadata of slice 18 is removed (the two binary downloads keep theirs for the media type). |
| S2 | **`Location` documented** on every 201 (the header `TypedResults.Created` already sets). |
| S3 | **Per-tenant alert routing.** `tenant_settings.alert_owner_email_enabled` (migration 0016, default off); when on, critical alerts are also emailed to the organization's active Owners — never warnings, never another tenant's. `PATCH /organization/alert-settings` (`tenant.settings.write`, audited `tenant.alert_settings_changed`); the flag is on `GET /organization/alerts`; a checkbox on the Audit screen's Alerts card. |

## 2. What the typing found

`GET /organization` answered `TypedResults.NotFound()` — a bare 404 without the problem body of doc 05 §0.2 — when the
tenant row is missing. The contract test could not see it (the snapshot showed `404` from the transformer, not from
the code); the type system did: `NotFound` is not `ProblemHttpResult`. Fixed to `ApiProblems.NotFoundProblem`.

The regenerated snapshot differs from slice 18's by exactly: the `Location` header on eighteen 201s, and
`POST /payments` now documenting its idempotent-replay `200` beside the `201` — every one of the 147 `.Produces<T>()`
declarations of slice 18 was correct.

## 3. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | The API builds with every handler typed; no `IResult` return remains in `Endpoints/` | the build; `grep` in the review |
| AC-02 | The OpenAPI snapshot is unchanged except the `Location` headers and the `POST /payments` 200 | `OpenApiSnapshotTests` (pin 148) |
| AC-03 | With the setting on, a critical alert emails the operator **and** the Owner (Mailpit shows the Owner's copy naming the alert id); a warning emails the operator only; the other tenant sees nothing; the change is audited | `OpsTests.OwnerEmail_OnCriticalAlerts_WhenEnabled` |
| AC-04 | The Alerts card shows the toggle to `tenant.settings.write` only and PATCHes on change | `audit.test.tsx` |

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| PATCH | `/organization/alert-settings` | `tenant.settings.write` |

Route pin 147 → **148**.
