# Slice 19 — Audit values on the viewer, and the coverage baseline: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **low**.

1. **Endpoints.** None added; `GET /audit` (`audit.read`, through the middleware) returns three more properties. Route pin 147; the anonymous set unchanged; the snapshot diff is the `AuditEventDto` schema only.
2. **Tables.** None.
3. **Pre-auth / cross-tenant paths.** None. The audit list is tenant-filtered as before (`Audit_IsReadableOnlyWithinOwnTenant`); the values it now returns are the tenant's own rows' values.
4. **Money.** No calculation. `changes` carries money as the strings the writers stored (DM-28); the viewer prints them verbatim.
5. **AI.** `aiSuggestionId` is now visible on the row it influenced — the auditability requirement of `CLAUDE.md` (Financial Safety) made visible; nothing about approval changed.
6. **Dependencies.** None. `coverage-report.py` is stdlib; coverlet was already a test dependency.

## Flagged

- **Coverage floor** Domain 95 / Infrastructure 90 / Api 85 is enforced (today 97.8 / 94.4 / 93.4 — 2.8 / 4.4 / 8.4 points of headroom). A slice that ships code without tests now fails the `api` job.
- **`changes` can be large** for an import commit (thousands of rows are separate events, but a template version can carry a body); the viewer renders it as text without truncation.
