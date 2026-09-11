# 05 — API Contracts

Status: DRAFT. Contracts are grouped by **vertical slice, in build order**. Each slice
is independently shippable; later slices never break earlier contracts.

Base URL: `/api/v1`. Transport: HTTPS only, HTTP/1.1 + HTTP/2.

---

## 0. Cross-cutting conventions

### 0.1 Tenancy

**API-01** The tenant is resolved **server-side from the validated access token
claim `tid`**. There is no `tenantId` path parameter, query parameter, header, or body
field on any endpoint. A request body containing `tenantId` MUST be rejected with
`400 unexpected_field` — silently ignoring it would let a client believe it worked.

**API-02** A user belonging to several tenants selects one at login/switch time,
producing a token scoped to that tenant. Switching tenants means getting a new token.

**API-03** Requesting an entity belonging to another tenant returns **`404 not_found`**,
never `403`. `403` would confirm the resource exists (SEC-13).

### 0.2 Errors

RFC 9457 `application/problem+json`:

```json
{
  "type": "https://finance-ai/errors/invalid_transition",
  "title": "Invalid state transition",
  "status": 409,
  "code": "invalid_transition",
  "detail": "Cannot move case from Resolved to InProgress.",
  "instance": "/api/v1/cases/018f.../transitions",
  "traceId": "0af7651916cd43dd8448eb211c80319c",
  "errors": [ { "field": "amount", "code": "exceeds_open_balance",
                "messageKey": "errors.allocation.exceeds_open_balance" } ]
}
```

**API-04** `detail` is English (for logs/support). The client renders user-facing text
from `messageKey` in the user's locale. **The API never returns a user-facing Arabic or
English sentence for display** — that would put translation in the backend and
guarantee drift (doc 06 §2).

| HTTP | code | Used when |
|------|------|-----------|
| 400 | `validation_failed`, `unexpected_field` | Malformed input |
| 401 | `unauthenticated` | Missing/expired token |
| 403 | `forbidden` | Authenticated, lacks permission **in this tenant** |
| 404 | `not_found` | Missing **or belongs to another tenant** |
| 409 | `invalid_transition`, `concurrency_conflict`, `duplicate` | State/version conflicts |
| 422 | `business_rule_violated` | e.g. `exceeds_open_balance`, `currency_mismatch` |
| 429 | `rate_limited` | Throttling; `Retry-After` present |
| 503 | `ai_unavailable` | Ollama down — **never blocks non-AI endpoints** (PRD-28) |

### 0.3 Money in JSON

**API-05** Money is always an object, never a bare number:

```json
{ "amount": "1250.500", "currency": "JOD" }
```

`amount` is a **string** with exactly 3 decimals. JSON numbers are IEEE-754 doubles in
most clients and would violate FIN-01 the moment a browser parses them. Client-side
arithmetic on these strings is forbidden; totals come from the server (doc 06 §4).

### 0.4 Other conventions

| ID | Rule |
|----|------|
| API-06 | Dates: `date` as `YYYY-MM-DD`; instants as RFC 3339 UTC (`2026-09-10T07:30:00Z`). |
| API-07 | Pagination: cursor-based, `?limit=50&cursor=…`, response `{ "items": [...], "nextCursor": "…", "totalCount": 1234 }`. `totalCount` may be omitted on expensive queries. |
| API-08 | Idempotency: every POST that creates money or sends a message requires an `Idempotency-Key` header; replays return the original result. |
| API-09 | Concurrency: mutating an entity requires `If-Match: "<row_version>"`; mismatch → `409 concurrency_conflict`. |
| API-10 | All list endpoints accept `?q=` (search), `?sort=`, and filters listed per endpoint. |
| API-11 | Responses include `Content-Language`; requests carry `Accept-Language`, which selects the locale for any localized *content* (templates, briefings), not for error text (API-04). |
| API-12 | Every endpoint declares its required permission in the contract below; the implementation authorizes on the **permission**, never on role (SEC-12). |
| API-13 | Rate limits: 600 req/min/user general; 10 req/min for auth endpoints; 5 concurrent AI calls per tenant. |
| API-14 | OpenAPI 3.1 is generated from the implementation and diffed against a checked-in snapshot in CI; an unreviewed contract change fails the build. |

---

## Slice 1 — Organization & Auth

