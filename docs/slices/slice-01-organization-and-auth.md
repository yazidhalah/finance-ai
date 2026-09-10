# Slice 1 — Organization & Authentication: acceptance criteria and test plan

Status: **Implemented.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 01 §5 (roles, permission matrix) · doc 04 §1, §4, §5.8 (tenancy, platform
tables, audit) · doc 05 §0, slice 1 (API contracts) · doc 06 §6.1–6.2 (screens) ·
doc 08 §1–§3, §5 (auth, authorization, isolation, audit) · doc 09 §4 (security tests) ·
doc 10 slice 1 · ADR-0001 (tenant isolation) · ADR-0005 (bilingual).

---

## 1. Scope of this slice

Slice 1 in doc 10 is sized **L** and bundles ten capabilities. This slice implements the
**organization, identity, role/permission and session core** — the part every later slice
copies — and explicitly defers the rest to slice 1b (§8).

**In scope**

| # | Capability |
|---|-----------|
| S1 | `tenants` (Organization) with `tenant_id` as the isolation key on every scoped table |
| S2 | `users` (global identity) + `tenant_memberships` (one role per user per tenant) |
| S3 | The full role → permission model of doc 01 §5.1, authorized on **permission**, never role |
| S4 | Registration: user + tenant + Owner membership + settings, atomically |
| S5 | Login: Argon2id, uniform errors, lockout, per-IP and per-account throttling |
| S6 | Session handling: 15-minute RS256 access token, opaque rotating refresh token with reuse detection, logout, tenant switching |
| S7 | The three-layer tenant isolation machinery (ADR-0001 L1/L2/L3) and its test harness |
| S8 | Append-only, hash-chained `audit_events` and the auth write path into it |
| S9 | Minimal bilingual (ar-JO / en-JO) RTL-correct UI: sign in, register organization, organization & members |
| S10 | Forward-only SQL migrations, least-privilege database roles (`app` / `migrator` / `readonly_reporting`) |

**Deferred to slice 1b** (see §8 for why): email verification · TOTP MFA · password reset ·
invitations · role change / deactivate / transfer ownership · `tenant_settings` PATCH ·
holiday calendar · audit viewer UI · restore drill (PRD-23/T-150).

---

## 2. Acceptance criteria

