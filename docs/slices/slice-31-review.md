# Slice 31 — Data lifecycle I: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **medium** (an irreversible write; operator tooling on backups).

1. **Endpoints.** One: `POST /customers/{id}/contacts/{cid}/erase` — authenticated, `customers.write`, `RequiresReauth`, routed through `TenantScopeMiddleware`; route pin 161; the sweep exercises it (a substituted `contactId`), the OpenAPI snapshot is regenerated and shows `x-requires-reauthentication`.
2. **Tables.** None new; `customer_contacts` gains `erased_at`, `erased_by` (migration 0021). RLS unchanged. `Erase_IsTenantScoped` is the targeted test; the update of `messages` and `inbound_messages` runs inside the request's tenant scope, so a cross-tenant address match is impossible by policy, not only by filter.
3. **Pre-auth paths.** None in the API. `record-restore` and `backup.sh` run from the operator's shell with the migrator/admin credentials; `record-restore` enters each tenant's scope in turn (platform scope only to list tenants, rolled back), takes the same advisory lock as `AuditWriter`, and inserts one row per tenant. Every branch reads the dump file fully (for the digest) before touching the database.
4. **Money.** None.
5. **AI.** None.
6. **Dependencies.** None added (`openssl` is already relied on by the stack scripts and CI).

## Flagged

- **Names inside frozen message bodies survive erasure** (D-3) until message retention purges the messages (slice 33). Stated in the erase dialog's wording: "the message record stays".
- **`erased_by` is a user id**, kept so the audit answers "who"; it is the operator's identity, not the customer's.
- **The passphrase is a single secret for all backups**; rotating it means re-encrypting kept backups (runbook §1 says so). A per-backup key wrapped by a master key would be the next step if backups leave the host.
- **`record-restore` must be run by hand** after a real restore; the runbook calls its absence a finding. There is no way for the database to know it was restored.