**Purpose:** a real tenant with a real Owner, real sessions, and the isolation
machinery proven end-to-end before any business data exists.

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| POST | `/auth/register` | — | Creates user + tenant + Owner membership atomically. Rate-limited, email verification required. |
| POST | `/auth/login` | — | Returns access (15 min) + refresh (14 d, rotating, httpOnly cookie). Generic failure message; constant-time. |
| POST | `/auth/refresh` | — | Rotation with reuse detection → revoke family. |
| POST | `/auth/logout` | authenticated | Revokes refresh family. |
| POST | `/auth/verify-email` | — | Token from email. |
| POST | `/auth/forgot-password`, `/auth/reset-password` | — | Always `202`, never reveals account existence. |
| POST | `/auth/mfa/enroll`, `/auth/mfa/verify` | authenticated | TOTP. Required for `Owner`/`Admin` [A-15]. |
| GET | `/auth/tenants` | authenticated | Tenants this user belongs to. |
| POST | `/auth/switch-tenant` | authenticated | Body `{ "tenantId": "…" }` → new token. **Only** place a tenant id is accepted, and only from the user's own membership list. |
| GET | `/me` | authenticated | Profile, current tenant, role, **effective permission list**. |
| PATCH | `/me` | authenticated | Name, `preferredLocale`. |
| GET/PATCH | `/organization` | `tenant.read` / `tenant.settings.write` | Name, base currency (immutable after first invoice), timezone, locale. |
| GET/PATCH | `/organization/settings` | `tenant.read` / `tenant.settings.write` | The `tenant_settings` row (doc 04 §4). |
| GET | `/organization/holidays`, POST/DELETE | `tenant.settings.write` | Holiday calendar (FIN-73). |
| GET | `/organization/members` | `users.read` | |
| POST | `/organization/members/invite` | `users.invite` | `{ email, role, locale }` → emails an invite. |
| PATCH | `/organization/members/{id}` | `users.role.write` | Change role. Cannot demote the last Owner. |
| POST | `/organization/members/{id}/deactivate` | `users.deactivate` | |
| POST | `/organization/transfer-ownership` | `tenant.transfer_ownership` | Requires re-authentication. |
| GET | `/audit` | `audit.read` | Filter by entity, actor, type, date range. |

**Example — login**

```http
POST /api/v1/auth/login
{ "email": "rana@example.jo", "password": "…", "totp": "123456" }

200 OK
{
  "accessToken": "eyJ…",
  "expiresIn": 900,
  "user": { "id": "018f…", "fullName": "Rana …", "preferredLocale": "ar-JO" },
  "tenant": { "id": "018f…", "name": "…", "baseCurrency": "JOD", "timezone": "Asia/Amman" },
  "role": "Owner",
  "permissions": ["tenant.read", "customers.write", "..."]
}
```

**Acceptance:** a user in tenant A receives `404` for every entity in tenant B; a
connection without `app.tenant_id` returns zero rows (DM-05); every endpoint above has
a 401/403/404 test (PRD-13).

---

## Slice 2 — Customers

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| GET | `/customers` | `customers.read` | `?q=` searches both Arabic and Latin names via normalized trigram (DM-20). Filters: `riskFlag`, `hasOverdue`, `currency`. |
| POST | `/customers` | `customers.write` | At least one of `nameAr`/`nameEn` required. |
| GET | `/customers/{id}` | `customers.read` | Includes per-currency balance blocks, unapplied cash, unapplied credit, open case, promise reliability (SM-37). |
| PATCH | `/customers/{id}` | `customers.write` | `If-Match` required. |
| DELETE | `/customers/{id}` | `customers.write` | Soft delete; refused (`422 has_open_balance`) if any open invoice. |
| GET | `/customers/{id}/contacts`, POST, PATCH, DELETE `/contacts/{cid}` | `customers.read`/`.write` | One primary contact enforced. |
| GET | `/customers/duplicates` | `customers.read` | Candidate duplicate pairs with a similarity score. |
| POST | `/customers/{id}/merge` | `customers.merge` | `{ "sourceCustomerId": "…", "confirmToken": "…" }`. Two-step: preview then confirm. Irreversible → audit high-severity. |
| GET | `/customers/{id}/statement` | `customers.read` | Statement of account: invoices, payments, credits, running balance, `?asOf=`, `?currency=`, `?format=json|pdf`. |

**Example — customer detail (fragment)**

