# 08 — Security, Audit, and Tenant Isolation

Status: DRAFT. Normative. Every `SEC-xx` rule maps to at least one test in doc 09 §4.

Threat framing: this system holds the receivables ledger of competing SMEs in one
database and sends messages to their customers in their name. The two catastrophic
failures are **cross-tenant data exposure** and **a wrong or hostile message sent to a
customer**. Everything below is ordered by that priority.

---

## 1. Identity and authentication

| ID | Requirement |
|----|-------------|
| SEC-01 | Passwords hashed with **Argon2id** (m=64MB, t=3, p=1 minimum), never MD5/SHA/bcrypt-with-low-cost. Password minimum 12 characters, checked against a breached-password list; no composition rules, no forced rotation. |
| SEC-02 | **TOTP MFA required for `Owner` and `Admin`**, optional for others, enforced server-side. Recovery codes issued once, hashed at rest. |
| SEC-03 | Access tokens: JWT, **15-minute** lifetime, signed with EdDSA/RS256, keys rotated quarterly with overlap. Claims: `sub`, `tid` (tenant), `role`, `perms` (or a permission-set version), `jti`, `iat`, `exp`. |
| SEC-04 | Refresh tokens: opaque, 14 days, **rotating with reuse detection**; a replayed refresh token revokes the entire family and alerts. Stored `httpOnly; Secure; SameSite=Strict`. |
| SEC-05 | Access tokens are held **in memory** in the SPA, never in `localStorage`. |
| SEC-06 | Login: constant-time comparison, uniform error message, exponential backoff, account lockout after 10 failures with a bounded window, and per-IP rate limiting (API-13). |
| SEC-07 | Enumeration resistance: registration, password reset, and invitation endpoints never reveal whether an email exists. |
| SEC-08 | Sessions are revocable per-device from the user's profile; a role change or deactivation invalidates existing access tokens within 60 seconds (short TTL + a revocation check on sensitive operations). |
| SEC-09 | Re-authentication required for: transferring ownership, changing email settings, viewing/rotating secrets, approving a write-off, and break-glass support access. |

> **Amended in slice 13 (SEC-02a / SEC-09a).** As built: TOTP (RFC 6238) with eight hashed single-use recovery codes;
> required for Owner and Admin with a seven-day grace from the membership's creation, then enforced on every request by
> the middleware from the membership, not the token. Re-authentication is a five-minute signed proof (`X-Reauth`)
> required by transfer of ownership and write-off approval; email-settings, secrets and break-glass remain future
> surfaces. The breached-password list of SEC-01 is not bundled (flagged in the slice 13 review).

---

## 2. Authorization

| ID | Requirement |
|----|-------------|
| SEC-10 | Authorization is **deny-by-default**. Every endpoint declares a required permission; an endpoint with no declaration fails a startup assertion — the application refuses to boot (this converts a forgotten attribute from a vulnerability into a crash). |
| SEC-11 | **Tenant scope is applied before permission.** The pipeline is: authenticate → resolve tenant from `tid` → open a DB transaction with `app.tenant_id` set → check permission → execute. A handler never receives an unscoped repository. |
| SEC-12 | Authorization checks a **permission**, never a role string. Role→permission mapping lives in one table and is unit-tested against doc 01 §5.1 as the source of truth. |
| SEC-13 | Cross-tenant access returns **404**, not 403 (API-03). Existence is itself information. |
| SEC-14 | Every protected endpoint has: a 401 test (no token), a 403 test (authenticated, wrong permission), and a **404 cross-tenant test** (valid token for tenant A, real id from tenant B). This is a checklist item in every slice's definition of done (`CLAUDE.md` step 5). |
| SEC-15 | **IDOR is structurally prevented**, not merely tested: every lookup is `WHERE tenant_id = @tid AND id = @id`, RLS repeats it, and composite FKs make a cross-tenant reference unrepresentable (doc 04 §3.3). |
| SEC-16 | Object-level checks additionally apply where a permission is not sufficient: `writeoff.approve` requires a different user than the proposer (PRD-11); `collector_sees_only_assigned` filters server-side (PRD-14). |
| SEC-17 | Mass-assignment protection: request DTOs are explicit; unknown fields are rejected (`400 unexpected_field`), and `tenantId`, `status`, `balance`, `approvedBy` are never bindable from a request body. |

