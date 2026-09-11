# Slice 12 — Member invitations, role management, restore drill: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **high** — a new anonymous endpoint that creates accounts and memberships.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

Five through the middleware, **one deliberately anonymous.** Route pin 131 → 137; the anonymous set in the
sweep grows from three to four and is pinned by name.

| Endpoint | Declaration | Why |
|----------|-------------|-----|
| `POST /organization/members/invite` | `users.invite` | |
| `GET /organization/invitations` | `users.read` | |
| `POST /organization/invitations/{id}/revoke` | `users.invite` | |
| `PATCH /organization/members/{id}` | `users.role.write` | |
| `POST /organization/members/{id}/deactivate` | `users.deactivate` | |
| `POST /auth/accept-invitation` | **anonymous**, in the `/auth` group's rate limit | The invitee has no session. The token in the body (256 random bits, single use, 7 days) is the credential; it was delivered to the invited address by the tenant's own invitation. |

## 2. Every new table: `tenant_id`, RLS enabled and forced, a policy, composite FKs?

**Yes.** `member_invitations`: `tenant_id NOT NULL → tenants`, RLS ENABLE + FORCE, `tenant_isolation` with the
platform-scope clause (the same shape as `refresh_tokens`, for the same reason: the accept flow runs before a
tenant is bound), `UNIQUE (tenant_id, id)`, `invited_by` / `accepted_user_id` → `users` (platform table, plain
FK as everywhere else), no DELETE. The enumeration test walks 34 tables. Targeted test
`MemberIsolationTests.CrossTenantMembers_IsRejected`: B lists none of A's invitations, gets 404 on revoke / role
change / deactivate for A's ids, sees none of A's members; under B's tenant scope A's invitation does not exist
and a row for A's tenant cannot be forged (`42501`).

## 3. Code paths before a tenant or full authentication, or cross-tenant by design? Timing and disclosure per branch.

**One new path: `PlatformIdentityStore.AcceptInvitationAsync`.** It is the only place this slice reads across
tenants, and it reads by the hash of the token. Branch by branch:

| Branch | Work done before answering | What it discloses |
|--------|----------------------------|-------------------|
| Token is not 64 lowercase hex | nothing — returns before any query or hash | that the syntax was wrong; no account or tenant information (the token space is not guessable, so this is a format check, not an oracle) |
| Hash not found | SHA-256 + one indexed lookup | `invitation_invalid` |
| Found but expired / revoked / used | the lookup + a second transaction with `FOR UPDATE` and a full fetch | `invitation_invalid` — identical body; the fetch is done before the decision |
| Valid, address has no user, no password given | all of the above + the user lookup | `password_required` — **this branch discloses to the token holder that the address has no account.** The token holder is, by construction, the person who received mail at that address; it discloses nothing to anyone else. Stated here rather than hidden. |
| Valid, new user | + Argon2id hashing (slow by design) + inserts | success |
| Valid, existing user | + inserts | success |

The Argon2id hash runs only on the success path, so a valid-token-new-user request takes measurably longer than
an invalid one. That difference tells an attacker nothing they did not already need (a valid token). No branch
returns early before the cryptographic comparison *that matters* (the hash lookup); the pre-check is a syntax check.

`POST /organization/members/invite` is tenant-scoped but its *response* is uniform (SEC-07): `202 { accepted: true }`
for a new address, an existing user elsewhere, and an existing member; `Invite_RevealsNothing_AndAccept_RefusesUniformly`
asserts the three bodies are identical. The three branches do different work (an existing member sends no mail); the
audit row carries the difference.

## 4. New money fields or calculations?

**None.** No money in this slice.

## 5. AI-touching code?

**None.** The AI service and its callers are untouched.

## 6. New dependencies?

**None.** Invitation mail goes through the existing `IMailTransport`; the drill uses the PostgreSQL client tools
(`pg_dump`, `pg_restore`, `psql`, PostgreSQL License) already required for development. The notices file says so.

## The restore drill (T-150, PRD-23)

`infrastructure/backup.sh` dumps the database (`pg_dump -Fc --no-owner`); `infrastructure/restore-drill.sh` restores
it into `<db>_drill_<time>` through `psql -v ON_ERROR_STOP=1`, compares `count(*)` for every public table, checks
that RLS-enabled tables, policies and the `schema_migrations` rows came back, prints the backup's age (RPO) and the
restore's wall time (RTO), and drops the drill database on exit. Locally: 34 tables, 35 policies, 12 migrations,
`RESTORE DRILL PASSED`. In CI it is the `ops` job against a freshly migrated database. Row counts, not checksums
(D-5): it proves the shape and the volume came back, not every byte.

## What passed

- 4 integration tests (invite → Mailpit → accept → sign in; existing user joins; uniform responses; role and
  deactivation guards with immediate loss of access and re-invitation), 37 security (route pin 137, the new
  isolation test), 68 web (invite form never offers Owner; role/deactivate never for the Owner or yourself; the
  accept screen's two shapes), E2E T-121 in both locales now walks register → invite → Mailpit → accept → sign in.

## Flagged for your decision

1. **`password_required` discloses account existence to a valid token holder** (question 3). I judge it harmless
   and necessary — the screen has to know whether to ask for a password — but it is a deliberate exception to "every
   failure looks the same".
2. **Invitation mail bypasses the tenant outbound switch and cap** (D-4); the global kill switch still applies.
3. **Transfer of ownership remains deferred** (needs re-authentication and MFA). Until then a tenant has exactly one
   Owner, who cannot be demoted or deactivated by anyone.
4. **Re-inviting a deactivated member reactivates the same membership** with the invited role (the audit trail shows
   both events). If you would rather deactivation be permanent, that is a one-line guard.
