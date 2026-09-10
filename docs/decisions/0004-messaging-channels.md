# ADR-0004 — Email is owned in-product; WhatsApp is click-to-chat only

Status: **Proposed** · Date: 2026-09-10

## Context

WhatsApp is how Jordanian SMEs actually talk to their customers. The official WhatsApp
Business Platform costs money per conversation and requires business verification;
`CLAUDE.md` forbids paid APIs and states plainly: *WhatsApp automation must never use
unofficial libraries; use click-to-chat until the official Business Platform is paid
for.*

Unofficial libraries (web-session automation) are also a security and continuity
disaster: they hold a user's WhatsApp session, break on every client update, and risk
the tenant's number being banned — losing them a channel they depend on.

## Decision

- **Email is fully owned**: composed, approved, queued, sent, and tracked in-product via
  the tenant's own SMTP, with bounce handling and full audit of the frozen body.
- **WhatsApp is click-to-chat only**: the product renders the message text and produces
  a `https://wa.me/<e164>?text=…` link. The **user** sends it from their own WhatsApp.
  The system records the prepared message and the user's confirmation that they sent it,
  so the case timeline stays complete.
- **Inbound WhatsApp** is handled by pasting the customer's reply into the product,
  which then classifies it like any other message (A-14).
- No unofficial library, no browser automation, no session capture — ever. A dependency
  test greps the lockfiles for known unofficial WhatsApp packages and fails the build
  (slice 8 acceptance criterion 6).

## Consequences

**Positive:** zero cost; zero risk to the tenant's WhatsApp account; legally clean;
still delivers most of the value because the drafting and the record-keeping — not the
transport — are the hard parts.

**Negative:** WhatsApp sending is manual, so no automated cadence on the channel SMEs
use most; inbound WhatsApp requires copy-paste, which users will find tedious and which
depresses the volume of replies the classifier sees.

**Revisit when:** the business is ready to pay for the Business Platform, at which point
the `messages` model already accommodates it (channel is a column, guards are
channel-agnostic).
