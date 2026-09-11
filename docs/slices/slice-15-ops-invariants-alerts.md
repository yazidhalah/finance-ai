# Slice 15 — Operations: the invariant job, the alert path, and the audit screen — acceptance criteria and test plan

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 03 §7 (INV-01…INV-13: "run as (a) unit tests over generated data, (b) a nightly job per tenant, and
(c) an integration test after every mutation-heavy scenario. Any violation is P1") · SEC-53 (the tamper-evident
audit chain) · SEC-101 (structured logs) · SEC-102 (page-worthy alerts: invariant violation, audit-chain break,
send-volume anomaly, AI guard rejection rate spike, failed backup, failed restore drill) · SEC-103 (the runbook, slice
14) · SEC-41 (PII never in logs or alerts) · SEC-66 (no user-supplied outbound requests) · doc 09 T-153 ("an injected
invariant violation fires the alert path") · doc 06 §6.11 (the audit log viewer) · slice 5 D-2 (the sweep is the
daily job).

The one rule this slice exists to keep: **the database is checked against its own invariants every day, by the
same C# that wrote the numbers, and a violation reaches a human without anyone having to look.**

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **The invariant job** (`InvariantService.RunAsync`): per tenant, INV-01, 02, 03, 04, 06, 07, 08, 09, 10 and 13 as SQL over the tenant's rows (through the RLS-bound `finance_app` connection, so a run can only ever see its own tenant), plus the SEC-53 audit-chain verification that already exists (`AuditWriter.VerifyAsync`). INV-05 and INV-11 are structural (composite foreign keys; column types) and are asserted by the security suite (`TenantIsolationTests`, `ImportIsolationTests.EveryMoneyColumn_IsNumeric19_3`), not re-derived at runtime; INV-12 is scenario-scoped and stays a test. Every run is recorded in `invariant_runs` (immutable) with each check's violation count and up to five sample ids. |
| S2 | **Runs** when the sweep runs (the nightly job of doc 03 §7(b), slice 5 D-2) and on demand (`POST /organization/invariants/run`). The latest run is readable (`GET /organization/invariants`). |
| S3 | **Detectors** evaluated in the same run (SEC-102): `invariant_violation` (any check with violations), `audit_chain_break`, `ai_guard_rejection_spike` (≥ 10 suggestions in the last 24 h and ≥ 50 % of them `schema_invalid` / `rejected_by_guard`), `send_volume_anomaly` (≥ 20 sends in the last 24 h and ≥ 3 × the mean of the previous seven days). Thresholds are constants in `OpsRules` (Domain) with unit tests. |
| S4 | **The alert path** (`AlertService.RaiseAsync`): one `alerts` row per (tenant, kind, day) — repeated detection does not repeat the page; a structured `Critical`/`Warning` log line; an email to `ALERT_EMAIL` through `IMailTransport` (**not** through the outbound switch — an alert is not customer messaging, and the switch being off is exactly when an operator must hear); a JSON POST to `ALERT_WEBHOOK_URL` (optional `ALERT_WEBHOOK_TOKEN` as a bearer). Delivery outcome is recorded on the row; a failed delivery never fails the sweep. Alerts carry the organization name, counts and ids — never a customer, an email address or an amount (SEC-41). |
| S5 | **`infrastructure/alert.sh <kind> <summary>`** — the same webhook from shell, used by `backup.sh` and `restore-drill.sh` on failure (`backup_failed`, `restore_drill_failed`), so the two script-side alerts of SEC-102 exist too. |
| S6 | **API**: `GET /organization/invariants`, `POST /organization/invariants/run`, `GET /organization/alerts`, `POST /organization/alerts/{id}/acknowledge`. |
| S7 | **UI**: the Audit screen (doc 06 §6.11, nav `audit`, until now `available: false`): the audit log with entity/event filters and a cursor, an **Integrity** card (latest run, per-check status, "Run now"), and **Alerts** (open first, acknowledge). |
| S8 | **Runbook** §6 gains the alert kinds and what to do first; `.env.example` gains `ALERT_EMAIL`, `ALERT_WEBHOOK_URL`, `ALERT_WEBHOOK_TOKEN`. |