```json
{
  "id": "018f…",
  "nameAr": "شركة الأمل التجارية",
  "nameEn": "Al Amal Trading Co.",
  "preferredLanguage": "ar",
  "paymentTermsDays": 30,
  "balances": [
    { "currency": "JOD",
      "openBalance":    { "amount": "12450.000", "currency": "JOD" },
      "overdueBalance": { "amount": "8200.000",  "currency": "JOD" },
      "disputedAmount": { "amount": "2000.000",  "currency": "JOD" },
      "unappliedCash":  { "amount": "0.000",     "currency": "JOD" },
      "unappliedCredit":{ "amount": "150.000",   "currency": "JOD" } }
  ],
  "promiseReliability": { "kept": 2, "total": 3, "windowMonths": 12 },
  "openCase": { "id": "018f…", "status": "PromiseActive", "priorityScore": 72 }
}
```

Note there is **no netted single balance** (FIN-15) and no cross-currency total (FIN-04).

---

## Slice 3 — Invoice import

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| POST | `/imports` | `invoices.import` | `multipart/form-data`, CSV/XLSX ≤ 10 MB. Returns batch in `Parsing`. Duplicate `fileHash` → `409 duplicate_file` with `?force=true` override (DM-23). |
| GET | `/imports/{id}` | `invoices.import` | Status, counts, progress. |
| GET | `/imports/{id}/rows` | `invoices.import` | `?outcome=Rejected|Warning|…`, paginated. Each row: raw values, parsed values, error code, proposed customer match. |
| POST | `/imports/{id}/mapping` | `invoices.import` | Column mapping, date format, decimal separator. Re-parses the batch. |
| POST | `/imports/{id}/rows/{rowId}/resolve` | `invoices.import` | Resolve one exception: `{ "action": "assign_customer" | "create_customer" | "override_duplicate" | "skip", … }`. |
| POST | `/imports/{id}/commit` | `invoices.import` | Idempotent. Accepts only rows in `Accepted`. Transactional per batch. Transitions invoices `Imported → Open` (I2). |
| POST | `/imports/{id}/cancel` | `invoices.import` | |
| GET | `/import-mappings`, POST, DELETE | `invoices.import` | Saved mappings. |
| GET | `/invoices` | `invoices.read` | Filters: `customerId`, `status`, `settlement`, `currency`, `dueBefore/After`, `overdueOnly`, `hasDispute`, `q` (number). |
| GET | `/invoices/{id}` | `invoices.read` | Includes lines, allocations, credits, disputes, PTPs, derived facets. |
| POST | `/invoices` | `invoices.write` | Manual single invoice (A-11 gap-filling only). |
| PATCH | `/invoices/{id}` | `invoices.write` | Only `dueDate`, `poReference`, `notes` are editable. **Amounts are never editable** — a wrong amount is a credit note or a void-and-reimport. |
| POST | `/invoices/{id}/void` | `invoices.void` | Guard I6 (zero financial history). |
| GET | `/invoices/{id}/audit` | `audit.read` | |

**Payments, cheques, credit notes** ship with this slice (a receivables product
without payments cannot be validated):

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| POST | `/payments` | `payments.write` | `{ customerId, amount, currency, method, receivedDate, reference, allocations?: [...] }`. `Idempotency-Key` required. |
| GET | `/payments`, `/payments/{id}` | `payments.read` | |
| POST | `/payments/{id}/allocations` | `payments.allocate` | Body: explicit lines. Validated against live balances under row lock (FIN-22). |
| GET | `/payments/{id}/allocation-proposal` | `payments.allocate` | FIFO proposal (FIN-25), skipping disputed/escalated. **Proposal only.** |
| POST | `/allocations/{id}/reverse` | `payments.allocate` | Compensating row (FIN-23), reason required. |
| POST | `/payments/{id}/reverse` | `payments.write` | Reverses payment and all allocations; may reopen invoices (I7). |
| POST | `/cheques` | `payments.write` | Post-dated cheque; optionally auto-creates an `Active` PTP (SM-51, E2). |
| POST | `/cheques/{id}/transitions` | `payments.write` | `{ "event": "deposit" | "clear" | "bounce" | "cancel", "reason": "…" }`. `clear` creates the allocation. |
| POST | `/invoices/{id}/withholding` | `payments.write` | Records a WHT deduction (FIN-29). |
| POST | `/credit-notes` | `credit_notes.write` | |
| POST | `/credit-notes/{id}/applications` | `credit_notes.write` | |
| POST | `/invoices/{id}/write-off` | `writeoff.propose` | Creates `Proposed`. |
| POST | `/write-offs/{id}/approve` | `writeoff.approve` | Four-eyes enforced (PRD-11, FIN-31). |

