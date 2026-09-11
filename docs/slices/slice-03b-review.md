# Slice 3b — Payments & Allocation: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **medium** (per the slice plan), with the write-off path treated as high.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**Through the middleware, all twenty-four. None is anonymous.**

| Endpoints | Declaration |
|-----------|-------------|
| `POST /payments`, `POST /payments/{id}/reverse`, `POST /cheques`, `POST /cheques/{id}/transitions`, `POST /invoices/{id}/withholding` | `RequiresPermission(payments.write)` |
| `GET /payments`, `GET /payments/{id}`, `GET /cheques`, `GET /cheques/{id}`, `GET /credit-notes`, `GET /credit-notes/{id}`, `GET /write-offs`, `GET /write-offs/{id}` | `RequiresPermission(payments.read)` |
| `GET /payments/{id}/allocation-proposal`, `POST /payments/{id}/allocations`, `POST /allocations/{id}/reverse` | `RequiresPermission(payments.allocate)` |
| `POST /credit-notes`, `POST /credit-notes/{id}/applications`, `POST /credit-notes/{id}/void` | `RequiresPermission(credit_notes.write)` |
| `POST /invoices/{id}/write-off` | `RequiresPermission(writeoff.propose)` |
| `POST /write-offs/{id}/approve`, `…/reject`, `…/reverse` | `RequiresPermission(writeoff.approve)` |
| `POST /invoices/{id}/void` | `RequiresPermission(invoices.void)` |

`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel` pins the route count at **60** and
the anonymous set at exactly `register`, `login`, `refresh`. The generated 401/403/404 sweeps
cover all twenty-four: `ForeignIds` gained `PaymentId`, `AllocationId`, `ChequeId`, `CreditNoteId`,
`WriteOffId`, seeded in tenant B, so every `{id}` route is exercised with an id that exists but is
not ours. `payments.read` joined the pinned universal-permission list (every role in doc 01 §5.1
holds it), so its boundary is tenancy, tested in question 2.

Two endpoint-shape notes:

- **`POST /payments` requires `Idempotency-Key`** (428 without it). A replay with the same key
  and body returns the original 201 body; the same key with a different body is `409
  idempotency_key_reused`. The key is unique *per tenant*, so tenant B cannot collide with or
  probe tenant A's keys. `Payment_IsIdempotent`.
- **Every rule violation is `422 business_rule_violated`** with the rule code in `errors[].code`
  and, where the message needs a fact (the invoice's open balance, the payment's remaining
  amount), that fact in `errors[].meta` — computed on the server, never in the browser (AC-17).

## 2. Every new table: `tenant_id`, RLS enabled AND forced, a policy, composite FK?

**Yes, all seven — composite keys in every direction, including self-references.**

| Table | `tenant_id NOT NULL` | RLS forced | Policy (no platform clause) | `(tenant_id, id)` key | Composite FKs |
|-------|:--:|:--:|:--:|:--:|---|
| `cheques` | ✅ | ✅ | ✅ | ✅ | `→ customers`, `→ payments` |
| `payments` | ✅ | ✅ | ✅ | ✅ | `→ customers`, `→ cheques` |
| `payment_allocations` | ✅ | ✅ | ✅ | ✅ | `→ payments`, `→ invoices`, `→ payment_allocations` (reversal) |
| `withholding_deductions` | ✅ | ✅ | ✅ | ✅ | `→ invoices`, `→ payments`, `→ withholding_deductions` (reversal) |
| `credit_notes` | ✅ | ✅ | ✅ | ✅ | `→ customers` |
| `credit_note_applications` | ✅ | ✅ | ✅ | ✅ | `→ credit_notes`, `→ invoices`, `→ credit_note_applications` (reversal) |
| `write_offs` | ✅ | ✅ | ✅ | ✅ | `→ invoices` |

The enumeration test now walks **19** tables. `NoSingleColumnForeignKey_JoinsTwoTenantScopedTables`
confirms every FK between tenant-scoped tables carries `tenant_id`. **`finance_app` holds no
DELETE on any of the seven** — reversal is a compensating row (FIN-23), and the grant makes the
rule structural rather than a convention.

