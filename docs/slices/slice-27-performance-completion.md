# Slice 27 — Performance completion (doc 09 §7)

Status: **Implemented and tested** (Performance category; the nightly runs it, the acceptance pass runs it on the host).

Source: doc 09 §7 — T-140 names four budgets and a three-tenant dataset, T-142 a sustained load profile; slice 16
built two of the budgets on a one-tenant seed and flagged the rest. PR #32 fixed the dataset; this slice finishes §7.

## 1. Scope

| # | Capability | Test |
|---|-----------|------|
| S1 | **Single API P95 < 500 ms** at 50k: forty single-entity reads (a customer, an invoice) on the T-140 dataset | `AgingTests.SingleEntityRead_P95_Under500ms_At50k` |
| S2 | **Import of 5,000 rows < 60 s**: upload, mapping (per-row validation, customer matching by name) and commit of a 5,000-row CSV, timed end to end with the three phases reported | `ImportTests.Import_5000Rows_Under60s` |
| S3 | **T-142**: twenty Accountant members of one tenant, each paced under API-13's 600/min, reading a six-path mix (queue, aging, customer, invoice, case, customer search) for `LOAD_PROFILE_SECONDS` (default 60; the nightly sets 600). The run is split into five windows; every status is counted | `LoadProfileTests.TwentyConcurrentUsers_Sustained_NoErrorRateIncrease` |
| S4 | The nightly workflow passes `LOAD_PROFILE_SECONDS=600`; the acceptance-pass §3 command names it and the new budgets | `.github/workflows/nightly.yml`, `docs/ops/acceptance-pass.md` |

## 2. Acceptance criteria

| ID | Criterion |
|----|-----------|
| AC-01 | Single-entity read P95 < 500 ms on the three-tenant 50k dataset |
| AC-02 | 5,000 accepted rows imported and committed (5,000 invoices exist afterwards) in under 60 s |
| AC-03 | Over the load run: no 5xx, no transport failure, no 429; the last window's error rate is not above the first's; every window has samples |
| AC-04 | The figures (P95/min/max, phase times, per-window counts and P95s, the status histogram) are in the test output for the nightly log and the decision record |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Twenty distinct users, not one user twenty times over | API-13 limits each user to 600/min; one user at 20× would only prove the limiter. The requirement says users. |
| D-2 | Paced at ≥ 150 ms between a user's requests | ≤ 400/min per user: under the limit with headroom, and closer to a person at a screen than a tight loop. A 429 is counted as an error, so a pacing regression fails the test. |
| D-3 | The assertion is the spec's: error rate, not latency | T-142 says "no error-rate increase". Latency per window is reported so a trend is visible; the P95 budgets are T-140's and are asserted there. |
| D-4 | Duration from `LOAD_PROFILE_SECONDS`, default 60 | Ten minutes belongs to the nightly and the host; a developer running the category locally should not wait ten minutes for a shape that shows in one. |
| D-5 | Classification P95 < 8 s stays with the evaluation harness | It needs the real model on the target host (§2 of the acceptance pass); the integration suite runs the scripted client. |

## 4. Local figures (2026-09-12, CPU dev box)

Single API P95 11 ms (min 5, max 110, 40 reads) · import of 5,000 rows 4.8 s (upload 0.2, mapping 1.2, commit 3.5) ·
T-142 at 30 s: 3,999 requests (133/s), 0 errors, P95 39 ms, windows flat (68/31/33/32/32 ms).

## 5. Runner baseline (2026-09-12, hosted ubuntu-24.04, run 34686449891, main `c052219`)

| Test | Figure |
|------|--------|
| Aging P95 at 50k (three tenants, 31 % share) | **375 ms** (min 344, max 410); bitmap heap scan on `tenant_id` |
| Queue P95 at 5,000 cases | **20 ms** (min 17, max 21) |
| Single API P95 at 50k | **8 ms** (min 7, max 51, 40 reads) |
| Import of 5,000 rows | **5.0 s** (upload 0.1, mapping 1.2, commit 3.7) |
| T-142, 20 users × 600 s | **79,484 requests (132.5/s), 0 errors, P95 79 ms**; windows 94 / 76 / 76 / 77 / 79 ms (max 678 → 183 ms); statuses 200 × 79,484 |

The first window's higher max is warm-up on a cold runner; from the second window on the profile is flat. Every
budget of doc 09 §7 that the integration suite can judge holds on the shared runner; the host's figures are the ones
the acceptance pass records.

