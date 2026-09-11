# Slice 21 — Acceptance-pass tooling: the corpus importer, the review pack, the procedure

Status: **Implemented and tested.** Written before the code, per `CLAUDE.md` → Development Process step 2.

Source specs: doc 09 §5.1 (T-90 corpus composition, T-91 inter-annotator agreement, T-92 no real customer data
without consent, provenance per item) · §5.2 gates · §7 performance · doc 10 acceptance 3 (native-speaker review) ·
SEC-90 · `CLAUDE.md` → First Product ("until this one passes its acceptance tests").

The one rule this slice exists to keep: **when the pilot's data arrives, nothing stands between it and the gates but
the procedure — and no customer's contact detail ever lands in a corpus file.**

## 1. Scope

| # | Capability |
|---|-----------|
| S1 | `services/ai/evaluations/import_corpus.py`: JSONL/CSV of labelled replies → corpus JSONL. Redacts IBAN/card/phone (the service's own `redact`) and emails; refuses rows without provenance, unknown labels/languages, empty or fully-redacted texts, duplicate ids — and refuses the whole import with them. Reports T-90 readiness (size, language mix vs targets, class coverage, consequential minimums) and T-91 agreement (percent, Cohen's κ, per class) with a disagreements file. `--check` reports on an existing corpus. |
| S2 | `FinanceAi.Migrator review-pack [path]`: renders `docs/review/arabic-review-pack.md` from `SystemTemplates.All` — every template in both languages side by side, the placeholder glossary, a seven-question checklist per template, and the briefing-review instructions. No database. |
| S3 | `docs/ops/acceptance-pass.md`: the step-by-step for the pass — corpus, gates on the target host, performance on the target host, the Arabic review, the sign-off record. |

## 2. Acceptance criteria

| ID | Criterion | Test |
|----|-----------|------|
| AC-01 | A row with a phone, an email and an IBAN is written with all three redacted and the counts recorded; the label and provenance survive | `test_clean_redacts_contact_details_and_keeps_the_label` |
| AC-02 | Missing provenance, unknown label/language/label_2, empty or fully-redacted text are refused with the row number | `test_clean_refuses_bad_rows` |
| AC-03 | κ is computed correctly (0.524 for a known table; 1.0 for perfect agreement; none without pairs); readiness names missing classes, consequential shortfalls and the classes humans disagree on | `test_kappa_and_readiness` |
| AC-04 | One bad row refuses the whole import and writes nothing; a clean import writes redacted items and prints the readiness block | `test_import_is_atomic_and_writes_a_clean_corpus` |
| AC-05 | The review pack contains every template key in both languages and every placeholder, with no "missing" cell | `ReviewPackTests` |
| AC-06 | `--check` on today's corpus reports T-90 as not met (72 items) — recorded honestly in the runbook | run recorded in the review |

## 3. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | Redaction at import, not at evaluation | A corpus file is committed and shared; the evaluation harness already redacts before the model, but the file itself must be clean (SEC-90). |
| D-2 | Refuse the whole file on one bad row | A partially imported corpus with silently dropped rows would misstate the T-90 numbers. |
| D-3 | The review pack is generated, never hand-written | It is rendered from the seeds the product ships; a template change re-generates it. |
| D-4 | `ar` covers MSA and dialect in the readiness mix | The corpus does not label register; the T-90 40/25 split is reported as one 65 % Arabic target with the note in the importer. |
