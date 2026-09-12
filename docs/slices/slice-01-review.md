# Slice 1 — Organization & Authentication: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **high** — this slice is the identity core and the tenant-isolation machinery every
later slice copies (`slice-01-organization-and-auth.md` §5).

**Written late, on 2026-09-12.** Slice 1 was reported complete on 2026-09-11 (commit `5eb59d9`)
without this document. A finding raised in that slice's review — the lockout was judged *before*
the password hash was compared — was therefore never written down anywhere that later slices
read, and it survived thirty slices of self-review that each answered "nothing was added to
`PlatformIdentityStore`" truthfully. Section 0 records the finding and its fix; §1–§6 answer the
six standard questions for the slice's **original** scope, so that the next reader of this
directory finds slice 1 held to the same standard as every slice after it.

---

## 0. The finding that was lost: pre-authentication lockout disclosure (SEC-06)

### What it was

`PlatformIdentityStore.AuthenticateAsync` ran this before any password comparison:

```csharp
if (user.IsLockedAt(now))
{
    await this.RecordFailureAsync(db, user, membership, actorIp, requestId, now, ct);
    await scope.CompleteAsync(ct);
    return new AuthenticationResult(AuthenticationOutcome.Locked, null, user.LockedUntil);
}
```

Every other outcome of a login costs one Argon2id derivation (m=64 MB, t=3): a wrong password
is compared against the stored hash, and an unknown email is compared against a fixed dummy
hash precisely so the two take the same time. The locked branch skipped that work. Measured by
the new test on the original code: **13.6 ms** for a locked address against **214.9 ms** for an
unknown one — a sixteen-fold gap readable from the response time alone, on top of the distinct
`423` body.

Why that matters, given that slice 1's F-7 already accepted a distinct `423 Locked` state:

- F-7 accepted `423` as the price of doc 06 §6.1's unlock time, *behind* ten failed attempts
  against a real address and the 10/minute auth rate limit — every one of those attempts paying
  a full hash comparison. The pre-hash branch changed the economics: once an attacker had locked
  an address (or simply guessed that a target was locked), each probe of "does this address
  exist, and is it locked?" was free of Argon2 and answered in the time of two lookups.
- `LockedUntil` — the exact time the lock lifts — was returned without the request having proved
  anything at all. The answer to "when can I resume guessing?" should cost at least one guess.
- `RecordFailureAsync` was still called, so the branch also wrote an `auth.login_failed` audit
  row for a request that never presented a credential. Harmless, but the audit trail suggested
  a comparison had happened when none had.

### The fix (this change)

The block is gone. `passwordMatches` was already computed unconditionally below it; the lock
now joins the existing failure condition:

```csharp
var passwordMatches = user.PasswordHash is not null &&
                      passwordHasher.Verify(password, user.PasswordHash);

if (!passwordMatches || user.Status != UserStatus.Active || membership is null || user.IsLockedAt(now))
{
    await this.RecordFailureAsync(db, user, membership, actorIp, requestId, now, ct);
    await scope.CompleteAsync(ct);
    return user.IsLockedAt(time.GetUtcNow()) || user.FailedLoginCount >= MaxFailedLogins
        ? new AuthenticationResult(AuthenticationOutcome.Locked, null, user.LockedUntil)
        : new AuthenticationResult(AuthenticationOutcome.Failed, null, null);
}
```

The ternary inside the branch already produced `Locked` with `LockedUntil` for a locked account,
so nothing else changed. `IsLockedAt(now)` **must** stay in the condition: without it a correct
password on a locked account would fall through to a successful login (AC-09 would fail).
`RecordFailureAsync` still declines to increment the counter while the lock is in force, so a
locked window is not extended by attempts made during it.

What the fix does *not* change: a locked address still answers `423` (F-7 stands, unchanged);
a wrong password and an unknown email still answer the same `401 invalid_credentials`; the
tenth failure still trips the lock; the successful login still resets the counter.