---

## 3. Tenant isolation (the defining control)

Three layers, doc 04 §1: application filter, PostgreSQL RLS (forced), composite FKs.

| ID | Requirement |
|----|-------------|
| SEC-20 | The tenant id used for `app.tenant_id` comes **only** from the validated token claim. There is no code path that reads a tenant id from a header, query string, body, cookie, or referer. A grep-based CI check bans `Request.Headers["X-Tenant"]`-style patterns. |
| SEC-21 | The application database role has **no `BYPASSRLS`** and is **not** the table owner (DM-03). Migrations run as a separate role in a separate connection string held only by the migration job. |
| SEC-22 | Any query executed outside the tenant transaction scope (background jobs, reports, exports) must explicitly declare the tenant and go through the same `set_config` path. Jobs iterate tenants one at a time; there is no "for all tenants" query in business code. |
| SEC-23 | **Connection-pool hygiene:** `app.tenant_id` is set with `set_config(..., is_local => true)` inside the transaction so it cannot leak to the next borrower of a pooled connection. An integration test asserts that a connection returned to the pool and re-borrowed has no tenant setting (this is the classic RLS-plus-pooling bug). |
| SEC-24 | Caches (in-process, Redis if added later) use **tenant-prefixed keys**; a cache key without a tenant prefix fails a static check. |
| SEC-25 | Full-text/trigram search and any future vector search filter by tenant **inside** the query (DM-27). |
| SEC-26 | File storage paths are `tenant/{tenantId}/…` and downloads are served through an authorizing endpoint, never a public URL or a guessable path. |
| SEC-27 | Exports and PDFs are generated inside the tenant scope and named without another tenant's identifiers. |
| SEC-28 | Error messages, validation messages, and `traceId`s never contain another tenant's data. |
| SEC-29 | **Isolation tests run on every build** (doc 09 §4.2): schema assertions (every business table has `tenant_id`, RLS enabled and forced, a policy, composite FKs) plus a two-tenant behavioural suite that attempts cross-tenant reads and writes on every endpoint. |
| SEC-30 | **Break-glass support access:** `PlatformSupport` has no tenant data by default. Elevation requires a documented reason, is time-boxed (≤ 4 hours), notifies the tenant Owner, is recorded in a platform audit log, and is visible to the tenant in their own audit view. There is no silent support impersonation. |

---

## 4. Untrusted input

Uploaded documents, imported files, and customer replies are hostile by default.

| ID | Requirement |
|----|-------------|
| SEC-40 | **Document and message text is data, never instruction** (`CLAUDE.md`). It is stored as data, framed as data for the model (AI-20..AI-27), and rendered as quoted content in the UI (SEC-42). |
| SEC-41 | **Secrets and personal financial data never reach logs or prompts** (`CLAUDE.md`). A structured-logging redaction layer scrubs known-sensitive field names and pattern-matches IBANs, card numbers, and national IDs. A CI test feeds a payload full of secrets through the logging pipeline and asserts none appear in output. |
| SEC-42 | Customer text is rendered escaped, in a visually distinct quoted block, never as HTML and never inside a control that looks like a system message. Email bodies are sanitized to plain text for classification (HTML is stripped before the model sees it, removing hidden-text injection via CSS). |
| SEC-43 | Inference logs (rendered prompts) are access-controlled, retained 90 days, and excluded from ordinary application logs (AI-32, PRD-27). |
| SEC-44 | **File upload:** allowlist by extension **and** sniffed content type (CSV, XLSX, PDF, PNG/JPG); 10 MB cap; filenames sanitized and never echoed into a path; files stored outside the web root with generated names. |
| SEC-45 | **CSV injection (formula injection)** on export: any exported cell beginning with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'`. A tenant exporting to Excel must not execute a payload a customer name carried in. |
| SEC-46 | **XLSX parsing** uses a maintained library with external-entity and formula evaluation disabled; zip-bomb and sheet-count limits applied. |
| SEC-47 | PDF/image processing (deferred slice, A-23) runs in a **separate, network-isolated container** with CPU/memory limits — OCR and PDF parsers are a large native attack surface. |
| SEC-48 | Import rows are stored raw (`import_rows.raw`) for audit but are never interpolated into SQL, shell, or prompts. |