**Example — allocation rejected**

```http
POST /api/v1/payments/018f…/allocations
{ "lines": [ { "invoiceId": "018f…", "amount": "5000.000", "currency": "JOD" } ] }

422 Unprocessable Content
{ "code": "business_rule_violated", "status": 422,
  "errors": [ { "field": "lines[0].amount", "code": "exceeds_open_balance",
                "messageKey": "errors.allocation.exceeds_open_balance",
                "meta": { "openBalance": "3200.000", "currency": "JOD" } } ] }
```

---

## Slice 4 — Aging

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| GET | `/reports/aging` | `aging.read` | Params: `asOf` (default today, tenant tz), `basis` (`due_date`\|`issue_date`), `currency`, `customerId`, `groupBy=customer|bucket`, `includeDisputed`. |
| GET | `/reports/aging/export` | `export.run` | `?format=xlsx|csv`, localized headers, RTL-safe XLSX. |
| GET | `/reports/aging/customers/{id}` | `aging.read` | Invoice-level detail behind a bucket. |
| GET | `/reports/dso` | `aging.read` | FIN-60; returns `null` with `insufficientHistory: true` if < 90 days. |
| GET | `/reports/reconciliation` | `audit.read` | Cache-vs-derived check for the tenant (INV-09). |

**Example — aging response (fragment)**

```json
{
  "asOf": "2026-09-10",
  "basis": "due_date",
  "currencies": [
    {
      "currency": "JOD",
      "buckets": [
        { "bucket": "Current",    "amount": "18000.000", "invoiceCount": 12, "disputedAmount": "0.000" },
        { "bucket": "Days1To30",  "amount": "9400.500",  "invoiceCount": 7,  "disputedAmount": "0.000" },
        { "bucket": "Days31To60", "amount": "4200.000",  "invoiceCount": 3,  "disputedAmount": "2000.000" },
        { "bucket": "Days61To90", "amount": "0.000",     "invoiceCount": 0,  "disputedAmount": "0.000" },
        { "bucket": "Days90Plus", "amount": "15750.250", "invoiceCount": 5,  "disputedAmount": "0.000" }
      ],
      "total": { "amount": "47350.750", "currency": "JOD" },
      "unappliedCash":   { "amount": "1200.000", "currency": "JOD" },
      "unappliedCredit": { "amount": "150.000",  "currency": "JOD" }
    }
  ],
  "baseCurrencyTotal": { "amount": "51120.400", "currency": "JOD", "indicative": true }
}
```

`indicative: true` is mandatory on any converted figure (FIN-06) and the UI must
render the disclaimer.

---

## Slice 5 — Collection queue & cases

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| GET | `/queue` | `cases.read` | The day's work. Params: `assignedTo=me|all|{userId}`, `bucket`, `minAmount`, `limit`. Excludes suppressed cases (`next_action_at` in the future), `OnHold`, and quiet `Escalated`. Ordered by `priorityScore DESC`. |
| GET | `/queue/summary` | `cases.read` | Counts by status/bucket for the header chips. |
| GET | `/cases` | `cases.read` | All cases including suppressed; filters `status`, `assignedTo`, `customerId`. |
| GET | `/cases/{id}` | `cases.read` | Customer snapshot, in-scope invoices with balances, timeline, PTPs, disputes, messages, priority factor breakdown (FIN-80). |
| POST | `/cases` | `cases.write` | Manual case creation (rare; the daily job normally creates them). `409 duplicate` if one is open (INV-06). |
| POST | `/cases/{id}/transitions` | varies | `{ "event": "hold" | "resume" | "escalate" | "abandon" | "contact_logged", "reasonCode": "…", "note": "…", "holdUntil": "…" }`. `escalate`/`abandon` require `cases.escalate`. Validated against doc 02 §2.3; illegal → `409 invalid_transition`. |
| POST | `/cases/{id}/assign` | `cases.assign` | |
| POST | `/cases/{id}/activities` | `cases.write` | Log a call/note/meeting; sets `last_contact_at`. |
| GET | `/cases/{id}/timeline` | `cases.read` | Merged activities, messages, payments, transitions, AI suggestions — one chronological stream. |
| POST | `/cases/{id}/snooze` | `cases.write` | `{ "untilDate": "…", "reason": "…" }` → sets `next_action_at`. |

