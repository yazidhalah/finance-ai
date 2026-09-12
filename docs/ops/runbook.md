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
| `STACK_SMTP_HOST` / `SMTP_PORT` | the relay the stack's API sends through (`mailpit` inside the stack by default — replace with the real relay before pilot; SEC-84 SPF/DKIM on its domain) | — |
| `EMAIL_WEBHOOK_SECRET` | the MTA's signed delivery/bounce events (`POST /api/v1/webhooks/email-events`, `X-Signature: sha256=…`) | `openssl rand -hex 32` |
| `BACKUP_PASSPHRASE` | every backup at rest (SEC-94, slice 31); `backup.sh` refuses to run without it; losing it loses the backups | `openssl rand -base64 32` |

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
renders metrics only. **One operation only** (T-105, a gate the pilot failed): `{"aiClassificationEnabled": false}`
or `{"aiBriefingEnabled": false}` — the other keeps running; the kill switch overrides both. **AI, everywhere:** `infrastructure/stack.sh --profile full stop ai` — the API treats the
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
| `JWT_SIGNING_KEY_PEM_BASE64` | 1. `JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS=<the current value>`; 2. generate a new key into `JWT_SIGNING_KEY_PEM_BASE64`; 3. `stack.sh --profile full up -d --no-build api`; 4. after 15 minutes remove `_PREVIOUS` and recreate `api` again (optional — the window closes by itself). | Every new token carries the new key's `kid`. Tokens the old process issued keep working until they expire (15 min; re-auth proofs 5 min): the previous key verifies **only tokens issued before the new process started**, so a leaked old key cannot mint anything the API accepts. Refresh tokens are unaffected; nobody is signed out. |
| `MFA_KEK_BASE64` | 1. `MFA_KEK_BASE64_PREVIOUS=<the current value>`; 2. `openssl rand -base64 32` into `MFA_KEK_BASE64`; 3. `stack.sh --profile full up -d --no-build api` — from here every successful sign-in re-seals that user's secret under the new key; 4. `stack.sh --profile full run --rm api rotate-mfa-kek` (or `dotnet FinanceAi.Migrator.dll rotate-mfa-kek`) re-seals everyone else and prints the counts; 5. remove `_PREVIOUS`, recreate `api`. | The command **refuses to run** while any row is under a key it does not hold, so step 5 is safe once it has reported. Envelopes carry the key id (`FKEK1 · kid`); rows written before slice 17 have none and are re-sealed the same way. If the old key is already lost, the affected users must re-enrol (`UPDATE users SET mfa_enabled_at = NULL, mfa_secret_enc = NULL, mfa_pending_secret_enc = NULL WHERE id = …`) within their grace period. |
| `AI_SERVICE_TOKEN` | replace in `.env`, `up -d --no-build ai api` | a few seconds of `ai_unavailable` while both restart |
| `POSTGRES_*_PASSWORD` | `ALTER ROLE finance_app PASSWORD '…'` (etc.) as the superuser, then update `.env`, `up -d --no-build api` | the migrator re-applies the role passwords from `.env` on its next run |

Both signing and KEK rotations are exercised by `KeyRotationTests` (slice 17) on every CI run.

## 5. Backups and the restore drill (PRD-23, T-150)

```
infrastructure/backup.sh                         # pg_dump -Fc, encrypted → backups/<db>-<UTC>.dump.enc (mode 600), via 127.0.0.1:5432
infrastructure/restore-drill.sh backups/x.dump.enc   # decrypts to a private temp file, restores into a scratch database, compares per-table counts, prints RPO/RTO
```

Both read `.env`. On the compose stack `POSTGRES_HOST=127.0.0.1` on the host (the loopback binding above). Run the
backup nightly from cron and the drill weekly; a failed drill is page-worthy (SEC-102). Backups contain every
tenant's data and the encrypted TOTP secrets, so they are **encrypted at rest** (SEC-94, slice 31): AES-256-CBC
with PBKDF2 under `BACKUP_PASSPHRASE` (§1). `backup.sh` refuses to run without it. Keep the passphrase with the other
secrets, never beside the backups — without it every backup is unreadable.

**Restoring for real** (a privileged operation that can resurrect deleted data — SEC-94):

