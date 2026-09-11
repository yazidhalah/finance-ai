# Slice 15 — Operations: invariants, alerts, the audit screen — self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **medium** — nothing here writes to a business table, but it is the path by which a P1 reaches a human,
and it found a real defect on its first run (§3).

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

Four, all through the middleware, none anonymous. Route pin 143 → 147; the anonymous set is unchanged at six.

| Endpoint | Permission | Why |
|----------|-----------|-----|
| `GET /organization/invariants` | `audit.read` | Integrity is what the audit reader reads. `{ run: null }` until the first run. |
| `POST /organization/invariants/run` | `tenant.settings.write` | Writes an `invariant_runs` row, may raise alerts, is audited (`invariants.run`). |
| `GET /organization/alerts` | `audit.read` | Open by default, `?all=1` for acknowledged. |
| `POST /organization/alerts/{id}/acknowledge` | `tenant.settings.write` | Audited (`alert.acknowledged`); 404 across tenants (`Ops_IsTenantScoped`). |

`GET /audit` gained two filters (`eventType`, `actorUserId`) — same permission, same middleware. A Collector gets 403
on all four (`Sweep_RunsTheJob_AndPermissionsHold`) and the authorization sweep addresses the alert route with B's
real alert id.

## 2. Every new table: `tenant_id`, RLS enabled and forced, a policy, composite FKs?

Two tables in `0014_invariants_and_alerts.sql`, both tenant-scoped, RLS enabled **and** forced, one
`tenant_isolation` policy each, `UNIQUE (tenant_id, id)`; neither references another tenant-scoped table (an
alert's `details` carries a run id as data, deliberately not a foreign key — an alert must survive whatever happens
to the run it cites). `invariant_runs` has no UPDATE or DELETE grant; `alerts` has no DELETE. The enumeration in
`TenantIsolationTests` picks both up automatically; the targeted test is `Ops_IsTenantScoped`: A's scope counts zero
of B's rows, and an insert into A's scope with B's tenant id is refused with `42501`.

## 3. Code paths before a tenant / full authentication, or across tenants by design?

None before authentication. Two things run "by design" over more than a request's worth of data:

- **The invariant SQL** runs on the tenant-scoped connection with `tenant_id = @t` in every statement *and* RLS
  underneath; INV-05 is what stops it seeing anyone else. Every branch does the same work whether or not there are
  violations (one statement per check; `LIMIT 5` on samples only).
- **The alert path** delivers to operator addresses from `.env`; it never takes an address or URL from a request
  (SEC-66). Delivery failures are recorded, never thrown (`AlertDelivery_IsIndependentOfTheOutboundSwitch`).

**What the first run found:** `AuditChainVerifier` reported every fresh tenant's chain broken at the first event
that carried `changes`. `changes` is `jsonb`; PostgreSQL returns its own key order and spacing, so the hash computed
over the string as written never matched the string read back. SEC-53 verification had only ever been exercised on
events without `changes`. Fixed in `AuditHash.CanonicalJson` — object keys sorted at every level, no whitespace,
no escapes — used by the writer and the verifier alike (`Compute_IsIndifferentToJsonbsFormattingOfChanges`). Any
existing chain with `changes` rows (dev databases only; there is no production) recomputes differently after this
and would report a break at its first such row; a fresh migration or a re-seed is the remedy, recorded here.

## 4. Money-related fields or calculations?

The invariant SQL compares `numeric(19,3)` columns to sums of `numeric(19,3)` columns inside PostgreSQL; INV-08
compares the aging report's per-currency `Total` (a `decimal` the report already computed) with an EF `Sum` of
`balance_cache` (`decimal`). No new arithmetic, no rounding, no float. Alerts carry counts and ids — the
integration test asserts the mail names the organization and **not** the customer or the amount (SEC-41).

## 5. AI-touching code?

`ai_guard_rejection_spike` reads `ai_suggestions.validation_status` counts. It changes nothing about how a
suggestion is validated or approved; the AI's output still cannot finalize a financial fact. INV-13 is the runtime
assertion of exactly that rule: an Active promise, a sent message or an AI-sourced dispute past Open with no human
id is a violation.

## 6. New dependencies?

None. The webhook uses `HttpClient`; canonical JSON uses `System.Text.Json.Nodes`; `alert.sh` uses `curl` and
`python3`, both already required by the scripts around it. THIRD-PARTY-NOTICES is unchanged.

## Flagged, in one place

- **Existing dev databases' audit chains** verify differently after the canonicalization fix (§3). Not a production
  concern yet; recorded so nobody is surprised by an `audit_chain_break` alert on a long-lived dev tenant.
- **INV-12 stays a test**, not a runtime check: "count(transitions) == count(audit rows)" is scenario-scoped.
- **INV-04 is recorded as holding by construction** (settlement is derived, never stored) — a reviewer may prefer
  it omitted rather than listed at zero.
- **Alerts route to the operator, not per tenant** — an Owner sees them on the Audit screen but is not emailed.
- **`alert.sh` is webhook-only** (no SMTP client in shell); its stderr reaches cron's mail.
- **The audit viewer shows no before/after values** — the API does not expose `changes` (doc 06 §6.11 asks for it).
- **INV-01 is also a check constraint** (`balance_in_range`), so the injected-violation test can only exercise INV-09;
  the runtime INV-01 check stays for the day the constraint is relaxed.
