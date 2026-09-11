# Slice 7 — Disputes: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **medium** (per the slice plan). Money moves once — through a credit note — and only on
a human's acceptance.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all ten. None is anonymous.**

| Endpoints | Declaration |
|-----------|-------------|
| `POST /invoices/{id}/disputes`, `POST /disputes/{id}/transitions`, `POST /disputes/{id}/evidence` | `RequiresPermission(disputes.write)` |
| `POST /disputes/{id}/resolve` | `RequiresPermission(disputes.resolve)` — SM-47 |
| `GET /disputes`, `GET /disputes/{id}`, `GET /disputes/{id}/evidence/{evidenceId}`, `GET /cases/{id}/dunning-eligibility` | `RequiresPermission(cases.read)` |
| `GET /tasks/payment-verification` | `RequiresPermission(payments.read)` |
| `POST /tasks/payment-verification/{id}/resolve` | `RequiresPermission(payments.write)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at **93**.
The sweep covers every `{id}` route with B's real dispute, evidence and task ids
(`ForeignIds.DisputeId / EvidenceId / TaskId`, seeded through an `already_paid` dispute with a PDF).
`Collector_CannotResolve` proves T-60 directly: a Collector raises (201) and assigns (200) but gets
403 on `/resolve`; an Accountant resolves and is recorded as `resolvedBy`. `accept` /
`partially_accept` / `reject` are refused as `transitions` events (400) — the resolution door is the
only one, and its permission is different.

The evidence upload is multipart (`IFormFile`, antiforgery disabled, bearer auth — the slice 3a
reasoning), so the sweep speaks multipart to it. The download sets `nosniff` and
`Content-Disposition: attachment`.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, all three.**

| Table | `tenant_id NOT NULL` | RLS forced | Policy (no platform clause) | `(tenant_id, id)` key | Composite FKs |
|-------|:--:|:--:|:--:|:--:|---|
| `disputes` | ✅ | ✅ | ✅ | ✅ | `→ invoices`, `→ customers`, `→ collection_cases`, `→ credit_notes` |
| `dispute_evidence` | ✅ | ✅ | ✅ | ✅ | `→ disputes` |
| `payment_verification_tasks` | ✅ | ✅ | ✅ | ✅ | `→ invoices`, `→ customers`, `→ disputes`, `→ payments` |

The enumeration test now walks **28** tables. No DELETE on any of the three. `dispute_evidence`
has no reporting grant: a file is not a report.

**Targeted tests for the non-standard patterns** (`DisputeIsolationTests`, `DisputeTests`):

- **Layer 3 alone** — `CrossTenantDispute_IsRejected`: as superuser, a dispute in A on B's
  invoice (`fk_dispute_invoice`), evidence in B on A's dispute (`fk_evidence_dispute`), a task in
  B on A's dispute (`fk_pvt_dispute`) — all refused.
- **Wide reads that now carry dispute data** — the same test: B's dispute list is empty, B's
  aging `disputedTotal` is `0.000` while A's is `400.000`, B's queue row shows `openDisputes: 0`.
- **Evidence bytes** — `Evidence_IsAllowlistedAndIsolated`: another tenant's download of the same
  evidence id is 404.
- **The credit note an acceptance creates** goes through `LedgerService`, whose own isolation
  tests (3b) cover the rows it writes; `credit_notes.dispute_id` is filled but carries no FK to
  keep 0004 unchanged (flagged below).

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`. `DisputeService` reaches the case layer
directly and the sweep reaches it through `IDisputeHooks` (same request, same bound tenant).

## 4. Any new money-related field or calculation?

**Two descriptive `numeric(19,3)` columns, one exact comparison, one exact minimum, and the credit
note the ledger already knew how to make.**