Each criterion is a test name. `AC-x` maps to doc 10 slice 1 criteria where they overlap.

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | A registered user is Owner of exactly one new tenant; the tenant, membership and settings rows are created in one transaction | doc 10 §1.1 | `Register_CreatesUserTenantOwnerMembershipAndSettings_Atomically` |
| AC-02 | Registration is atomic: when the commit fails, no user, tenant, membership or settings row survives — forced by two concurrent registrations of the same address, where the unique index rejects the loser after it has already inserted its organization | DM-34 | `Register_WhenCommitFails_LeavesNoRows` |
| AC-03 | `one_owner_per_tenant` is enforced by the database, not only by code | doc 04 §4 | `SecondOwnerMembership_IsRejectedByDatabase` |
| AC-04 | Registration does not reveal whether an email already exists | SEC-07 | `Register_WithExistingEmail_ReturnsSameShapeAsSuccess` |
| AC-05 | Passwords are hashed with Argon2id (m=64MB, t=3, p=1); no plaintext or reversible form is stored | SEC-01 | `PasswordHasher_UsesArgon2idWithSpecifiedParameters`, `Register_StoresNoPlaintextPassword` |
| AC-06 | Passwords shorter than 12 characters are rejected; no composition rules are imposed | SEC-01 | `Register_WithShortPassword_Returns400` |
| AC-07 | Login with correct credentials returns a 15-minute access token, the tenant, the role and the **effective permission list** | SEC-03, doc 05 | `Login_ReturnsAccessTokenTenantRoleAndPermissions` |
| AC-08 | Login with a wrong password and login with an unknown email return the **same** status, code and body shape | SEC-06, SEC-07 | `Login_WrongPassword_And_UnknownEmail_AreIndistinguishable` |
| AC-09 | Account locks after 10 consecutive failures; a correct password during lockout still fails | SEC-06 | `Login_LocksAccountAfterTenFailures` |
| AC-10 | A successful login resets the failure counter | SEC-06 | `Login_Success_ResetsFailedLoginCount` |
| AC-11 | The refresh cookie is `httpOnly`, `Secure`, `SameSite=Strict`, and the access token is never set in a cookie | SEC-04, SEC-05 | `Login_SetsHardenedRefreshCookie_AndNoAccessTokenCookie` |
| AC-12 | Refresh rotates: the presented token is invalidated and a new one issued | SEC-04 | `Refresh_RotatesToken` |
| AC-13 | **Refresh-token reuse revokes the entire family**, and the reuse is audited | SEC-04, T-85 | `Refresh_Reuse_RevokesFamily_AndAudits` |
| AC-14 | Logout revokes the family; the refresh token no longer works | doc 05 | `Logout_RevokesFamily` |
| AC-15 | A refresh token is stored hashed; the raw value never appears in the database | SEC-67 | `RefreshTokens_AreStoredHashed` |
| AC-16 | A token whose signature is forged or absent, or which is expired, is rejected with 401 | T-85 | `ForgedToken_Rejected`, `ExpiredToken_Rejected` |
| AC-17 | A token for a disabled membership is rejected within 60 s | SEC-08, T-85 | `DisabledMembership_TokenRejected` |
| AC-18 | `/auth/switch-tenant` is the only endpoint accepting a tenant id, and only from the caller's own membership list; another tenant's id returns 404 | API-01, API-02, SEC-13 | `SwitchTenant_ToForeignTenant_Returns404` |
| AC-19 | A body containing `tenantId` on any other endpoint is rejected `400 unexpected_field` | API-01, SEC-17 | `Body_WithTenantId_Returns400UnexpectedField` |
| AC-20 | Unknown fields in any request body are rejected `400 unexpected_field` | SEC-17 | `Body_WithUnknownField_Returns400UnexpectedField` |
| AC-21 | **The role → permission map equals doc 01 §5.1**, parsed from the document itself | SEC-12, T-60 | `RolePermissionMap_MatchesProductRequirementsDocument` |
| AC-22 | Authorization checks a permission, never a role string | SEC-12 | `NoEndpoint_AuthorizesOnRoleName` |
| AC-23 | **An endpoint without a declared permission prevents application startup** | SEC-10, T-61 | `Startup_FailsWhenAnEndpointDeclaresNoPermission` |
| AC-24 | Every protected endpoint has a 401 test (no token) | SEC-14 | `EveryProtectedEndpoint_Returns401WithoutToken` |
| AC-25 | Every protected endpoint has a 403 test (authenticated, lacking the permission) | SEC-14 | `EveryProtectedEndpoint_Returns403ForRoleWithoutPermission` |
| AC-26 | Every endpoint taking an entity id returns **404, never 403**, for another tenant's id | SEC-13, SEC-14, T-71 | `EveryEndpointWithId_Returns404ForCrossTenantId` |
| AC-27 | **A user of organization A cannot read organization B's data**: members, organization profile and audit of B are invisible and unreachable with A's token | SEC-14, T-71 | `TenantA_CannotReadTenantB_Members_Organization_Or_Audit` |
| AC-28 | **A user of organization A cannot authenticate into organization B**: correct credentials never yield a token scoped to a tenant the user has no membership in | SEC-11, API-02 | `UserOfTenantA_CannotObtainTokenForTenantB` |
| AC-29 | **Schema assertions**: every tenant-scoped table has `tenant_id NOT NULL`, RLS `ENABLED` **and** `FORCED`, at least one policy, a `(tenant_id, id)` unique key, and no single-column FK to another tenant-scoped table | DM-01/02/10, T-70 | `EveryTenantScopedTable_HasTenantIdRlsForcedPolicyAndCompositeKeys` |
| AC-30 | **No-GUC test**: a connection that never sets `app.tenant_id` returns zero rows from every tenant-scoped table — fail closed, not fail open | DM-05, T-72 | `ConnectionWithoutTenantGuc_ReturnsZeroRows` |
| AC-31 | **Pool leakage test**: a connection returned to the pool and re-borrowed carries no tenant setting | SEC-23, T-73 | `PooledConnection_DoesNotLeakTenantGuc` |
| AC-32 | **Raw-SQL path test**: a deliberately unfiltered raw query returns only the current tenant's rows — L2 works with L1 bypassed | T-74 | `UnfilteredRawSql_ReturnsOnlyCurrentTenantRows` |
| AC-33 | **Composite-FK test**: inserting a child row whose parent belongs to another tenant fails at the database level with L1 and L2 bypassed — L3 works independently | DM-10, T-75 | `CrossTenantChildRow_IsRejectedByCompositeForeignKey` |
| AC-34 | The application database role has **no `BYPASSRLS`** and does **not** own the tables | SEC-21, DM-03 | `ApplicationRole_HasNoBypassRlsAndOwnsNothing` |
| AC-35 | The tenant id is taken **only** from the validated token claim; no code path reads it from a header, query string, body, cookie or referer | SEC-20 | `NoSourceFile_ReadsTenantIdFromRequestInput` |
| AC-36 | `audit_events` is append-only: `UPDATE` and `DELETE` raise at the database level | SEC-51, DM-28, T-87 | `AuditEvents_UpdateAndDelete_Raise` |
| AC-37 | The per-tenant audit hash chain verifies, and a tampered row breaks it | SEC-53, T-87 | `AuditChain_Verifies_AndDetectsTampering` |
| AC-38 | Registration, login success, login failure, logout, refresh reuse and tenant switch each write exactly one audit row with the correct actor | SEC-50 | `AuthEvents_AreAudited` |
| AC-39 | Audit rows are readable only within the caller's own tenant | SEC-54 | `Audit_IsReadableOnlyWithinOwnTenant` |
| AC-40 | Auth endpoints are rate limited to 10 req/min and return `429` with `Retry-After` | API-13, SEC-70, T-86 | `AuthEndpoints_AreRateLimited` |
| AC-41 | Errors are RFC 9457 `application/problem+json` with `code`, `traceId` and machine-readable `messageKey`s — never display prose | API-04, doc 05 §0.2 | `Errors_AreProblemJsonWithMessageKeys` |
| AC-42 | No secret, password, token or hash appears in log output | SEC-41, T-82 | `LogPipeline_RedactsSecrets` |
| AC-43 | Every user-visible string exists in both `ar.json` and `en.json`; a missing or untranslated key fails the build | PRD-20, UI-11 | `web: i18n key parity check` |
| AC-44 | The Arabic UI renders `dir="rtl"`; component code uses logical CSS properties only | UI-20, PRD-21 | `web: rtl direction test`, `web: no-physical-properties lint` |
| AC-45 | Navigation and actions are filtered by the permission list from `/me`, not by role name | UI-01 | `web: navigation filters by permission` |
| AC-46 | No monetary value is computed anywhere in this slice; no `float`/`double` on any money-named member | FIN-01, T-23 | `NoAssemblyType_HasFloatingPointMoneyMember` |

