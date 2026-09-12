# Slice 26 — Per-operation AI enablement

Status: **Implemented and tested.**

Source: doc 05 `/organization/ai-settings` ("`aiEnabled`, `minConfidence`, **per-operation enablement**, autosend
never available") — the note under it said "no per-operation enablement yet (one operation exists)"; two exist since
slice 10, and the slice 10 review flagged the single switch · T-105 (an operation that fails its gate stays off for
the pilot while the product keeps working) · decision 0003.

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Two more switches** on `tenant_settings` (migration 0020): `ai_classification_enabled`, `ai_briefing_enabled`, both default on. `ai_enabled` stays the **kill switch**: an operation runs only when it and its own switch are on (`TenantSettings.ClassificationActive` / `BriefingActive`). |
| S2 | **Contract**: `GET/PATCH /organization/ai-settings` carry `aiClassificationEnabled` / `aiBriefingEnabled`; `GET /ai/health` reports the effective `classificationActive` / `briefingActive`. The audit event `tenant.ai_settings_changed` records every switch before and after. No new route (pin 160); the OpenAPI snapshot changes. |
| S3 | **Gates**: `InboundService.ClassifyAsync` answers `409 ai_disabled` (unchanged code — the caller's situation is the same) when classification is not active; `BriefingService.NarrateAsync` returns `narrativeStatus: disabled` when the briefing is not active. Neither calls the AI service. |
| S4 | **UI**: the AI settings card shows the kill switch and one row per operation (inert while the kill switch is off); the inbox banner keys on the effective classification state, so a briefing-only pause says nothing in the inbox. |

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | Both switches default on; settings and health report them | `Defaults_AreOn_AndHealthReportsEffectiveStates` |
| AC-02 | Classification off alone: `409 ai_disabled` with no AI call; the briefing still narrates | `ClassificationOff_LeavesBriefingOn` |
| AC-03 | Briefing off alone: `narrativeStatus: disabled` with no AI call; the classifier still runs | `BriefingOff_LeavesClassificationOn` |
| AC-04 | The kill switch overrides both; the audit trail carries every switch before/after | `KillSwitch_OverridesBoth_AndIsAudited` |
| AC-05 | One tenant's switches never move another's (SEC-13) | `Switches_AreTenantScoped` |
| AC-06 | The card toggles one operation without touching the other; the banner ignores a briefing-only pause | `ai.test.tsx` |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Two named columns, not a JSON map of operations | Two operations exist and doc 07 names both; a third would be a migration either way, and a column is checkable, typed and visible in the audit diff. |
| D-2 | The kill switch overrides, the operation switches do not replace it | Runbook §2 keeps one command that stops everything; the operation switches are the pilot's dial, not a second kill switch. |
| D-3 | `ai_disabled` stays the classifier's refusal code for both cases | The caller's situation is identical (label by hand); which switch is off is on the settings screen and in the audit. |
| D-4 | Health reports *effective* states, settings report the *raw* switches | Screens ask "may I?"; the settings screen asks "what is set?". |

## 4. What was not built

Nothing resembling autosend, per doc 05. No per-operation confidence threshold (one threshold, one classifier).
