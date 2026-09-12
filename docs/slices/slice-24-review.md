# Slice 24 — Contract completion II: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **high** — an anonymous signed endpoint, tenant credentials at rest,
outbound connections to tenant-named hosts.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

Five. Route pin 155 → 160; the anonymous set grows from six to eight, each on purpose.

| Endpoint | Declaration | Why |
|----------|-------------|-----|
| `POST /auth/verify-email` | **anonymous**, `/auth` rate limit | The person has no session yet; `200 {accepted}` for every token, one time budget. |
| `POST /webhooks/email-events` | **anonymous, HMAC-signed** | The MTA has no session. The signature over the raw body is verified in constant time before the body is parsed; an unset secret is `503`, a bad one `401`. The tenant is taken from our own header value echoed back, then the message is looked up **inside that tenant's scope** — a wrong pairing finds nothing and is skipped. |
| `GET /organization/email-settings`, `POST …/test` | `tenant.settings.write` | doc 05's row. |
| `PUT /organization/email-settings` | `tenant.settings.write` + `RequiresReauth` | SEC-09: changing email settings needs the five-minute proof (`TenantEmailSettings_…` proves `403 reauthentication_required` without it). |

The login change is not a new endpoint but is the sensitive one: `email_unverified` is answered **only after the
password matched** — a wrong password is still `invalid_credentials` (asserted first in the test), so the unverified
state is disclosed to nobody but the address's owner (SEC-07).

## 2. Every new table: `tenant_id`, RLS enabled and forced, a policy, composite FKs?

Two, in migration 0018.

- `email_verification_tokens`: a **platform table by design** (DM-06), the shape and policy of `password_reset_tokens`
  (`platform_only`), touched only by the anonymous identity flows in `PlatformIdentityStore` (the source rule holds).
- `tenant_email_settings`: `tenant_id`, RLS enabled **and** forced, `tenant_isolation` policy, `UNIQUE (tenant_id,
  id)`, one row per tenant, **no DELETE grant and no reporting grant** — credentials, sealed or not, are never readable
  by the reporting role. `TenantIsolationTests` picks it up; the targeted evidence is the settings test: the stored
  column holds an `FKEK1` envelope, and neither the API response nor the audit row contains the clear password.
- `messages` gains `delivered_at` and `bounce_reason`.

## 3. Pre-auth / cross-tenant paths?

- `VerifyEmailAsync` (`PlatformIdentityStore`): hash → single lookup → same false for missing, used and expired.
- The webhook handler: per event it constructs a context bound to the tenant named in the tag and enters that
  tenant's RLS scope (the way a background job does, SEC-22). It never reads across tenants; an event naming another
  tenant's message finds no row.
- The mail transport now resolves tenant-named hosts. **SEC-66** is enforced twice: at `PUT` and again before every
  connection after DNS resolution (`SmtpHostPolicy`), refusing loopback, RFC 1918, link-local (169.254.169.254),
  CGNAT, unique-local and `localhost`/`.local`/`.internal`. `SMTP_ALLOW_PRIVATE_HOSTS=1` is the documented dev/test
  switch; the tests exercise the policy's `IsPublic` directly as well.

## 4. Money?

None.

## 5. AI?

None. The bounce lands on the message and its audit trail; no AI path reads it.

## 6. Dependencies?

None. HMAC, SMTP and DNS are the base class library.

## What changed for existing deployments

- **Registration now requires verification before the first sign-in.** Users who registered before this slice have
  `email_verified_at` NULL unless they accepted an invitation or reset their password — they would be locked out.
  The runbook says what to do: `UPDATE users SET email_verified_at = now() WHERE email_verified_at IS NULL AND
  created_at < '<deploy time>'` once, as the operator, since those addresses were already trusted in practice.
- The stack's API sends through the Mailpit container by default (`STACK_SMTP_HOST` names the real relay) — the
  slice 14 compose file had interpolated the dev `.env`'s loopback host into the container, which the smoke test's
  new verification step caught.

## Flagged

- **Verification mail failures are logged, not surfaced** (an unsendable address still gets the uniform 202); recovery is the forgot-password path (D-4). A resend endpoint is not in doc 05.
- **`…/email-settings/test` sends a real message to the From address** — System.Net.Mail has no bare handshake; documented in the endpoint.
- **The webhook trusts the `occurredAt` the MTA sends** (falls back to now); it is recorded, not used for any decision.
- **Bounces do not yet mark the contact** (`bounced_email` on the customer contact would let the send guard refuse the address next time) — a small follow-up.