---

## 5. Audit

| ID | Requirement |
|----|-------------|
| SEC-50 | Every state transition (doc 02), every money mutation (doc 03), every message send, every permission/role change, every login and failed login, every export, and every AI-influenced action produces an `audit_events` row (doc 04 §5.8). |
| SEC-51 | The log is **append-only**: `UPDATE`/`DELETE` revoked at the database level plus a trigger that raises (DM-28). |
| SEC-52 | Each row records: tenant, actor (user / `system` / `ai_assisted` / `support`), actor IP, event type, entity, from/to state, reason code, note, field-level changes with money as **strings**, `ai_suggestion_id`, and `request_id`. |
| SEC-53 | **Tamper evidence:** each row stores `hash = H(prev_hash ‖ canonical_row)`, forming a per-tenant chain. A daily job verifies the chain and alerts on a break. This is not a blockchain and makes no cryptographic custody claim — it detects post-hoc editing by someone with database access. |
| SEC-54 | Audit rows are readable by `audit.read` holders **within their tenant only**, and the audit view is one click from any invoice, case, or payment (UI §6.11). |
| SEC-55 | Retention 7 years (PRD-27). Audit rows are excluded from soft-delete purges and survive customer deletion (the customer row is anonymized; the audit trail retains the reference). |
| SEC-56 | **AI provenance is part of the audit contract** (`CLAUDE.md`): model, model digest, prompt version, input reference, output, confidence, and whether a human approved it — all reachable from the affected entity (doc 04 §5.7). |
| SEC-57 | Every message sent to a customer is auditable with its **frozen body** (DM-25): who approved it, who sent it, what exactly it said, in which language. |

---

## 6. Application security

