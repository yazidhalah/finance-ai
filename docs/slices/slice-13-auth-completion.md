# Slice 13 — Auth completion: TOTP MFA, password reset, re-authentication, transfer of ownership

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 05 (`/auth/forgot-password`, `/auth/reset-password`, `/auth/mfa/enroll`, `/auth/mfa/verify`,
login `totp`, `/organization/transfer-ownership`) · doc 08 SEC-01, SEC-02, SEC-06, SEC-07, SEC-08, SEC-09 ·
doc 06 §6.1 (sign in with a TOTP step; forgot/reset always-success) · slice 1 D-4 (deferred list) · slice 3b D-6
(re-authentication on write-off approval) · slice 12 (transfer of ownership deferred).

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **TOTP MFA** (RFC 6238, SHA-1, 30 s, 6 digits, ±1 step): `POST /auth/mfa/enroll` returns a fresh secret and its `otpauth://` URI; `POST /auth/mfa/verify` `{ code }` activates it and returns **eight recovery codes once**; the secret is stored AES-GCM-encrypted under `MFA_KEK_BASE64`, recovery codes are stored as SHA-256 hashes and are single use |
| S2 | **Login with MFA**: `POST /auth/login` `{ email, password, totp? }` — once enrolled, a correct password without a code answers `401 mfa_required` (only after the password matched); a wrong code counts as a failed login (lockout applies); a recovery code is accepted in place of the TOTP and is burnt |
| S3 | **Enforcement for Owner and Admin (SEC-02)**: an Owner/Admin membership carries `mfa_grace_until` (creation + 7 days). Past it, a session of that member without MFA enrolled can reach only `/me`, `/auth/*` and the enrollment endpoints; everything else answers `403 mfa_enrollment_required`. The access token carries `amr` (`pwd` / `mfa`) |
| S4 | **Password reset**: `POST /auth/forgot-password` `{ email }` → `202` always; if the address is a user, a single-use one-hour token (hash stored) is emailed as a link; `POST /auth/reset-password` `{ token, password }` → `400 reset_invalid` for every unusable token, sets the new hash, revokes every refresh token of the user, and audits |
| S5 | **Re-authentication (SEC-09)**: `POST /auth/reauthenticate` `{ password, totp? }` → a five-minute proof token (JWT, purpose `reauth`, bound to the user); sensitive endpoints require it in `X-Reauth`: **transfer of ownership** and **write-off approval** (slice 3b D-6 closed) |
| S6 | **Transfer of ownership**: `POST /organization/transfer-ownership` `{ targetMembershipId }` + `X-Reauth` (`tenant.transfer_ownership`): the target must be an Active member with MFA enrolled; in one transaction the caller becomes `Admin` and the target `Owner` (the `one_owner_per_tenant` index is satisfied by ordering); audited on both memberships |
| S7 | **UI**: the sign-in form gains the TOTP step; forgot/reset password screens (always-success wording); the MFA enrollment screen (forced when `/me` says enrollment is required, optional from the profile otherwise), recovery codes shown once; a re-authentication dialog used by transfer of ownership and write-off approval; transfer of ownership on the members card |

**Deferred:** disabling MFA once enrolled (needs re-auth and a support policy; flagged) · a breached-password
list (SEC-01 second half; no offline list is bundled) · per-device session listing (SEC-08 first half) ·
QR rendering (the `otpauth://` URI and the secret are shown as text; authenticator apps accept either).

## 2. Rules, stated explicitly

