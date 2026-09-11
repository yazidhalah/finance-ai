# Slice 8 — Email templates & reminders: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 01 PRD-15 · doc 02 C3, SM-25, SM-26, SM-53 · doc 04 §5.6 (`message_templates`,
`messages`), DM-25 · doc 05 slice 8 (endpoints and the seven guard codes) · doc 06 §6.10, UI-13,
UI-14 · doc 08 SEC-52, SEC-67, SEC-81, SEC-86, SEC-103 · doc 09 T-47, T-48, T-128, T-152 · doc 10
slice 8 · ADR-0004 · `CLAUDE.md` → Cost Rules (no paid email API, no WhatsApp automation).

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `message_templates` (bilingual, versioned, closed placeholder set) and `messages` (frozen body, DM-25) on the isolation pattern; two columns on `tenant_settings` for the kill switch and the daily cap |
| S2 | **Templates**: one row per language; a new version on every content change; `Draft → Approved` by a human (`templates.write`); eleven system templates seeded per tenant in Arabic and English, written independently (UI-13) |
| S3 | **Rendering**: `{{placeholder}}` from a closed set; unknown → `422 unknown_placeholder`; money rendered from `decimal` as strings; SEC-81 post-render content binding |
| S4 | **The message machine**: `Draft → PendingApproval → Approved → Queued → Sent | Failed`, `Cancelled`, `PreparedForManualSend` (WhatsApp); every transition one method, audit row, case activity (C3 on send) |
| S5 | **Approval (PRD-15)**: required when the tenant says so, on the first message to a customer, for free text, for `final`-tone templates, for AI-drafted messages (slice 9) — and the approver is always a named user |
| S6 | **Send guards** (doc 05): `dispute_blocks_send` (slice 7's guard), `case_escalated`, `quiet_hours`, `no_contact_email`, `customer_on_hold`, `approval_required`, `duplicate_send_window`, plus `outbound_disabled` (kill switch) and `send_cap_exceeded` (SEC-86) |
| S7 | **Dispatch**: an idempotent outbound worker (`POST /messages/dispatch`, also run by the daily sweep) sends `Queued` email through the SMTP host in `.env` (Mailpit locally), retries three times, respects quiet hours, the kill switch and the cap |
| S8 | **Cadence**: the sweep drafts a reminder per queue-eligible case whose oldest invoice sits on a `dunning_cadence_days` step, from the approved template for the customer's language; auto-queues only when every automatic condition holds (§2) |
| S9 | **WhatsApp click-to-chat** (ADR-0004): a `wa.me` link and the text; the user sends it and confirms; a lockfile test asserts no unofficial library |
| S10 | UI: template list/editor with placeholder picker and side-by-side languages, compose with the rendered preview and guard reasons, the approval queue, message history and a customer statement page |

**Deferred, and to which slice:** per-tenant SMTP/IMAP settings with write-only encrypted
secrets (SEC-67 KEK — the SMTP host comes from `.env` for every tenant; flagged) · bounce/delivery
webhooks (`Delivered` / `Bounced` — Mailpit emits none; the statuses exist) · inbound mail (slice
9) · `ptp_reminder` and `dispute_ack` are seeded but no automatic path sends them (the promise and
dispute flows do not draft yet) · the anomaly alert half of SEC-86 (the cap is enforced; the alert
needs a notification channel) · visual-regression snapshots of Arabic email (manual check documented).

---

## 2. Rules, stated explicitly — which paths can send without a human clicking approve

| Path | Human click | Conditions | Why it is safe |
|------|-------------|-----------|----------------|
| **A. Compose → approve → send** | Two: `approve` (by `ai.suggestions.approve`) and `send` (by `messages.send`) | Always available. This is the only path while `require_approval_before_send` is on (the default). | Two named users on the row; the body is frozen at approval and shown exactly as the customer will see it. |
| **B. Compose → send** (no separate approval) | One: `send` | `require_approval_before_send` **off** **and** the template is human-approved **and** not `final` tone **and** not the first message to this customer **and** not free text **and** not AI-drafted. | The click is the approval: `approved_by` = the sender, `approval_kind = 'sender'`. The wording was approved earlier by a named user. |
| **C. Cadence auto-send** (no click at all) | None | All of B's conditions **and** the template is a system key on the cadence **and** the same template was not sent to the customer inside the duplicate window **and** the dispatcher's own guards pass (kill switch, cap, quiet hours). | `approved_by` = **the human who approved the template**, `approval_kind = 'template'`. The only text that can go out unattended is text a named user approved verbatim, filled from a closed placeholder set, to a customer who has already received a human-approved message. |
| **D. WhatsApp** | The user sends it from their own phone | Always | The system never sends; it produces a link and records the user's confirmation. |

**Never automatic, no exception path:** free text; `final`-tone templates (final notices, legal
wording); the first message to a customer; AI-drafted messages; anything while the kill switch is
off or the cap is reached; anything to a customer whose case is escalated or on hold, or whose
invoice is disputed. These are checked server-side at `send` **and again** by the dispatcher.

| Question | Answer | Why |
|----------|--------|-----|
| **Quiet hours** | `quiet_hours_start`–`quiet_hours_end` in the tenant timezone (a window that may cross midnight). `send` inside it → `422 quiet_hours` with `nextWindowAt`; the dispatcher skips queued mail inside it and picks it up after. | The prompt; SM-06. |
| **Cadence** | A case is due a reminder when its oldest in-scope invoice's days past due equals a `dunning_cadence_days` step (`0` = due today, `7`, `14`, `30`…), mapped to the system keys `due_today`, `dunning_7`, … The duplicate window is the smallest gap between cadence steps (default 7 days): the same template key to the same customer inside it → `duplicate_send_window`. | "Not more often than configured." |
| **Language** | `customer.preferred_language` picks the template row; the compose screen shows and may override it (UI-14). | UI-13/14. |
| **Recipient** | The billing contact with an email, else the primary contact with one, else `no_contact_email`. | Doc 05 guard. |
| **Frozen body** | Rendered at compose, frozen at approval (path A) or at send (path B/C); template edits afterwards create a new version and never touch history. | DM-25. |
| **Content binding** | After rendering, any invoice number of the tenant that does not belong to the customer appearing in the subject or body → `422 content_binding_violation`. | SEC-81, T-48. |
| **Kill switch** | `tenant_settings.outbound_sending_enabled` (per tenant, `tenant.settings.write`) and `OUTBOUND_SENDING_ENABLED` (global env). Either off → `send` and the dispatcher refuse with `outbound_disabled`; queued mail waits. | SEC-103, T-152. |
| **Cap** | `tenant_settings.daily_send_cap` (default 200) counted on `sent_at` in the tenant day → `send_cap_exceeded`. | SEC-86. |
| **SMTP** | `SMTP_HOST` / `SMTP_PORT` / `MAIL_FROM` from `.env`, plain SMTP via `System.Net.Mail` — no paid API, no new dependency. Per-tenant SMTP with write-only secrets is deferred. | The prompt; Cost Rules. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | Placeholders: the closed set is listed; a template with `{{customer_nam}}` → 422 `unknown_placeholder`; rendering substitutes every allowed placeholder with real case data, money as F3 strings, and leaves nothing unresolved | doc 05 | `Placeholders_AreAClosedSet` (unit + API) |
| AC-02 | Template versions: PATCH creates version 2 as `Draft`, version 1 stays; an approved template's `approvedBy` is recorded; only `Approved` templates may be used to compose without approval | doc 04 | `Templates_AreVersioned_AndApproved` |
| AC-03 | Message machine: 10 states × 10 events exhaustive (unit) | T-10 | `MessageMachine_Matrix_IsExhaustive` |
| AC-04 | Each guard returns its code from `send`: `dispute_blocks_send`, `case_escalated`, `quiet_hours`, `no_contact_email`, `customer_on_hold`, `approval_required`, `duplicate_send_window`, `outbound_disabled`, `send_cap_exceeded` | T-47 | `SendGuards_ReturnTheirCodes` |
| AC-05 | SEC-81: a free-text body naming another customer's invoice number → 422 `content_binding_violation`; the same number for this customer passes | T-48 | `ContentBinding_RejectsOtherCustomersInvoices` |
| AC-06 | DM-25: after approval the body is frozen; editing the template creates a new version and the message's body and `templateVersion` do not change | DM-25 | `Body_IsFrozenAtApproval` |
| AC-07 | Approval: with the setting on, `send` before `approve` → `approval_required`; `approve` records the approver; with the setting off, the first message still requires approval, a free-text one too, a `final` template too; a later templated message sends with `approvalKind = sender` | PRD-15, §2 | `Approval_Rules` |
| AC-08 | T-128: compose from an Arabic template → preview → approval required → approved by a second user → send → message `Sent` through Mailpit (found in its API) → appears in the case timeline with the frozen body; the case moves to `AwaitingCustomer` with a follow-up date (C3) | T-128, C3 | `WorkTheMessage_T128` |
| AC-09 | Cadence: with approval off and a human-approved `dunning_7` template, a case at 7 days past due whose customer already received a message gets a reminder auto-queued by the sweep with `approvalKind = template` and the template approver as `approvedBy`; a second sweep the same day creates nothing; a customer with no prior message gets a `PendingApproval` draft instead; a case at 8 days gets nothing | §2 path C, T-13 | `Cadence_AutoQueuesOnlyWhenSafe` |
| AC-10 | Kill switch: tenant off → `send` 422 and the dispatcher leaves `Queued` mail untouched; on again → the next dispatch sends it; global env off → the same | SEC-103, T-152 | `KillSwitch_StopsEverything` |
| AC-11 | Quiet hours: the clock pinned to 22:00 Amman → `send` 422 `quiet_hours` with `nextWindowAt` next morning; the dispatcher skips; at 08:30 it sends | §2 | `QuietHours_HoldTheMail` |
| AC-12 | WhatsApp: a `wa.me/<E.164 digits>?text=<url-encoded body>` link, status `PreparedForManualSend`; confirming records `Sent` with `sentBy`; no lockfile mentions `whatsapp-web.js`, `baileys`, `venom`, `wppconnect`, `open-wa` | ADR-0004 | `WhatsApp_IsClickToChatOnly`, `NoUnofficialWhatsAppLibrary` (unit, greps the manifests) |
| AC-13 | Dispatcher: a Queued message is sent once; an SMTP failure is retried up to 3 times then `Failed` with the reason; a second dispatch does not resend a `Sent` message | S7 | `Dispatcher_RetriesThenFails` |
| AC-14 | The customer statement lists open invoices, payments and messages sent, per currency, nothing summed across | doc 06 | `Statement_ListsHistory` |
| AC-15 | Cross-tenant: every `{id}` route → 404 with B's ids; a message in A for B's customer refused by the composite key; B's dispatch never sends A's mail; A's template is invisible to B | INV-05 | sweep + `CrossTenantMessage_IsRejected` |
| AC-16 | Every transition writes one audit row and (when on a case) one case activity; `Sent` rows have `approved_by` (CHECK) | SM-03, INV-13 | `MessageTransitions_AreAudited` |
| AC-17 | UI: the editor offers only allowed placeholders and warns when ar/en placeholder sets differ; compose shows the rendered preview and the guard reason; the WhatsApp screen says the system does not send; the browser computes no amount | doc 06 §6.10 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/templates`, `/templates/{id}`, `/templates/placeholders`, `/messages`, `/messages/{id}`, `/customers/{id}/statement` | `cases.read` |
| POST | `/templates`, `/templates/{id}` (new version), `/templates/{id}/approve` | `templates.write` |
| POST | `/templates/{id}/preview` | `cases.read` |
| POST | `/cases/{id}/messages`, `/messages/{id}/cancel`, `/messages/{id}/confirm-manual-send`; GET `/messages/{id}/whatsapp-link` | `messages.draft` |
| POST | `/messages/{id}/approve` | `ai.suggestions.approve` |
| POST | `/messages/{id}/send` (`Idempotency-Key`), `/messages/dispatch` | `messages.send` |
| GET / PUT | `/organization/outbound` (kill switch, cap) | `tenant.read` / `tenant.settings.write` |

All through `TenantScopeMiddleware`; none anonymous. The route count pin moves from 93 to 112 (nineteen routes).

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | SMTP is the `.env` host for every tenant; per-tenant settings with encrypted write-only secrets are deferred | The prompt asks for the local Mailpit config; the KEK infrastructure for SEC-67 does not exist and would be built for one screen. Flagged. |
| D-2 | `approved_by` on an auto-sent cadence message is the template's approver, with `approval_kind = 'template'` | INV-13's CHECK wants a human on every sent row; the human who approved the exact wording is the honest one. Path B records the sender. |
| D-3 | System templates are seeded `Draft` per tenant and must be approved by a tenant user before any automatic use | Shipped wording is a starting point; a named person in the tenant reads it before it goes out unattended. |
| D-4 | `System.Net.Mail.SmtpClient` against the env host | In the BCL; Mailpit speaks plain SMTP; no dependency to record. |
| D-5 | The duplicate window is the smallest gap between cadence steps | "Not more often than configured" without another setting. |
| D-6 | Bounce webhooks are deferred | Mailpit has none; `Bounced` / `Delivered` exist for the MTA that will. |
