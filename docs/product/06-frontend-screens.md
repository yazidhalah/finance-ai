# 06 — Frontend Screens, States, and Bilingual Behaviour

Status: DRAFT. Stack: React + TypeScript + Vite + Tailwind + shadcn/ui.

---

## 1. Global shell

```
┌──────────────────────────────────────────────────────────────┐
│ Top bar: org switcher · search · language toggle · user menu │
├────────────┬─────────────────────────────────────────────────┤
│ Sidebar    │ Route content                                    │
│ Today      │                                                  │
│ Queue      │                                                  │
│ Customers  │                                                  │
│ Invoices   │                                                  │
│ Payments   │                                                  │
│ Aging      │                                                  │
│ Disputes   │                                                  │
│ Messages   │                                                  │
│ Import     │                                                  │
│ Settings   │                                                  │
└────────────┴─────────────────────────────────────────────────┘
```

In Arabic the sidebar is on the **right** and the entire layout mirrors (§3).

**UI-01** Navigation items are filtered by the user's **permission list** returned by
`/me`, not by role name. A `Viewer` never sees an Import link at all — but hiding is a
UX affordance, not security; the server still authorizes (SEC-12).

---

## 2. Internationalization strategy

| ID | Rule |
|----|------|
| UI-10 | Two locales in v1: `ar-JO` (default) and `en-JO`. Locale comes from the user profile, overridable per session by the toggle, persisted to the profile. |
| UI-11 | All UI strings live in `ar.json` / `en.json` keyed by dot-path. **A missing or untranslated key fails the build** (a CI check compares key sets and flags identical Arabic/English values as suspicious). No runtime fallback that silently shows English inside an Arabic UI (PRD-20). |
| UI-12 | The backend never sends display prose (API-04). The client maps `messageKey` → localized string. Unknown `messageKey` renders a generic localized error **and logs a client-side warning** so the gap is caught. |
| UI-13 | **Customer-facing content is authored per language**, not machine-translated at send time: `message_templates` has one row per language, and the Arabic template is written in Arabic (PRD-03). |
| UI-14 | The language of a message to a customer is chosen from `customer.preferredLanguage`, **not** from the current UI language. An Arabic-speaking user sending to an English-preferring customer gets an English draft, with the choice visible and overridable. |
| UI-15 | Pluralization uses ICU MessageFormat. Arabic has six plural categories (`zero`, `one`, `two`, `few`, `many`, `other`) — a naive `count === 1 ? …` is wrong in Arabic and MUST NOT appear. |
| UI-16 | Dates: Gregorian in both locales for v1 (A-03/Q-03), formatted with `Intl.DateTimeFormat`. Hijri display is a deferred option. |
| UI-17 | Digits: default **Western Arabic numerals (0-9) in both locales** — Jordanian business documents overwhelmingly use them — with a per-user setting for Arabic-Indic (٠-٩). Financial digits never change shape based on layout direction alone. |

---

## 3. RTL and bidirectional text

**UI-20** Arabic uses a real RTL layout: `<html dir="rtl" lang="ar">`. Tailwind is
configured with **logical properties** — `ms-*`/`me-*`, `ps-*`/`pe-*`,
`start-*`/`end-*`, `text-start`/`text-end`. A lint rule bans physical
`ml-*`, `mr-*`, `pl-*`, `pr-*`, `left-*`, `right-*`, `text-left`, `text-right` in
component code.

**UI-21 What mirrors:** layout flow, sidebar side, table column order, breadcrumbs,
progress direction, drawer/sheet side, back/forward chevrons, list bullet side, form
label alignment.

**UI-22 What does NOT mirror:** clock icons, media playback controls, checkmarks,
logos, chart *time* axes (time still runs left→right in charts by convention — but the
axis labels are Arabic), and **any Latin-script identifier** (invoice numbers,
references, email addresses).

**UI-23 Numbers and currency inside Arabic text** are the single most common RTL bug.
Every number, amount, invoice number, and date rendered inside Arabic prose MUST be
wrapped in an isolate (`<bdi>` or `unicode-bidi: isolate`). Without it,
"فاتورة رقم INV-2026-001 بمبلغ 1,250.500 د.أ" renders with the parts in the wrong
visual order, and the user reads a different invoice number than the one stored. This
has a dedicated Playwright + visual-regression test (doc 09 §3.4).

**UI-24 Mixed-direction input.** Free-text fields (notes, message bodies) accept mixed
Arabic/Latin/Arabizi. They use `dir="auto"` so each paragraph takes its own direction
from its first strong character.

**UI-25 Fonts.** An Arabic webfont with proper Arabic numerals and financial glyph
support (e.g. IBM Plex Sans Arabic or Noto Sans Arabic — both open-licensed, recorded
in `THIRD-PARTY-NOTICES.md`), with a matching Latin face so mixed strings do not
visually jump. Tabular figures (`font-variant-numeric: tabular-nums`) on every money
column so digits align in tables in both directions.

