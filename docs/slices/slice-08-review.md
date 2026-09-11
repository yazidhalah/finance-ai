# Slice 8 — Email templates & reminders: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **high** (per the slice plan: this slice sends real communications to real customers).

## Which paths can send without a human clicking approve — and why each is safe

The slice prompt asked for this explicitly. There are exactly four ways a message reaches a
customer, all in `MessagingService`; the table in the slice doc §2 is the contract and the tests
below pin it.

| Path | Human clicks | What stands behind the send | Test |
|------|-------------|-----------------------------|------|
| **A** compose → `approve` → `send` | two, by two permissions (`ai.suggestions.approve`, `messages.send`) | `approved_by` = the approver, `approval_kind = message`; the body was frozen at compose and shown as the customer sees it | `WorkTheMessage_T128` (approved by a second user, sent through Mailpit) |
| **B** compose → `send` | one (`messages.send`) | only when `require_approval_before_send` is **off**, the template is human-approved, not `final`, not free text, not AI-drafted, and the customer has already received a message; the click is the approval: `approval_kind = sender` | `Body_IsFrozen_AndApprovalRulesHold` |
| **C** cadence → auto-queue → dispatcher | **none** | everything in B **plus**: the same template not sent inside the duplicate window; `approved_by` = **the human who approved the template's exact wording**, `approval_kind = template`; the dispatcher re-runs every guard before the SMTP call | `Cadence_AutoQueuesOnlyWhenSafe` (a fresh customer gets a `PendingApproval` draft instead; an unapproved template drafts but never queues; the same day drafts nothing twice) |
| **D** WhatsApp | the user sends it from their own phone | the system produces a `wa.me` link and records the user's confirmation; it never transmits | `WhatsApp_IsClickToChatOnly`, `NoUnofficialWhatsAppLibrary` |

**Never without a click, no exception path:** free text (`free_text`), `final`-tone templates
(`final_tone` — the seeded `dunning_90` is one), the first message to a customer
(`first_message`), AI-drafted messages (`ai_drafted`, slice 9), an unapproved template
(`template_not_approved`), and anything while `require_approval_before_send` is on
(`tenant_setting`). The reasons are stored on the row (`approval_reasons`) and shown in the UI.
Path C is additionally impossible while the tenant or global kill switch is off, past the daily
cap, during quiet hours, for a case that is escalated, on hold, disputed or waiting on a promise,
and for any invoice slice 7's guard blocks — `SendGuards_ReturnTheirCodes`,
`KillSwitch_AndQuietHours_HoldTheMail`, and the dispatcher's own guard pass.

**The database holds the last line:** `sent_requires_approval` refuses any `Queued` / `Sent` /
`Delivered` row without `approved_by` and `approval_kind` (`MessageTransitions_AreAudited` tries
to null it and is refused).

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all nineteen. None is anonymous.**

| Endpoints | Declaration |
|-----------|-------------|
| `GET /templates`, `/templates/placeholders`, `/templates/{id}`, `/messages`, `/messages/{id}`, `/customers/{id}/statement`; `POST /templates/{id}/preview` | `RequiresPermission(cases.read)` |
| `POST /templates`, `/templates/{id}`, `/templates/{id}/approve` | `RequiresPermission(templates.write)` |
| `POST /cases/{id}/messages`, `/messages/{id}/cancel`, `/messages/{id}/confirm-manual-send`; `GET /messages/{id}/whatsapp-link` | `RequiresPermission(messages.draft)` |
| `POST /messages/{id}/approve` | `RequiresPermission(ai.suggestions.approve)` — PRD-15 |
| `POST /messages/{id}/send` (`Idempotency-Key` required, 428 without), `/messages/dispatch` | `RequiresPermission(messages.send)` |
| `GET /organization/outbound` | `RequiresPermission(tenant.read)` |
| `PUT /organization/outbound` (kill switch, cap) | `RequiresPermission(tenant.settings.write)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at **112**.
The 404 sweep covers every `{id}` route with B's real template and message ids. Existence is
checked before the body on every write. The webhook of doc 05 (`POST /webhooks/email-events`,
signed) is **not built** — Mailpit emits no events (D-6); when an MTA does, that route arrives with
its signature check and will be the first anonymous-but-signed endpoint, to be reviewed as such.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, both — plus two columns on `tenant_settings`.**

| Table | `tenant_id NOT NULL` | RLS forced | Policy (no platform clause) | `(tenant_id, id)` key | Composite FKs |
|-------|:--:|:--:|:--:|:--:|---|
| `message_templates` | ✅ | ✅ | ✅ | ✅ | — |
| `messages` | ✅ | ✅ | ✅ | ✅ | `→ customers`, `→ collection_cases`, `→ customer_contacts`, `→ message_templates` |

The enumeration test now walks **30** tables. No DELETE on either.

**Targeted tests** (`MessagingIsolationTests`, `MessagingTests`):

- **Layer 3 alone** — `CrossTenantMessage_IsRejected`: as superuser, a message in B for A's
  customer (`fk_message_customer`), on A's case (`fk_message_case`), from A's template
  (`fk_message_template`) — all refused.
- **Wide reads and the worker**: B's template list has no `private_note`; B's `/messages` is
  empty; B composing on A's case is 404 before the template is looked at; **B's dispatcher sends
  nothing of A's** (A's mail stays `Queued`; A's own dispatch sends it).
- **SEC-81 content binding** is a raw query with an explicit `tenant_id` predicate under RLS
  (`AssertContentBoundAsync`); `ContentBinding_RejectsOtherCustomersInvoices` proves the guard.
- **Templates are seeded per tenant** (`EnsureSystemTemplatesAsync`, `is_system`, `Draft`) inside
  the tenant scope; a tenant never sees another's versions.

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`. The dispatcher and the cadence run inside the
sweep's tenant-scoped request; `OutboundSwitch.GloballyEnabled` reads an environment variable, not
data. `SmtpMailTransport` reads `SMTP_HOST` / `SMTP_PORT` / `MAIL_FROM` from the environment (D-1).