**Example — queue item**

```json
{
  "caseId": "018f…",
  "caseNumber": 1042,
  "customer": { "id": "018f…", "nameAr": "…", "nameEn": "…", "preferredLanguage": "ar" },
  "status": "InProgress",
  "priorityScore": 78,
  "priorityFactors": [
    { "factor": "amount",           "contribution": 30, "detail": "8200.000 JOD overdue" },
    { "factor": "days_past_due",    "contribution": 25, "detail": "62 days" },
    { "factor": "broken_promises",  "contribution": 15, "detail": "1 of 3 promises broken" },
    { "factor": "recent_contact",   "contribution": -8, "detail": "contacted 2 days ago" }
  ],
  "overdueBalance": { "amount": "8200.000", "currency": "JOD" },
  "maxDaysPastDue": 62,
  "invoiceCount": 3,
  "lastContactAt": "2026-09-08T09:12:00Z",
  "suggestedAction": { "kind": "send_reminder", "templateKey": "dunning_60", "language": "ar" }
}
```

`suggestedAction` at this slice is **rule-based**, not AI. AI enrichment arrives in
slice 9 and appears in a separate `aiSuggestion` field.

---

## Slice 6 — Promise-to-Pay

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| POST | `/cases/{id}/promises` | `ptp.write` | `{ invoiceIds, promisedAmount, promisedDate, source, notes }`. Human-created → `Active` directly, `confirmedBy` = caller. Guards SM-32, SM-36 (supersede). |
| GET | `/promises` | `cases.read` | Filters `status`, `dueBefore`, `customerId`. Powers the "promises due today" view. |
| GET | `/promises/{id}` | `cases.read` | Includes `receivedInWindow` once evaluated. |
| POST | `/promises/{id}/confirm` | `ptp.write` | `Proposed → Active`. **The only path from an AI-extracted promise to an active one** (SM-31). Body may correct amount/date; corrections are stored as `human_correction` on the suggestion (doc 09 §5.4). |
| POST | `/promises/{id}/reject` | `ptp.write` | `Proposed → Rejected`, reason required. |
| POST | `/promises/{id}/cancel` | `ptp.write` | `Active → Cancelled`, reason required. |
| GET | `/customers/{id}/promise-history` | `cases.read` | Reliability with denominator (SM-37). |

There is deliberately **no** endpoint that sets a PTP to `Kept`/`Broken` — those are
system evaluations only (SM-33).

---

## Slice 7 — Dispute

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| POST | `/invoices/{id}/disputes` | `disputes.write` | `{ reasonCode, disputedAmount, currency, customerClaim, source }`. `reasonCode` from the closed set (SM-43). `already_paid` → also creates a payment-verification task (SM-44). |
| GET | `/disputes` | `cases.read` | Filters `status`, `slaBreached`, `assignedTo`, `reasonCode`. |
| GET | `/disputes/{id}` | `cases.read` | Includes SLA clocks and evidence list. |
| POST | `/disputes/{id}/transitions` | `disputes.write` / `disputes.resolve` | `{ "event": "assign"|"request_info"|"info_received"|"withdraw"|"cancel" }` needs `disputes.write`; `{ "event": "accept"|"partially_accept"|"reject" }` needs `disputes.resolve`. |
| POST | `/disputes/{id}/resolve` | `disputes.resolve` | `{ "outcome": "accepted"|"partially_accepted"|"rejected", "resolutionAmount": {...}, "reason": "…" }`. Accept/partial creates the credit note **atomically** (SM-45); `422 exceeds_open_balance` if SM-46 fails. |
| POST | `/disputes/{id}/evidence` | `disputes.write` | Attach a file (delivery note, signed PO). Untrusted content (SEC-40). |
| GET | `/tasks/payment-verification` | `payments.read` | Tasks from `already_paid` claims and from AI "customer says paid" classifications. |
| POST | `/tasks/payment-verification/{id}/resolve` | `payments.write` | `{ "outcome": "payment_found"|"no_payment_found"|"partial", "paymentId": "…" }`. **Never marks an invoice paid directly** — it records a payment, and settlement follows (SM-10). |