**UI-26 Truncation and ellipsis** must respect direction — truncating an RTL string
from the wrong side hides the meaningful part of an Arabic company name.

---

## 4. Money and numbers in the UI

| ID | Rule |
|----|------|
| UI-30 | The client **never performs monetary arithmetic** — no summing a column, no computing a remainder, no percentage of an amount. Every displayed total comes from the API (FIN-01, API-05). A code review that finds `+` between two money values in the frontend is a blocking finding. |
| UI-31 | Money strings arrive as `{amount: "1250.500", currency: "JOD"}` and are formatted for display only. The raw string is preserved in the DOM `title`/`data-` attribute so a user can copy an exact value. |
| UI-32 | Currency is **always** shown with the amount. There is no screen where a bare number means money. |
| UI-33 | Mixed-currency lists group by currency with subheaders; there is no "total" row across currencies (FIN-04). |
| UI-34 | Indicative base-currency conversions render with a distinct style and an inline "indicative" badge with a tooltip explaining the frozen rate (FIN-06). |
| UI-35 | Negative/credit values use an explicit label ("credit") plus colour, never colour alone (PRD-25). |

---

## 5. Standard states — every screen implements all of them

| State | Requirement |
|-------|-------------|
| **Loading** | Skeletons matching final layout (no spinners on tables). Never a layout shift when data lands. |
| **Empty (first-run)** | Explains what the screen is for and gives the one action that fills it ("Import your invoices"). Different from **empty (filtered)**, which offers "clear filters". |
| **Populated** | The normal case. |
| **Partial / stale** | Some data failed (e.g. AI panel down while the queue loaded). The healthy part stays usable, the failed part shows an inline retry. Never a whole-page error because one panel failed. |
| **Error** | Localized message, `traceId` shown small and copyable for support, retry action. |
| **Permission-denied** | Clean "you don't have access to this" with a link to the tenant admin — never a raw 403 or a blank page. |
| **Offline / AI-degraded** | A persistent banner: "AI assistant unavailable — everything else works" (PRD-28). AI panels collapse to their manual equivalents. |
| **Saving / optimistic** | Buttons disable with an inline spinner; money actions are **never optimistic** — they wait for the server, because an optimistic balance that rolls back is a lie. |
| **Conflict** | On `409 concurrency_conflict`: a dialog showing what changed and a re-fetch, never a silent overwrite. |

---

## 6. Screen inventory

### 6.1 Auth (slice 1)

| Screen | Notes and states |
|--------|------------------|
| **Sign in** | Email, password, TOTP step. Language toggle **before** login (a user who cannot read the form cannot log in). Generic error for wrong credentials; distinct locked-account state with unlock time. |
| **Register organization** | Org name, base currency (with a clear "this cannot be changed later" warning), timezone (default Asia/Amman), locale. |
| **Verify email** | Pending / success / expired-token states. |
| **Forgot / reset password** | Always-success messaging (never reveals account existence). |
| **Accept invitation** | Shows inviting org and role. Expired-invite state. |
| **Tenant switcher** | Only for multi-tenant users. Switching reloads all data and clearly re-labels the shell. |

### 6.2 Settings (slice 1, extended by later slices)

Organization profile · Members & roles (with a permission-matrix viewer so an Owner can
see exactly what a role can do) · AR settings (aging basis, buckets, grace days, dunning
cadence, quiet hours) · Holidays · Email settings (SMTP/IMAP; secrets are write-only,
shown as `••••`, with a "Send test email" flow) · AI settings (enable, confidence
threshold with a plain-language explanation, per-operation toggles) · Briefing settings
· Audit log viewer.

### 6.3 Customers (slice 2)

| Screen | States and detail |
|--------|-------------------|
| **Customer list** | Search (Arabic-aware, DM-20), filters, columns: name (in UI language, falling back to the other with a subtle marker), open balance per currency, overdue, oldest days past due, risk flag, open case. Empty-first-run → "Add a customer or import invoices". |
| **Customer detail** | Header with name, contacts, preferred language, terms. **Balance blocks per currency** (open / overdue / disputed / unapplied cash / unapplied credit — never netted, FIN-15). Tabs: Invoices · Payments & cheques · Credit notes · Case & timeline · Contacts · Statement. Promise-reliability badge with denominator (SM-37). |
| **Customer form** | Arabic and English name fields side by side, each with its own `dir`. Validation: at least one name. |
| **Duplicate review** | Side-by-side comparison with a similarity score and a merge preview showing exactly which rows move. Merge requires typing the customer name to confirm. |
| **Statement of account** | Print-friendly, per currency, `asOf` picker, running balance, export PDF/XLSX with correct RTL rendering. |

### 6.4 Import (slice 3)