### The regression test — `LockoutDisclosureTests` (FinanceAi.SecurityTests)

The tests drive `PlatformIdentityStore` directly with the **real** Argon2id hasher behind a
counting decorator, so "a hash comparison happened in this request" is a deterministic fact and
the timing assertion sits on top of it rather than standing alone.

| Assertion | Test | How it is proved |
|-----------|------|------------------|
| A locked account with a wrong password and an unknown email take comparable time | `LockedAccount_WrongPassword_AndUnknownEmail_TakeComparableTime` | Seven interleaved rounds of each after a warm-up; the medians must lie within a factor of two of each other (measured after the fix: 220.8 ms vs 205.8 ms, ratio 1.07; before: 13.6 ms vs 214.9 ms, ratio 15.8). Independently, the hasher must have been asked for exactly one comparison per request on both paths. |
| A locked account with the **correct** password still returns `Locked`, not `Succeeded` | `LockedAccount_CorrectPassword_StillReturnsLocked` | Outcome `Locked`, no session, `LockedUntil` set, exactly one comparison (the password matched and the lock still won); no new `refresh_tokens` row and no `auth.login_succeeded` audit row; and over HTTP the same request is `423` with no `Set-Cookie`. |
| `LockedUntil` is never returned unless a real password hash comparison occurred in this request | `LockedUntil_IsOnlyReturnedAfterAHashComparison` | Six attempts — right and wrong password, known and unknown address, before and after the lock — each must cost exactly one comparison, and every result carrying `LockedUntil` must be one of them. Then the strongest form: a hasher that *throws* on every comparison must make the locked-account request throw rather than answer, for both a wrong and the correct password. On the original code that request returned `Locked` with `LockedUntil` set without touching the hasher. |

All three failed on the original code and pass on the fixed code (run both ways before this
was committed). The existing `LoginTests.Login_LocksAccountAfterTenFailures` (AC-09) and
`Login_WrongPassword_And_UnknownEmail_AreIndistinguishable` (AC-08) still pass unchanged.

---

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

Thirteen endpoints in the original slice. **Three anonymous by design, ten through the middleware.**

| Endpoint | Declaration | Why |
|----------|-------------|-----|
| `POST /auth/register`, `POST /auth/login` | **anonymous**, `/auth` rate limit (API-13) | There is no session yet — that is what they create. Both return one shape for every outcome (AC-04, AC-08). |
| `POST /auth/refresh` | **anonymous** (cookie-authenticated) | The access token has expired; the `httpOnly` cookie is the credential. Handled entirely inside `PlatformIdentityStore` (§3). |
| `POST /auth/logout`, `GET /auth/tenants`, `POST /auth/switch-tenant`, `GET /me`, `PATCH /me` | authenticated, no permission | They act on the caller's own identity. `switch-tenant` is the one endpoint anywhere that accepts a tenant id, and only from the caller's own membership list (AC-18). |
| `GET /organization` | `tenant.read` | |
| `PATCH /organization` | `tenant.settings.write` | |
| `GET /organization/members`, `GET /organization/members/{id}` | `users.read` | |
| `GET /audit` | `audit.read` | |

Anonymous is a decision on record, not an omission: an endpoint that declares neither a
permission nor an explicit access level prevents the application from booting
(`Startup_FailsWhenAnEndpointDeclaresNoPermission`, AC-23) and the anonymous set is pinned by
name (`EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel`). The 401, 403 and
404-cross-tenant sweeps are generated from the route table
(`EveryProtectedEndpoint_Returns401WithoutToken`, `…Returns403ForRoleWithoutPermission`,
`EveryEndpointWithId_Returns404ForCrossTenantId`; AC-24…AC-26), and the tenant is read only from
the validated token claim — never from a header, body, query, cookie or referer
(`NoSourceFile_ReadsTenantIdFromRequestInput`, `NoRoute_TakesATenantIdParameter`, AC-35).

