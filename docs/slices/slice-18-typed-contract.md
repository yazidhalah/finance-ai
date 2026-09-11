# Slice 18 — The typed contract: response shapes and failure responses in the OpenAPI document

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: API-14 / T-51 (a contract change fails the build) · doc 05 §0.2 (one problem shape for every failure)
· FIN-02 (money is a string on the wire) · slice 16 review, which flagged: *"Response shapes are not in the OpenAPI
document — every operation shows `200 OK` without a schema."*

The one rule this slice exists to keep: **the document says what every operation returns, on success and on failure,
so a change to either is a reviewed change.**

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | Every endpoint mapping declares its success response — `.Produces<T>(200|201|202)`, `.Produces(204)`, or a binary media type for the two downloads — beside the permission it already declares. The two responses that were anonymous objects (`/cases/{id}/timeline`, `/templates/placeholders`) become named records with the same JSON. |
| S2 | The document transformer adds one `Problem` component (RFC 9457 + `code`, `messageKey`, `traceId`, `errors[]` with `meta`) and, per operation, the failures it can produce: `401` unless anonymous, `403` when permissioned, `404` when the route has an id, `400`/`422` for bodies, `429` always — all `application/problem+json`. |
| S3 | `OpenApiSnapshotTests.EveryOperation_DocumentsItsResponses` fails for any operation without a typed 2xx (or 204/binary), without its 401/404 where due, or with a 4xx that is not the problem shape. |

**Deferred:** typed handler return types (`Results<Ok<T>, …>`), which would let the compiler check the declaration
against the code — the declaration is metadata beside the mapping, verified by the same integration suite that
exercises every route; `Created` `Location` headers and per-endpoint 409/423 cases are not enumerated.

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | Every operation in `docs/api/openapi.json` has a 2xx with a schema (or 204 / a binary media type), 401 unless anonymous, 404 when addressed by id, and only problem-shaped 4xx responses; `Problem` is a component | `EveryOperation_DocumentsItsResponses` |
| AC-02 | No property in the document is a JSON `number`; `MoneyDto.amount` is a string | asserted in the review (grep over the snapshot: zero `"type": "number"`) |
| AC-03 | The snapshot is deterministic across runs and unchanged in routes, parameters and access (pin 147; anonymous set six) | the existing snapshot and access tests |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Metadata on the mapping, not typed results in ~140 handlers | Same information in the document, one line per route beside the permission, no change to any handler's behaviour or to the error paths (`ApiProblems`). The tests that call every route are what keep the declaration honest. |
| D-2 | Failure responses come from the transformer, not per endpoint | They follow from facts the middleware already knows (anonymous, permissioned, id in the route, has a body); enumerating them by hand would drift. |