A four-step wizard with a persistent progress indicator (mirrored in RTL, UI-21):

1. **Upload** — drag/drop CSV or XLSX, size and type validation, duplicate-file warning with an explicit override (DM-23).
2. **Map columns** — detected headers on one side, target fields on the other, with a live preview of the first 5 parsed rows; date-format and decimal-separator pickers (`1,250.500` vs `1.250,500` — getting this wrong silently corrupts every amount, so the preview shows the parsed `decimal`). Saved mappings reusable.
3. **Preview & resolve** — counts of accepted / warning / rejected / duplicate. A row-level exception table: unmatched customer (assign or create), duplicate invoice number, missing due date, line-sum mismatch (DM-22), negative total. Bulk-resolve where safe. **The Commit button is disabled while any blocking exception is unresolved.**
4. **Commit & result** — progress, then a summary with links: N invoices created, M skipped, and a link to the batch report. Failure state shows a transactional rollback message ("nothing was imported").

Additional: **Import history** list with per-batch drill-down; a batch is permanently viewable so an auditor can trace an invoice to its source row.

### 6.5 Invoices, payments, cheques (slice 3)

| Screen | Detail |
|--------|--------|
| **Invoice list** | Filters (status, settlement, overdue, disputed, currency, customer). Status shown as **two chips** — lifecycle and settlement — because they are different facts (SM §1.1). Overdue shown as a day count, not a separate status. |
| **Invoice detail** | Header amounts, lines (if present), and a **money history panel**: every allocation, credit application, withholding deduction and write-off with dates and actors, ending in the current open balance. This panel is the answer to "why is the balance this?" (PRD-04). |
| **Record payment** | Customer, amount + currency, method, dates, reference. Then the **allocation step**. |
| **Allocation screen** | The most important money screen. Shows open invoices oldest-first with balances; a FIFO proposal pre-filled but **always editable and always requiring confirmation** (FIN-26); a live "unallocated remaining" figure returned by the server on each change; over-allocation blocked inline with the reason. Disputed and escalated invoices are shown but excluded from the proposal, with an explanatory badge. |
| **Short-payment resolver** | Triggered when allocation leaves a residual: a five-way choice (withholding / discount / bank charges / dispute / partial payment) with the consequence of each spelled out (FIN-28). This screen is what prevents the product from chasing phantom balances. |
| **Cheque register** | Cheques by status with dates; post-dated cheques prominently show their date and the linked PTP. Bounce action requires a reason and warns that the case will reopen (SM-51). |
| **Credit notes** | Create, apply across invoices, void. Reason code required. |
| **Write-off** | Propose (with computed open balance, not a typed amount, FIN-32) → approval queue for an Admin/Owner. The approver's screen shows the proposer and forces a distinct user (PRD-11), or an explicit self-approval acknowledgement. |

### 6.6 Aging (slice 4)

**Aging report** — the credibility screen. Bucket columns, per-currency sections, group
by customer or by bucket, `asOf` date picker, basis indicator in the header (FIN-50),
"of which disputed" column (FIN-56), unapplied cash/credit lines below the table
(FIN-59), export. Clicking a cell drills to the invoice list behind it — **every number
must be clickable to its constituents**. A small "how this is calculated" link opens a
plain-language explanation in the user's language.

States: loading skeleton preserving the table shape; empty ("no open invoices");
stale-data warning if the reconciliation job last failed.

### 6.7 Today (daily briefing home) and Queue (slices 5, 10)

**Today** — the default landing screen: overdue total, collected yesterday, promises
due today, broken promises, disputes breaching SLA, queue size, top cases. The AI
narrative sits in a clearly-labelled card marked "AI summary" with the model name; when
unavailable the card is replaced by the metrics alone and a quiet notice (PRD-28,
API-22).

**Collection queue** — the work screen. A ranked list; each row shows customer, overdue
amount, oldest days past due, invoice count, last contact, priority score with an
**expandable factor breakdown** (FIN-80), and a suggested action. Keyboard-first: `j`/`k`
to move, `Enter` to open, `s` to snooze, `m` to message (mirrored key semantics are not
changed in RTL — `j`/`k` are positional, not directional). Filters: assigned to me,
bucket, minimum amount. Empty state when the queue is worked to zero is a genuine
celebration state — that is the daily goal (PRD-06).

**Case detail** — customer snapshot, in-scope invoices with balances, one merged
**timeline** (activities, messages, payments, transitions, AI suggestions), and an
action rail: log contact · send message · record promise · raise dispute · snooze ·
hold · escalate. Escalate shows a hard confirmation explaining that **all automation
stops permanently** (SM-26). Disputed state visibly disables dunning actions with the
reason (SM-25).

### 6.8 Promises (slice 6)

