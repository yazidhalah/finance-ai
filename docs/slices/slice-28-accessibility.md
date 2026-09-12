# Slice 28 — Accessibility (T-133, PRD-25)

Status: **Implemented and tested** (the `e2e` CI job runs it in both locales).

Source: doc 09 §6 T-133 ("axe scan on every screen in both locales; keyboard-only traversal of the queue and the
allocation screen; screen-reader label assertions on money fields") · PRD-25 (WCAG 2.2 AA: keyboard-operable queue,
visible focus, 4.5:1 contrast, screen-reader labels in both languages, no colour-only status) · slice 5 deferred
"axe scans (T-133's tooling)".

## 1. Scope

| # | Capability | Test |
|---|-----------|------|
| S1 | **axe on every screen, both locales**: the three anonymous screens and the twenty-three signed-in screens (every navigation entry plus the customer, invoice, case and import-batch details and the two "new" forms), seeded so each shows data; tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa`; **zero violations** of any impact | `T-133 axe · anonymous …`, `T-133 axe · every signed-in screen` |
| S2 | **Keyboard-only queue**: `j` moves the cursor (row `aria-selected`), Enter opens the case; the case link is reachable by Tab alone, shows a visible focus ring, and Enter activates it | `T-133 keyboard · the queue …` |
| S3 | **Keyboard-only allocation**: a payment is opened from the list by Tab + Enter, the amount is moved between invoices by Tab + typing, the allocation confirmed by Tab + Enter; every money input has an accessible name naming its invoice; the allocation screen passes axe | `T-133 keyboard · a payment …` |
| S4 | **Money labels**: every rendered amount ends with its ISO currency code, so a screen reader announces "1,160.000 JOD" rather than a symbol | `T-133 labels · money values …` |

## 2. What the scan found, and the fixes

| Screen | Finding (axe rule, impact) | Fix |
|--------|----------------------------|-----|
| case detail, aging, queue, ledger, audit, inbox | `color-contrast` (serious): `text-slate-400` on white is 2.9:1 | `text-slate-500` (4.6:1) on text content; disabled controls keep their grey (exempt) |
| inbox | `aria-required-children` (critical): `role="tablist"` with buttons that are not tabs | the tab buttons carry `role="tab"` and `aria-selected` |
| inbox | `color-contrast` on the confidence figure inside the classification chip | the figure inherits the chip's own text colour |
| import wizard | `label` (critical): the file input had no label; step chips `slate-500` on `slate-100` | a visible `<label>` (`import.fileLabel`, both languages); chips `text-slate-600` |
| audit | `aria-conditional-attr` (serious): `aria-expanded` on a `<tr>` | the expand toggle is a real `<button aria-expanded>` (keyboard-operable; the row click stays for the mouse) |
| payments | (keyboard) the payment row opened only on click | the date cell is a button (`open-payment`) |
| allocation | (labels) amount inputs in a table had no accessible name | `aria-label` = "Amount to allocate to invoice ‹number›" in both languages |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Zero violations at every impact, not "no critical" | A threshold invites drift; every finding was fixable in an hour. New findings fail the `e2e` job with the rule, the screen and the first three selectors. |
| D-2 | `@axe-core/playwright` (MPL-2.0, test-only), recorded and Flagged | The de-facto WCAG engine; file-scoped weak copyleft on a package that never ships, the same footing as `lightningcss`. |
| D-3 | Keyboard traversal by pressing Tab up to sixty times until the target is focused | The exact tab count is not a contract; reachability and activation are. |
| D-4 | Money labels assert the currency code in the text rather than an `aria-label` | The visible text already carries it (UI-23); an `aria-label` would replace the locale-formatted digits a screen reader reads correctly. |

## 4. Not covered

Screen-reader *behaviour* (announcement order, live regions) is not automatable here; the checks are structural
(names, roles, states). Colour-only status encoding: axe cannot judge it; every status badge in the product carries a
text label (slices 5–9), which the Arabic run's exact-text assertions exercise.