| Item | As built |
|------|----------|
| `disputed_amount`, `resolution_amount` | `numeric(19,3)` / `decimal`; `EveryMoneyColumn_IsNumeric19_3` covers them. Neither is ever subtracted from a balance (SM-41). |
| Raise guard | `disputed_amount ≤ balance_cache`, exact. `Raise_Guards_AndSlaClocks`. |
| Resolution guard (SM-46) | `DisputeRules.CheckResolutionAmount`: `accept` requires `disputed ≤ open` *now*; `partially_accept` requires `0 < amount < disputed` and `≤ open`; `reject` takes none. `ResolutionAmount_IsChecked` (unit, 10 cases). |
| **SM-45 atomicity** | `ResolveAsync` writes the dispute, then `ledger.CreateCreditNoteAsync` + `ledger.ApplyCreditNoteAsync` — the ledger's own `exceeds_open_balance` (its SM-46) throws inside the request transaction and unwinds the dispute write too. `Accept_BeyondOpenBalance_RollsBackEverything` asserts: 422, dispute still `UnderReview`, zero credit notes, zero `Accepted` audit rows. `Accept_CreatesAndAppliesTheCreditNote_E3` runs E3 to `Settled` and the case to `Resolved` through SM-50. |
| Aging (FIN-56) | `disputedAmount` per bucket / section / customer row = Σ open `disputed_amount` per invoice **capped at the invoice's open balance** (`DisputeRules.DisputedForAging`), still inside the bucket amount. `disputedAvailable: true`. `DisputedForAging_IsCappedAtTheOpenBalance`, `Accept_…_E3` (aging assertion). |
| SLA clocks | Business-day arithmetic on dates; the pause shifts `resolution_due_at` by the exact `now − pending_since`. Not money. `SlaDueAt_IsBusinessDaysAtEndOfDay`, `Sla_PausesAndBreaches`. |
| Frontend | `Disputes.tsx` computes nothing; the credit-note preview is the typed figure or the disputed amount verbatim; `disputes.test.tsx` greps for arithmetic. |

**Nothing rounds.** The verification task never touches an invoice: `AlreadyPaid_OpensAVerificationTask`
records the payment through `/payments` and the invoice settles there; the task only points at it,
and three plausible "mark paid" routes are asserted 404.

## 5. Any AI-touching code?

**None runs.** `source` admits `customer_email` / `ai_suggested` and `ai_suggestion_id` exists so
slice 9 can raise a dispute from a reply classification — into `Open`, like any other. Resolution
is a human action against a named user by construction: `disputes.resolve` on the route,
`resolved_by IS NOT NULL` in the `resolved_by_a_human` CHECK for every terminal state that decides
the claim, and the only system-actor transition is `timeout`, which merely returns a pending dispute
to review (`DisputeTransitions_AreAudited_OneToOne` shows the actor on every row). The reason set
is a CHECK, so an AI can never invent a reason (SM-43). Evidence bytes are never parsed and never
handed to a model (SEC-40).

## 6. Any new dependency?

**None.** Evidence sniffing is three magic-byte checks in `EvidenceInspector`.
`THIRD-PARTY-NOTICES.md` is unchanged apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Six things deserve the reviewer's attention:

1. **SLA days are constants (D-1):** 2 / 10 business days and a 5-business-day pending timeout in
   `DisputeSla`. SM-48 says tenant-configurable; `tenant_settings` has no columns for them. Adding
   three integer columns is a one-line migration when someone needs it.
2. **`allow_split_dunning_during_dispute` is enforced by a read endpoint, not a send.** There is
   no message to send until slice 8. `GET /cases/{id}/dunning-eligibility` returns
   `dispute_blocks_send` per invoice and the case screen disables its (still inert) send control
   with that reason. Slice 8's send path **must** call `DisputeService.DunningEligibilityAsync`;
   the review of that slice should look for it.
3. **A dispute opened on a `PromiseActive` case lifts the queue suppression** so the case shows as
   `Disputed` work (`resolve_dispute`) rather than staying hidden until the promise deadline. SM-52
   says the promise "stays Active but stops driving reminders"; this is the reading.
4. **The credit note's `dispute_id` has no foreign key.** The column exists from 0004; adding the
   composite FK means altering a 3b table. The link is written by the service and read by nothing
   yet. Worth a two-line migration in the slice that first reads it.
5. **Disputes have no as-of history.** The aging report's disputed column is *today's* open
   disputes even when `asOf` is in the past. Stated in the slice doc; a `raised_at` / `resolved_at`
   window would be a small change if a reproducible past disputed figure is ever needed.
6. **Evidence is stored as `bytea` capped at 10 MB with a three-type allowlist and no scanner
   (D-5).** SEC-44's virus scan is infrastructure the project does not run; the allowlist and the
   sniff are what stand in for it.
