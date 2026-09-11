# Slice 21 — Acceptance-pass tooling: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **low for the product, high for privacy** — the importer is the
door through which real customer text enters the repository.

1. **Endpoints.** None. Route pin 147; snapshot unchanged.
2. **Tables.** None. `review-pack` never opens a connection.
3. **Pre-auth / cross-tenant paths.** None in the product. The importer runs on the operator's machine on an export the pilot consented to; it never reads the database.
4. **Money.** None. Amounts in replies are text the model reads; the importer leaves them as written.
5. **AI.** No change to validation or approval. The corpus the gates will run on is now clean by construction (redaction) and honest by construction (T-90/T-91 readiness printed, refusal on missing provenance).
6. **Dependencies.** None. The importer is stdlib plus the service's own `redact`; the renderer is Domain + `System.Text`.

## Verified locally

- `import_corpus.py --check` on the committed 72-item corpus: "T-90 minimum 300: NOT MET", Arabic 49 % vs ~65 %, Arabizi 8 % vs ~15 %, English 40 % vs ~20 %, all four consequential classes under 15, no second label — the honest statement of where slice 9's evaluation stands.
- `review-pack`: 28 templates (14 keys × ar/en, incl. WhatsApp variants and the daily briefing), every placeholder, no missing cell.
- pytest 87 passed; unit suite with `ReviewPackTests`.

## Flagged

- **Redaction is pattern-based.** Names inside the text are not redacted (a name is not a contact detail, and the labels need the text intact); the consent in T-92 covers this, and the runbook says the export must strip invoice context. A reviewer who wants names masked too can add a pattern — the importer's structure allows it.
- **The `ar` bucket merges MSA and dialect**, so the 40/25 split of T-90 is reported as one 65 % target; labelling register would need a fifth column and a labeller instruction.
- **Nothing here runs the pass** — it needs the pilot, the labellers, the reviewer and the host. The runbook names each.
