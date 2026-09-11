# Slice 10 — Daily briefing: acceptance criteria and test plan

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 05 slice 10 (API-22) · doc 07 §6.3 (AI-80…83) · doc 04 §5.7 (`daily_briefings`,
`tenant_settings.briefing_send_at`) · doc 06 §6.7 (Today) · doc 09 T-103, T-104, T-131 · doc 10 slice 10 ·
PRD-22, PRD-28 · FIN-62 · slice 8 (templates, approval, the mail transport) · slice 9 (suggestions, inbox).

The one rule this slice exists to keep: **every number a person reads in the briefing was computed by
the same C# that computes it elsewhere, and the model can only talk about numbers it was handed.**

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Metrics in C#** (`BriefingService.ComputeAsync`): overdue total (base currency, indicative, plus per currency), collected yesterday, promises due today (count + amount), promises broken yesterday, new disputes (raised yesterday), disputes breaching SLA, queue size, top cases, unverified payment claims, unmatched and unclassified replies, pending AI suggestions, and the overdue delta against the previous briefing ("material aging changes"). Each figure is produced by the existing computation it belongs to — the aging rows and `ToBaseIndicative` (slice 4), the queue predicate (slice 5), the stored promise / dispute / task / inbox states (slices 6–9). No new arithmetic path. |
| S2 | **`daily_briefings`**: one immutable row per tenant, date and language; metrics as JSON (the API shape, money as strings), the narrative (nullable), its status, the `ai_suggestions` row behind it |
| S3 | **The narrative** through a second AI operation, `daily_briefing` (`services/ai`): input is the already-computed metrics as strings (AI-80), output is `narrative` ≤ 1200 chars, `highlights` ≤ 5, `numbers_used` (AI-82); generated per language independently (AI-83) |
| S4 | **The numeric-fidelity guard** (AI-81, API-22) in C#: every numeral in the narrative and highlights must be traceable to a metric value (or the briefing date); otherwise the narrative is discarded, the suggestion row says `rejected_by_guard`, the screen shows the metrics alone |
| S5 | **Schedule**: the sweep (`POST /cases/sweep`, slice 5's daily job) generates today's briefing once the tenant's local time has passed `briefing_send_at`; `POST /briefings/regenerate` redoes today's on demand; past dates are immutable |
| S6 | **Email** through slice 8's infrastructure: a `daily_briefing` system template per language that a human must **approve** before anything is sent, rendered by a closed briefing placeholder set, delivered by the same `IMailTransport` behind the same global and tenant kill switches, audited as `briefing.sent`. Recipients are members of the organization, chosen in the settings |
| S7 | **UI**: the Today screen (metrics, top cases, the "AI summary" card with the model name, the quiet notice when there is no narrative), historical briefings by date, briefing settings (send time, language, email on/off, recipients) |

**Deferred, and to which slice:** LangGraph (doc 10 names "the LangGraph briefing flow"; one model call per
language does not need a graph — deferred until a multi-step flow exists, flagged) · a real scheduler (the
sweep remains the job runner, slice 5 D-2) · P95 measurement of the briefing precomputation (it is
precomputed; T-104 is satisfied by construction) · native-speaker review of the Arabic narrative (doc 10
acceptance 3 — recorded as not done) · Playwright T-131 (an integration test walks the AI-down path).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **Where do the numbers come from?** | `AgingService.OverdueAsync` (the aging rows, `ToBaseIndicative` per row, summed — FIN-55), `CaseService.QueueQuery` (the same predicate the queue endpoint now uses), and plain counts/sums over stored states. Money is `decimal` until it is serialized as an `F3` string. | FIN-62; the slice plan: "does not introduce a second calculation path". |
| **What does the model get?** | The metrics object with every value already a string, the company display name, the date and the language. No customer emails, no ids, no invoice numbers — top cases carry a display name, an amount string and days past due. | AI-80, AI-30. |
| **What can the model say?** | Prose about those numbers. `NumericFidelityGuard` extracts every numeral (Western and Arabic-Indic digits, with or without thousands separators) from `narrative` and `highlights`; each must equal a metric value, its integer part, a top-case figure, or a component of the briefing date. `numbers_used` must name existing metric keys. One untraceable numeral → the whole narrative is dropped. | AI-81, T-103. |
| **What if the AI is down or the guard trips?** | `narrative = null`, `narrativeAvailable = false`, `narrativeStatus ∈ {unavailable, rejected_by_guard, disabled, schema_invalid}`; the briefing row still exists and the screen renders the metrics. Never an error state. | PRD-28, API-22, T-131. |
| **Immutability** | A past date's row is never updated; `regenerate` on a past date → 409 `briefing_immutable`. Today's row may be regenerated (metrics recomputed, a new suggestion row); the old suggestion rows stay. | Doc 10 acceptance 5. |
| **When is it generated?** | By the sweep, once per day per language, when tenant-local time ≥ `briefing_send_at` and no row exists for today; by `regenerate` on demand; lazily by `GET /briefings/today` if nothing exists yet (metrics only if the AI is off). | PRD-22 "precomputed". |
| **Email** | Only if `briefing_email_enabled`, recipients are set, an **Approved** `daily_briefing` template exists in the briefing language, `OUTBOUND_SENDING_ENABLED` and `outbound_sending_enabled` are on. The body is the approved template rendered with the closed briefing placeholder set (`{{narrative}}` is one of them and renders as the guarded text or empty). Sent once per day (`sent_at`). | Slice 8's path C reasoning: a human approved the exact wording. |
| **Who may read?** | `cases.read`, so every role. Collectors with `collector_sees_only_assigned` still see the organization's briefing — it is the Owner's summary by design (PRD-14 scopes the queue, not the briefing). Flagged. | Doc 05. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | Metrics are correct on a known scenario: overdue total equals the aging report's overdue (base and per currency), collected yesterday equals yesterday's payments, promises due today / broken yesterday, new disputes, SLA breaches, queue size equals `/queue` total, top cases are the queue's head, unverified claims equal open verification tasks, unmatched replies equal the inbox's unmatched count | FIN-62 | `Metrics_MatchTheirSources` |
| AC-02 | The AI service receives only the pre-computed values as strings: the captured request equals the metrics the API returned; no ids, no emails | AI-80, FIN-62 | same test, on the captured payload |
| AC-03 | A narrative containing an untraceable numeral is discarded: `narrative` null, `narrativeAvailable` false, `narrativeStatus = rejected_by_guard`, the suggestion row `validation_status = rejected_by_guard`; a narrative whose numerals all trace is accepted, including Arabic-Indic digits and thousands separators | AI-81, T-103 | `NumericFidelityGuard` unit table + `Guard_DropsUntraceableNumerals` |
| AC-04 | AI down → the briefing renders with `narrativeAvailable: false` and 200, no error; AI disabled → the same with `disabled`; schema-invalid → `schema_invalid` | PRD-28, T-131 | `AiDown_StillRenders` |
| AC-05 | Arabic and English are separate rows generated by separate calls in the requested language; `?language=ar` returns the Arabic row | AI-83 | `Languages_AreIndependent` |
| AC-06 | The sweep generates today's briefing only once tenant-local time ≥ `briefing_send_at`; a second sweep does not regenerate; a pinned clock before the send time generates nothing | PRD-22 | `Sweep_GeneratesAtSendTime` |
| AC-07 | Past dates are immutable: `GET /briefings/{date}` returns the stored row; `regenerate` on a past date → 409; `regenerate` needs `ai.settings.write` | Doc 10 acceptance 5 | `History_IsImmutable` + sweep |
| AC-08 | Email: nothing is sent without an approved template; with the template approved, recipients set and email enabled, the sweep sends one mail per recipient through `IMailTransport` (counted), the body contains the metrics and the guarded narrative, `sent_at` is set, `briefing.sent` is audited; the tenant kill switch stops it | Slice 8 path C | `Email_NeedsAnApprovedTemplate` |
| AC-09 | Settings: `GET/PATCH /organization/briefing-settings` (send time `HH:mm`, language, email on/off, recipient member ids validated against the organization's members); audited | Doc 05 | `Settings_RoundTrip` |
| AC-10 | Cross-tenant: B cannot read A's briefing by date (404 / empty), B's sweep does not generate A's, a `daily_briefings` row in B with A's suggestion id is refused by the composite key, RLS hides A's rows; the projection A sends names A's customers only | INV-05 | sweep + `CrossTenantBriefing_IsRejected` |
| AC-11 | UI: Today shows the metrics with `MoneyText`, the top cases as links, the "AI summary" card labelled with the model name only when a narrative exists, otherwise the quiet notice; Arabic renders RTL with Western digits; settings panel; nav `today` is available | Doc 06 §6.7 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/briefings/today`, `/briefings/{date}` | `cases.read` |
| POST | `/briefings/regenerate` | `ai.settings.write` |
| GET | `/organization/briefing-settings` | `tenant.read` |
| PATCH | `/organization/briefing-settings` | `tenant.settings.write` |

All through `TenantScopeMiddleware`; none anonymous. Route pin 126 → 131.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | No LangGraph | One call per language; a graph library (and its licence tree) for a linear step is weight without a reason. Flagged for the day a flow has branches. |
| D-2 | The sweep is the scheduler | Slice 5 D-2 stands; `briefing_send_at` is honoured by comparing tenant-local time at sweep time. |
| D-3 | The briefing email is a `message_templates` row (`key = daily_briefing`) with its own closed placeholder set | It reuses versioning, human approval, and the single mail transport; the customer placeholder set does not fit a staff summary, so the renderer is selected by key. |
| D-4 | Recipients are member user ids, not free addresses | Nothing this system sends goes to an address a human did not already establish as a member (SEC). |
| D-5 | The guard allows a metric's integer part and the date's components | "4 promises" for `4`, "47,350" for `47350.750`, "September 11" for `2026-09-11`; anything else — a percentage, a rounded "about 50,000", a day count not in the metrics — is a rejection. Strictness is the point (AI-81). |
| D-6 | Top cases carry the display name, not the customer id | The narrative may name a customer; the screen links the case from the metrics, not from the prose. |
