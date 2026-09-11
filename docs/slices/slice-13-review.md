# Slice 13 — Auth completion: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **high** — second factors, password resets, ownership, and the re-authentication gate on write-offs.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

Four through the middleware, **two deliberately anonymous.** Route pin 137 → 143; the anonymous set is pinned
by name at six.

| Endpoint | Declaration | Why |
|----------|-------------|-----|
| `POST /auth/forgot-password`, `POST /auth/reset-password` | **anonymous**, `/auth` rate limit | The person has no session — that is the situation. `202` always; `reset_invalid` for every unusable link. |
| `POST /auth/mfa/enroll`, `/auth/mfa/verify`, `/auth/reauthenticate` | authenticated, `AllowsWithoutMfa` | A session but no permission: they act on the caller's own account. Allowed before enrolment because they *are* the enrolment. |
| `POST /organization/transfer-ownership` | `tenant.transfer_ownership` + `RequiresReauth` | Only the Owner holds the permission; the middleware also demands the five-minute proof for this user. |
| `POST /write-offs/{id}/approve` (existing) | + `RequiresReauth` | Slice 3b D-6 closed. Every existing test that approves a write-off now re-authenticates first. |

`/me`, `/auth/logout`, `/auth/tenants`, `/auth/switch-tenant` are marked `AllowsWithoutMfa` so an Owner past the
grace can still see who they are, enrol, or leave. Everything else answers `403 mfa_enrollment_required`
(`Mfa_IsEnforcedAfterGrace`, with the clock pinned eight days ahead; a Collector in the same tenant is untouched).

## 2. Every new table: `tenant_id`, RLS enabled and forced, a policy, composite FKs?

Two new tables, **both platform tables by design (DM-06), like `users`**: a recovery code and a reset link belong
to a person, not to an organization. Both have RLS enabled and forced with a policy: `user_recovery_codes`
readable by the platform scope or `user_id = app_current_user()`; `password_reset_tokens` by the platform scope
only. No `tenant_id` — there is none to have; no DELETE. The enumeration test (`TenantIsolationTests`) walks the
tenant-scoped tables and is unaffected; `AuthIsolationTests.SecondFactor_AndOwnership_StayWithTheirOwner` is the
targeted test: inside A's tenant scope as a different user, zero recovery codes and zero reset tokens are visible;
A's Owner cannot transfer to B's membership id (404); a reset link's hash is not its token.

`tenant_memberships.mfa_grace_until` is a column on an existing table under the existing policy.

## 3. Code paths before a tenant or full authentication, or cross-tenant by design? Timing and disclosure per branch.

Three identity flows were added to `PlatformIdentityStore` and one was extended.

**`AuthenticateAsync` (extended).** The second factor is checked only after the password matched, so:

| Branch | Discloses |
|--------|-----------|
| unknown email / wrong password / disabled / no membership | `invalid_credentials` — unchanged, one message (SEC-06/07); the dummy-hash timing equalizer is unchanged |
| password matched, MFA enrolled, no code | `mfa_required` — **discloses "this account has MFA" to someone who already holds the password**; the alternative (accepting a password alone) is worse. Stated. |
| password matched, MFA enrolled, wrong code | `invalid_credentials`, and the same failure counter and lockout as a wrong password (`Mfa_Enroll_Login_Recovery` asserts the count) |
| recovery code | verified by SHA-256 lookup, burnt in the same transaction; a second use is `invalid_credentials` |

**`ForgotPasswordAsync`.** One `202` for known and unknown addresses (`PasswordReset_Flow_IsUniform` asserts the
bodies are identical). The known branch does more work (a row and an email); the timing difference exists and is
the same class as registration's. Only one live link per user.

**`ResetPasswordAsync`.** Syntax check (64 hex) → hash → one lookup → `reset_invalid` for missing, expired, used;
identical bodies asserted for three kinds of bad token. Success sets the hash, revokes every refresh token of the
user across tenants (`refresh_tokens` read with the platform scope), and audits under the first tenant found.

**`ReauthenticateAsync`.** Authenticated only; wrong password → 401 with the generic message; the proof names its
subject and the middleware compares it to the session's user (`TransferOwnership_NeedsReauth_AndMfa` presents
another user's proof and gets 403).

## 4. New money fields or calculations?

**None.**

## 5. AI-touching code?

**None.**

## 6. New dependencies?

**None.** TOTP, Base32, HMAC-SHA1, AES-GCM and SHA-256 are the .NET base class library (slice 13 D-2); the RFC 6238
appendix vectors pin the implementation (`TotpTests`). The e2e suite's "authenticator app" is 20 lines of
`node:crypto`. The notices file says so.

## What passed

- 643 unit (RFC vectors, skew, Base32, recovery codes, the policy) · 192 integration (4 new: enrol/login/recovery,
  enforcement after grace, password reset, re-auth + transfer + write-off gate; 7 existing write-off approvals now
  re-authenticate; two clock-pinning tests waive the grace explicitly) · 38 security (route pin 143, anonymous set 6,
  the new isolation test) · 72 web (TOTP step only after `mfa_required`, enrolment with recovery codes shown once,
  re-auth dialog asks for a code only when enrolled, forgot/reset) · 24 e2e (T-121 now enrols and signs in with a
  computed code).

## Flagged for your decision

1. **`mfa_required` after a correct password** discloses that the account has a second factor (question 3). Standard,
   and strictly better than the alternative; stated.
2. **Seven-day grace** (D-1) rather than a hard gate at first login: SEC-02 says "required"; the grace is how a new
   Owner reaches the enrolment screen at all. The deadline is the server's clock and the check is per request.
3. **No way to disable MFA once enrolled**, and no re-enrolment of an active secret without support. A lost device
   is covered by the eight recovery codes; after those, it is a support task.
4. **`MFA_KEK_BASE64` is a new required secret** in every environment (`.env.example`, CI). Without it enrolment
   answers `mfa_unavailable`, and Owners/Admins would be locked out after their grace — the ops runbook must include it.
5. **The breached-password list (SEC-01)** is still not bundled.
6. **Transfer makes the previous Owner an Admin** (D-5); Admin is also MFA-required, so nothing loosens.
