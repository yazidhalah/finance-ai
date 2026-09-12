# Slice 25 — Password hygiene and bounced contacts: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **low**.

1. **Endpoints.** None added; route pin 160. `PATCH /customers/{id}/contacts/{contactId}` now clears a bounce when the email changes; the send path gains one guard code. The OpenAPI snapshot changes by the two new `ContactResponse` properties.
2. **Tables.** None new; `customer_contacts` gains `bounced_at` and `bounce_reason` (migration 0019). RLS unchanged.
3. **Pre-auth paths.** The breached check runs inside the anonymous registration/reset/acceptance validations, before any token or account lookup — a constant-time-irrelevant set lookup that answers the same for every caller (it never depends on whether the account exists, SEC-07). The webhook's contact marking runs inside the tagged tenant's scope, as the message update does.
4. **Money.** None.
5. **AI.** None.
6. **Dependencies.** One data file: SecLists (MIT), recorded under "Data files" in THIRD-PARTY-NOTICES with its provenance and the filter applied. `check-notices.py` passes (it scans packages and images, not data files — the row is the record).

## Flagged

- **The list is a snapshot** (SecLists as of 2026-09-12). Refreshing it is a one-line `awk | gzip` documented in the notices row; no automation.
- **Case-insensitive matching** refuses a few more passwords than a strict list would (e.g. a capitalised variant of a leaked one). That is the intent.
- **A bounce on a non-contact address** (a message whose `contact_id` is null) marks nothing; only the message status changes.