**Targeted tests for the non-standard patterns** (`LedgerIsolationTests`, `LedgerRuleTests`):

- **Layer 3 alone**: `CrossTenantAllocation_IsRejectedByCompositeForeignKey` inserts, as
  superuser with RLS bypassed, an allocation in A pointing at B's invoice and a payment in A for
  B's customer. Both refused by the composite key.
- **Raw SQL under RLS**: the three `SELECT 1 FROM … WHERE id = @id FOR UPDATE` lock statements in
  `LedgerService.LockAsync` run inside the request transaction with the tenant GUC set, so the
  policy filters them like any other statement; a foreign id locks nothing and the subsequent EF
  load answers 404 (the sweep proves the 404; `ConcurrentAllocations_ExactlyOneWins` proves the
  lock does its job within a tenant).
- **Aggregation**: `CustomerPosition_IsComputedWithinOneTenant` seeds the same customer id in two
  tenants with different ledgers and checks each tenant's position reflects only its own rows.
- **Constraint triggers**: `allocation_within_payment` and `application_within_credit_note` read
  sibling rows with the same `tenant_id` and fire as the table owner. `DatabaseTriggers_HoldWithoutTheApplication`
  bypasses the service and proves the cap holds; a tenant-A row can never be counted against a
  tenant-B payment because the trigger's `WHERE` carries `tenant_id` from `NEW`.

## 3. Any new code path before a tenant is established, or querying across tenants by design?

**None.** Nothing added to `PlatformIdentityStore`; the static test still confines
`EnterPlatformAsync` / `IgnoreQueryFilters` to that file. All twenty-four handlers run after the
tenant is bound and the membership re-read.

Ordering within a handler follows slices 2 and 3a: every `{id}` route loads the aggregate first
and answers 404 before the body is examined, so a foreign id discloses nothing about what would
have been valid. The idempotency check on `POST /payments` runs *after* tenant binding, so a
replayed key from another tenant is simply a new key.

## 4. Any new money-related field or calculation?

**Fields: twenty-two `numeric(19,3)` columns across the seven tables. Calculations: many — all
exact, and rounding still happens nowhere.**

