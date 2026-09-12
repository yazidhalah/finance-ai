# Slice 30 — Corpus loop and determinism: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **low** (an operator command that reads one tenant's data; harness code).

1. **Endpoints.** None; route pin 160; OpenAPI unchanged.
2. **Tables.** None. The export reads `ai_suggestions` and `inbound_messages` inside a transaction with `app.tenant_id` set to the named tenant and platform scope off — the same RLS path the application takes; `Corrections_BecomeProposals_ForTheNamedTenantOnly` seeds a second tenant's correction and asserts it is absent.
3. **Pre-auth paths.** The command runs with the migrator role from the operator's shell, like `rotate-mfa-kek` and `review-pack`; it has no network surface. It refuses to run without a tenant id and a consent reference; the transaction is rolled back (read-only by construction).
4. **Money.** The human's amount is copied as the stored `F3` string into `expect`; nothing is computed.
5. **AI.** The export contains customer text destined for the evaluation harness, not for the model in production; it goes through the importer's redaction before it becomes a corpus item (proven in `test_evaluate_extras.py`). The determinism check adds model calls only in the harness.
6. **Dependencies.** None added.

## Flagged

- **Names are not redacted** by the importer (a name is not a contact detail); the consent in T-92 covers this, as slice 21 recorded.
- **`--since` filters on the decision time**, so a correction made today for an old message is included; that is the intent (what was learned since the last export).
- **The 4,000-character skip** is a heuristic for pasted threads; the count is printed so the operator can look at those by hand.
