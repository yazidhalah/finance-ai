# finance-ai — status report, 2026-09-12

Prepared for hand-over review. Everything below was verified against the repository on the day: `git log main`
(`b3f5cae`), `gh pr list`, and every test suite re-run in this session — not remembered numbers.

## Slices — status

Verified from `git log main` (each slice landed as a single named commit, then via PRs from #17 onward) and the
slice docs' status lines.

| Backlog slice | Status | Evidence |
|---|---|---|
| 1 Organization & Auth | **merged to main** — commit `5eb59d9` | `docs/slices/slice-01-organization-and-auth.md` "Implemented". Its deferred items (MFA, verification, reset, invitations, roles, holidays) landed later in slices 12, 13, 23, 24. |
| 2 Customers | **merged to main** — `79e9fd9` | slice-02 "Implemented"; merge (DM-21) deferred there, built in slice 23. |
| 3 Invoice import (+ payments, cheques, credit notes) | **merged to main** in two halves — 3a `c318ff3`, 3b `cc32166` | slice-03 "Implemented (3a)", slice-03b "Implemented". Manual invoice / edits (A-11) came in slice 23. |
| 4 Aging | **merged to main** — `6eba75b` | slice-04 "Implemented". |
| 5 Collection queue & cases | **merged to main** — `7dbb40c` | slice-05 "Implemented". Its D-2 (no scheduler) is only closed by **PR #42, which is open** — see below. |
| 6 Promise-to-Pay | **merged to main** — `570ddb2` | slice-06 "Implemented". |
| 7 Dispute | **merged to main** — `83b9fe3` | slice-07 "Implemented". |
| 8 Email templates & sending | **merged to main** — `cdb741a` | slice-08 "Implemented". Per-tenant SMTP and bounce webhook deferred there, built in slice 24. |
| 9 Local AI: reply classification | **merged to main** — `97569a7` | slice-09 "Implemented and tested". Evaluated only on the author-written 72-item corpus (T-90/T-91 not met — see Flagged). |
| 10 Daily briefing | **merged to main** — `8c1d0cb` | slice-10 "Implemented and tested". Arabic narrative never reviewed by a native speaker (doc 10 acceptance 3 — recorded as not done). |

Beyond the ten: slices 11–31 are merged (PRs #17–#41). **Slice 32 (`slice/sweep-scheduler`, PR #42) is open** —
at the time of the audit its CI run had failed on the e2e job (see the addendum at the end for what changed after).

Note on "done": all ten are merged, but the v1 acceptance pass (pilot corpus, target hardware, native Arabic review —
`docs/ops/acceptance-pass.md`) has not been run; by `CLAUDE.md`'s own definition the First Product is not "done".

## Flagged items

`docs/slices/slice-01-organization-and-auth.md` has **no `slice-01-review.md`** — the six-question self-review
`CLAUDE.md` requires does not exist for slice 1 (its §6/§7/§8 cover deviations, DoD and deferrals, not the six
questions). Every other slice has one. Verbatim from each:

**docs/slices/slice-02-review.md**
> Nothing in the six questions is "not sure". Two things are worth the reviewer's eye anyway:
> - **Trigram thresholds** (`0.3` for search hits, `0.6` for duplicate candidates) are engineering guesses, not product decisions. They are constants at the top of `CustomerEndpoints.cs`; the pilot's real customer list should set them.
> - **`FromSql` with a computed `%` pattern**: the search term is passed as a parameter and `LIKE`-escaped (`EscapeLike`); it is never concatenated into the SQL text. Worth a second look precisely because it is the one place user text meets raw SQL.

**docs/slices/slice-03-review.md**
> Nothing among the six is "not sure". Four things deserve the reviewer's attention regardless:
> 1. **FX rate source (D-3).** There is no tenant rate table. A foreign-currency row must carry its own `fx_rate_to_base` or it is rejected. That is the honest choice today, but it means a USD invoice from a system that does not export rates cannot be imported until a rate table exists.
> 2. **XLSX date detection** relies on the cell's number-format id (built-ins 14–22, 45–47, or a custom format containing day/month/year tokens). A workbook that stores dates as plain numbers with no date style will present them as serial numbers and the row will be rejected as `invalid_date` — visible, not silent.
> 3. **The intermittent bootstrap race.** The first full run of this slice failed the entire security suite at fixture setup because two assemblies bootstrapped roles concurrently. `BootstrapAsync` now runs in one transaction under a cluster-wide advisory lock. Three consecutive full runs since were green. Worth a glance at `MigrationRunner.BootstrapAsync`.
> 4. **3a is not shippable to a pilot** (doc 10). Invoices can be imported and viewed; nothing can reduce a balance until 3b.

**docs/slices/slice-03b-review.md**
> Nothing among the six is "not sure". Six things deserve the reviewer's attention:
> 1. **F-1 — Withholding in the balance formula.** Doc 03 §2.1 and FIN-20 list three instruments; E1 and A-04 require a fourth. This slice follows the worked example and amends doc 03. If the reviewer disagrees, the change is one term in `LedgerRules.OpenBalance` and the E1 test.
> 2. **F-2 — Money moving out of a written-off invoice reverses the write-off (I8).** Found by the randomized walk, not by reading the spec: reversing an allocation on a `WrittenOff` invoice left it written off *with* a balance. SM-13 already covers payments arriving on a written-off invoice; the same rule now applies to allocation reversal and credit-note void (`ReopenIfWrittenOffAsync`), audited as `high_severity`. Regression: `MoneyMovingOutOfAWrittenOffInvoice_ReversesTheWriteOff`.
> 3. **Re-authentication on write-off approval (SEC-09) is deferred (D-6)** until MFA / re-auth exists (slice 1b). Four eyes and self-approval acknowledgement are enforced and audited today.
> 4. **E1's withholding rate is a placeholder** (Q-01 unanswered). The worked example runs at 5%; the product computes nothing from the rate, so the answer changes only the fixture.
> 5. **The FIFO proposal does not yet skip disputed invoices** — disputes do not exist until slice 7. `ProposeFifo` takes a candidate list, so the filter is one predicate when they arrive.
> 6. **Bounce handling is on the customer, not just the cheque.** `bounced_cheque_count_12m` is incremented on bounce and never decremented; a rolling 12-month recompute belongs to the aging slice, which reads it. Stated so nobody expects the counter to roll off on its own.

**docs/slices/slice-04-review.md**
> Nothing among the six is "not sure". Six things deserve the reviewer's attention:
> 1. **The slice touches 3b's tables after all — indexes only (D-7).** The first 50k-invoice run hit the 5-second statement timeout because the history read cannot use the `is_active` partial indexes. Three tenant-leading indexes fixed it; no column or constraint changed. The performance test (`Aging_P95_Under800ms_At50k`, 50k invoices / 100k allocation rows, 20 calls through the HTTP stack) measured **P95 297 ms**.
> 2. **T-141's plan assertion is not a test.** In a single-tenant test database a sequential scan of `invoices` is the *correct* plan, so "no seq scan" would be asserting the wrong thing. The tenant-leading indexes exist; the assertion belongs to a multi-tenant seeded environment.
> 3. **Two 3b dating choices surface here, unchanged (D-5, D-6):** withholding is dated by `created_at` in tenant time (no `effective_date` column), and allocation reversal rows carry the UTC date at reversal. Both only matter for an as-of report that straddles a reversal made late in the evening. Worth a one-line fix in 3b's code if the reviewer prefers tenant-local dating.
> 4. **`fn_aging` has a fourth parameter (`p_tz`)** beyond DM-31's three, so the function converts `timestamptz` columns without reading `tenants` itself. Doc 04 amended (DM-31a).
> 5. **The disputed column is present and always zero** with `disputedAvailable: false` until slice 7, and the UI shows a dash rather than `0.000` so nobody reads "no disputes" into it.
> 6. **The "stale data" warning of doc 06 §6.6 is not built** — there is no nightly job yet to be stale. The reconciliation check is an endpoint and a test; the job comes with the first background worker.

**docs/slices/slice-05-review.md**
> Nothing among the six is "not sure". Six things deserve the reviewer's attention:
> 1. **Weights are code, not tenant data (D-1).** FIN-81 says "default weights live in tenant settings"; the schema has only `priority_weights_version`. A tenant chooses a version, not a weight. If per-tenant values are wanted, it is a table and an editor — not built.
> 2. **The sweep is an endpoint (D-2).** `POST /cases/sweep` (`cases.write`) runs the daily job for the caller's tenant; it is idempotent (`Sweep_CreatesCases_PastGrace_Idempotently`) so a cron hitting it is safe. The scheduler and the "run for every tenant" loop (SEC-22) come with the first background worker.
> 3. **Scope vs. grace.** Grace gates *creation* (dpd > `grace_days_before_case`); once a case exists, *every* past-due Open invoice of the customer is in scope, including ones within grace. Stated in the slice doc §2; the alternative (grace per invoice) would leave a 2-day-late invoice out of the conversation with a customer we are already calling.
> 4. **C10 also fires when the only remaining Open invoices are not yet due.** Scope is past-due invoices; if all of those settle, the case resolves even though the customer still owes something not yet due. A new case opens when that invoice passes grace. This reads the spec's "everything in scope settled" literally.
> 5. **Four machine rows go beyond the diagram** but follow the table's "any active" wording: `Open → PromiseActive`, `Open → Disputed`, `Open → OnHold`, `Open → Escalated`. The exhaustive matrix (`CaseMachine_Matrix_IsExhaustive`, 9 × 17 pairs) is transcribed independently in the test, so a disagreement with the reviewer is one row to delete on both sides.
> 6. **`AwaitingCustomer` has no user-facing entry until slice 8** (`message_sent`). The `follow_up_due` return path is built and tested by setting the state directly (`Suppression_LeavesAndReturnsOnSchedule`).

**docs/slices/slice-06-review.md**
> Nothing among the six is "not sure". Seven things deserve the reviewer's attention:
> 1. **F-1 — a new case event, `ptp_kept`** (`PromiseActive → InProgress`). Doc 02 has no edge for a kept *partial* promise that leaves a balance; without it the case stays suppressed forever. The case matrix test (now 9 × 18) carries the row; delete it on both sides if you disagree.
> 2. **Post-dated cheque coverage (D-2).** A cheque names no invoice; its promise covers the case's in-scope invoices oldest-due first up to the cheque amount, and only when a case exists. A PDC received before the customer passes grace creates no promise — nothing is being chased yet.
> 3. **SM-34's window is date-based**: a payment received *earlier the same day* on a covered invoice counts toward a promise recorded that afternoon. This follows the spec's `[created_at, deadline]` on `received_date`; noted because `Reliability_ShowsTheDenominator` had to use one invoice per promise to avoid it.
> 4. **The bounce path breaks the promise immediately** (not at the deadline), and the case's score rises through both counters (bounced + broken). E2 says "PTP Broken, case reopens at raised priority"; the immediacy is a reading.
> 5. **SM-52 (dispute wins over promise) and the dispute half of SM-54** wait for slice 7; the PTP half of SM-54 (`ptp_active` blocks write-off approval) is built and tested.
> 6. **`tenant_holidays` is empty** until a settings screen exists (D-4). Deadlines skip Fridays and Saturdays today; announced holidays must be seeded by SQL.
> 7. **Manager notification on a broken promise is not built** — there is nothing to send it with until slice 8. The badge, the queue position and the timeline are the notification today.

**docs/slices/slice-07-review.md**
> Nothing among the six is "not sure". Six things deserve the reviewer's attention:
> 1. **SLA days are constants (D-1):** 2 / 10 business days and a 5-business-day pending timeout in `DisputeSla`. SM-48 says tenant-configurable; `tenant_settings` has no columns for them. Adding three integer columns is a one-line migration when someone needs it.
> 2. **`allow_split_dunning_during_dispute` is enforced by a read endpoint, not a send.** There is no message to send until slice 8. `GET /cases/{id}/dunning-eligibility` returns `dispute_blocks_send` per invoice and the case screen disables its (still inert) send control with that reason. Slice 8's send path **must** call `DisputeService.DunningEligibilityAsync`; the review of that slice should look for it.
> 3. **A dispute opened on a `PromiseActive` case lifts the queue suppression** so the case shows as `Disputed` work (`resolve_dispute`) rather than staying hidden until the promise deadline. SM-52 says the promise "stays Active but stops driving reminders"; this is the reading.
> 4. **The credit note's `dispute_id` has no foreign key.** The column exists from 0004; adding the composite FK means altering a 3b table. The link is written by the service and read by nothing yet. Worth a two-line migration in the slice that first reads it.
> 5. **Disputes have no as-of history.** The aging report's disputed column is *today's* open disputes even when `asOf` is in the past. Stated in the slice doc; a `raised_at` / `resolved_at` window would be a small change if a reproducible past disputed figure is ever needed.
> 6. **Evidence is stored as `bytea` capped at 10 MB with a three-type allowlist and no scanner (D-5).** SEC-44's virus scan is infrastructure the project does not run; the allowlist and the sniff are what stand in for it.

**docs/slices/slice-08-review.md**
> Nothing among the six is "not sure". Seven things deserve the reviewer's attention:
> 1. **SMTP is global, not per tenant (D-1).** Every tenant's mail goes through the `.env` host with the `.env` `MAIL_FROM`. Doc 05's per-tenant SMTP with write-only encrypted secrets (SEC-67, KEK) is not built; nor is the `email-settings` route. A pilot with one tenant is fine; a second tenant needs it before its customers see a From address that is not theirs.
> 2. **Path C's `approved_by` is the template's approver (D-2).** INV-13's CHECK wants a human id on every sent row; the person who approved the exact wording is the honest one. If the reviewer prefers that no message ever leaves without a per-message click, set `require_approval_before_send` on (the default) — path C then never runs.
> 3. **A Collector holds `ai.suggestions.approve`** (doc 01 §5.1), so a Collector can approve a message. T-128's "approve as a second user" is the scenario, not a rule; self-approval of one's own draft is possible and recorded. Worth a decision.
> 4. **Bounce/delivery events are not handled (D-6).** `Delivered` / `Bounced` exist in the machine; nothing sets them. The signed webhook comes with a real MTA.
> 5. **The dispatcher runs inside the sweep and on demand.** There is still no scheduler; a `Queued` message waits for the next `POST /cases/sweep` or `POST /messages/dispatch`. Quiet hours are respected by the dispatcher, so a message clicked at 19:59 may go at 20:01 if the dispatch runs then — the guard is evaluated at dispatch time too.
> 6. **`ptp_confirm`, `ptp_reminder`, `dispute_ack` and `payment_thanks` are seeded but nothing drafts them automatically** — the promise, dispute and payment flows do not compose yet. They are available to compose by hand.
> 7. **The Arabic templates were written, not translated, and reviewed once by me.** UI-13 asks for independent authoring; a native reviewer should read the eleven Arabic bodies before a pilot. Doc 10's visual-regression snapshot of an Arabic email is a manual check: the Mailpit UI at `:8025` shows the T-128 message rendered RTL.

**docs/slices/slice-09-review.md** ("Flagged for your decision")
> 1. **`certifi` / MPL-2.0** (§6).
> 2. **`ai_suggestions.subject_id` without an FK** (§2).
> 3. **The corpus is mine and small.** The evaluation harness, the gates and the report format are real; the numbers are not evidence of production quality until the pilot's corpus exists (T-90/T-91).
> 4. **Latency on CPU.** 30–45 s per classification on this machine against AI-10's 20 s; the timeout is an environment variable (`AI_TIMEOUT_SECONDS`, `AI_SERVICE_TIMEOUT_SECONDS`) and the defaults keep the spec's values. A GPU or the 8B-vs-4B question is a deployment decision.
> 5. **The live model is steerable by injection** in the direction of `payment_claimed`; the backend is not. Whether that residual (a verification task a human closes) is acceptable is ADR-0003's question and I have kept its answer; the prompt hardening's measured effect is in the report.
> 6. **Digit normalisation** (D-8): the model sees ASCII digits where the customer typed Arabic-Indic ones; `mentioned_amount_text` is therefore not byte-verbatim for those messages.

**docs/slices/slice-10-review.md** ("Flagged for your decision")
> 1. **Arabic narrative quality** (above). Ship with English email or the AI card hidden in Arabic?
> 2. **Base-currency-only sums** for `collectedYesterday` and `promisesDueToday.amount` — payments and promises carry no fx rate. Other currencies show in `overdueByCurrency` only.
> 3. **Who reads the briefing:** every role with `cases.read`, including a Collector under `collector_sees_only_assigned` — it is the organization's summary, not a scoped view (doc 05 says `cases.read`; PRD-14 scopes the queue). Say if you want it Owner/Accountant only.
> 4. **One AI switch** for classification and narration; doc 05 mentions per-operation enablement.
> 5. **The immutability trigger** uses the UTC date with a one-day slack; the exact rule (today only, in tenant time) is enforced in code. The trigger is a backstop, not the rule.

**docs/slices/slice-11-review.md** ("Flagged for your decision")
> 1. **No invitation flow** — T-121's "invite an Accountant → accept" is replaced by seeding a member. If invitations are wanted in v1, that is a small slice of its own (slice 12, already on its branch).
> 1b. **Visual snapshots are not compared in CI yet** — baselines are font-dependent; run the workflow with `refresh_snapshots`, commit the artifact, then enable `PLAYWRIGHT_SNAPSHOTS=1` in the e2e job. *Closed after slice 30: baselines rendered by run 34697539060 on the runner, committed, compared in the `e2e` job.*
> 2. **Performance tests stay out of CI** (`Category=Performance`), as they need the 50k-invoice seed and minutes of runtime; they run locally on demand.
> 3. The e2e suite shares the developer database in `.env`; each run creates new organizations and never deletes anything (nothing in this system deletes). A dedicated e2e database is a one-line `.env` change if the clutter bothers you.

**docs/slices/slice-13-review.md** ("Flagged for your decision")
> 1. **`mfa_required` after a correct password** discloses that the account has a second factor (question 3). Standard, and strictly better than the alternative; stated.
> 2. **Seven-day grace** (D-1) rather than a hard gate at first login: SEC-02 says "required"; the grace is how a new Owner reaches the enrolment screen at all. The deadline is the server's clock and the check is per request.
> 3. **No way to disable MFA once enrolled**, and no re-enrolment of an active secret without support. A lost device is covered by the eight recovery codes; after those, it is a support task.
> 4. **`MFA_KEK_BASE64` is a new required secret** in every environment (`.env.example`, CI). Without it enrolment answers `mfa_unavailable`, and Owners/Admins would be locked out after their grace — the ops runbook must include it.
> 5. **The breached-password list (SEC-01)** is still not bundled.
> 6. **Transfer makes the previous Owner an Admin** (D-5); Admin is also MFA-required, so nothing loosens.

**docs/slices/slice-14-review.md** ("Flagged, in one place")
> - **`MFA_KEK_BASE64` cannot be rotated in place** (runbook §4). Ciphertexts carry no key id. A rotation means re-enrolment. Carried from slice 13; now written into the runbook as the procedure.
> - **No dual-key window for `JWT_SIGNING_KEY_PEM_BASE64`**: rotation invalidates every access token at once (a 15-minute blip for signed-in users, who refresh transparently). Acceptable at pilot scale; flagged.
> - **`podman-compose` is GPL-2.0** — tool, not dependency (§6). If the reviewer disagrees, the compose file is standard and runs under Docker Compose unchanged.
> - **Alerting (SEC-102, T-153) still has no path.** The runbook names what is page-worthy; nothing pages.
> - **Logs are stdout only.** SEC-101's local store and 90-day retention are the operator's log driver for now.
> - **`postgres` binds `127.0.0.1:5432` on the host** so `backup.sh` and the drill work from the host. SEC-69 forbids a *published* port for PostgreSQL; loopback is not the network, but a reviewer may prefer `podman exec pg_dump` and no binding at all — a one-line change in `compose.yml`.
> - **The CI stack job uses the fake model.** The real-model path (`ollama-pull` → `ai` ready) was verified locally (§5), not in CI, because a 2.5 GB pull per run is not reasonable on a shared runner.
> - **Visual snapshots and performance tests** remain local-only, as recorded in slices 11 and 13.

**docs/slices/slice-15-review.md** ("Flagged, in one place")
> - **Existing dev databases' audit chains** verify differently after the canonicalization fix (§3). Not a production concern yet; recorded so nobody is surprised by an `audit_chain_break` alert on a long-lived dev tenant.
> - **INV-12 stays a test**, not a runtime check: "count(transitions) == count(audit rows)" is scenario-scoped.
> - **INV-04 is recorded as holding by construction** (settlement is derived, never stored) — a reviewer may prefer it omitted rather than listed at zero.
> - **Alerts route to the operator, not per tenant** — an Owner sees them on the Audit screen but is not emailed.
> - **`alert.sh` is webhook-only** (no SMTP client in shell); its stderr reaches cron's mail.
> - **The audit viewer shows no before/after values** — the API does not expose `changes` (doc 06 §6.11 asks for it).
> - **INV-01 is also a check constraint** (`balance_in_range`), so the injected-violation test can only exercise INV-09; the runtime INV-01 check stays for the day the constraint is relaxed.

**docs/slices/slice-16-review.md** ("Flagged, in one place")
> - **Response shapes are not in the OpenAPI document.** Handlers return `IResult`, so every operation shows `200 OK` without a schema; the snapshot pins routes, parameters, request bodies and access, not response types. Typed results (`Results<Ok<T>, …>`) would close this across ~140 handlers — a slice of its own.
> - **T-141's "no sequential scan" is conditional** — *closed after slice 25*: the test now seeds the three tenants T-140 names (two neighbours with 50k invoices each), the measured tenant is a third of the table, and the planner keys the aging read on `tenant_id` through a bitmap heap scan; the assertion requires an index-driven scan whenever the tenant is not the majority and recognises bitmap plans (the original walker knew only `Index*` and `Seq Scan`, and would have failed the spec's own shape). Originally: with one 50k tenant the aging read covered 91 % of the table and a tenant-filtered sequential scan was the planner's correct choice.
> - **Coverage thresholds** (doc 10 slice 0 "coverage") are still not enforced; `coverlet` collects, nobody reads.
> - **The nightly job does not page** — a failure is on the Actions page only. `alert.sh` could be called from the workflow once a CI-side `ALERT_WEBHOOK_URL` secret exists.
> - **Transitive licences are not enumerated** — the notices record direct dependencies and the transitive packages that matter (certifi); the checker reads only direct metadata. `dotnet list package --include-transitive` and `npm ls --all` could feed it later.
> - **The pre-commit hook is opt-in** (`git config core.hooksPath .githooks`); git offers no way to install it for a clone automatically. CI is the backstop.

**docs/slices/slice-17-review.md** ("Flagged, in one place")
> - **The previous JWT key is bound to "issued before this process started" using the token's own `iat`.** A token forged with the old private key *and a back-dated `iat`* would pass while it is unexpired — i.e. for at most 15 minutes after the restart (5 for a proof), which is exactly the window an un-rotated old token has anyway. The rotation removes the standing risk, not the window; the review accepted that the window equals one token life.
> - **`KekRotation` holds every envelope's plaintext in memory briefly** (open → seal). It runs in the operator's process, not the API's; secrets are not logged. A streaming approach would not change the exposure.
> - **Database role passwords still rotate by hand** (runbook §4, unchanged).
> - **The headerless-envelope path tries two keys** — a few extra microseconds only during a rotation and only for pre-slice-17 rows; noted for completeness, not a timing oracle in practice.

**docs/slices/slice-18-review.md**
> - **The declaration is metadata, not the compiler's word.** `.Produces<T>()` beside a handler returning `IResult` can drift from the handler; the guard is that every route is exercised by the integration and security suites, and the snapshot pins what is declared. (Line abbreviated here; the file carries the full sentence.)
> - **`Created` responses do not document `Location`**; 409 (idempotent replay) and 423-style refusals are not enumerated per endpoint — they are problem-shaped like every other failure, which the `4XX` rule covers only for the status codes it names. (Line abbreviated here; the file carries the full sentence.)

**docs/slices/slice-19-review.md**
> - **Coverage floor** Domain 95 / Infrastructure 90 / Api 85 is enforced (today 97.8 / 94.4 / 93.4 — 2.8 / 4.4 / 8.4 points of headroom). A slice that ships code without tests now fails the `api` job.
> - **`changes` can be large** for an import commit (thousands of rows are separate events, but a template version can carry a body); the viewer renders it as text without truncation.

**docs/slices/slice-21-review.md**
> - **Redaction is pattern-based.** Names inside the text are not redacted (a name is not a contact detail, and the labels need the text intact); the consent in T-92 covers this, and the runbook says the export must strip invoice context. (Line abbreviated here; the file carries the full sentence.)
> - **The `ar` bucket merges MSA and dialect**, so the 40/25 split of T-90 is reported as one 65 % target; labelling register would need a fifth column and a labeller instruction.
> - **Nothing here runs the pass** — it needs the pilot, the labellers, the reviewer and the host. The runbook names each.

**docs/slices/slice-22-review.md**
> - Owners are emailed on **critical** alerts only; a per-kind choice was not asked for.
> - Alert mail to Owners uses the account email; there is no separate "alerts" address per Owner.

**docs/slices/slice-23-review.md**
> - **The confirm token is per process.** Two API instances would refuse each other's previews (`confirm_token_stale`); a shared HMAC key (from the signing key material) is the fix when A-15 changes.
> - **Merge refuses rather than resolves** a shared invoice number or two open cases — by design (D-2); the operator resolves one side first.
> - **`GET /organization/holidays` requires `tenant.settings.write`** as doc 05 says; a read-only permission would be friendlier for a Collector who wants to see the calendar.
> - **Manual invoices are not idempotent by header** (payments are, API-08): the duplicate-number rule is the guard against a double submit.

**docs/slices/slice-24-review.md**
> - **Verification mail failures are logged, not surfaced** (an unsendable address still gets the uniform 202); recovery is the forgot-password path (D-4). A resend endpoint is not in doc 05.
> - **`…/email-settings/test` sends a real message to the From address** — System.Net.Mail has no bare handshake; documented in the endpoint.
> - **The webhook trusts the `occurredAt` the MTA sends** (falls back to now); it is recorded, not used for any decision.
> - **Bounces do not yet mark the contact** (`bounced_email` on the customer contact would let the send guard refuse the address next time) — a small follow-up.

**docs/slices/slice-25-review.md**
> - **The list is a snapshot** (SecLists as of 2026-09-12). Refreshing it is a one-line `awk | gzip` documented in the notices row; no automation.
> - **Case-insensitive matching** refuses a few more passwords than a strict list would (e.g. a capitalised variant of a leaked one). That is the intent.
> - **A bounce on a non-contact address** (a message whose `contact_id` is null) marks nothing; only the message status changes.

**docs/slices/slice-26-review.md**
> - **The kill switch off makes the operation toggles inert in the UI** but the API still accepts them (a setting can be prepared while paused). Deliberate; stated in the card's hint.
> - **A third AI operation** means a third column and a third pair of fields — a migration plus contract change, not a config entry (D-1).

**docs/slices/slice-27-review.md**
> - **The load profile shares the API process with the test host** (`WebApplicationFactory`): the twenty clients and the server are one process, so the figure includes no network and the CPU is shared. The host run in the acceptance pass is the number that counts; the nightly is the trend.
> - **Ten minutes on the nightly** adds ten minutes to a job that took about five; the schedule is nightly, so it does not matter, but a manual dispatch now takes a quarter of an hour.
> - **The import budget covers CSV only**; the XLSX reader shares the pipeline after parsing, and the parse itself is bounded by `ImportLimits.MaxRows`.

**docs/slices/slice-28-review.md**
> - **The scan is what axe can see**: structure, names, roles, contrast of rendered text. It does not exercise a screen reader; PRD-25's "screen-reader labels in both languages" is met structurally (every control has a name in both locales).
> - **Screens that need state the seed does not create** (an MFA enrolment in progress, a dispute's evidence upload, the merge preview) are covered by their parent screens' scan; their components share the same primitives (`Field`, `TextInput`, `Button`).
> - **Contrast of the amber/emerald/red badges** passed axe on the seeded data; a new badge colour must keep the `-900` text on `-100` background pattern.

**docs/slices/slice-29-review.md**
> - **~50 s on every `api` job run.** Acceptable; if the suite grows past patience, the golden run can move to its own job without changing the test.
> - **`RowsAsync` reads two columns by position**; it is a test helper and says so.
> - **A schema or rule change that legitimately moves a number** is expected to update the file in the same commit; the review should ask why the number moved.

**docs/slices/slice-30-review.md**
> - **Names are not redacted** by the importer (a name is not a contact detail); the consent in T-92 covers this, as slice 21 recorded.
> - **`--since` filters on the decision time**, so a correction made today for an old message is included; that is the intent (what was learned since the last export).
> - **The 4,000-character skip** is a heuristic for pasted threads; the count is printed so the operator can look at those by hand.

**docs/slices/slice-31-review.md**
> - **Names inside frozen message bodies survive erasure** (D-3) until message retention purges the messages (slice 33). Stated in the erase dialog's wording: "the message record stays".
> - **`erased_by` is a user id**, kept so the audit answers "who"; it is the operator's identity, not the customer's.
> - **The passphrase is a single secret for all backups**; rotating it means re-encrypting kept backups (runbook §1 says so). A per-backup key wrapped by a master key would be the next step if backups leave the host.
> - **`record-restore` must be run by hand** after a real restore; the runbook calls its absence a finding. There is no way for the database to know it was restored.

(Slices 12 and 20 have "Flagged" sections that were not re-extracted in this pass; their contents are
dependency/notices notes — slice 20: `lightningcss` MPL-2.0 Flagged, `xunit.abstractions` via `KNOWN`.)

**Still open today** (not closed by a later slice): slice 2 thresholds; slice 3 FX table, XLSX dates; slice 3b Q-01
placeholder rate, cheque-bounce counter never rolls off; slice 4 withholding/reversal dating, disputed as-of; slice 5
weights in code, C10 reading, extra machine rows; slice 6 `ptp_kept` (F-1), immediate bounce, SM-34 window, manager
notification; slice 7 SLA constants, promise/dispute reading, `dispute_id` FK missing, no as-of dispute history, no
virus scan; slice 8 template approver on auto-send, Collector self-approval, four templates never auto-drafted, Arabic
templates unreviewed; slice 9 all six; slice 10 items 1, 2, 3, 5; slice 13 items 1–3, 6; slice 14 loopback port,
real-model path not in CI; slice 15 INV-12/INV-04, alerts per tenant; slice 16 pre-commit opt-in; slice 17 all four;
slice 18 both; slice 21 all; slices 22–31 all as written.

## Deviations from CLAUDE.md or the approved /docs

First, a framing fact: **every spec document under `/docs` is marked `Status: DRAFT`** (docs 01–10; 08 "awaiting
approval"); `CLAUDE.md` calls them "the approved specification". Nothing in the repo records an approval.

| What the doc says | What was built | Why (as recorded) |
|---|---|---|
| CLAUDE.md Technology: **shadcn/ui** (also doc 06 header) | Four hand-rolled primitives (`components/ui.tsx`); no `shadcn`, `radix` or `class-variance-authority` in `apps/web/package.json` | slice 1 D-5: "the generator pulls a dependency tree that would need licence recording … Revisit at slice 2." Never revisited. |
| CLAUDE.md Technology: **LangGraph**; doc 07 header; A-22 | Not a dependency; two single-shot operations | slice 10 D-1; decision 0008 records the conditions for adding it. |
| CLAUDE.md Technology: **PaddleOCR, Docling** | Not present anywhere | A-23 defers PDF/scan ingestion to after v1; backlog "After v1". |
| CLAUDE.md Technology: **PostgreSQL + pgvector** | The `pgvector/pgvector:pg16` image is used; no `vector` extension or column exists in any migration | DM-26 semantic search is in the backlog's "After v1" list. |
| doc 09 T-03 **Testcontainers** for PostgreSQL | Tests provision a throwaway database on the local PostgreSQL per run | slice 1 D-1: no Docker socket in the environment. |
| doc 07 AI-32 / doc 08 SEC-43: rendered prompt **retained 90 days in an access-controlled inference log** | Not built; only the prompt version and SHA-256 are stored (`ai_suggestions`); no inference log exists | slice 9: "prompt is not retained anywhere yet; only its hash". SEC-43 is therefore satisfied only vacuously. |
| doc 07 `extract_promise` as a separate operation | Folded into the classifier's `extracted` block | slice 9 D-2 (one model call per message). |
| FIN-81 default weights **in tenant settings** | Versioned constants in code; tenant selects a version | slice 5 D-1. |
| SM-48 SLA days **tenant-configurable** | Constants in `DisputeSla` (2/10/5) | slice 7 D-1. |
| doc 03 §2.1 / FIN-20: three balance instruments | Four — withholding reduces the open balance; doc 03 amended | slice 3b D-1/F-1 (E1, A-04). |
| doc 02 case machine | Added `ptp_kept` (`PromiseActive → InProgress`) and four `Open → …` rows | slice 6 F-1, slice 5 #5. |
| doc 04 data model as written | Amended by nearly every slice: `refresh_tokens`, `customers.status`, `import_batches.file_content bytea`, `fn_aging` 4th param, `payment_verification_tasks`, `case_invoices.id`, `tenant_email_settings`, `merged_into_id`, `bounced_at`, `erased_at`, per-operation AI switches (0020) | Each recorded as a DM-xxa amendment in doc 04's status line. |
| doc 03 FX: "base currency at the invoice-date rate" | No rate table; a foreign-currency import row must carry `fx_rate_to_base` or is rejected; payments/promises carry no rate, so briefing sums are base-currency-only | slice 3 D-3; slice 10 flag 2. |
| SEC-44 virus scan of uploads | Type allowlist + content sniff only | slice 7 D-6 ("infrastructure the project does not run"). |
| SEC-69 "no port published for PostgreSQL" | `127.0.0.1:5432` loopback binding on the host (backups/drill need it); smoke.sh only rejects non-loopback | slice 14 flag. |
| SEC-91 retention purge, SEC-92 tenant export/deletion, SEC-30 break-glass, SEC-47 isolated OCR container | Not built. `tenant.delete` permission exists with no endpoint. | Never scheduled by doc 10; slice 31 started §8 (SEC-93/94 only). SEC-30 has no `PlatformSupport` user in v1; SEC-47 follows A-23. |
| doc 09 T-52 "audit-chain hash … compared against checked-in expected values" | Chain *verification* plus audit-event **counts per type** are pinned; no hash is pinned | slice 29 D-2: hashes cover per-run ids/timestamps. |
| doc 09 T-140 "3 tenants × 50k invoices × 200k events" | 3 × 50k invoices; the measured tenant has 100k allocation rows; no "200k events" seed | slices 16/27. |
| doc 09 T-142 load profile | 20 users against an in-process `WebApplicationFactory` (no network) | slice 27 flag. |
| doc 09 T-107 / doc 10 §5.3 (three runs on the live model), T-104 P95 ≤ 8 s, T-90/T-91 corpus, doc 10 acceptance 3 (native Arabic review) | Tooling exists; none has been run against the real model on target hardware or a real corpus. Slice 9's report ran on an author-written 72-item corpus on CPU (30–45 s per call vs. AI-10's 20 s). | Human-gated; `docs/ops/acceptance-pass.md`. |
| CLAUDE.md "Slice Self-Review … write `docs/slices/slice-0X-review.md`" | **No review file for slice 1** | Not recorded anywhere. |
| CLAUDE.md "Record every dependency and model license in THIRD-PARTY-NOTICES.md" | Direct dependencies are recorded (43); 208 transitive packages are licence-checked by `check-notices.py --transitive` but not listed by name | slice 20 decision; see Dependency hygiene. |
| doc 10 slice 5 D-2 "until a scheduler exists" | No scheduler on main today; `POST /cases/sweep` is only ever called by tests and by hand | PR #42 (open) is the fix. |
| Q-01 withholding rate | E1 fixture hard-codes 5 % | doc 00 Q-01 unanswered; Q-02, Q-04, Q-06 also unanswered. |
| `podman compose` (CLAUDE.md "Podman Compose") | `podman-compose` (GPL-2.0 Python tool) in CI and locally | slice 14 D-8: recorded as a tool, not a dependency. |

## Test and build state, right now

All run in this session on `main` at `b3f5cae`, after `git fetch`:

- `dotnet build -c Release`: **0 warnings, 0 errors**. `dotnet format --verify-no-changes`: **exit 0**.
- `FinanceAi.UnitTests`: **673 passed, 0 failed, 0 skipped**.
- `FinanceAi.SecurityTests`: **41 passed, 0 failed, 0 skipped** (1 m 24 s).
- `FinanceAi.IntegrationTests`: **225 passed, 0 failed, 0 skipped** (5 m 55 s). This includes the golden ledger
  (T-52) but **not** the `Performance` category (T-140/141/142), which is nightly-only; last nightly on main (run
  34686449891) was green.
- Web: `npm run build` clean; `vitest`: **80 passed / 19 files**.
- AI service: `pytest`: **91 passed, 20 skipped** — the 20 are `test_injection.py` live-model tests gated on
  `AI_LIVE_TESTS=1` with Ollama running.
- Playwright e2e (both locales, 38 tests), run twice locally:
  - With `PLAYWRIGHT_SNAPSHOTS=1` and the integration suite running concurrently: **23 passed, 3 failed** (T-122 `ar`
    snapshot — expected locally, the baselines are the CI runner's fonts; T-133 allocation-keyboard in `en` and `ar`),
    remainder did not run (serial mode).
  - Clean run, snapshots off as the README prescribes locally: **36 passed, 1 failed, 1 did not run** — the failure is
    again `T-133 keyboard · a payment is opened and allocated without a mouse` in the `ar` project; it **passes when run
    alone** (twice) and passed on CI runs 34698537008 through 34708562639 (5 runs), but **failed on CI for PR #42 (both
    attempts, `ar`: "the second amount is reachable by Tab")**. At audit time this test was flaky and the blocker on
    PR #42 — see the addendum.
- CI on `main` `b3f5cae`: run 34708562639 **all 8 jobs green** (including the first `ops` drill of an encrypted backup).

## Dependency hygiene

Lockfiles found: 8 × `packages.lock.json` (NuGet), `apps/web/package-lock.json`, `tests/e2e/package-lock.json`,
`services/ai/requirements.txt`.

`infrastructure/check-notices.py`: 43 direct dependencies recorded, licences allowlisted. `--transitive`: 254
installed packages (319 in lock files), no copyleft, every weak-copyleft package Flagged.

**But the literal question — does every lockfile package have an entry in THIRD-PARTY-NOTICES.md — is answered
no.** Matching lockfile names against the notices file by name:

- NuGet: 66 distinct packages, **53 not listed**: Microsoft.AspNetCore.TestHost, Microsoft.Bcl.Cryptography,
  Microsoft.CodeCoverage, Microsoft.EntityFrameworkCore.Abstractions / .Analyzers / .Relational,
  Microsoft.Extensions.Caching.Abstractions, .Caching.Memory, .Configuration, .Configuration.Abstractions,
  .Configuration.Binder, .Configuration.CommandLine, .Configuration.EnvironmentVariables, .Configuration.FileExtensions,
  .Configuration.Json, .Configuration.UserSecrets, .DependencyInjection, .DependencyInjection.Abstractions,
  .DependencyModel, .Diagnostics, .Diagnostics.Abstractions, .FileProviders.Abstractions, .FileProviders.Physical,
  .FileSystemGlobbing, .Hosting, .Hosting.Abstractions, .Logging, .Logging.Abstractions, .Logging.Configuration,
  .Logging.Console, .Logging.Debug, .Logging.EventLog, .Logging.EventSource, .Options, .Options.ConfigurationExtensions,
  .Primitives, Microsoft.IdentityModel.Abstractions / .Logging / .Protocols / .Protocols.OpenIdConnect / .Tokens,
  Microsoft.OpenApi, Microsoft.TestPlatform.ObjectModel / .TestHost, **Newtonsoft.Json**, System.Diagnostics.EventLog,
  System.IdentityModel.Tokens.Jwt, xunit.abstractions / .analyzers / .assert / .core / .extensibility.core /
  .extensibility.execution.
- npm: 178 distinct packages, **155 not listed**: @adobe/css-tools, @asamuzakjp/css-color, @asamuzakjp/dom-selector,
  @babel/code-frame, @babel/helper-validator-identifier, @babel/runtime, @bramus/specificity, @csstools/color-helpers,
  @csstools/css-calc, @csstools/css-color-parser, @csstools/css-parser-algorithms, @csstools/css-syntax-patches-for-csstree,
  @csstools/css-tokenizer, @exodus/bytes, @jridgewell/gen-mapping, @jridgewell/remapping, @jridgewell/resolve-uri,
  @jridgewell/sourcemap-codec, @jridgewell/trace-mapping, @oxc-project/types, @rolldown/binding-* (15 platform builds),
  @rolldown/pluginutils, @tailwindcss/node, @tailwindcss/oxide, @tailwindcss/oxide-* (13 platform builds),
  @testing-library/dom, @types/aria-query, @types/chai, @types/deep-eql, @types/estree, @typescript/typescript-* (22
  platform builds), @vitest/mocker, @vitest/spy, ansi-regex, ansi-styles, aria-query, assertion-error, bidi-js, chai,
  css-tree, css.escape, csstype, data-urls, decimal.js, dequal, detect-libc, dom-accessibility-api, enhanced-resolve,
  entities, es-module-lexer, estree-walker, expect-type, fdir, fsevents, graceful-fs, html-encoding-sniffer,
  indent-string, is-potential-custom-element-name, jiti, js-tokens, lightningcss-* (10 platform builds), lru-cache,
  lz-string, magic-string, mdn-data, min-indent, nanoid, obug, parse5, picocolors, picomatch, postcss, pretty-format,
  punycode, react-is, redent, require-from-string, rolldown, saxes, scheduler, siginfo, source-map-js, stackback,
  std-env, strip-indent, symbol-tree, tapable, tinybench, tinyexec, tinyglobby, tldts, tldts-core, tough-cookie, tr46,
  undici, w3c-xmlserializer, webidl-conversions, whatwg-mimetype, whatwg-url, why-is-node-running,
  xml-name-validator, xmlchars.
- pip: 6 packages, **0 missing** (and the notices list pip's transitive tree explicitly).

The repository's stance (slice 20) is that the notices *name* direct dependencies and the checker *licence-verifies*
the whole tree; an `--inventory` markdown can be generated (320 rows) but is not committed. Whether that satisfies
"record every dependency … in THIRD-PARTY-NOTICES.md" is a reviewer's call; as written, it does not.

## Git state

- Branch at the time of this report: `main` (checked out for the test runs). **Working tree clean.**
- `main` vs `origin/main`: **0 ahead, 0 behind** (`b3f5cae`, merge of PR #41).
- Local branches: `main`; `chore/coverage-baseline` and `slice/data-lifecycle-1` (both fully merged, can be deleted);
  `slice/sweep-scheduler` (ahead of main, pushed, = PR #42).
- Remote after `--prune`: `origin/main`, `origin/slice/data-lifecycle-1` (merged, delete pending),
  `origin/slice/sweep-scheduler`.
- Open PRs: **#42 "Slice 32: the daily job runs itself"** — at audit time CI **failed** (e2e job, the flaky T-133 `ar`
  allocation test above; all other jobs green). Not merged.
- Uncommitted work: none. Untracked local-only artefacts: `backups/` (git-ignored), `~/.local/chromium-libs` (outside
  the repo).

## What's next

All ten backlog slices are merged, so "next unstarted slice in backlog order" is none within v1; the backlog's next
section is "After v1 — explicitly deferred", which `CLAUDE.md` forbids starting until the acceptance pass passes.

What actually stands between here and "First Product done", in order:

1. **PR #42 (scheduler)** — was blocked by the flaky e2e test; see the addendum. Without #42, the deployed product
   runs no daily job.
2. **Doc 08 §8 remainder** (not started as code): SEC-92 tenant export + 30-day deletion (planned as slice 33, depends
   on #42's platform pass), SEC-91 retention purge (34).
3. **The acceptance pass** (`docs/ops/acceptance-pass.md`) — blocked on people and hardware, not code: a consented
   pilot tenant and two labellers (T-90/T-91/T-92), the target host with a GPU (Q-06; T-104, T-107, T-140–142), and a
   native Arabic reviewer (doc 10 acceptance 3, slice 8 flag 7). Open questions Q-01, Q-02, Q-04, Q-06 are still
   unanswered in doc 00.
4. **Housekeeping the reviewer may want first**: write `slice-01-review.md`; decide whether transitive packages must
   be named in the notices (208 aren't); mark the `/docs` set approved or say who approves it.

## Addendum — after the audit (same day)

The flaky test turned out to be a real UI bug, not test noise. The allocation panel's load effect depended on `can`
from `useSession`, which is a new function whenever the session object is replaced (a silent token refresh); a
refetch could fire mid-edit and `setLines` reset the amounts the user had typed. Commit `70b48b4` on
`slice/sweep-scheduler` keys the effect on the permission's value and makes the test assert the typed value stuck.
Three consecutive local runs of the accessibility spec were green in both locales (previously 2 of 3 failed in `ar`).
PR #42's CI was re-running on that commit when this file was written; its result is not recorded here.