---

## Slice 8 — Email templates & sending

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| GET | `/templates` | `cases.read` | Filters `channel`, `language`, `key`. |
| POST/PATCH | `/templates`, `/templates/{id}` | `templates.write` | New version on every content change (never in-place). |
| POST | `/templates/{id}/preview` | `cases.read` | `{ "caseId": "…" }` → rendered subject/body with real data, both languages. |
| GET | `/templates/placeholders` | `cases.read` | The **closed set** of allowed placeholders with types. Unknown placeholder → `422 unknown_placeholder`. |
| POST | `/cases/{id}/messages` | `messages.draft` | Compose from template or free text → `Draft`. |
| POST | `/messages/{id}/approve` | `ai.suggestions.approve` | `Draft/PendingApproval → Approved`. Freezes `body` (DM-25). |
| POST | `/messages/{id}/send` | `messages.send` | `Approved → Queued`. Guards: SM-25 (dispute), SM-26 (escalated), quiet hours, contact has an email, customer not on hold. Requires `Idempotency-Key`. |
| GET | `/messages/{id}/whatsapp-link` | `messages.draft` | Returns a `https://wa.me/<e164>?text=<urlencoded>` **click-to-chat** URL and marks the message `PreparedForManualSend`. No unofficial API, ever (`CLAUDE.md`, ADR-0004). |
| GET | `/messages` | `cases.read` | Outbound history. |
| POST | `/messages/{id}/cancel` | `messages.draft` | Only while `Draft`/`PendingApproval`/`Queued`. |
| GET/PUT | `/organization/email-settings` | `tenant.settings.write` | Tenant SMTP/IMAP (host, port, TLS, username, secret). Secrets are **write-only** (never returned). `POST /organization/email-settings/test` verifies. |
| POST | `/webhooks/email-events` | signed | Bounce/delivery events from the local MTA. Updates message status. |

**Send guards (`422` with a specific code, all tested):**
`dispute_blocks_send` · `case_escalated` · `quiet_hours` · `no_contact_email` ·
`customer_on_hold` · `approval_required` · `duplicate_send_window` (same customer,
same template, within N days).

---

## Slice 9 — Local AI integration (reply classification)

Backend ↔ AI service is an **internal** contract (doc 07 has the schemas). The public
API exposes only the results and the human gates.

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| POST | `/inbound-messages` | internal / `cases.write` | Ingested by the IMAP poller, or pasted by a user (WhatsApp). Stores raw text as **data** (SEC-40). |
| GET | `/inbound-messages` | `cases.read` | Filters `classificationStatus`, `customerId`, `unmatched`. |
| POST | `/inbound-messages/{id}/classify` | `cases.write` | Triggers classification (normally automatic). `503 ai_unavailable` if Ollama is down — the message stays in the human queue (PRD-28). |
| POST | `/inbound-messages/{id}/match-customer` | `cases.write` | Human resolves an unmatched sender. |
| GET | `/ai/suggestions` | `ai.suggestions.read` | Pending suggestions with model, prompt version, confidence, reason code. |
| GET | `/ai/suggestions/{id}` | `ai.suggestions.read` | Full provenance including the **redacted** prompt projection and output. |
| POST | `/ai/suggestions/{id}/approve` | `ai.suggestions.approve` | Applies the suggestion **through the normal state-machine endpoint**, not by direct write. Records `decidedBy`. |
| POST | `/ai/suggestions/{id}/edit-and-approve` | `ai.suggestions.approve` | Body carries the corrected values; stores `human_correction` for evaluation. |
| POST | `/ai/suggestions/{id}/reject` | `ai.suggestions.approve` | Reason code required. |
| POST | `/inbound-messages/{id}/classify-manually` | `cases.write` | Human classification when AI is unavailable or below threshold. Also becomes an evaluation label. |
| GET | `/ai/health` | `tenant.read` | Model name, digest, reachable, P95 latency, today's below-threshold rate. |
| GET/PATCH | `/organization/ai-settings` | `ai.settings.write` | `aiEnabled`, `minConfidence`, per-operation enablement, autosend never available. |

**API-20** No endpoint accepts an AI output as an authoritative value. `approve` calls
the same service method a human action would, with `aiSuggestionId` recorded as
provenance (SM-04, ADR-0003).