**Deferred, and to which slice:** a paging integration (PagerDuty-style) — the webhook is the integration point ·
per-tenant alert routing (alerts go to the operator; Owners see them on the Audit screen) · INV-12 as a runtime
check · the "production canary" cross-tenant assertion of SEC-102 (the security suite is the canary until there is a
production) · expanding audit rows to before/after values on the screen (the API does not expose `payload` yet;
flagged).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **Who computes the checks?** | SQL issued by C#, under the tenant's RLS scope, each returning `count` and `array_agg(id) LIMIT 5`. Money comparisons are `numeric(19,3)` against `numeric(19,3)` in the database — nothing is re-added in C#. | doc 03 §7; FIN-01. |
| **Can a run see another tenant?** | No: it runs on the tenant-scoped connection; RLS hides other rows even if a query forgot `tenant_id` (it does not). The audit verifier is called with the current tenant. | INV-05, SEC-11. |
| **Does a violation change data?** | Never. The job reads, records, alerts. Fixing is a human's job (runbook §6). | doc 03 §7 "any violation is P1". |
| **What goes in an alert?** | Kind, severity, organization name, tenant id, counts, sample ids, timestamps. No customer name, contact, invoice number or amount. | SEC-41. |
| **Is the webhook an SSRF vector?** | No: the URL is operator configuration in `.env`, never a request value; no tenant can set it. | SEC-66, SEC-67. |
| **Why is the alert email outside the kill switch?** | `OUTBOUND_SENDING_ENABLED` exists to stop customer-facing mail in an incident; the alert path is how the operator learns about the incident. It uses the same transport and the same `MAIL_FROM`, sends only to `ALERT_EMAIL`, and is counted separately (`alerts.delivery`). | SEC-103. |
| **How often can one alert page?** | Once per tenant, kind and UTC day (`dedupe_key`). Acknowledging does not re-arm it; the next day does. | SEC-102 "page-worthy" is only true once. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | On the golden ledger scenario every check reports zero violations, the audit chain is intact, and the run is recorded with `status = ok` | doc 03 §7(c) | `Invariants_AreClean_OnTheLedgerScenario` |
| AC-02 | **T-153**: an injected violation (`balance_cache` bumped by SQL) makes the next run report INV-01/INV-09 violations with the invoice id as a sample, `status = violations`, an `alerts` row of kind `invariant_violation` (critical), one email to `ALERT_EMAIL` through the transport, one webhook POST carrying the same payload, and a `Critical` log line; a second run the same day adds no second alert | T-153, SEC-102 | `InjectedViolation_FiresTheAlertPath_Once` |
| AC-03 | A tampered audit row (`AuditIntegrityTests`' technique) makes the run report `audit_chain_break` with the first broken id and raises that alert | SEC-53, SEC-102 | `AuditChainBreak_Alerts` |
| AC-04 | Ten suggestions in 24 h of which six were rejected by the guard raise `ai_guard_rejection_spike` (warning); nine do not; ten with four rejections do not | SEC-102 | `OpsRulesTests` (unit table) + `GuardSpike_Alerts` |
| AC-05 | Twenty-one sends today against a seven-day mean of five raise `send_volume_anomaly`; twenty-one against a mean of ten do not; nineteen never do | SEC-102 | `OpsRulesTests` + `SendAnomaly_Alerts` |
| AC-06 | The alert path is used when the outbound switch is off (global or tenant) and the email still goes out; when `ALERT_EMAIL` is unset the row says `email: skipped` and the webhook still fires; a failing webhook records `webhook: failed` and the run still completes | SEC-103 | `AlertDelivery_IsIndependentOfTheOutboundSwitch` |
| AC-07 | The sweep runs the job; a manual run needs `tenant.settings.write` and is audited (`invariants.run`); reading runs and alerts needs `audit.read`; acknowledging needs `tenant.settings.write` and is audited | doc 05 | `Sweep_RunsTheJob_AndPermissionsHold` + the authorization sweep |
| AC-08 | Cross-tenant: B's injected violation appears in B's run only; A's run stays `ok`; B cannot read or acknowledge A's alert (404); RLS hides A's `invariant_runs` and `alerts` from B's scope; a row in B with A's tenant id is refused | INV-05 | `Ops_IsTenantScoped` + `TenantIsolationTests` enumeration |
| AC-09 | `infrastructure/alert.sh` POSTs the JSON envelope to `ALERT_WEBHOOK_URL` and exits 0 whether or not the webhook answers; `backup.sh` and `restore-drill.sh` call it on failure | SEC-102 | shell check in the slice review (a local run against a listener) |
| AC-10 | UI: nav `audit` is available to `audit.read`; the Audit screen lists events with filters, shows the latest run per check with the violation counts, runs on demand, lists alerts with acknowledge; Arabic and English keys are paired | doc 06 §6.11, UI-11 | web tests |

---

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| GET | `/organization/invariants` | `audit.read` |
| POST | `/organization/invariants/run` | `tenant.settings.write` |
| GET | `/organization/alerts` | `audit.read` |
| POST | `/organization/alerts/{id}/acknowledge` | `tenant.settings.write` |

All through `TenantScopeMiddleware`; none anonymous. Route pin 143 → 147.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Checks are SQL, not EF | The invariants are set arithmetic over whole tables; SQL says each in one statement and the database does the `numeric` arithmetic. Each statement is a constant in `InvariantService` beside the INV id it implements. |
| D-2 | The job runs inside the sweep, not a new scheduler | Slice 5 D-2 stands: one daily entry point. The invariant job is last, after every mutation the sweep makes. |
| D-3 | Alerts are tenant rows, delivered to the operator | Every alert is about one tenant's data, so it is tenant-scoped like everything else (RLS); the operator's inbox and webhook are the cross-tenant view. No platform table. |
| D-4 | The webhook is a plain JSON POST with an optional bearer | Any pager, chat or ticketing system accepts one; no vendor SDK, no paid API. |
| D-5 | `ai_guard_rejection_spike` and `send_volume_anomaly` are warnings, the other two are critical | A spike may be a bad day for the model; a broken invariant is never fine. |
| D-6 | No self-healing | doc 03 §7: a violation is a P1 for a human. The job never writes to a business table. |
