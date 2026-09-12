# Slice 31 — Data lifecycle I: customer erasure (SEC-93) and encrypted, audited backups (SEC-94)

Status: **Implemented and tested.**

Source: doc 08 §8 "Privacy and data lifecycle" — SEC-93 ("a customer contact's personal data can be anonymized while
retaining financial records; the audit trail records the erasure") and SEC-94 ("backups are encrypted at rest,
tested by an actual restore drill, and restores are audited"). Neither was scheduled by doc 10; a pilot with real
customer data needs both. SEC-91 (retention purge) and SEC-92 (tenant export and deletion) follow in slices 32–33.

## 1. Scope

| # | Capability | Test |
|---|-----------|------|
| S1 | **`POST /customers/{id}/contacts/{cid}/erase`** (`customers.write`, re-authentication): the contact's name becomes "Erased contact"; role, email, phone and the bounce mark go; `erased_at` / `erased_by` are set (migration 0021); every outbound message to the contact or its address loses `to_address`, every inbound message from the address loses `from_address`. The row, the messages, their frozen bodies (SEC-57), the invoices, payments and cases are untouched. The audit event `customer.contact_erased` carries the contact id and the counts, never the person. Later edits and a second erasure answer `422 contact_erased`. | `ContactErasureTests` |
| S2 | **Tenant scoping**: another tenant's erase call is a clean 404 and changes nothing (SEC-13). | `Erase_IsTenantScoped` |
| S3 | **UI**: an "Erase personal data" action on the contact row opens the re-authentication dialog (the same one as transfer of ownership and SMTP settings); the erased row shows an "Erased" badge and offers no second erasure; the rule message exists in both languages. | `contacts.test.tsx` |
| S4 | **Backups encrypted at rest**: `backup.sh` pipes `pg_dump -Fc` through `openssl enc -aes-256-cbc -pbkdf2 -iter 600000` under `BACKUP_PASSPHRASE` (`.env`, runbook §1; every CI `.env` sets one), writes `*.dump.enc` with mode 600, and refuses to run without the passphrase. `restore-drill.sh` decrypts to a private temporary file for the length of the drill and removes it. | the `ops` CI job (T-150) now drills an encrypted backup; run locally: RPO 0 s, RTO 2 s, 40 tables, 41 policies |
| S5 | **Restores are audited**: `FinanceAi.Migrator record-restore --dump <file> --reason <why> [--by <who>]` appends `instance.restored` (file name, SHA-256, reason, operator, time; actor `system`) to **every tenant's audit chain**, hashed and locked exactly as `AuditWriter` does, so Owners see it on their Audit screen and SEC-53 still verifies. Runbook §5 makes it step 5 of a real restore. | `RestoreRecordTests` |

## 2. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Anonymize in place; never delete the row | Messages reference the contact by composite FK; the financial and communication record must keep its shape (SEC-93's own words). DELETE remains for a contact nobody ever wrote to. |
| D-2 | Clear the address on messages by contact *and* by literal address | An inbound reply matched by address alone has no `contact_id`; the address is the personal datum, wherever it sits. |
| D-3 | Frozen message bodies are left as they are | They are the SEC-57 record and fall under message retention (3 years, PRD-27 — slice 33's purge); a salutation with the person's name is noted, not scrubbed, because rewriting a frozen body would break what "frozen" means. |
| D-4 | Re-authentication, not just the permission | Irreversible, like write-off approval and transfer of ownership (SEC-09). |
| D-5 | `openssl enc`, not a new tool | Present on every host and runner already; AES-256-CBC with PBKDF2 at 600k iterations. A passphrase, not a key file, because the runbook already manages secrets as `.env` values. |
| D-6 | The restore record goes on every tenant's chain, as a system event | A restore touches every tenant; each Owner's audit trail should say so in its own chain, verifiable by the same SEC-53 walk. A platform-only log would be invisible to the people whose data moved. |
| D-7 | The drill records nothing | It restores into a scratch database and drops it; recording it would teach Owners to ignore the event. |

## 3. Not in this slice

SEC-91 (retention purge with dry-run and an audit record) and SEC-92 (tenant export; two-step, 30-day-delayed
deletion) — slices 32 and 33. SEC-30 (break-glass support access) needs a `PlatformSupport` role that v1 does not
have; with no such user possible, there is nothing to elevate.
