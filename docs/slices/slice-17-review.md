# Slice 17 — Key rotation: self-review

Per `CLAUDE.md` → Slice Self-Review. Every "yes" names the test or file that makes it so.
Risk tier: **high** — this slice touches the two keys that stand between an attacker and every session and every
second factor.

## 1. Every new endpoint: authenticated and routed through `TenantScopeMiddleware`, or deliberately anonymous?

**No new endpoints.** Route pin stays 147; the OpenAPI snapshot is unchanged. `rotate-mfa-kek` is a migrator
command (also reachable as the container's entrypoint argument), run with the migrator's credentials by an operator.

## 2. Every new table?

None. No migration: the versioned envelope lives inside the existing `bytea` columns (D-1).

## 3. Code paths before a tenant / full authentication, or across tenants by design?

Three, all examined for timing and disclosure:

| Path | Comparable time and disclosure? |
|------|--------------------------------|
| `AesGcmSecretBox.Open` | Header parse and key choice are a few byte comparisons; the AES-GCM tag check is constant-time. A wrong key and a corrupt envelope fail identically (`CryptographicException`). The headerless path may try two keys — only for rows written before this slice, and only during a rotation; the login answers the same `invalid_credentials` for a failed open as for a wrong code (`MfaKek_RotatesOnUse`, the row put back under the discarded key). |
| `RsaAccessTokenIssuer.AcceptsSignature` | Runs after signature validation on a token that already verified against one of the two keys; a comparison of two short strings and a timestamp. A rejected previous-key token gets the same 401 as any other invalid token. |
| `KekRotation.RunAsync` | Platform scope by design (`app.platform_scope = on`) over every user with a secret — this is the one code path that reads every tenant's users' envelopes, and it does so under the migrator role, in one transaction, and logs counts only. It refuses (before any write) when a row cannot be opened, rather than skipping it silently. |

**No branch returns early before a cryptographic comparison** except the explicit "no key configured" and "key id
not held" cases, both of which fail closed.

## 4. Money-related fields or calculations?

None.

## 5. AI-touching code?

None.

## 6. New dependencies?

None. AES-GCM, SHA-256 and RSA are the .NET base class library; THIRD-PARTY-NOTICES is unchanged and
`check-notices.py` passes.

## What changed for existing data

- Envelopes written by slice 13 (no header) still open, and are re-sealed with a header on the next successful
  sign-in or by `rotate-mfa-kek` — run once against the local stack: "6 re-sealed, 0 already under the current key".
- Tokens: nothing changes until an operator sets `JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS`. With it set, the test host
  proves a retired-key token issued before the process is honoured and one issued after is not
  (`Jwt_PreviousKey_IsAcceptedOnlyForOlderTokens`, plus the unit test with a pinned clock).

## Flagged, in one place

- **The previous JWT key is bound to "issued before this process started" using the token's own `iat`.** A token
  forged with the old private key *and a back-dated `iat`* would pass while it is unexpired — i.e. for at most 15
  minutes after the restart (5 for a proof), which is exactly the window an un-rotated old token has anyway. The
  rotation removes the standing risk, not the window; the review accepted that the window equals one token life.
- **`KekRotation` holds every envelope's plaintext in memory briefly** (open → seal). It runs in the operator's
  process, not the API's; secrets are not logged. A streaming approach would not change the exposure.
- **Database role passwords still rotate by hand** (runbook §4, unchanged).
- **The headerless-envelope path tries two keys** — a few extra microseconds only during a rotation and only for
  pre-slice-17 rows; noted for completeness, not a timing oracle in practice.
