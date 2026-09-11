# Slice 22 — The deferred items: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **low** (a type-level refactor with an identical contract, and one setting).

1. **Endpoints.** One added, `PATCH /organization/alert-settings`, `tenant.settings.write`, through the middleware, audited; the authorization sweep covers it (pin 148). No anonymous change.
2. **Tables.** No new table; `tenant_settings` gains a boolean (migration 0016). RLS unchanged.
3. **Pre-auth / cross-tenant paths.** None. Owner recipients are resolved inside the tenant's scope (`TenantMemberships` ∩ `Users` under the current tenant); the test shows the other tenant untouched.
4. **Money.** None.
5. **AI.** None.
6. **Dependencies.** None.

## Verified locally

- `grep -rn "IResult" apps/api/FinanceAi.Api/Endpoints/` → only the `using` line; 147 handlers typed; build clean; one real defect found and fixed (§2 of the slice doc).
- Snapshot diff: 18 × `Location`, 1 × `POST /payments` 200. 663 unit · 202 integration · 41 security · 75 web.

## Flagged

- Owners are emailed on **critical** alerts only; a per-kind choice was not asked for.
- Alert mail to Owners uses the account email; there is no separate "alerts" address per Owner.