---

## 3. Test inventory

| Layer | Project | Tests | Covers |
|-------|---------|------:|--------|
| Unit (xUnit) | `tests/unit/FinanceAi.UnitTests` | 49 | AC-05, AC-21, AC-22, AC-35, AC-42, AC-46 and the domain guards |
| Integration (xUnit + real PostgreSQL) | `tests/integration/FinanceAi.IntegrationTests` | 47 | AC-01…AC-20, AC-38…AC-41 |
| Security / isolation (xUnit + real PostgreSQL) | `tests/security/FinanceAi.SecurityTests` | 19 | AC-23…AC-37, AC-39 |
| Web unit (Vitest) | `apps/web` | 19 | AC-43, AC-44, AC-45 |
| | **Total** | **134** | |

Tests run against **real PostgreSQL** (T-03): the fixture provisions a uniquely-named
database per run, applies the real migrations as `migrator`, and connects as `app`.
Every test creates its own tenants (T-04).

---

## 4. Endpoints delivered, with their declared permission

| Method | Path | Permission | 401 | 403 | 404 cross-tenant |
|--------|------|-----------|:---:|:---:|:---:|
| POST | `/api/v1/auth/register` | *anonymous* | — | — | — |
| POST | `/api/v1/auth/login` | *anonymous* | — | — | — |
| POST | `/api/v1/auth/refresh` | *anonymous (cookie)* | — | — | — |
| POST | `/api/v1/auth/logout` | *authenticated* | ✅ | — | — |
| GET | `/api/v1/auth/tenants` | *authenticated* | ✅ | — | — |
| POST | `/api/v1/auth/switch-tenant` | *authenticated* | ✅ | — | ✅ |
| GET | `/api/v1/me` | *authenticated* | ✅ | — | — |
| PATCH | `/api/v1/me` | *authenticated* | ✅ | — | — |
| GET | `/api/v1/organization` | `tenant.read` | ✅ | ✅ | n/a (implicit) |
| PATCH | `/api/v1/organization` | `tenant.settings.write` | ✅ | ✅ | n/a (implicit) |
| GET | `/api/v1/organization/members` | `users.read` | ✅ | ✅ | ✅ (no B rows) |
| GET | `/api/v1/organization/members/{id}` | `users.read` | ✅ | ✅ | ✅ |
| GET | `/api/v1/audit` | `audit.read` | ✅ | ✅ | ✅ (no B rows) |