| Question | Answer |
|----------|--------|
| When is MFA demanded at login? | Only after the password matched and only when the user has MFA enrolled. `mfa_required` therefore never discloses an account to someone without the password. |
| What does enrollment change? | Nothing until `verify` succeeds: a pending secret can be replaced by enrolling again; an active secret is replaced only through a new enrolment + verify while re-authenticated (deferred; flagged). |
| Where is the secret? | `users.mfa_secret_enc`, AES-256-GCM with a per-row nonce, keyed by `MFA_KEK_BASE64` from the environment (never in source; the server refuses to start MFA without it and reports `mfa_unavailable`). |
| What proves "the same person, now"? | A password check (and the TOTP when enrolled) inside the last five minutes, carried as a signed token that names the user; the endpoint compares the subject to the session's user. |
| Who can become Owner? | Only through transfer, only by the Owner, only to a member who already has MFA — the requirement that will bind them the moment they are Owner. |
| Password reset and sessions | A reset revokes every refresh token the user has (all tenants); access tokens expire within 15 minutes. |
| Grace period | 7 days from the membership's creation (registration, invitation, or promotion to Admin). Existing Owner/Admin memberships get 7 days from the migration. |

## 3. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | RFC 6238 test vectors pass; codes ±1 step are accepted, ±2 are not; Base32 round-trips | `TotpTests` (unit) |
| AC-02 | Enroll → verify with a computed code → login without a code is `401 mfa_required`, with the code succeeds and the token carries `amr = mfa`; a wrong code counts toward lockout; a recovery code works once | `Mfa_Enroll_Login_Recovery` |
| AC-03 | Past the grace period an Owner without MFA gets `403 mfa_enrollment_required` on business routes but not on `/me`, `/auth/mfa/enroll`; within the grace nothing changes; after enrolling, everything works again | `Mfa_IsEnforcedAfterGrace` (pinned clock) |
| AC-04 | Forgot/reset: identical `202` for a known and an unknown address; the link works once and within the hour; a reset revokes existing refresh tokens; invalid, expired and used tokens answer the same `400` | `PasswordReset_Flow_IsUniform` |
| AC-05 | Re-auth: a proof from a wrong password is refused; a valid proof is bound to the user (another user's proof is 403); transfer of ownership without `X-Reauth` is 403 `reauthentication_required`; with it the roles swap and both memberships are audited; a target without MFA is refused | `TransferOwnership_NeedsReauth_AndMfa` |
| AC-06 | Write-off approval now requires `X-Reauth` (slice 3b D-6) — the existing test is amended to supply it and a new assertion shows the refusal without it | `LedgerRuleTests` amendment |
| AC-07 | Cross-tenant: transfer to another tenant's membership id is 404; recovery codes and reset tokens are reachable only by their user / the platform scope | sweep + `AuthIsolationTests` |
| AC-08 | UI: TOTP step appears only on `mfa_required`; forced enrollment screen for an Owner past grace; recovery codes shown once; forgot/reset always-success; transfer dialog asks for password + code | web tests |

## 4. Endpoints, with declared permission

| Method | Path | Access |
|--------|------|--------|
| POST | `/auth/forgot-password`, `/auth/reset-password` | **anonymous**, auth rate limit |
| POST | `/auth/mfa/enroll`, `/auth/mfa/verify`, `/auth/reauthenticate` | authenticated (allowed before MFA enrollment) |
| POST | `/organization/transfer-ownership` | `tenant.transfer_ownership` + `X-Reauth` |

Route pin 137 → 143; the anonymous set grows to six and is pinned by name.

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | A grace period rather than a hard gate at first login | Registration creates an Owner who must be able to reach the enrollment screen; a week is enough to enrol and short enough to mean it. Enforcement is still server-side and the clock is the server's. |
| D-2 | TOTP and Base32 implemented in the Domain (≈60 lines), no package | RFC 6238 is HMAC-SHA1 over a counter; a dependency for that is a licence entry for nothing. Test vectors from the RFC pin it. |
| D-3 | Recovery codes hashed with SHA-256, not Argon2id | 80 bits of entropy each; the slow hash exists for low-entropy passwords. |
| D-4 | The re-auth proof is a JWT with `purpose = reauth`, five minutes, signed by the same key as access tokens | No new table, no new key; the API already validates this signature. The proof is not an access token: it has no `tid`/`role` and the middleware ignores it. |
| D-5 | Transfer makes the old Owner an Admin, not a Viewer | The person who just handed over usually keeps running the organization. They can be re-roled afterwards. |
