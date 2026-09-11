# Operations runbook (SEC-103)

Status: **written in slice 14, before pilot.** Every action here was exercised at least once on the compose stack
(see `docs/slices/slice-14-deployment.md` §3). Keep this file short enough to read during an incident.

**Who is called:** the Owner of the affected organization (their email is on `/organization/members`) and the
operator running the stack. There is no on-call rotation in v1 (A-15: single self-hosted region).

---

## 1. The stack

```
infrastructure/stack.sh --profile full up -d --build     # everything from a clean clone
infrastructure/stack.sh --profile full ps                # health of every container
infrastructure/stack.sh --profile full logs -f api       # structured JSON logs with request_id (SEC-101)
```

| Container | Image | Reachable from | Port |
|-----------|-------|----------------|------|
| `web` | `finance-ai/web` (nginx, non-root) | the internet / edge | **the only published port** (`WEB_PORT`, default 8080) |
| `api` | `finance-ai/api` (.NET, non-root, read-only rootfs) | `web` only | 5080, not published |
| `ai` | `finance-ai/ai` (FastAPI, non-root, read-only rootfs) | `api` only | 8090, not published (SEC-69, AI-101) |
| `ollama` | `ollama/ollama` | `ai` only | 11434, not published |
| `postgres` | `pgvector/pgvector:pg16` | `api`, `migrate` | 5432 bound to **127.0.0.1** on the host for backups; never a public interface (SEC-69) |
| `mailpit` | dev only | operator | 127.0.0.1:8025 |
| `migrate` | the api image, `migrate` command | one-shot before `api` | — |
| `ollama-pull` | pulls `AI_MODEL` | one-shot before `ai` | — |
| `caddy` | `--profile tls` | the internet | 80/443, automatic HTTPS for `DOMAIN` (SEC-60) |

The refresh cookie is `Secure`, so the stack is only usable over **HTTPS** or on `http://localhost` (browsers exempt
it). Use `--profile tls` with a public `DOMAIN`, or put your own TLS edge in front of `web:8080` and forward
`X-Forwarded-Proto`.

### Secrets (`.env`, git-ignored, SEC-67)

| Variable | What it protects | Generate |
|----------|-----------------|----------|
| `POSTGRES_PASSWORD` | the database superuser (backups, migrations bootstrap) | `openssl rand -hex 24` |
| `POSTGRES_APP_PASSWORD`, `POSTGRES_MIGRATOR_PASSWORD`, `POSTGRES_REPORTING_PASSWORD` | the least-privilege roles (SEC-100) | `openssl rand -hex 24` |
| `JWT_SIGNING_KEY_PEM_BASE64` | every access token and re-auth proof (SEC-03) | `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 \| base64 -w0` |
| `MFA_KEK_BASE64` | TOTP secrets at rest (SEC-02) | `openssl rand -base64 32` |
| `AI_SERVICE_TOKEN` | the API → AI service call (AI-101) | `openssl rand -hex 24` |
| `WEB_ORIGINS` | which origins the API accepts (SEC-62) — set to the public URL, e.g. `https://ar.example.jo` | — |
| `DOMAIN` | the `tls` profile's certificate subject | — |

Never paste any of these into a ticket, a log line or a chat. The API refuses to start without the signing key; the
AI service refuses to start without a token of ≥ 16 characters.

---

## 2. Stop all outbound email — one action

**Global (every tenant), no deploy:**

```
sed -i 's/^OUTBOUND_SENDING_ENABLED=.*/OUTBOUND_SENDING_ENABLED=false/' .env
infrastructure/stack.sh --profile full up -d --no-build api      # recreates only the api container (≈10 s)
```

`OUTBOUND_SENDING_ENABLED` is read on every send (`MailTransport.GloballyEnabled`); anything other than `true`/`1`
stops sends **and** the daily job's reminder and briefing sends. Verified by the integration test
`KillSwitch_AndQuietHours_HoldTheMail` (T-152). `GET /organization/outbound` shows `globallyEnabled: false` to every
tenant so Owners know why nothing goes out.

**One tenant** (an Owner/Admin with `tenant.settings.write`, from the Outbound settings screen or the API):

```
PUT /api/v1/organization/outbound   {"outboundSendingEnabled": false}
```

Audited as `tenant.outbound_settings_changed`. Pending approved messages stay `Approved` and are sent when the switch
comes back — nothing is lost, nothing goes out meanwhile.