Anonymous endpoints declare `AllowAnonymous` explicitly; the startup assertion (AC-23)
treats an *undeclared* endpoint as a boot failure, so "anonymous" is a decision on record,
not an omission.

---

## 5. The tenant-isolation pattern every later slice copies

1. **Migration.** A new tenant-scoped table is created by `database/migrations/*.sql` and
   MUST, in the same migration, declare `tenant_id uuid NOT NULL`, `ENABLE`+`FORCE ROW
   LEVEL SECURITY`, a `tenant_isolation` policy built from `app_current_tenant()`, a
   `UNIQUE (tenant_id, id)` key, and composite `(tenant_id, …)` foreign keys. AC-29 fails
   the build otherwise — there is no way to add a table and forget.
2. **Entity.** The C# entity implements `ITenantScoped`. `TenantDbContext` applies a global
   query filter to every `ITenantScoped` entity automatically and stamps `TenantId` on save.
3. **Request pipeline.** `authenticate → resolve tenant from the token's tid → open a
   transaction and `set_config('app.tenant_id', …, true)` → check permission → handle`
   (SEC-11). A handler never sees an unscoped context.
4. **Endpoint.** Declares `.RequiresPermission(Permissions.X)` or `.AllowAnonymousEndpoint()`.
   Anything else refuses to boot.
5. **Tests.** The three generated suites (401 / 403 / 404-cross-tenant) pick the new endpoint
   up from the route table automatically; the schema suite picks the new table up from
   `pg_catalog`.

The platform tables `tenants`, `users`, `tenant_memberships`, `refresh_tokens` and
`schema_migrations` are the documented exception (DM-06): they are reachable only through
`PlatformIdentityStore`, the single file permitted to open a platform scope, which AC-35's
sibling test enforces by static check.

---

## 5a. What the tests found

Written down because each was a real defect or a real gap in the specification, not a test that
needed adjusting.

| # | Found by | What it was |
|---|----------|-------------|
| F-1 | `Register_WhenCommitFails_LeavesNoRows` | Two concurrent registrations of the same address produced an **unhandled 500**. A 500 on a duplicate address is an account-existence oracle (SEC-07). The unique-violation is now caught and reported as the ordinary "pending" outcome. |
| F-2 | `Refresh_Reuse_RevokesFamily_AndAudits` | Reuse detection fired again every time the victim's client retried with its now-revoked cookie, so one theft produced a stream of alerts. Detection is now scoped to a genuinely *spent* (rotated) token; a merely revoked one is rejected quietly. SEC-04's alert is only useful if it is rare. |
| F-3 | `Body_WithTenantId_Returns400UnexpectedField` | Minimal-API binding swallowed the JSON failure and returned a **bodiless 400**, so `unexpected_field` never reached the client and doc 05 §0.2's problem shape was never produced. `ThrowOnBadRequest` now hands it to the problem-details middleware. |
| F-4 | `AuthEndpoints_AreRateLimited` | `UseRateLimiter()` sat **before** `UseRouting()`, so it never saw the endpoint's policy metadata and the auth limit of API-13 was not applied at all. Middleware order corrected. |
| F-5 | Live run of the built application | `/health` returned **401**: deny-by-default caught it correctly, but a liveness probe that always fails is an outage waiting to happen. It now declares anonymous access explicitly, like every other endpoint. |
| F-6 | `EveryProtectedEndpoint_Returns403ForRoleWithoutPermission` | **A gap in the specification, not the code.** PRD-13 requires "a 403 for at least one role that lacks it" for *every* permission, but doc 01 §5.1 grants `tenant.read` (and `customers.read`, `invoices.read`, `payments.read`, `aging.read`, `cases.read`, `ai.suggestions.read`) to all five roles. For those, no role can be refused, so no 403 exists to assert. The sweep records them explicitly and pins the list, so a permission becoming universal is a visible change rather than a silent loss of coverage. **PRD-13 should be reworded to exempt universal permissions, whose boundary is tenancy rather than role.** |
| F-7 | `Login_LocksAccountAfterTenFailures` | The tenth failed attempt returns `423 Locked` rather than `401`, which does reveal that the address exists. That is what doc 06 §6.1 asks for (a distinct locked state with an unlock time) and it costs ten attempts against a real address, behind a 10/minute rate limit. Recorded as an accepted trade-off rather than an oversight. |
| F-8 | `AuditEvents_UpdateAndDelete_Raise` | Not a defect, but worth knowing: `FORCE ROW LEVEL SECURITY` means the table owner cannot see a row without a tenant bound, so an owner-level `UPDATE` matches nothing and never reaches the append-only trigger. Both controls are real; the test now binds a tenant first so it exercises the trigger rather than accidentally re-testing RLS. |

