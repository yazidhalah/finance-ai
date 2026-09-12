# The v1 acceptance pass — how "First Product done" is decided

`CLAUDE.md` → First Product: *do not implement any other module until this one passes its acceptance tests.* Every
automated gate of doc 09 is green on `main` today; what remains is the part that needs a pilot and real hardware
(doc 09 §5 and §7, doc 10 acceptance 3). This page is the procedure. Slice 21 built the tooling it names.

## 0. Who and what

| Role | Needed for |
|------|-----------|
| The pilot tenant (an SME that has consented in writing, SEC-90/T-92) | the reply corpus, the performance data, three real briefings |
| Two labellers who read Jordanian Arabic and Arabizi | T-90/T-91 labels |
| A native reviewer of business Arabic | doc 10 acceptance 3 — templates and briefings |
| The target host (the GPU box the stack will run on, Q-06) | T-104 latency, T-140 P95 |

## 1. The corpus (doc 09 §5.1 — T-90, T-91, T-92)

1. Export the pilot's replies (email/WhatsApp texts) with the invoice context stripped — the text alone. Once the
   pilot has been using the inbox, its own corrections are the best source (T-109): every edit-and-approve and every
   rejected suggestion a person later labelled becomes a proposal row —

   ```
   dotnet run --project apps/api/FinanceAi.Migrator -- corpus-proposals --tenant <tenant id> \
       --consent "<reference to the written consent, T-92>" --out pilot-proposals.csv [--since 2026-10-01]
   ```

   The command refuses to run without the tenant and the consent reference; it writes `provenance = consented`
   because you named them. Rows carry the human label, the model's label and confidence in `note`, and the human's
   amount/date/reason in `expect`; the second labeller adds `label_2` in the sheet before import.
2. Two labellers label independently in a sheet with the columns the importer expects: `text`, `label` (one of the
   15 classes), `label_2` (the second labeller), `language` (`ar` | `en` | `ar_latin` | `mixed`), `provenance`
   (`consented` for the pilot's own replies), optional `note` and `expect` (JSON, e.g. `{"payment_reference_text":
   "TRX-…"}`).
3. Import — the importer **redacts** phones, IBANs, cards and emails, refuses any row without provenance, and refuses
   the whole file if one row is bad:

   ```
   services/ai/.venv/bin/python services/ai/evaluations/import_corpus.py pilot.csv \
       --out services/ai/evaluations/corpus/pilot.jsonl --disagreements services/ai/evaluations/corpus/pilot-disagreements.jsonl
   ```

4. Read the readiness block it prints: ≥ 300 items, the language mix against the T-90 targets, every class present,
   ≥ 15 for the four consequential classes, and the **T-91 agreement** (percent and Cohen's κ, per class). Any class
   where the two humans agree under 80 % goes back to the labellers with a sharper definition **before** the model is
   scored on it — a model cannot be held to 90 % where people manage 80.
5. Resolve the disagreements file (a third opinion, recorded in `note`), re-import, commit the corpus.

Today's author-written corpus (72 items) is what slice 9 was evaluated on; `--check` on it says plainly that T-90 is
not met. It stays as the regression set; the pilot corpus is the acceptance set.

## 2. The AI gates on the target host (doc 09 §5.2 — T-93 … T-104)

On the target host, with Ollama and the pinned model (`qwen3:4b`, digest in THIRD-PARTY-NOTICES):

```
cd services/ai && AI_EVAL_HARDWARE="<gpu name, RAM>" .venv/bin/python -m evaluations.evaluate \
    --corpus pilot.jsonl --determinism 20 --report ../../docs/decisions/0009-ai-evaluation-<date>-qwen3-4b-pilot.md
```

The report renders every gate with its verdict, including **T-107**: `--determinism 20` reruns twenty items spread
through the corpus twice more and lists any output that differed at temperature 0 with the fixed seed — a
difference is a runtime or prompt defect to chase, never a number to average. The pass criteria are the table in doc 09 §5.2; T-104 (classify
P95 ≤ 8 s) is the one that cannot be judged on a CPU box — the committed reports say so. If a gate fails, T-105
applies: the operation stays **off by default** for the pilot (`aiEnabled: false`), the manual path carries the loop,
and the prompt or model is iterated with a new report — never a silent threshold change.

## 3. Performance on the target host (doc 09 §7 — T-140, T-141, T-142)

```
LOAD_PROFILE_SECONDS=600 dotnet test tests/integration/FinanceAi.IntegrationTests -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
```

Seeds the T-140 dataset (three tenants × 50k invoices) in the test database on the host and asserts: aging and queue
P95 < 800 ms, single-entity reads P95 < 500 ms, a 5,000-row import in < 60 s, the T-141 plan assertions, and T-142 —
twenty members of one tenant reading for ten minutes with no error-rate increase (five windows, every status counted).
The tests print their figures through `ITestOutputHelper`, which the console logger only shows at `verbosity=detailed`
— record those lines in the decision record. (The nightly workflow runs the same on a shared runner; the number that
counts is the host's.) The fourth T-140 budget, classification P95 < 8 s, is measured by the evaluation harness in §2.

## 4. The Arabic review (doc 10 acceptance 3)

```
dotnet run --project apps/api/FinanceAi.Migrator -- review-pack docs/review/arabic-review-pack.md
```

Hand the generated pack (every template in both languages, the placeholder glossary, a seven-question checklist per
template) to the native reviewer together with three real briefings from the pilot's Today screen. Their answers go
into the pack's "Reviewer notes" blocks; wording changes become a normal slice (templates are seeds in
`SystemTemplates.All`, versioned and approved per tenant).

## 5. Sign-off

Write `docs/decisions/0010-v1-acceptance-<date>.md` with:

- the corpus readiness block (from §1) and the agreement figures;
- the gate table from §2 with the hardware named;
- the P95s from §3;
- the reviewer's summary from §4 and the list of template changes made;
- one sentence per open gate, if any, and what T-105 means for it in the pilot.

"First Product done" is that record with no open gate — and only then does `CLAUDE.md`'s second-module rule lift.
