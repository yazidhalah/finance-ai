# Slice 24 — Contract completion II: email verification, tenant email settings, the MTA webhook

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source: the last three rows of doc 05 that were never built — `POST /auth/verify-email` ("email verification
required", doc 05 line 94/98; T-121 "register → verify email → invite"), `GET/PUT /organization/email-settings` +
`POST /organization/email-settings/test` (line 378: tenant SMTP with **write-only** secrets; SEC-09 re-authentication
to change; SEC-66 SSRF blocklist; SEC-67 encrypted under a KEK; slice 8 D-1 deferred it), `POST /webhooks/email-events`
(line 379: signed bounce/delivery events from the local MTA; the `Sent → Delivered | Bounced` transitions the message
machine has carried as "deferred" since slice 8 D-6).

## 1. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **What does "verification required" mean?** | A registration sends a verification link (24 h, single use, hashed like the reset tokens). Until the address is verified, **login answers `401 email_unverified` after the password matched** — never before (SEC-07: the outcome for a wrong password is unchanged). Accepting an invitation or completing a password reset verifies the address as a side effect (they already did). | Doc 05; SEC-07. |
| **Uniformity** | `POST /auth/verify-email {token}` answers `200 {accepted: true}` for a good token and `200 {accepted: false}` for every unusable one; one time budget either way. | SEC-07. |
| **Tenant SMTP** | One row per tenant (`tenant_email_settings`): host, port, TLS, username, `from_address`, and the password sealed by `ISecretBox` (the MFA KEK — the same envelope, the same rotation). `GET` returns everything but the password (`hasPassword`); `PUT` needs the five-minute re-authentication proof (SEC-09); `POST …/test` opens an SMTP session with the stored settings and reports. When a tenant has settings, its customer mail goes through them; otherwise the `.env` host (slice 8) — unchanged behaviour for every tenant that never sets one. | SEC-67, SEC-09, slice 8 D-1. |
| **SSRF** | A tenant host must be a hostname or public IP: loopback, RFC 1918, link-local (incl. `169.254.169.254`), unique-local and `localhost` are refused at `PUT` and again before every connection, after DNS resolution. `SMTP_ALLOW_PRIVATE_HOSTS=1` (dev/test, documented) lets Mailpit through. | SEC-66. |
| **Webhook** | `POST /webhooks/email-events` is anonymous **and signed**: `X-Signature: sha256=<HMAC-SHA256(body, EMAIL_WEBHOOK_SECRET)>`, compared in constant time; unset secret → `503`; bad signature → `401`. Each event names the `messageId` the mail carried in `X-FinanceAi-Message` — now `<messageId>.<tenantId>` so the handler can enter that tenant's scope as the system actor — and applies `Delivered` or `Bounced` to a `Sent` message through the machine (audited); anything else is skipped and counted. A bounce records `bounced` on the message and leaves a note on the case timeline. | Doc 05; SEC-20 (never trust a tenant id from a request — here it comes from our own header echoed back and is checked against the message row). |

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | Register → the mail in Mailpit carries the link → login before verifying is `401 email_unverified` (only after a correct password: a wrong password is still `invalid_credentials`) → `verify-email` → login works; a reused or garbage token answers the same 200 shape with `accepted: false`; an accepted invitation is verified without a link | `EmailVerificationTests` |
| AC-02 | `PUT /organization/email-settings` without `X-Reauth` is `403 reauthentication_required`; with it, the settings are stored, the password is sealed (not the clear text in the row), `GET` never returns it; a private host is `422 smtp_host_not_allowed` unless `SMTP_ALLOW_PRIVATE_HOSTS`; `…/test` reports `ok: true` against Mailpit; dispatch sends the tenant's mail through the tenant's host (Mailpit sees the tenant's `From`) | `TenantEmailSettingsTests` |
| AC-03 | Webhook: unsigned → 401; wrong secret → 401; a `delivered` event moves a `Sent` message to `Delivered` (audited `message.delivered`); a `bounced` one to `Bounced` with the reason; an event for a message in another state or an unknown id is counted as skipped; a tenant id in the header that does not own the message is skipped | `EmailWebhookTests` |
| AC-04 | Route pin 155 → 159; anonymous set 6 → 8 (`verify-email`, `webhooks/email-events`) | security suite |

## 3. Endpoints, with declared permission

| Method | Path | Permission |
|--------|------|-----------|
| POST | `/auth/verify-email` | anonymous (`/auth` rate limit) |
| GET | `/organization/email-settings` | `tenant.settings.write` |
| PUT | `/organization/email-settings` | `tenant.settings.write` + re-authentication (SEC-09) |
| POST | `/organization/email-settings/test` | `tenant.settings.write` |
| POST | `/webhooks/email-events` | anonymous, HMAC-signed |

## 4. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Login is where verification is enforced, after the password | The one place every path converges; and the failure ordering keeps SEC-07 intact. |
| D-2 | The tenant's SMTP password lives in the MFA envelope | One KEK, one rotation procedure (slice 17), one place to audit. |
| D-3 | The tenant id rides in our own mail header, not in the webhook's trust | The MTA echoes what we set; the handler still checks the message belongs to that tenant before touching it. |
| D-4 | No resend endpoint | Not in doc 05; an expired link is recovered through forgot-password, which verifies on use. |