1. `stack.sh --profile full stop api`
2. `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass env:BACKUP_PASSPHRASE -in x.dump.enc -out /tmp/x.dump`
3. `pg_restore --clean --if-exists -d finance_ai /tmp/x.dump` as `finance`; `rm /tmp/x.dump`
4. `stack.sh --profile full up -d --no-build migrate api` (the migrator is idempotent)
5. **Record it:** `stack.sh --profile full run --rm api record-restore --dump x.dump.enc --reason "<why>" --by "<you>"`
   (or `dotnet FinanceAi.Migrator.dll record-restore …`) — appends `instance.restored` (the file, its SHA-256, the
   reason, who) to **every tenant's audit chain**, hashed like any other event, so Owners see on their Audit screen
   that data may have moved back in time. A restore without this step is a finding.

---

## 6. Alerts (SEC-102) — what pages, and the first thing to do

Alerts are raised by the sweep's last step (the invariant job and the detectors, slice 15) and by the two scripts.
Every alert is a row on the tenant's Audit screen, a `Critical`/`Warning` JSON log line, an email to `ALERT_EMAIL`
and a POST to `ALERT_WEBHOOK_URL` — set both in `.env`. An Owner may also opt their organization into a copy of
**critical** alerts (Audit screen → Alerts → "Email the Owners"; slice 22). One page per tenant, kind and UTC day;
acknowledging on the Audit screen marks it handled and is audited.

| Kind | Severity | Means | First |
|------|----------|-------|-------|
| `invariant_violation` | critical | A doc 03 §7 invariant fails on stored rows: the run lists which (`INV-xx`) with up to five ids. **P1.** | Stop the sweep? No — it only reads. Open the ids on the Ledger, compare `balance_cache` with the allocations; do not "fix" by SQL until the cause is known. The run is `GET /organization/invariants`. |
| `audit_chain_break` | critical | An audit row was rewritten after the fact (SEC-53). The first broken id is in the alert. | Treat as a security incident: who has DDL rights (§1), when the trigger was disabled (`pg_stat_user_functions` / server log), revoke sessions (§3). |
| `ai_guard_rejection_spike` | warning | ≥ 50 % of ≥ 10 suggestions in 24 h were rejected by the schema check or the guard. | `logs ai`; check `ollama` is healthy and the model digest is the pinned one (`GET /ai/health`); consider `aiEnabled: false` for the tenant (§2) until it settles. |
| `send_volume_anomaly` | warning | ≥ 20 sends in 24 h and ≥ 3× the trailing seven-day mean. | Look at the tenant's Outbox: a bulk import followed by cadence is normal; a template loop is not. The tenant switch (§2) stops it in one call. |
| `backup_failed` | critical (script) | `infrastructure/backup.sh` exited non-zero. | Disk, credentials, `pg_dump` version. Re-run by hand. No backup tonight means yesterday's is the RPO. |
| `restore_drill_failed` | critical (script) | The drill restored but counts differed, or the restore itself failed. | The backup may be unusable. Take a fresh one, re-run the drill; if it fails again the dump format or a migration is the suspect. |

The two script alerts go to the webhook only (they run where there is no API); their stderr is in cron's mail.

## 7. One-time step when deploying slice 24

Registration now requires the address to be verified before the first sign-in. Accounts that registered earlier were
trusted without it; mark them once, as the superuser, so nobody is locked out:

```
UPDATE users SET email_verified_at = now() WHERE email_verified_at IS NULL AND created_at < '<the deploy timestamp>';
```

Accounts created afterwards verify through the mail; a lost link is recovered through "Forgot password", which
verifies the address on completion.

## 8. Symptoms → first look

| Symptom | Look at |
|---------|---------|
| Sign-in returns `mfa_unavailable` | `MFA_KEK_BASE64` missing or malformed in `.env` |
| Owners see `mfa_enrollment_required` | their 7-day grace expired; they enrol at `/security` — nothing to fix |
| Every send stays `Approved` | §2: the global switch, then the tenant switch, then `daily_send_cap` in `/organization/outbound` |
| `ai_unavailable` everywhere | `stack.sh --profile full ps` — `ollama-pull` must have exited 0 (the model is ~2.5 GB); `logs ai` |
| Briefing has metrics but no narrative | expected when the guard rejects a numeral (`rejected_by_guard` in the AI suggestions list); no action |
| `api` unhealthy after `migrate` | `logs migrate` — a migration failed; the API never starts against a half-migrated schema |
| Sign-in answers `email_unverified` | the address never opened its verification link; the user can use "Forgot password" (verifies on completion), or the operator runs §7 for pre-slice-24 accounts |
| Customer mail is not leaving | `GET /organization/outbound` (the switches), then `/organization/email-settings/test` for a tenant with its own SMTP; `smtp_host_not_allowed` means the host resolved to a private address (SEC-66) |
| Browser gets 401 on everything after sign-in | the site is served over plain HTTP on a non-localhost host — the `Secure` cookie is dropped; use TLS |
