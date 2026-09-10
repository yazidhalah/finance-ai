# ADR-0005 — Bilingual content: authored per language, RTL as a first-class layout

Status: **Proposed** · Date: 2026-09-10

## Context

The product is Arabic-first for Jordanian SMEs (PRD-03), and the same screens must serve
an English-preferring accountant. Three sub-decisions matter: where translation lives,
how customer-facing content is produced, and how RTL is implemented.

The cheap path — English UI with runtime machine translation, and RTL as a CSS mirror —
produces an Arabic product that reads like a translation and renders invoice numbers and
amounts in the wrong visual order. In a financial product, the second is not cosmetic:
a bidi-mangled invoice number is a wrong invoice number.

## Decision

1. **Translation lives in the frontend.** The API returns `messageKey` codes, never
   display prose (API-04, UI-12). One place to translate; no backend/frontend drift.
2. **Missing keys fail the build** (UI-11). Arabic parity is a release blocker, not a
   backlog item.
3. **Customer-facing templates are authored per language**, one row per language, Arabic
   written in Arabic (UI-13). Message language follows the **customer's** preference, not
   the sending user's UI language (UI-14).
4. **RTL is a real layout**: `dir="rtl"`, logical CSS properties only, with a lint rule
   banning physical properties (UI-20).
5. **Bidi isolation is mandatory** for every number, amount, date, and Latin identifier
   rendered inside Arabic prose (UI-23), with dedicated tests and visual regressions.
6. **Western digits by default in both locales**, with an Arabic-Indic option (UI-17) —
   matching how Jordanian business documents are actually written.
7. **ICU MessageFormat** for plurals; Arabic's six categories are honoured (UI-15).
8. **The E2E suite runs twice**, once per locale (UI-70, T-120).

## Consequences

**Positive:** the Arabic experience is native rather than mirrored; a whole class of
financial-display bugs is prevented structurally; translation gaps surface at build time.

**Negative:** every user-facing string costs two authorings; templates are maintained in
two languages and can drift in meaning (mitigated by the side-by-side editor and the
placeholder-parity warning); the E2E suite takes twice as long.

**Revisit when:** a third language is required — the architecture supports it, but the
template-authoring cost grows linearly.