Later slices changed this table (email verification before first sign-in, MFA, password reset,
re-authentication, invitations); each is reviewed in its own slice's document.

## 2. Every new table: `tenant_id`, RLS enabled AND forced, at least one policy, composite FKs?

Migration `0001_tenancy_foundation.sql` created six tables (plus `schema_migrations`, the
migrator's own bookkeeping, created by `MigrationRunner`, holding no business data and never granted to `finance_app`).

| Table | `tenant_id NOT NULL` | RLS enabled | RLS forced | Policy | `(tenant_id, id)` key | FK to a tenant-scoped table |
|-------|:--:|:--:|:--:|:--:|:--:|---|
| `tenants` | is the id | ✅ | ✅ | `tenant_isolation` + platform clause | n/a | none |
| `users` | **none — global identity by design** (DM-06) | ✅ | ✅ | `users_visible_to_co_members`, `users_self_update` + platform clause | n/a | none |
| `tenant_memberships` | ✅ | ✅ | ✅ | `tenant_isolation` + platform clause | ✅ | none (references `tenants` and `users`, both platform) |
| `refresh_tokens` | ✅ | ✅ | ✅ | `tenant_isolation` + platform clause | ✅ | **composite** `(tenant_id, user_id) → tenant_memberships (tenant_id, user_id)` |
| `tenant_settings` | ✅ (is the key) | ✅ | ✅ | `tenant_isolation`, **no platform clause** | n/a | none |
| `audit_events` | ✅ | ✅ | ✅ | `tenant_isolation`, **no platform clause** | ✅ | none |

`users` is the deliberate exception: a person can belong to several organizations, so the row
has no single tenant. It is reachable during a tenant-scoped request only through the co-member
policy (`users_visible_to_co_members`), and otherwise only from the platform scope (§3).

Enumeration-based coverage (`TenantIsolationTests`): `EveryTenantScopedTable_HasTenantIdRlsForcedPolicyAndCompositeKeys`
(AC-29), `NoSingleColumnForeignKey_JoinsTwoTenantScopedTables`, `ConnectionWithoutTenantGuc_ReturnsZeroRows`
(fail closed, AC-30), `PooledConnection_DoesNotLeakTenantGuc` (AC-31), `ApplicationRole_HasNoBypassRlsAndOwnsNothing`
(AC-34).

**Non-standard access patterns, with their targeted tests** — the checklist is right that
enumeration is not enough here, because the platform tables are the one place the standard
pattern is bypassed on purpose:

- **Layer 2 alone**: `UnfilteredRawSql_ReturnsOnlyCurrentTenantRows` (AC-32) issues a raw,
  unfiltered query with EF's filter bypassed and still sees one tenant.
- **Layer 3 alone**: `CrossTenantChildRow_IsRejectedByCompositeForeignKey` (AC-33) inserts as
  superuser with RLS bypassed and is refused by the composite key.
- **The platform escape is confined**: `PlatformScope_IsOpenedOnlyByPlatformIdentityStore` and
  `QueryFilters_AreBypassedOnlyWhereDocumented` walk the source tree; a second file calling
  `EnterPlatformAsync` or `IgnoreQueryFilters` fails the unit suite.
- **The escape does not reach business data**: `tenant_settings` and `audit_events` have no
  platform clause, so even `PlatformIdentityStore` must bind a real tenant before it can write
  an audit row (`RecordFailureAsync` does exactly that, and skips the row when there is no
  membership — an unknown email leaves no tenant-attributable trace).
- **Audit rows are per tenant**: `AuditChains_ArePerTenant`, `Audit_IsReadableOnlyWithinOwnTenant`
  (AC-39); append-only and hash-chained: `AuditEvents_UpdateAndDelete_Raise` (AC-36),
  `AuditChain_Verifies_AndDetectsTampering` (AC-37).
- **Cross-tenant reads over HTTP**: `TenantA_CannotReadTenantB_Members_Organization_Or_Audit`
  (AC-27), `UserOfTenantA_CannotAuthenticateIntoTenantB` (AC-28),
  `WriteScopedToOneTenant_CannotTouchAnotherTenantsRow`.

## 3. Code paths before a tenant or full authentication, or cross-tenant by design? Does every branch take comparable time and disclose comparable information?

This is the question the slice existed to answer and the one it got wrong once. Every such path
lives in `PlatformIdentityStore` (DM-06). The original slice's flows, branch by branch:

**`AuthenticateAsync` (login).** After this change:

| Branch | Hash comparisons | Discloses |
|--------|:---:|-----------|
| unknown email | 1 (dummy hash) | `401 invalid_credentials` |
| wrong password | 1 | `401 invalid_credentials` — same body, same time (AC-08, `LockedAccount_WrongPassword_AndUnknownEmail_TakeComparableTime`) |
| correct password, disabled user or no active membership | 1 | `401 invalid_credentials` |
| correct password, tenant not active | 1 | `401 invalid_credentials` |
| **locked, any password** | **1** (was 0) | `423 account_locked` with the unlock time — F-7's accepted trade-off, now costing a comparison per probe like every other outcome |
| tenth failure | 1 | `423 account_locked` |

**Branches that return early before a cryptographic comparison or a full data fetch — named,
as the checklist asks:**

- *Login*: **none reachable.** The lock check was the one such branch; it is the subject of §0.
  One latent branch is worth naming: `user.PasswordHash is not null && …` skips the comparison
  for a user row with no hash. Every write path today sets one (registration, invitation
  acceptance, password reset), so the branch cannot be taken; `password_hash` is nullable only
  for the SSO-only users doc 04 anticipates. Whoever adds SSO must make that branch spend the
  dummy hash, the way the unknown-email branch does, and `LockedUntil_IsOnlyReturnedAfterAHashComparison`
  will fail for a hash-less user if they forget.
- *Registration* (`RegisterAsync`): **one, still open.** `emailTaken` is checked with a single
  lookup and returns before `passwordHasher.Hash` runs. The response body is byte-identical for
  a taken and a fresh address (AC-04, `Register_WithExistingEmail_ReturnsSameShapeAsSuccess`),
  but the fresh branch additionally derives an Argon2id hash, inserts four rows and sends a
  verification mail, so the two differ in time by roughly one hash derivation. Slice 13's review
  named `ForgotPasswordAsync` as "the same class as registration's"; this document is the first
  to name registration's itself. It is **not** fixed by this change, whose scope is the lockout.
  It is the same shape of gap and should be closed the same way — spend the hash on the taken
  branch too — in a change of its own, with a timing test like `LockoutDisclosureTests`.
  Recorded under "Flagged" below.
- *Refresh* (`RedeemRefreshTokenAsync`): the token is looked up by SHA-256 of the raw value, so
  there is no secret comparison to short-circuit; an unknown, revoked or expired token all
  answer `401` with the same body. A *spent* token (already rotated) does more work — it
  revokes the family and writes an audit row — and the timing of that branch is observable.
  That is acceptable: it is reachable only by presenting a token that was genuinely valid once,
  and the response it discloses ("your session is gone") is what the legitimate holder must
  learn anyway. Unchanged since slice 1; `Refresh_Reuse_RevokesFamily_AndAudits` (AC-13).
- *Session resolution per request* (`ResolveSessionAsync`, called by the middleware): membership
  → user → tenant, each fetched only if the previous one was found. All three failures collapse
  to one `401`; the caller already holds a signed token naming the user and tenant, so nothing
  is disclosed that the token did not already carry. `DisabledMembership_TokenRejected` (AC-17).
- *Tenant switching* (`SwitchTenantAsync`): authenticated; a tenant the user is not a member of
  answers `404`, indistinguishable from a tenant that does not exist (AC-18,
  `SwitchTenant_ToForeignTenant_Returns404`).

**Cross-tenant by design:** `ListMembershipsAsync` (for `/auth/tenants`) reads every membership
of one user across tenants. It runs in the platform scope, is keyed by the authenticated user
id only, and returns tenant names and roles the user already holds. Slice 32 later added
`ListActiveTenantsAsync` for the scheduler; reviewed there.

## 4. Any new money-related field or calculation?

**None.** No monetary field, no amount, no currency arithmetic exists in this slice.
`NoAssemblyType_HasFloatingPointMoneyMember` (AC-46, T-23) asserts that no member with a
money-like name in any assembly is `float` or `double`, so the rule was enforced before there
was anything for it to catch. `tenant_settings.base_currency` is a three-letter code, not an
amount.

## 5. Any AI-touching code?

**None.** The AI service did not exist until slice 9. No prompt, no model call and no model
output anywhere in slice 1. The `AuditEvent` shape (actor, reason code, before/after state,
hash chain) is what slices 9 and 10 later used to make every AI recommendation auditable, but
nothing in slice 1 consumed AI output.

## 6. Any new dependency?

**Yes — all recorded in `THIRD-PARTY-NOTICES.md` in the slice's commit (`5eb59d9`), with licences.**

| Package | Licence | Why |
|---------|---------|-----|
| `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL License | driver and provider |
| `Microsoft.EntityFrameworkCore` | MIT | ORM; the global query filter is isolation layer 1 |
| `Konscious.Security.Cryptography.Argon2` (+ `Blake2`, transitive) | MIT | Argon2id at SEC-01's parameters |
| `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.AspNetCore.Authentication.JwtBearer` | MIT | RS256 access tokens |
| `react`, `react-dom`, `vite`, `@vitejs/plugin-react`, `tailwindcss`, `@tailwindcss/vite` | MIT | web |
| `typescript` | Apache-2.0 | web |
| IBM Plex Sans Arabic / IBM Plex Sans | SIL OFL 1.1 | referenced by family name only, not bundled, not fetched from a CDN |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`, `Microsoft.AspNetCore.Mvc.Testing`, `vitest`, `jsdom`, `@testing-library/*`, `@types/*` | Apache-2.0 / MIT | tests only |
| `docker.io/pgvector/pgvector:pg16`, `docker.io/axllent/mailpit` | PostgreSQL License / MIT | local infrastructure; Mailpit is never deployed |

All permissive; no paid API, no proprietary runtime dependency. Slice 16 later added
`infrastructure/check-notices.py` so this answer is checked in CI rather than by hand, and slice
20 extended it to the transitive tree. **This change adds no dependency**: the counting and
throwing hashers in `LockoutDisclosureTests` are private test classes wrapping the existing
`Argon2idPasswordHasher`.

---

## Flagged

Nothing in the six questions is "not sure". Two things are on record so they cannot drop again:

1. **Registration timing (SEC-07), open.** `RegisterAsync` returns on a taken address before
   hashing (§3). Same class as the lockout gap, not fixed here because it is outside this
   change's scope. Should be its own small change: derive a hash on the taken branch (or on
   both branches identically), plus a timing test in `LockoutDisclosureTests`' style.
   `ForgotPasswordAsync` (slice 13) has the same shape and should be closed in the same change.
2. **`423 Locked` discloses existence after ten failures (F-7), accepted.** Unchanged by this
   fix and restated so the accepted trade-off and the fixed defect are not confused: the
   trade-off is that a *lock* is visible; the defect was that it was visible *for free*.

## Why this document was missing, and what prevents a repeat

Slice 1 (`5eb59d9`) was committed before the "Slice Self-Review" section of `CLAUDE.md` existed
(`13d2724`, later the same day); slice 2's review was the first instance. The finding above was raised in conversation during slice 1's
review and was never written into a file; every later review then answered question 3 with
"nothing was added to `PlatformIdentityStore`", which was true and missed the point. The
protection now is threefold: the fix itself, a security test that fails on the original code in
three independent ways, and this document sitting where the next reader of `docs/slices/` will
look for it.
