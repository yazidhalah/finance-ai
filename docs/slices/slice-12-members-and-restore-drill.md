# Slice 12 — Member invitations, role management, and the restore drill

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 05 (`/organization/members/invite`, `PATCH /organization/members/{id}`,
`/organization/members/{id}/deactivate`) · doc 01 §5 (`users.invite`, `users.role.write`,
`users.deactivate`) · doc 08 SEC-06, SEC-07, SEC-14 · doc 09 T-121, T-150 · doc 10 slice 1 §1.6/§1.7 ·
PRD-23 · slice 1 (deferred list, D-4) · slice 11 (flag 1: T-121 used a seeded member).

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Invite**: `POST /organization/members/invite` `{ email, role, locale }` → a `member_invitations` row holding only the SHA-256 of a 256-bit token, and an email through the slice 8 transport with the accept link. The response is the same whether or not the address is already a user or a member (SEC-07); the difference is audited server-side. Roles: `Admin`, `Accountant`, `Collector`, `Viewer` — never `Owner` (the database allows exactly one; ownership moves only through the deferred transfer flow) |
| S2 | **Accept**: `POST /auth/accept-invitation` `{ token, fullName?, password? }` — anonymous, rate-limited with the auth group. A valid token for an address that has no user creates the user (password required, same rules as registration) and the membership; for an existing user it creates the membership only. Invalid, expired (7 days), revoked or already-used tokens all answer the same `400 invitation_invalid` after the same work |
| S3 | **Pending invitations**: `GET /organization/invitations` (`users.read`), `POST /organization/invitations/{id}/revoke` (`users.invite`) |
| S4 | **Role change**: `PATCH /organization/members/{id}` `{ role }` (`users.role.write`): closed set minus `Owner`; the last Owner cannot be demoted (`WouldRemoveLastOwner`); takes effect on the next request (the role is resolved per request already) |
| S5 | **Deactivate**: `POST /organization/members/{id}/deactivate` (`users.deactivate`): not yourself, not the last Owner; the member's refresh tokens for this tenant are revoked and the next request is refused |
| S6 | **UI**: the members card gains invite (email + role), the pending list with revoke, a role select and a deactivate button per member; an anonymous `/accept-invitation?token=…` screen (name + password for a new user, nothing for an existing one) that ends at sign-in; T-121 in the E2E suite now walks the real flow through Mailpit |
| S7 | **Restore drill (T-150, PRD-23)**: `infrastructure/backup.sh` (`pg_dump` custom format) and `infrastructure/restore-drill.sh` (restore into a fresh database, verify row counts per business table match the source, drop it), run in CI as its own job |

**Deferred:** transfer of ownership (needs re-authentication and MFA, slice 1 D-4) · resending an
invitation (revoke and invite again) · per-tenant email templates for the invitation (the text is a
system string in the tenant's locale; not a customer message, so not a `message_templates` row).

## 2. Rules, stated explicitly

| Question | Answer |
|----------|--------|
| What does the invite response reveal? | Nothing: `202 { accepted: true }` whether the address is new, an existing user, or already a member. The email goes only where it can do something: a new or non-member address gets the accept link; an existing Active member gets nothing (and the audit note says `already_member`). |
| What does the accept endpoint reveal? | `invitation_invalid` for every failure; the token lookup is one indexed query on the hash and the hash is computed before anything is compared; the password is hashed only on success (an invalid token never reaches the hasher — that timing difference exists and is stated in the review, question 3). |
| Can a token be replayed? | No: `accepted_at` is set in the same transaction that creates the membership; a second use is `invitation_invalid`. |
| Who can become Owner? | Nobody through these endpoints. `RoleAssignment.CanAssign(role)` refuses `Owner`; the `one_owner_per_tenant` index refuses it again. |
| What happens to a deactivated member's sessions? | Refresh tokens are revoked; the access token expires within 15 minutes but every request already resolves the membership (`ResolveActiveRoleAsync`), so a Disabled membership is refused on the next call. |
| Email content | A system string in the invitee's locale with the organization's name and the link; no customer data, no other member's data. Sent through `IMailTransport` behind the global kill switch only (it is not customer outreach, so the tenant switch and the daily cap do not apply — stated). |
| Restore drill | `backup.sh` dumps the whole database with `pg_dump -Fc`; `restore-drill.sh` creates `<db>_drill`, restores, compares `count(*)` for every table under RLS-forced ownership (as the admin role), prints RPO (dump age) and RTO (restore wall time), drops the drill database. Exit non-zero on any mismatch. |

## 3. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | Invite → email in Mailpit with a link whose token accepts → new user can sign in with the role given; the `member_invitations` row holds a 64-hex hash, never the token | `Invite_Accept_SignIn` |
| AC-02 | Invite an existing user of another organization → accept creates the membership only; their password is unchanged | `Invite_ExistingUser_JoinsWithoutANewAccount` |
| AC-03 | Invite responses are byte-identical for a new address, an existing user and an existing member; only the last sends no mail | `Invite_RevealsNothing` |
| AC-04 | Invalid, expired, revoked and reused tokens → `400 invitation_invalid`, identical bodies | `Accept_RefusesUniformly` |
| AC-05 | Role change: Collector → Accountant takes effect on the next request; `Owner` → 422 `owner_via_transfer_only`; demoting the last Owner → 422 `last_owner`; self-deactivate → 422; deactivate the last Owner → 422 | `RoleChange_AndDeactivate_Guards` |
| AC-06 | Deactivated member: the next request with the old access token is 403/401; refresh is refused | `Deactivate_CutsAccess` |
| AC-07 | Cross-tenant: B's ids for `members/{id}` and `invitations/{id}` → 404; a token issued by A cannot be revoked by B; an invitation row in B for A's tenant is impossible (tenant_id from the scope) | sweep + `CrossTenantMembers_IsRejected` |
| AC-08 | E2E T-121 uses the invitation email from Mailpit in both locales | `journeys.spec.ts` |
| AC-09 | Restore drill: backup, restore into a fresh database, counts match, RPO/RTO printed, drill database dropped; runs in CI | `restore-drill.sh` + CI job `ops` |

## 4. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| POST | `/organization/members/invite` | `users.invite` |
| GET | `/organization/invitations` | `users.read` |
| POST | `/organization/invitations/{id}/revoke` | `users.invite` |
| PATCH | `/organization/members/{id}` | `users.role.write` |
| POST | `/organization/members/{id}/deactivate` | `users.deactivate` |
| POST | `/auth/accept-invitation` | **anonymous**, auth rate limit — the invitee has no session yet |

Route pin 131 → 137; the anonymous set grows from three to four and the pin says so.

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Token in the link, hash in the database, 7-day expiry, single use | A leaked database row is not an invitation; a leaked link expires. |
| D-2 | Accept creates the user with the invited locale as `preferred_locale` and marks the email verified | The address proved it receives mail by producing the token. |
| D-3 | No `Owner` through role change | `one_owner_per_tenant`; transfer of ownership is its own gated flow. |
| D-4 | Invitation mail bypasses the tenant outbound switch and cap | Those exist for customer outreach (SEC-103, SEC-86); locking an Owner out of inviting staff because dunning is paused would be perverse. The global `OUTBOUND_SENDING_ENABLED` still applies. |
| D-5 | The drill compares row counts, not checksums | Counts per table catch a missing table or a partial restore; a byte-level comparison would need the source frozen. Stated in the script's output. |