**AI, one tenant:** `PATCH /api/v1/organization/ai-settings {"aiEnabled": false}` — suggestions stop, the briefing
renders metrics only. **AI, everywhere:** `infrastructure/stack.sh --profile full stop ai` — the API treats the
service as unavailable (`ai_unavailable`, never an error to the user).

---

## 3. Revoke sessions

Access tokens live **15 minutes** (SEC-03); nothing can revoke one earlier, so the target is the refresh token —
after it is revoked the session dies at the next refresh, at most 15 minutes later.

| Scope | How |
|-------|-----|
| One member | `POST /api/v1/organization/members/{id}/deactivate` — revokes every refresh token of that membership (`membership_deactivated`), the member can no longer sign in |
| One user, all their tenants | `POST /api/v1/auth/forgot-password` → the reset link → `reset-password`: every refresh token of the user is revoked (`password_reset`) |
| Everyone (compromise of the signing key or a mass leak) | rotate the signing key (§4) — every existing access token and re-auth proof becomes invalid immediately; then `UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'superseded' WHERE revoked_at IS NULL;` as `finance` (the superuser; the `app` role is RLS-bound and cannot do this across tenants) |

Only the SQL step needs the database; everything else is the product's own API and is audited.

---

## 4. Rotate keys

| Key | Procedure | Effect |
|-----|-----------|--------|
| `JWT_SIGNING_KEY_PEM_BASE64` | generate a new key, replace it in `.env`, `stack.sh --profile full up -d --no-build api` | all access tokens and re-auth proofs are invalid at once; refresh tokens survive, so signed-in users get a new access token on their next call without noticing. There is no dual-key grace window in v1 (flagged). |
| `AI_SERVICE_TOKEN` | replace in `.env`, `up -d --no-build ai api` | a few seconds of `ai_unavailable` while both restart |
| `POSTGRES_*_PASSWORD` | `ALTER ROLE app PASSWORD '…'` (etc.) as the superuser, then update `.env`, `up -d --no-build api` | the migrator re-applies the role passwords from `.env` on its next run |
| `MFA_KEK_BASE64` | **cannot be rotated in place in v1.** TOTP secrets are AES-256-GCM under this key with no key id; changing it makes every enrolment undecryptable and every Owner/Admin unable to sign in. Keep a copy in the secret store. If it is compromised: disable MFA for affected users in SQL (`UPDATE users SET mfa_enabled_at = NULL, mfa_secret_enc = NULL, mfa_pending_secret_enc = NULL …`), change the key, ask them to re-enrol within their 7-day grace period. Flagged in `docs/slices/slice-14-review.md`. |

---

## 5. Backups and the restore drill (PRD-23, T-150)

```
infrastructure/backup.sh                     # pg_dump -Fc → backups/<db>-<UTC>.dump, via 127.0.0.1:5432
infrastructure/restore-drill.sh backups/x.dump   # restores into a scratch database, compares per-table counts, prints RPO/RTO
```

Both read `.env`. On the compose stack `POSTGRES_HOST=127.0.0.1` on the host (the loopback binding above). Run the
backup nightly from cron and the drill weekly; a failed drill is page-worthy (SEC-102). Backups contain every
tenant's data and the encrypted TOTP secrets — store them where only the operator can read them.

**Restoring for real:** `stack.sh --profile full stop api`, `pg_restore --clean --if-exists -d finance_ai x.dump`
as `finance`, `stack.sh --profile full up -d --no-build migrate api` (the migrator is idempotent).

---

## 6. Symptoms → first look

| Symptom | Look at |
|---------|---------|
| Sign-in returns `mfa_unavailable` | `MFA_KEK_BASE64` missing or malformed in `.env` |
| Owners see `mfa_enrollment_required` | their 7-day grace expired; they enrol at `/security` — nothing to fix |
| Every send stays `Approved` | §2: the global switch, then the tenant switch, then `daily_send_cap` in `/organization/outbound` |
| `ai_unavailable` everywhere | `stack.sh --profile full ps` — `ollama-pull` must have exited 0 (the model is ~2.5 GB); `logs ai` |
| Briefing has metrics but no narrative | expected when the guard rejects a numeral (`rejected_by_guard` in the AI suggestions list); no action |
| `api` unhealthy after `migrate` | `logs migrate` — a migration failed; the API never starts against a half-migrated schema |
| Browser gets 401 on everything after sign-in | the site is served over plain HTTP on a non-localhost host — the `Secure` cookie is dropped; use TLS |
