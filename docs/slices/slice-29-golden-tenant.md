# Slice 29 — The golden tenant (T-52)

Status: **Implemented and tested** (the `api` CI job runs it with the integration suite).

Source: doc 09 §3 T-52 — "a seeded golden tenant with ~2,000 invoices and a scripted year of activity is rebuilt in
CI; its aging report, per-customer balances, and audit-chain hash are compared against checked-in expected values.
Any change to financial logic that shifts a number must consciously update the golden file." Slice 15's "golden
scenario" ran the invariants on a small ledger; nothing was checked in to compare against.

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **The script** (`GoldenTenantTests`): from `Random(52)`, 40 customers (34 JOD, 6 USD at a fixed 0.709 rate; terms 15/30/45/60), one 2,000-row import spread over the year before as-of, then for every invoice due more than five days ago a roll of the dice: 55 % paid in full (explicit allocation), 15 % half-paid, 8 % short-paid with 5 % withholding on the net (E1), 6 % paid with no lines and allocated by the FIFO proposal, 4 % credited a tenth by an applied credit note, 12 % left overdue; every 40th payment reversed; then the sweep. Everything goes through the product's endpoints — nothing is inserted directly. |
| S2 | **The golden file** `tests/integration/FinanceAi.IntegrationTests/Golden/golden-ledger.json`: the aging report per currency (buckets with amounts and counts, totals, unapplied cash and credit, every customer's total and count), the base-currency total, invoice status counts, the reconciliation result, the invariant run's status (which includes the SEC-53 chain verification), and the audit trail as counts per event type. |
| S3 | **The comparison**: exact text equality; a mismatch fails with the first differing lines and the instruction to rerun with `UPDATE_GOLDEN=1`, which rewrites the source file for the commit that consciously accepts the change. |

## 2. What the golden ledger contains (seed 52)

1,499 payments (259 partial, 143 with withholding, 104 by FIFO proposal), 64 credit notes, 37 reversals;
1,224 invoices settled and 776 open; JOD 1,639,262.156 open over 666 invoices, USD across 110; reconciliation
0 mismatches over 2,000 invoices; invariants `ok`; 3,292 `invoice.status_changed` events among 18 event types.

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Dates are relative to the real day, offsets fixed by the seed | The database's own immutability rule on briefings reads `current_date`; a clock pinned to a fixed past date collided with it. Every figure in the file depends on offsets (days past due, received before as-of), none on the calendar date, so the file is stable across days. Verified: two runs, byte-identical. |
| D-2 | The audit chain is verified, and the trail is pinned as counts per event type — not as a hash | Every event's hash covers ids and timestamps that are new on each run; the chain's *integrity* is what SEC-53 asserts (the invariant run), and the *shape* of the trail (how many of what) is what a logic change would move. A hash of the trail would fail on every run and prove nothing. |
| D-3 | Exact string comparison, a diff of lines, a one-flag update | The point is that a number moving is noticed and accepted on purpose; tolerance would defeat it. |
| D-4 | Regular category, not Performance | ~50 s locally; it belongs with every PR, because that is where financial logic changes. |

## 4. Proven

The stale placeholder file (`{}`) failed the comparison with the diff shown; the generated file passed on the next
run without regeneration.
