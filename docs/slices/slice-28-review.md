# Slice 28 — Accessibility: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **none** (UI markup, styling and e2e tests; no API, table or money change).

1. **Endpoints.** None; route pin 160; OpenAPI snapshot unchanged.
2. **Tables.** None.
3. **Pre-auth paths.** None. The three anonymous screens are scanned as rendered; nothing is submitted.
4. **Money.** Presentation only — `MoneyText` is unchanged; the allocation inputs gained an `aria-label`, not a new parser.
5. **AI.** None.
6. **Dependencies.** `@axe-core/playwright` 4.10.2 + `axe-core` 4.10.3, MPL-2.0, dev/test only; recorded and Flagged in `THIRD-PARTY-NOTICES.md` in this commit; `check-notices.py` and `--transitive` pass.

## Flagged

- **The scan is what axe can see**: structure, names, roles, contrast of rendered text. It does not exercise a screen reader; PRD-25's "screen-reader labels in both languages" is met structurally (every control has a name in both locales).
- **Screens that need state the seed does not create** (an MFA enrolment in progress, a dispute's evidence upload, the merge preview) are covered by their parent screens' scan; their components share the same primitives (`Field`, `TextInput`, `Button`).
- **Contrast of the amber/emerald/red badges** passed axe on the seeded data; a new badge colour must keep the `-900` text on `-100` background pattern.
