# Slice 30 — The corpus loop (T-109) and determinism (T-107)

Status: **Implemented and tested.**

Source: doc 09 §5.4 T-109 ("every `edit-and-approve` and `reject` stores `human_correction`; a periodic job proposes
these as new corpus items, anonymized, consent-gated") — the storing was slice 9, the proposing was not built · doc 09
§5.3 T-107 ("the same input at `temperature=0` with a fixed seed produces the same output across three runs;
non-determinism is itself a reported defect") — the harness ran at temperature 0 with a seed but never repeated an input.

## 1. Scope

| # | Capability | Test |
|---|-----------|------|
| S1 | **`FinanceAi.Migrator corpus-proposals --tenant <id> --consent <reference> --out <csv> [--since date]`** (`CorpusProposals.RenderAsync`): one tenant's `classify_customer_reply` suggestions decided `edited` or `rejected`, joined to their message, written in `import_corpus.py`'s exact columns — `text` (the normalized body), `label` (the human's), `language` (detected; `other` → `mixed`), `provenance = consented`, `note` (consent reference, decision, the model's label and confidence), `expect` (the human's amount / date / dispute reason). A rejected suggestion whose message nobody labelled is counted as *awaiting a label* and not exported; a body over 4,000 characters is skipped. Runs in one transaction with the tenant scope set, so RLS applies exactly as for the application, and rolls back. | `CorpusProposalsTests` |
| S2 | **Consent gating**: the tenant id and a non-blank consent reference are arguments, not defaults; the command refuses without them. Anonymization stays where it already is — the importer redacts phones, IBANs, cards and emails on the way in, and the test proves the handoff. | `CorpusProposalsTests`, `test_corpus_proposals_export_is_accepted_by_the_importer` |
| S3 | **`evaluate.py --determinism N`**: N items spread evenly through the corpus (not the first N, which share a language block) are run twice more; `determinism_metrics` compares classification, confidence, reason code, extracted fields and validation status across the three runs; the report gains a T-107 gate row and a section listing every item that differed. An empty sample does not pass. | `test_evaluate_extras.py` |
| S4 | The acceptance pass (§1, §2) names both; the runbook lists the command. | — |

## 2. Decisions

| # | Decision | Why |
|---|----------|-----|
| D-1 | A command the operator runs, not a scheduled job | "Periodic" in T-109 is about cadence, not automation. An unattended export of customer text is exactly what T-92 forbids; the operator who holds the consent runs it, and the consent reference travels in every row. |
| D-2 | Export the human's label, never the model's, as `label` | The corpus teaches from what people decided; the model's label and confidence go in `note` so the labeller sees what the model thought. |
| D-3 | Rejected-but-unlabelled rows are counted, not exported | "Not this" is not a label; the count tells the operator how many replies still need a person. |
| D-4 | Three runs, full-body comparison, reported item by item | T-107's words. Averaging a flaky output into an accuracy figure would hide the defect the test exists to surface. |
| D-5 | The determinism sample is spread through the corpus | The corpus is ordered by language; the first N would all be Arabic. |

## 3. Not covered

Running T-107 against the live model is part of the acceptance pass on the host (`--determinism 20`); the unit
tests prove the sampling, the comparison and the report, not the model.
