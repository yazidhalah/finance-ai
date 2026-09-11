# Slice 10 — Daily briefing: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **medium** — a model writes prose about money, but it cannot touch a number.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all five. None is anonymous.** The route pin in
`EndpointAuthorizationSweepTests` moved from 126 to 131; the sweep replaces `{date}` with a real date so
`GET /briefings/{date}` is exercised unauthenticated (401), without the permission (403) and across tenants (404).

| Endpoints | Declaration |
|-----------|-------------|
| `GET /briefings/today`, `GET /briefings/{date}` | `RequiresPermission(cases.read)` |
| `POST /briefings/regenerate` | `RequiresPermission(ai.settings.write)` (a Collector gets 403 — `Sweep_GeneratesAtSendTime_AndHistoryIsImmutable`) |
| `GET /organization/briefing-settings` | `RequiresPermission(tenant.read)` |
| `PATCH /organization/briefing-settings` | `RequiresPermission(tenant.settings.write)` |

The AI service's `POST /internal/ai/v1/daily_briefing` requires `X-Service-Token` (`test_requires_token`).

## 2. Every new table: `tenant_id`, RLS enabled and forced, a policy, composite FKs?

**Yes** (`database/migrations/0011_daily_briefings.sql`). `daily_briefings`: `tenant_id NOT NULL →
tenants`, RLS ENABLE + FORCE with `tenant_isolation`, `UNIQUE (tenant_id, id)`, composite FKs
`(tenant_id, ai_suggestion_id) → ai_suggestions` and `(tenant_id, template_id) → message_templates`,
no DELETE grant, and a `BEFORE UPDATE` trigger that refuses to rewrite a row older than yesterday. The
enumeration test walks 33 tables. The targeted test, `BriefingIsolationTests.CrossTenantBriefing_IsRejected`,
covers the non-standard patterns: two tenants' briefings on the same date are two rows with two ids; the
figures A sends the model name A's customers and none of B's; a row in B pointing at A's suggestion is
refused by the composite FK (layer 3 alone); with B's tenant set, A's row does not exist (layer 2 alone).

## 3. Code paths before a tenant or full authentication, or cross-tenant by design?

**None.** Nothing touched `PlatformIdentityStore`; the sweep's briefing step runs inside the per-tenant
sweep with the tenant already established; `ComputeAsync` reads only through the tenant-filtered sets.
The AI service remains tenant-agnostic: the briefing request has no tenant, customer or user id — the
type has nowhere to put one (`BriefingRequestPayload`).

## 4. New money fields or calculations: `decimal` only? Where does rounding happen?

**`decimal` until the string; no new arithmetic path.**

| Figure | Where it is computed | Rounding |
|--------|----------------------|----------|
| `totalOverdue` (base) and `overdueByCurrency` | `AgingService.OverdueAsync`: the aging report's own rows, `DaysPastDue > 0`, summed per currency; base = Σ `AgingRules.ToBaseIndicative(open, fx)` per row (FIN-55) | the same per-invoice rounding the aging report does — the product's only money rounding, unchanged |
| `overdueChange` | today's base total − the previous briefing's stored base total (both `decimal`) | none |
| `collectedYesterday` | Σ confirmed, unreversed payments received yesterday **in the base currency** | none (flagged: other currencies are not converted — payments carry no fx rate) |
| `promisesDueToday.amount` | Σ `PromisedAmount` of Active promises due today **in the base currency** | none (same flag) |
| top cases' `amount` | `collection_cases.overdue_balance_base`, computed by slice 5 | none here |
| counts | `COUNT(*)` over stored states | — |

Every figure is serialized once as an `F3` string into the metrics document, which is what is stored,
returned and handed to the model. `Metrics_MatchTheirSources_AndTheModelGetsOnlyStrings` asserts the
briefing's overdue equals the aging report's non-current buckets and its queue size equals `/queue`'s
total. The queue predicate itself was extracted into `CaseService.QueueQuery` and the queue endpoint now
uses it, so there is one definition, not two.

## 5. AI-touching code: schema validation before acting, and can the AI alone finalize a financial fact?

**Validation — twice, and then a guard the model cannot argue with.**

1. The service validates the model's output against `daily_briefing.response.v1.json` (`jsonschema`,
   grammar-constrained decoding first), runs the numeral check, and uses its single repair retry on a
   schema or guard failure (`test_briefing.py`).
