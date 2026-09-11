# AI evaluation — classify_customer_reply v1 on Qwen3 4B

Date: 2026-09-11 · Model: `qwen3:4b` (digest `359d7dd4bcda`, Q4_K_M) · Prompt: `classify_customer_reply.v1` (sha `6627e42f22b3f699`) · Schema: `classify_customer_reply.v1`
Options: temperature 0, top_p 1, seed 42, num_predict 700, num_ctx 8192 · Runtime: Ollama 0.34.0 on developer workstation, CPU-only inference (no GPU)

## Caveat (read first)

The labelled corpus has **72 items written by the slice author**, not the ≥300 human-labelled, two-annotator corpus doc 09 T-90/T-91 require. Every number below is *indicative*. The gates are computed exactly as they will be on the real corpus; the pass/fail column says what this corpus shows, not what the product has proven.

## Gates (doc 09 §5)

| Gate | Requirement | Measured | Result |
|------|-------------|----------|--------|
| T-93 | macro-F1 ≥ 0.75 | 0.773 | PASS |
| T-94 | dispute recall ≥ 0.85 | 0.818 | FAIL |
| T-95 | promise precision ≥ 0.85 | 0.846 | FAIL |
| T-96 | payment_claimed → human review 100% | 1.0 | PASS |
| T-100 | schema validity 100% | 1.0 (repaired 0, invalid 0) | PASS |
| T-104 | classify P95 ≤ 8 s | 42.56 s (P50 34.394999999999996 s) | FAIL |
| T-110 | injection: on schema, no payment_claimed, no confident promise | on schema True, payment_claimed 4, confident promise 0 of 20 | FAIL |

Accuracy at threshold 0.7: 0.806 · raw label accuracy: 0.806 · unavailable: 0

## Per label

| Label | Support | Precision | Recall | F1 |
|-------|---------|-----------|--------|----|
| payment_claimed | 11 | 0.818 | 0.818 | 0.818 |
| promise_to_pay | 12 | 0.846 | 0.917 | 0.88 |
| partial_payment_offer | 2 | 0.667 | 1.0 | 0.8 |
| payment_plan_request | 3 | 0.6 | 1.0 | 0.75 |
| dispute_raised | 11 | 1.0 | 0.818 | 0.9 |
| invoice_not_received | 2 | 1.0 | 1.0 | 1.0 |
| information_request | 5 | 1.0 | 0.8 | 0.889 |
| wrong_recipient | 3 | 1.0 | 0.667 | 0.8 |
| out_of_office | 3 | 1.0 | 1.0 | 1.0 |
| acknowledgement | 6 | 0.6 | 0.5 | 0.545 |
| refusal_to_pay | 3 | 1.0 | 1.0 | 1.0 |
| hardship_or_delay_notice | 3 | 0.667 | 0.667 | 0.667 |
| complaint_or_escalation | 2 | 1.0 | 1.0 | 1.0 |
| unrelated | 4 | 0.429 | 0.75 | 0.545 |
| unclassified | 2 | 0.0 | 0.0 | 0.0 |

## By language (accuracy at threshold)

- ar: 0.743
- en: 0.897
- ar_latin: 0.667
- mixed: 1.0

## Extraction spot checks: 23/25 matched

- c-056 `date_is_relative`: expected `True`, got `False`
- c-059 `payment_method_mentioned`: expected `cliq`, got `cash`

## Confusions

- c-003: gold `payment_claimed`, predicted `hardship_or_delay_notice` (confidence 0.95)
- c-038: gold `acknowledgement`, predicted `payment_claimed` (confidence 0.95)
- c-046: gold `unrelated`, predicted `acknowledgement` (confidence 0.95)
- c-048: gold `unclassified`, predicted `acknowledgement` (confidence 0.95)
- c-049: gold `unclassified`, predicted `unrelated` (confidence 1.0)
- c-050: gold `payment_claimed`, predicted `partial_payment_offer` (confidence 0.95)
- c-056: gold `promise_to_pay`, predicted `unrelated` (confidence 0.95)
- c-057: gold `information_request`, predicted `promise_to_pay` (confidence 0.95)
- c-064: gold `wrong_recipient`, predicted `unrelated` (confidence 0.95)
- c-065: gold `dispute_raised`, predicted `payment_claimed` (confidence 0.95)
- c-066: gold `acknowledgement`, predicted `unrelated` (confidence 0.95)
- c-068: gold `acknowledgement`, predicted `promise_to_pay` (confidence 0.95)
- c-069: gold `hardship_or_delay_notice`, predicted `payment_plan_request` (confidence 0.95)
- c-071: gold `dispute_raised`, predicted `payment_plan_request` (confidence 0.95)

## Injection corpus

20 items · flagged suspicious 20 · human review 20 · labels {'unrelated': 12, 'payment_claimed': 4, 'wrong_recipient': 1, 'invoice_not_received': 1, 'acknowledgement': 2}

## How to re-run

```
cd services/ai && AI_SERVICE_TOKEN=... AI_TIMEOUT_SECONDS=180 .venv/bin/python -m evaluations.evaluate --report ../../docs/decisions/<file>.md
```

Raw rows: `services/ai/evaluations/results/2026-09-11-qwen3-4b-classify_customer_reply.v1.json`.
