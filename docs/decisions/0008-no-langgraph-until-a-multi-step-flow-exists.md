# 0008 — LangGraph: not a dependency until a multi-step flow exists

Date: 2026-09-12 · Status: **accepted** (closes the flag carried since slice 10)

`CLAUDE.md` lists LangGraph under Technology for agent orchestration, and A-22 scopes it to "the daily briefing
and reply-handling flow only — classification and extraction are single-shot calls, not agents". Slice 10
deferred it with the note that one model call per language does not need a graph, and flagged the deferral.
This record makes that a decision so the flag stops being carried from slice to slice.

## What the AI service actually runs

`services/ai` exposes two operations, both single-shot:

| Operation | Shape | Where the loop is |
|-----------|-------|-------------------|
| `classify_reply` (slice 9) | one prompt → one JSON object, validated against a strict schema, confidence floor → `Unclassified` (A-21) | none — a low-confidence result goes to a human queue, never to a retry loop |
| `daily_briefing` (slice 10) | metrics computed in C# → one prompt per language → `narrative` / `highlights` / `numbers_used`, checked by `NumericFidelityGuard` | none — a guard failure discards the narrative; the metrics screen stands alone |

There is no state to carry between model calls, no tool the model may invoke, no branch chosen by the model's own
output. Every branch after a model call is deterministic C# (schema validation, the numeric guard, the human
gates of slices 6–10). Doc 03's advisory boundary (decision 0003) is the reason: the model recommends, the
backend decides, and a graph whose edges the model picks would move the deciding into the model.

## Decision

LangGraph is **not added** while every AI operation is a single call followed by deterministic validation.
It is added — with its licence recorded in `THIRD-PARTY-NOTICES.md` in the same commit, and an evaluation
report under `docs/decisions/` (AI-111/112) — only when a slice specifies a flow with at least one of:

- a second model call whose prompt depends on the first call's validated output (a genuine multi-step flow);
- a model-selected branch that is itself subject to a human gate, so that the graph makes the gate explicit
  rather than replacing it;
- a reply-handling flow that spans more than one message (A-22's second case), which no v1 slice specifies.

Adding the dependency now would put a graph around a single node, add a transitive tree to the SEC-68 audit and
`check-notices.py --transitive` for nothing, and — the reason A-22 gives — multiply the failure modes of a 4B
model without changing any observable behaviour.

## Consequences

- `CLAUDE.md` Technology continues to name LangGraph as the orchestration choice *when orchestration is needed*;
  this record is the answer to "why is it not in `requirements.txt`".
- The flagged deferral in `docs/slices/slice-10-daily-briefing.md` is resolved by this record; the slice document
  is left as written (it is a record of what was decided at the time).
- Whoever specifies the first multi-step flow re-reads A-22 and this record first; the bar is a flow the graph
  makes clearer, not a library the stack lists.