**Promises list** — grouped: due today, overdue-to-evaluate, active future, recently
broken/kept. **Record promise dialog** — invoices covered (multi-select with balances),
amount, date (with a business-day hint), source. Over-promise is blocked (SM-32);
superseding an existing promise shows an explicit warning (SM-36). **Proposed-promise
review** (from AI) is covered in §6.10. **Promise detail** shows the evaluation
arithmetic — what was promised, what arrived in the window, and the verdict — so a
`Broken` verdict is never mysterious.

### 6.9 Disputes (slice 7)

**Dispute list** with SLA indicators (on-track / due today / breached; never colour
alone). **Raise dispute** — reason code from the closed set with plain-language
descriptions in both languages, disputed amount (≤ open balance), customer claim text.
**Dispute detail** — SLA clocks (with the pause state explained), evidence attachments,
resolution panel. **Resolution** requires an outcome and, for accept/partial, shows the
credit note that will be created **before** confirming (SM-45) — the user sees the money
consequence before agreeing to it. **Payment-verification tasks** get their own small
queue (SM-44): "customer says they paid" → find the payment → record it, or reply that
we cannot find it. This screen must never offer a "mark as paid" button (SM-10).

### 6.10 Messages and templates (slices 8, 9)

**Template list** — by key, channel, language, tone; version history; Arabic and English
side by side so a gap is obvious. **Template editor** — body with a placeholder picker
restricted to the allowed set (API), a live preview against a real case in both
languages, and a warning if the Arabic and English versions differ in placeholder usage.

**Compose / send** — recipient contact, language (defaulted from the customer, UI-14),
template or free text, the rendered preview **exactly as the customer will see it**,
and the list of invoices referenced. Send is blocked with a specific, localized reason
when a guard fires (§ slice 8 guards). When approval is required, the flow is
draft → request approval → approve → send, with the approver named on the message.

**WhatsApp** — a "Prepare WhatsApp message" action produces the text and a click-to-chat
link the user opens themselves; the UI states plainly that the message is **not sent by
the system** and asks the user to confirm they sent it, which logs the activity
(ADR-0004).

**Inbox / replies** — inbound messages with match status, classification chip, and
confidence. Unmatched senders get a "link to customer" action.

**AI suggestion review** — the human gate. For each suggestion: the customer's original
text (rendered as quoted, non-actionable content, SEC-42), the proposed classification
with its reason code and confidence, the exact fields that would be written, and three
actions: **Approve**, **Edit and approve**, **Reject** (with reason). Provenance is
always visible: model name, digest, prompt version. Below-threshold results appear here
as "needs your classification" with no pre-selected answer (A-21) — a greyed-out guess
would anchor the user.

### 6.11 Audit (slice 1+)

**Audit log viewer** — filter by entity, actor, event type, date. Each row expands to
show before/after values, reason code, and the AI suggestion that influenced it, if any.
An invoice's audit trail is reachable in one click from the invoice.

---

## 7. Component-level requirements

**UI-40 Money input.** A dedicated component: locale-aware parsing of `1,250.500` /
`١٢٥٠٫٥٠٠`, three-decimal enforcement, currency selector adjacent, always `inputmode="decimal"`,
LTR text direction even in RTL layout, and no client-side rounding — the raw string goes
to the server.

**UI-41 Date input.** Gregorian picker with weekend shading for Friday/Saturday (A-08)
and holiday markers; the week starts on **Sunday** for `ar-JO` and `en-JO`.

**UI-42 Status chips.** Every state from doc 02 has one chip component with a
localized label, an icon, and a colour — icon plus text always, never colour alone
(PRD-25).

**UI-43 Confidence display.** AI confidence is shown as a three-band label
(low / medium / high) with the numeric value on hover — a bare "0.73" invites false
precision from a 4B model.

**UI-44 Explain affordance.** Any derived number (balance, bucket, score, reliability)
has an "explain" affordance opening the constituents. This is a product principle, not
a nicety (PRD-04).

---

## 8. Responsive and device

**UI-50** Desktop-first (the accountant's screen), but the **Today** and **Queue**
screens and the case timeline MUST be fully usable at 375 px — the owner reads the
briefing on a phone (P1). Tables collapse to card lists at small widths, never to
horizontally-scrolling tables with hidden money columns.

**UI-51** Touch targets ≥ 44 px on the mobile-critical screens.

---

## 9. Frontend performance

**UI-60** Route-level code splitting; the queue and aging tables virtualize beyond 200
rows. First contentful paint < 1.5 s and interactive < 3 s on a mid-range Android over
3G for the **Today** screen. Arabic webfont subsetted and preloaded to avoid a
layout-shifting fallback.

## 10. Frontend testing hooks

**UI-70** Every interactive element carries a stable `data-testid`; Playwright specs run
the full suite **twice — once in `en`, once in `ar`** — and the Arabic run includes the
bidi assertions from UI-23 (doc 09 §3.4).
