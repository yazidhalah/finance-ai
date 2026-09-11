# Slice 17 — Key rotation: the MFA KEK and the JWT signing key — acceptance criteria and test plan

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: SEC-103 ("how to rotate keys") · SEC-02 (TOTP secrets encrypted at rest under a KEK held outside the
database) · SEC-03 (short-lived RS256 access tokens) · SEC-67 (secrets by environment) · SEC-09 (the re-authentication
proof) · slice 13 review and slice 14 runbook §4, which both flag: *"`MFA_KEK_BASE64` cannot be rotated in place"*
and *"there is no dual-key grace window for the signing key"*.

The one rule this slice exists to keep: **either key can be changed without locking anyone out, and after the
change nothing is protected by the old key any more.**

---

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Versioned MFA envelopes.** `AesGcmSecretBox` gains a key id (`kid` = first 8 bytes of SHA-256 over the key) and writes `FKEK1 · kid · nonce · tag · ciphertext`. `Open` picks the key by `kid` from the current key and the optional `MFA_KEK_BASE64_PREVIOUS`; an envelope without the header (slice 13 layout) is opened with the current key, then the previous. `NeedsReseal` says whether an envelope is under anything but the current key. |
| S2 | **Rotate on use.** A successful second-factor check whose envelope needs resealing rewrites it under the current key in the same transaction (`PlatformIdentityStore.VerifySecondFactorAsync`); enrolment always seals with the current key. |
| S3 | **Rotate in bulk.** `FinanceAi.Migrator rotate-mfa-kek` re-seals every `users.mfa_secret_enc` and `mfa_pending_secret_enc` under the current key (platform scope, the migrator role), reports counts, and refuses to run when `MFA_KEK_BASE64_PREVIOUS` is unset while a row needs it — so the operator knows before removing the old key. |
| S4 | **JWT dual-key window.** `JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS` (optional): tokens are always signed with the current key; validation accepts the previous key's signatures **only for tokens issued before this process started** (their `iat` ≤ process start + clock skew) — the old process is the only thing that ever signed with it, so this bounds the window to one token lifetime (15 min; 5 min for a re-auth proof) after the restart with no operator action, and a leaked old key cannot mint new tokens. `kid` on every token names the key. |
| S5 | **Runbook §4** rewritten as the two procedures; `.env.example` gains the two `_PREVIOUS` variables. |

**Deferred:** rotating the least-privilege database role passwords by tooling (runbook §4 already describes the SQL) ·
automatic scheduled rotation (an operator decision, not a job) · a key id on recovery codes (they are hashes, not
ciphertexts — nothing to rotate).

---

## 2. Rules, stated explicitly

| Question | Answer | Why |
|----------|--------|-----|
| **Can the old KEK be removed safely?** | After `rotate-mfa-kek` reports zero rows needing the previous key — and it refuses to run without the previous key while any row still needs it. | An operator removing the old key early would be back to the slice 13 lockout. |
| **What if a `kid` matches neither key?** | `Open` throws; the login answers `mfa_unavailable` for that user (never a bypass, never a clear-text fallback). | SEC-02. |
| **Is the previous JWT key a standing backdoor?** | No: it validates only tokens whose `iat` predates this process, so nothing signed with it after the rotation is ever accepted, wherever it was signed. | SEC-03; the window closes itself. |
| **Does rotation revoke sessions?** | No. Refresh tokens are opaque database rows (not signed); users get a new access token from the new key on their next refresh. Revocation is a separate runbook action (§3). | Rotation and revocation are different incidents. |
| **Timing** | Both the header parse and the key choice are branch-cheap; the AES-GCM open is constant-time in the tag check. A wrong-key open fails on the tag, exactly as a corrupt envelope does. | Slice self-review question 3. |

---

## 3. Acceptance criteria

| ID | Criterion | Spec | Test |
|----|-----------|------|------|
| AC-01 | An envelope sealed under key A opens under a box whose current key is B and previous key is A; a slice-13-layout envelope (no header) sealed under A opens the same way; sealing always produces the current key's `kid`; an envelope under a key the box does not hold fails to open; `NeedsReseal` is true for the previous-key and headerless envelopes and false after resealing | SEC-02 | `SecretBoxRotationTests` (unit) |
| AC-02 | A user enrolled under KEK A signs in with a TOTP after the API restarts with KEK B (+ previous A); the row is now under B (header `kid` = B's) and a second sign-in with only B configured still works | SEC-02, SEC-103 | `MfaKek_RotatesOnUse` (integration, two boxes swapped on the running host) |
| AC-03 | `rotate-mfa-kek` re-seals every enrolled and pending secret, reports the count, is idempotent, and refuses when a row needs the missing previous key | SEC-103 | `MfaKek_BulkRotation` (integration, through the shared `KekRotation` routine the migrator calls) |
| AC-04 | A token signed by key A is accepted by a host configured with current B + previous A when its `iat` predates the host's start; the same token is rejected when `iat` is after the start; a token signed by A is rejected when A is not the previous key; new tokens carry B's `kid`; the re-auth proof behaves the same | SEC-03, SEC-09 | `SigningKeyRotationTests` (unit, with a pinned clock) + `Jwt_PreviousKey_IsAcceptedOnlyForOlderTokens` (integration) |
| AC-05 | Runbook §4 describes both procedures and the CI stack starts with the new variables absent | SEC-103 | docs; existing `stack` job |

---

## 4. Endpoints, with declared permission

None added. Route pin stays **147**. `rotate-mfa-kek` is a migrator command, not an endpoint.

---

## 5. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | A header on the envelope, not a column | The two `bytea` columns keep their shape; slice-13 rows stay readable; the header (`FKEK1`) is unambiguous against a random 12-byte nonce in practice (2⁻⁴⁰) and by length (a headerless TOTP envelope is exactly 48 bytes). |
| D-2 | Rotate on use **and** in bulk | On use costs nothing and covers active users; the bulk command is what lets the operator retire the old key on a schedule. |
| D-3 | The previous JWT key is bound by process start, not by a clock the operator sets | No operator step to forget; the window is exactly the tokens that could exist. |
| D-4 | No new endpoint for rotation | Rotation is an operator action on secrets the API must never expose; the migrator already runs with the platform-level credentials and is on every deployment. |
