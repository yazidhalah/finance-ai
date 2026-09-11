# Slice 20 — Supply-chain integrity: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **medium** (a new database trigger on an AI table; CI gates).

1. **Endpoints.** None. Route pin 147; snapshot unchanged.
2. **Tables.** None new. `ai_suggestions` gains a `BEFORE INSERT OR UPDATE` trigger (`ai_suggestions_subject_exists`, ERRCODE `foreign_key_violation`). RLS on the table is unchanged; the trigger function is not `SECURITY DEFINER`, so it sees only what the writing role sees. Targeted test: four smuggled rows in `AiIsolationTests`.
3. **Pre-auth / cross-tenant paths.** None added. The trigger is the opposite: it closes a cross-tenant reference path.
4. **Money.** None.
5. **AI.** The trigger makes every suggestion's subject a real row of the same tenant — the auditability chain (`CLAUDE.md` → Financial Safety: input reference) can no longer dangle. Nothing about validation or approval changed.
6. **Dependencies.** None added. `lightningcss` (transitive, dev, MPL-2.0) is now recorded and Flagged; `xunit.abstractions` recorded as Apache-2.0 via `KNOWN`. `check-notices.py` (direct and transitive) and `--self-test` pass.

## Verified locally

- `--transitive`: 252 installed / 317 locked, no copyleft, MPL only where Flagged; inventory written.
- `AiIsolationTests` with the trigger: the four smuggled subjects refused; the whole security, integration and unit suites green.
- `load-env.sh`: an exported `ALERT_WEBHOOK_URL` survives the empty `.env` line; `backup.sh` and `restore-drill.sh` pass.

## Flagged

- **Optional platform binaries** (65 of the 317 lock entries) are not installed here, so their licences are not read; they are the same publishers' packages for other OSes and are excluded from the served product. A run on each platform would close this.
- **The trigger does not fire on `DELETE` of the subject** — an inbound message or case is never deleted in this product (no DELETE grants), so a dangling subject cannot arise that way.
- **CI-side alerts need the repository secret** `ALERT_WEBHOOK_URL`; without it the scripts print to stderr only.