| Item | As built |
|------|----------|
| Every amount column | `numeric(19,3)` / `decimal`. `EveryMoneyColumn_IsNumeric19_3` enumerates `information_schema` and now covers the seven new tables. `rate_pct` on `withholding_deductions` is `numeric(5,2)` — a rate, not an amount. |
| Balance formula | `LedgerRules.OpenBalance = total − Σ active allocations − Σ active credit applications − Σ approved write-offs − Σ active withholding`. Withholding is the **fourth instrument** (D-1; flagged F-1 below). `InvoiceBalance.Recompute` remains the sole writer of `balance_cache`; `RecomputeAsync` is the only caller. |
| Settlement | `open == 0m` → Paid; `open == total` → Unpaid; else PartiallyPaid. Exact comparison, no tolerance (FIN-13). `OpenBalance_AndSettlement_AreExact`. |
| Allocation | Each line is validated `≥ 0.001`, `≤ invoice open balance` and `Σ lines ≤ payment remaining` under `FOR UPDATE`; the database repeats the payment cap in a constraint trigger and the invoice range in `balance_in_range`. `Allocation_RejectsBadLines`, `ConcurrentAllocations_ExactlyOneWins`. |
| FIFO proposal | `LedgerRules.ProposeFifo` walks oldest due date, then invoice number, allocating `min(remaining, open)` — exact remainders, nothing rounded. It is a **proposal**: `Proposal_IsFifoAndNotApplied` proves the GET writes nothing. |
| Residual (FIN-14) | When an allocation leaves `0 < open < auto_clear_residual_below`, the response carries `proposedRoundingAdjustment = open`. Nothing is cleared; a human issues a `rounding_adjustment` credit note. `E5_RoundingResidual_IsProposedNotAssumed`. |
| Withholding | `withheld_amount` is **entered from the certificate**, never computed from `rate_pct`; `base_amount`, `rate_pct` and the amount are all stored so the reconciliation is visible. `E1_WithholdingTax`. |
| Write-off amount | Taken from the computed open balance at proposal time, never from the request (FIN-32). `WriteOff_FourEyes_UsesComputedBalance_AndReverses`. |
| Reversal | A compensating row with `reversal_of_id`; both rows `is_active = false`; `RecomputeAsync` re-derives from active rows. `Reversal_IsACompensatingRow_AndReopens`. |
| Customer position | `GROUP BY currency`; open balance, unapplied cash and unapplied credit are three separate per-currency figures, never combined. `E6_MultiCurrency_NeverSummed`. |
| Cache integrity | `BalanceReconciliation` re-derives every invoice's balance from rows and reports mismatches (INV-09/INV-10). `BalanceCache_MatchesDerived_AndCorruptionIsDetected` corrupts the cache directly and expects it to be caught. |
| Invariant tests | `RandomizedLedger_HoldsAllInvariants` (unit, 2,000 steps against an in-memory model) and `RandomizedLedger_AgainstTheDatabase` (integration, seeded walk through the real service) assert INV-01, INV-02, FIN-21, FIN-22, the settlement/status coupling and the cache after every step. The seed is printed on failure (D-4). |
| **Rounding** | **Nowhere in this slice.** Every input is scale ≤ 3 or refused (`MoneyInput.TryParse`); every derived figure is a sum or difference of scale-3 decimals; FIFO allocates exact remainders. The first rounding in the product will be base-currency conversion (slice 4) and must state its rule there. |
| Frontend (AC-17) | The allocation screen pre-fills the server proposal, passes edited strings through verbatim and displays `unallocated` / residuals from the response body. `ledger.test.tsx` asserts the request body and the rendered figures, and greps `Payments.tsx` / `Ledger.tsx` for any arithmetic on amount strings. |

## 5. Any AI-touching code?

**None.**

## 6. Any new dependency?

**None.** The randomized invariant tests use `System.Random` with a fixed seed (D-4) rather than
FsCheck. `THIRD-PARTY-NOTICES.md` is unchanged apart from the "last updated" line.

## Not sure / flagged

Nothing among the six is "not sure". Six things deserve the reviewer's attention:

1. **F-1 — Withholding in the balance formula.** Doc 03 §2.1 and FIN-20 list three instruments;
   E1 and A-04 require a fourth. This slice follows the worked example and amends doc 03. If the
   reviewer disagrees, the change is one term in `LedgerRules.OpenBalance` and the E1 test.
2. **F-2 — Money moving out of a written-off invoice reverses the write-off (I8).** Found by the
   randomized walk, not by reading the spec: reversing an allocation on a `WrittenOff` invoice left
   it written off *with* a balance. SM-13 already covers payments arriving on a written-off
   invoice; the same rule now applies to allocation reversal and credit-note void
   (`ReopenIfWrittenOffAsync`), audited as `high_severity`. Regression:
   `MoneyMovingOutOfAWrittenOffInvoice_ReversesTheWriteOff`.
3. **Re-authentication on write-off approval (SEC-09) is deferred (D-6)** until MFA / re-auth
   exists (slice 1b). Four eyes and self-approval acknowledgement are enforced and audited today.
4. **E1's withholding rate is a placeholder** (Q-01 unanswered). The worked example runs at 5%;
   the product computes nothing from the rate, so the answer changes only the fixture.
5. **The FIFO proposal does not yet skip disputed invoices** — disputes do not exist until slice 7.
   `ProposeFifo` takes a candidate list, so the filter is one predicate when they arrive.
6. **Bounce handling is on the customer, not just the cheque.** `bounced_cheque_count_12m` is
   incremented on bounce and never decremented; a rolling 12-month recompute belongs to the aging
   slice, which reads it. Stated so nobody expects the counter to roll off on its own.