| ID | Requirement |
|----|-------------|
| SEC-60 | TLS 1.2+ everywhere (1.3 preferred); HSTS with a long max-age; no mixed content. |
| SEC-61 | Security headers: `Content-Security-Policy` with no `unsafe-inline`/`unsafe-eval` (nonce-based), `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `X-Frame-Options: DENY` / `frame-ancestors 'none'`, `Permissions-Policy` minimal. |
| SEC-62 | CORS: explicit origin allowlist, credentials only for the known SPA origin, no wildcard. |
| SEC-63 | CSRF: refresh cookie is `SameSite=Strict`; state-changing endpoints require the bearer token (not cookie-only auth), so CSRF surface is minimal by design. |
| SEC-64 | SQL: parameterized queries only; EF Core or Dapper with parameters. String-concatenated SQL is a blocking review finding. |
| SEC-65 | Output encoding: React's default escaping; `dangerouslySetInnerHTML` is banned by lint rule with no exceptions in v1. |
| SEC-66 | SSRF: the backend makes no user-supplied outbound requests. SMTP/IMAP hosts come from tenant settings and are validated against private-range and metadata-endpoint blocklists before connecting. |
| SEC-67 | Secrets: never in source, never in the repo, never in logs. Injected via environment/secret files at deploy; `.env` is git-ignored; a pre-commit secret scanner runs in CI. Tenant SMTP passwords are encrypted at rest with a KEK held outside the database and are **write-only** through the API (doc 05, slice 8). |
| SEC-68 | Dependencies: pinned versions, lockfiles committed, automated vulnerability scanning (`dotnet list package --vulnerable`, `pip-audit`, `npm audit`) in CI; a high-severity advisory blocks the build. Every dependency's license recorded in `THIRD-PARTY-NOTICES.md` in the same commit (`CLAUDE.md` → Cost Rules). |
| SEC-69 | Containers: non-root user, read-only root filesystem where possible, no capabilities beyond default, resource limits, and **no port published for PostgreSQL or the AI service** — only the API and web are reachable. |
| SEC-70 | Rate limiting and abuse controls at the edge per user, per tenant, and per IP (API-13). |

---

## 7. Outbound messaging safety

The product speaks to third parties in the tenant's name. That is a security surface.

| ID | Requirement |
|----|-------------|
| SEC-80 | **Recipient binding:** a message can only be addressed to a contact belonging to the customer on the case, in the same tenant. Free-typing an arbitrary recipient is not supported in v1 — that would make the product a spam relay. |
| SEC-81 | **Content binding:** the rendered body may reference only invoices in the case's scope. A post-render guard asserts every invoice number in the body belongs to that customer in that tenant. A cross-customer leak in a dunning email is a P0 incident. |
| SEC-82 | No AI-authored numbers in outbound text (AI-60). |
| SEC-83 | Quiet hours, duplicate-send suppression, dispute block, escalation block, and hold block are enforced **server-side at send time**, not only in the UI (doc 05, slice 8). |
| SEC-84 | Email authentication: tenant-owned domain with SPF/DKIM/DMARC alignment documented in onboarding [A-05/Q-05]. We do not send on behalf of an unverified domain. |
| SEC-85 | **WhatsApp:** click-to-chat links only. No unofficial library, no browser automation, no session hijacking of WhatsApp Web — ever (`CLAUDE.md`, ADR-0004). The system records intent and the user's confirmation that they sent it. |
| SEC-86 | A per-tenant daily send cap and an anomaly alert (e.g. > 3× the trailing average) catches a runaway loop before it reaches hundreds of customers. |

---

## 8. Privacy and data lifecycle

| ID | Requirement |
|----|-------------|
| SEC-90 | Data minimization: we store what collections needs. No national IDs, no bank credentials of customers, no payment card data (the system is out of PCI scope by design and MUST stay that way). |
| SEC-91 | Retention per PRD-27; a scheduled purge job with a dry-run mode and an audit record of what it deleted. |
| SEC-92 | Tenant export: an Owner can export all tenant data (invoices, customers, payments, cases, messages, audit) in machine-readable form. Tenant deletion is a two-step, delayed (30-day), irreversible process with an export offered first. |
| SEC-93 | Customer erasure: a customer contact's personal data can be anonymized while retaining financial records (which must be kept for tax/audit reasons). The audit trail records the erasure. |
| SEC-94 | Backups are encrypted at rest, tested by an actual restore drill (PRD-23), and **restores are audited** — a restore is a privileged operation that can resurrect deleted data. |

---

## 9. Operations

| ID | Requirement |
|----|-------------|
| SEC-100 | Least-privilege database roles: `app` (DML, no DDL, no BYPASSRLS), `migrator` (DDL), `readonly_reporting` (SELECT, RLS-bound), `backup`. |
| SEC-101 | Logs are structured JSON with `request_id` correlation, shipped to a local store; PII-redacted (SEC-41); retained 90 days. |
| SEC-102 | Alerts (page-worthy): invariant violation (doc 03 §7), audit-chain break, cross-tenant assertion failure in production canaries, send-volume anomaly, AI guard rejection rate spike, failed backup, failed restore drill. |
| SEC-103 | A written incident-response runbook exists before pilot: who is called, how to disable outbound sending in one action (a tenant-level and a global kill switch — this must exist and be tested), how to revoke sessions, how to rotate keys. |
| SEC-104 | Threat model reviewed and updated at each slice that adds an external interface (import, email in/out, AI service). |
| SEC-105 | `security-review` runs on the changes in every slice before merge; findings triaged before the slice is called done. |

---

## 10. Explicitly accepted residual risks

Recorded so they are decisions, not oversights:

1. **A determined prompt injection can produce a wrong classification.** Mitigated by
   capability starvation (AI-23) and human review of every consequential class (AI-40).
   Accepted: the worst outcome is a human seeing a wrong suggestion.
2. **Shared-schema multi-tenancy concentrates blast radius.** Mitigated by three
   independent layers and continuous tests (SEC-29). Revisit at ~100 tenants or the
   first enterprise customer with a contractual isolation requirement (ADR-0001).
3. **Tenant SMTP credentials are held by us.** Mitigated by encryption with an external
   KEK, write-only API, and re-authentication to change (SEC-67, SEC-09).
4. **A self-hosted single node has a real RTO.** Accepted for pilot with a tested
   restore drill (PRD-23); revisit before the first customer who cannot lose a day.