2. The backend re-validates with `BriefingResponseValidator` (C#, no shared code; enums pinned to the
   schema file in `Validator_IsStrict_AndPinnedToTheSchemaFile`), then runs `NumericFidelityGuard.Check`
   on the figures **it** sent — not on what the model claims in `numbers_used`, which is cross-checked
   too. A narrative with one untraceable numeral is discarded whole: `narrative = null`,
   `narrativeStatus = rejected_by_guard`, the suggestion row `validation_status = rejected_by_guard` with
   the offending tokens in `guard_reason` (`Guard_DropsUntraceableNumerals_AndAiDown_StillRenders`, and
   the guard's table test: "about 47,000", "12%", a sum of two figures, a bare day number — all rejected;
   Arabic-Indic digits, thousands separators, integer parts, the full date — accepted).

**Can the AI's output alone finalize a financial fact? No.** The briefing operation produces prose and
nothing else: `BriefingService.NarrateAsync` returns a string, a list of strings and a status; it writes
`daily_briefings.narrative`, `highlights`, `narrative_status` and an `ai_suggestions` row. There is no
branch that touches an invoice, payment, promise, dispute, case, message or setting. The metrics are
computed before the model is called and are not changed by its answer (`Guard_DropsUntraceableNumerals…`
reads the same `5700.000` in every status). The email body is the approved template rendered from the
stored metrics with `{{narrative}}` filled by the guarded text or nothing — never by an unguarded one.

**What the model gets (AI-80):** the metrics as strings, the date, the language, the company name and
the top cases' display names. The captured request in `Metrics_MatchTheirSources…` contains no id and
no `@`. **What is logged:** the service logs counts and hashes; the `ai.briefing_narrated` audit event
carries model, digest, prompt version, confidence, validation status and the guard reason (numerals
only, never the prose).

**Degradation (PRD-28, API-22, T-131):** AI unreachable → `unavailable`; switched off → `disabled`;
off-schema → `schema_invalid`; guard → `rejected_by_guard`. In every case the row exists, the figures
render, the response is 200 and the Today screen shows a quiet notice (`today.test.tsx`).

## 6. New dependencies: in `THIRD-PARTY-NOTICES.md` with accurate licenses?

**None added.** The Python service reuses slice 9's tree; the backend uses `System.Text.Json` and a
`GeneratedRegex`; the web has no new package. LangGraph, which doc 10 names for this slice, was
deliberately not adopted (D-1): one call per language is a function, not a graph, and its licence tree
would be recorded for nothing. The notices file says so.

## Observed on the live model

- English: first-time guard pass, every figure exact, sensible order ("4 promises due … 47,350.750 JOD
  overdue … 3,200.000 collected … Petra Supplies 62 days"). ~50 s on this CPU-only box.
- Arabic: the guard passes (numbers exact) but the prose is poor — invented terms for "promise"
  (العُقد المُعلَّقة), the currency abbreviated to "جود". A glossary in the prompt helped little. **Doc 10
  acceptance 3 (native-speaker review) is not met**; my recommendation is to leave `briefing_language`
  on English for the email until an 8B model or a reviewed Arabic prompt exists, or to keep the AI card
  off in Arabic. The figures are unaffected either way.
- The full flow ran end to end: sweep → both languages generated → Arabic template approved by a human →
  email sent through the slice 8 transport → received in Mailpit with subject
  "Live Org — موجز التحصيل ليوم 2026-09-11".

## What I deliberately did not build (and why)

- LangGraph (D-1); a real scheduler (the sweep, slice 5 D-2); per-operation AI toggles (one switch covers
  both operations; flagged); `GET /metrics` on the service; Playwright T-131 (walked by the integration
  test and the web tests instead); an evaluation corpus for the briefing (the guard is deterministic and
  fully unit-tested; what a corpus would measure is prose quality, which needs the reviewer above).

## Flagged for your decision

1. **Arabic narrative quality** (above). Ship with English email or the AI card hidden in Arabic?
2. **Base-currency-only sums** for `collectedYesterday` and `promisesDueToday.amount` — payments and
   promises carry no fx rate. Other currencies show in `overdueByCurrency` only.
3. **Who reads the briefing:** every role with `cases.read`, including a Collector under
   `collector_sees_only_assigned` — it is the organization's summary, not a scoped view (doc 05 says
   `cases.read`; PRD-14 scopes the queue). Say if you want it Owner/Accountant only.
4. **One AI switch** for classification and narration; doc 05 mentions per-operation enablement.
5. **The immutability trigger** uses the UTC date with a one-day slack; the exact rule (today only, in
   tenant time) is enforced in code. The trigger is a backstop, not the rule.