## 4. Any new money-related field or calculation?

**No new money column. One sum, in the ledger's own terms, and no arithmetic anywhere else.**

| Item | As built |
|------|----------|
| `{{amount_due}}` | `RenderContext.AmountDue` = `Σ balance_cache` of the referenced invoices (all one currency, taken from the case's scope), rendered as `F3 + " " + currency` by `TemplateRenderer` — a string, never parsed back. `Placeholders_AreAClosedSet_AndRenderFromStoredValues`. |
| `{{promised_amount}}`, `{{payment_amount}}` | stored decimals, same formatting. |
| The statement | positions per currency from `LedgerService.CustomerPositionAsync` (3b), open invoices from the aging service, payments and messages listed. Nothing summed across currencies (`Statement_ListsHistory` asserts no `1000.000` after a 250 payment). |
| Frontend | `Messaging.tsx` renders the server's preview and frozen body verbatim; `messaging.test.tsx` greps for arithmetic. |

**Nothing rounds.**

## 5. Any AI-touching code?

**None runs.** `ai_drafted` / `ai_suggestion_id` exist for slice 9, and an AI-drafted message is
`PendingApproval` by construction (`ai_drafted` is one of the approval reasons; the CHECK
`ai_drafted_has_suggestion` keeps the provenance). Templates render from a closed placeholder set
over stored rows — an AI has no way to put text into a send except through a human-approved
draft (AI-61 applies from slice 9).

## 6. Any new dependency?

**None.** SMTP is `System.Net.Mail` (BCL); Mailpit is the local MTA already in
`infrastructure/compose.yml`. WhatsApp is a URL. `NoUnofficialWhatsAppLibrary` greps every
`package.json`, lockfile, `csproj` and Python manifest for the known unofficial packages and fails
on any. `THIRD-PARTY-NOTICES.md` is unchanged apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Seven things deserve the reviewer's attention:

1. **SMTP is global, not per tenant (D-1).** Every tenant's mail goes through the `.env` host
   with the `.env` `MAIL_FROM`. Doc 05's per-tenant SMTP with write-only encrypted secrets
   (SEC-67, KEK) is not built; nor is the `email-settings` route. A pilot with one tenant is fine;
   a second tenant needs it before its customers see a From address that is not theirs.
2. **Path C's `approved_by` is the template's approver (D-2).** INV-13's CHECK wants a human id on
   every sent row; the person who approved the exact wording is the honest one. If the reviewer
   prefers that no message ever leaves without a per-message click, set
   `require_approval_before_send` on (the default) — path C then never runs.
3. **A Collector holds `ai.suggestions.approve`** (doc 01 §5.1), so a Collector can approve a
   message. T-128's "approve as a second user" is the scenario, not a rule; self-approval of one's
   own draft is possible and recorded. Worth a decision.
4. **Bounce/delivery events are not handled (D-6).** `Delivered` / `Bounced` exist in the machine;
   nothing sets them. The signed webhook comes with a real MTA.
5. **The dispatcher runs inside the sweep and on demand.** There is still no scheduler; a
   `Queued` message waits for the next `POST /cases/sweep` or `POST /messages/dispatch`. Quiet
   hours are respected by the dispatcher, so a message clicked at 19:59 may go at 20:01 if the
   dispatch runs then — the guard is evaluated at dispatch time too.
6. **`ptp_confirm`, `ptp_reminder`, `dispute_ack` and `payment_thanks` are seeded but nothing
   drafts them automatically** — the promise, dispute and payment flows do not compose yet. They
   are available to compose by hand.
7. **The Arabic templates were written, not translated, and reviewed once by me.** UI-13 asks for
   independent authoring; a native reviewer should read the eleven Arabic bodies before a pilot.
   Doc 10's visual-regression snapshot of an Arabic email is a manual check: the Mailpit UI at
   `:8025` shows the T-128 message rendered RTL.
