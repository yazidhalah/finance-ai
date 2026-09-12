# Slice 25 — Password hygiene (SEC-01) and bounced contacts

Status: **Implemented and tested.**

Source: SEC-01 ("Password minimum 12 characters, **checked against a breached-password list**") — flagged not bundled in
the slice 13 review · the slice 24 review's follow-up ("bounces do not yet mark the contact") · doc 05 send guards.

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | **Breached-password check**, offline: the ≥ 12-character subset of SecLists' 1M most-used passwords (46,296 entries, MIT), gzipped into the Infrastructure assembly and loaded once. Exact and case-insensitive match after trimming. Applied wherever a password is set — registration, invitation acceptance, password reset — as the validation code `breached` on the `password` field. Nothing about the candidate leaves the process. |
| S2 | **Bounced contacts**: the MTA webhook's bounce sets `customer_contacts.bounced_at` / `bounce_reason` (migration 0019); the send guard answers `422 contact_email_bounced` for that contact until its email is edited, which clears the mark; the contact list shows a badge with the reason. |

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | The list is bundled with > 40k entries; known leaked passwords (any case, trimmed) are refused; ordinary passphrases, Arabic passphrases and empty values pass | `BreachedPasswordsTests` |
| AC-02 | A leaked password is `400` with field `password`, code `breached` at registration; refused at reset and invitation acceptance before the token is judged | `BreachedPassword_IsRefusedEverywhereAPasswordIsSet` |
| AC-03 | After a bounce the contact carries the reason, the next send to it is `422 contact_email_bounced`, and editing the address clears the mark | `Webhook_AppliesDeliveredAndBounced_OnlyWhenSigned` (extended) |
| AC-04 | The contact row shows the badge with the reason as its title | `contacts.test.tsx` |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | A bundled list, not a k-anonymity API | "No paid APIs" is satisfied either way, but SEC-66 forbids outbound requests derived from user input, and a password hash prefix is exactly that. The list costs 344 KB in the assembly. |
| D-2 | Only entries of 12+ characters | Anything shorter is already refused by the length rule; keeping them would be dead weight. |
| D-3 | The bounce marks the contact, not the customer | The customer may have other addresses; the guard is per recipient, and the fix is editing that recipient. |