---

## 6. Deviations from the specification, and why

| # | Spec | Deviation | Reason |
|---|------|-----------|--------|
| D-1 | T-03 "Testcontainers for PostgreSQL" | Tests use the local PostgreSQL instance and provision a per-run database | No Docker socket in this environment; podman's API socket is not enabled by default. The requirement that matters — **real PostgreSQL, not SQLite or in-memory** — is met. The fixture is behind `PostgresTestDatabase`, so swapping in Testcontainers in CI is a one-file change. |
| D-2 | doc 04 §4 | `refresh_tokens` added; `users.status` gains `'Locked'`-equivalent via `locked_until` (already specified) | Doc 04 specifies no session storage. SEC-04 requires opaque rotating refresh tokens with family revocation, which needs a table. Doc 04 amended in the same commit (DM-06 note). |
| D-3 | doc 04 §4 | `tenant_memberships` and `refresh_tokens` carry RLS **in addition to** being platform tables | Stronger than specified: during a tenant-scoped request they are protected by L2 like any business table; the platform escape is confined to one file and statically checked. |
| D-4 | doc 10 slice 1 | MFA, email verification, password reset, invitations, role management, holidays deferred | See §8. The slice as scoped by the product owner is registration, login and session handling. |
| D-5 | doc 06 | shadcn/ui components are hand-rolled to the same API surface rather than pulled in via the generator | Only four primitives are needed at this size; the generator pulls a dependency tree that would need licence recording for components this slice does not use. Revisit at slice 2. |

---

## 7. Definition of done (doc 09 §9) — status

1. Acceptance criteria implemented as named tests and passing — see §2.
2. Unit, integration and tenant-isolation suites pass (115 .NET + 19 web = **134**, 0 failures). E2E: **not run** (see §8).
3. Every new endpoint has 401 / 403 / 404-cross-tenant tests — AC-24…AC-26, generated from
   the route table.
4. Every new tenant-scoped table passes the schema-isolation assertions — AC-29.
5. Financial invariants: **not applicable**, no money in this slice (AC-46 asserts none).
6. `security-review` on the diff: to be run by the reviewer before merge (SEC-105).
7. `dotnet format --verify-no-changes`, `dotnet build -c Release` (0 warnings, 0 errors), `dotnet test -c Release`, `npm run build` and `npx vitest run` all green.
8. `THIRD-PARTY-NOTICES.md` updated in the same commit.
9. Docs updated where behaviour diverged — §6, plus the doc 04 amendment.

---

## 8. Explicitly not delivered, and what it blocks

| Deferred | Blocks | Note |
|----------|--------|------|
| Email verification | doc 05 `/auth/verify-email`, T-121 | `users.email_verified_at` exists and is `NULL`; Mailpit is already running for it. |
| TOTP MFA (SEC-02, doc 10 §1.5) | Owner/Admin login hardening | `users.mfa_secret_enc` exists and is unused. **This is a security requirement, not a nice-to-have** — it should be slice 1b's first item. |
| Password reset (SEC-07) | Account recovery | Without it, a locked-out Owner needs operator intervention. |
| Invitations, role change, deactivate, transfer ownership | doc 10 §1.6, §1.7, T-121 | The permission model, the `users.invite` / `users.role.write` / `users.deactivate` permissions and the last-Owner guard are all implemented and tested at the domain level; only the endpoints are missing. |
| `tenant_settings` PATCH, holiday calendar | doc 10 §1 settings screens | The `tenant_settings` row is created at registration with the documented defaults. |
| Audit viewer UI | doc 06 §6.2 | `GET /api/v1/audit` exists and is tested. |
| Playwright E2E (T-120, T-121) | doc 09 §9.2 | T-121 requires the invitation flow, which is deferred. Web behaviour is covered by Vitest at this size. |
| Restore drill (PRD-23, T-150) | doc 09 §9 slice-1 DoD | Operational task requiring a backup target; not performable from this environment. |