**API-21** `503 ai_unavailable` MUST NOT be returned by any endpoint outside this
slice's AI-specific routes and the briefing narrative.

> **Amended in slice 9 (API-20a).** As built:
> - `GET /inbound-messages/{id}` added (`cases.read`). `POST /inbound-messages` is user-facing only for
>   now (channels `email`, `whatsapp_pasted`, `manual`); the IMAP poller is deferred. A `fromAddress`
>   equal to a contact email matches the customer; `customerId` binds explicitly; `inReplyToMessageId`
>   binds through the outbound message's customer.
> - `classify` is on demand (there is no poller yet) and refuses with `409 ai_disabled` when the tenant
>   switch is off, `422 customer_required` before a match, `409 already_classified` after one. On
>   `503 ai_unavailable` the request's transaction rolls back: the message is exactly as it was.
> - `GET /ai/suggestions/{id}` returns the validated output fields (classification, reason, extracted,
>   secondary, rationale) and the provenance; the prompt projection itself is not stored (AI-32 deferred)
>   so it is not returned. Filters: `decision`, `classification`, `review`, `subjectId`.
> - `approve` on a `promise_proposed` outcome confirms the promise (`Proposed → Active`, `confirmedBy`
>   = the caller). `approve` on a promise or dispute suggestion whose values failed AI-41/AI-51 or
>   whose invoice was ambiguous answers `422 values_required`; `edit-and-approve` takes `classification`,
>   `invoiceId`, `invoiceIds`, `amount`, `promisedDate`, `disputeReasonCode`, `note`. `reject` takes a
>   free-text `reason` (no reason-code catalogue yet), withdraws a Proposed promise (`Rejected`) or
>   cancels an Open dispute, and returns the message to `Unclassified`.
> - `GET /ai/health` returns configured / reachable / ready / model / digest / prompt version /
>   `aiEnabled`; the P95 and the below-threshold rate are not computed yet.
> - `GET /organization/ai-settings` is `tenant.read`; `PATCH` is `ai.settings.write` and takes
>   `aiEnabled` and `aiMinConfidence` (0.500–1.000, three decimals). There is no per-operation
>   enablement yet (one operation exists) and, as specified, nothing that resembles autosend.

---

## Slice 10 — Daily briefing

| Method | Path | Permission | Notes |
|--------|------|-----------|-------|
| GET | `/briefings/today` | `cases.read` | `?language=ar|en`. Returns computed metrics + optional narrative. |
| GET | `/briefings/{date}` | `cases.read` | Historical, immutable. |
| POST | `/briefings/regenerate` | `ai.settings.write` | Re-runs the narrative for today (metrics are recomputed too). |
| GET/PATCH | `/organization/briefing-settings` | `tenant.settings.write` | Send time, recipients, channel, language. |

```json
{
  "date": "2026-09-10",
  "language": "ar",
  "metrics": {
    "totalOverdue":        { "amount": "47350.750", "currency": "JOD" },
    "collectedYesterday":  { "amount": "3200.000",  "currency": "JOD" },
    "promisesDueToday":    { "count": 4, "amount": { "amount": "11500.000", "currency": "JOD" } },
    "promisesBrokenYesterday": { "count": 1 },
    "newDisputes":         { "count": 2 },
    "disputesBreachingSla":{ "count": 1 },
    "queueSize":           41,
    "topCases": [ { "caseId": "018f…", "customerName": "…",
                    "amount": { "amount": "8200.000", "currency": "JOD" }, "daysPastDue": 62 } ]
  },
  "narrative": "لديك ٤ وعود دفع مستحقة اليوم …",
  "narrativeAvailable": true,
  "aiSuggestionId": "018f…"
}
```

**API-22** Every number in `metrics` is computed in C# (FIN-62). The narrative is
generated **from those already-computed values**, and a post-generation guard rejects
any narrative containing a numeral that is not present in `metrics` (doc 07 §7.3). If
the guard trips or Ollama is down, `narrative` is `null`, `narrativeAvailable` is
`false`, and the UI shows the metrics alone — the briefing is still useful (PRD-28).

---

## Appendix — Endpoint → permission matrix

Generated from the tables above and checked in CI against the implementation's
authorization attributes; a mismatch fails the build (SEC-12, doc 09 §4.1).
