# Slice 32 — The daily job runs itself

Status: **Implemented and tested.**

Source: slice 5 D-2 ("the sweep is an endpoint until a scheduler exists — a scheduler for the sweep is the first
background worker"), carried since; doc 05 slice 10 ("scheduled precomputation before `briefing_send_at`"); doc 03
§7 (the invariant job "runs last in the sweep"). Nothing in the compose stack, the runbook or cron ran
`POST /cases/sweep`: deployed as it was, no case would open, no reminder go out and no briefing render unless
someone pressed the button for every tenant every day.

## 1. Scope

| # | Capability | Test |
|---|-----------|------|
| S1 | **`SweepRunner.RunOnceAsync`** (Infrastructure): lists the active tenants through `PlatformIdentityStore.ListActiveTenantsAsync` (the one place platform scope is entered), then enters each tenant separately — its own DI scope, its own `TenantContext`, its own `DatabaseScope` — and runs `CaseService.SweepAsync(null)`: the system is the actor. A tenant that fails is logged (by id, never by data) and recorded in the outcome; the next tenant still runs. Suspended and closed tenants are skipped. | `SweepSchedulerTests` |
| S2 | **`SweepScheduler`** (API, `BackgroundService`): a 30 s settle delay, then one pass every `SWEEP_INTERVAL_MINUTES` (default 60). `0` disables it — the test suites and the e2e API set `0` so the pass is driven explicitly and the clock-pinned tests stay deterministic. | `IntervalMinutes` asserted in the test host |
| S3 | **Operations**: `.env.example` documents the variable; runbook §1 lists it and §2 gains "the daily job, everywhere: `SWEEP_INTERVAL_MINUTES=0`" as the pause switch; `POST /cases/sweep` stays for an operator who wants a tenant swept now. | — |

## 2. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | An hourly pass, not one run at a fixed local hour | The sweep is idempotent (SM-06) and its steps carry their own clocks: the briefing renders once local time passes `briefing_send_at`, reminders respect quiet hours and duplicate windows, cases open on the day they qualify. One fixed hour would either miss the briefing time or need a per-tenant timetable; an hourly idempotent pass needs neither. |
| D-2 | In the API process, not a cron on the host | A-15 is one process; the compose stack has no cron; a pilot should not depend on an operator remembering a crontab. The interval is a variable so an operator can pause it or move to an external trigger later. |
| D-3 | Per-tenant scope, per-tenant failure | SEC-22: a background job declares its tenant; one tenant's broken state must not stop the others' day. The failure is logged by tenant id only. |
| D-4 | The system is the actor | SM-03: the sweep's transitions are the system's; the audit `collection_case.sweep_run` says `system` when nobody pressed the button. |

## 3. Cost

At pilot scale a pass is one sweep per tenant per hour; the sweep run audit event appears hourly per tenant
(24 a day). If that proves noisy, the interval is the dial; the event is the record that the job ran.
