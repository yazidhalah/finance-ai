# ADR-0003 — AI is advisory only; the state machine and the human act

Status: **Proposed** · Date: 2026-09-10

## Context

`CLAUDE.md` states that AI may recommend but may never post accounting entries, write
off balances, move money, submit regulatory documents, or initiate legal escalation, and
that an AI classification of "paid" creates a verification task rather than marking an
invoice paid. The local model is Qwen3 4B — small, running on modest hardware, reading
mixed Arabic/dialect/Arabizi text (A-18, A-19) written by people who are sometimes
actively trying to avoid paying, and who can inject instructions into the text we feed
the model (§ doc 07 §2).

The temptation in a collections product is autonomy: auto-classify, auto-send,
auto-record the promise. Each of those, wrong, damages a customer relationship the
tenant owns.

## Decision

**Structural, not procedural, separation.** AI output is an *input* to a
human-or-system-triggered transition, never an actor:

1. There is **no transition whose actor is the model** (SM-04). Approving a suggestion
   calls the same service method a human action calls, with `ai_suggestion_id` recorded
   as provenance (API-20).
2. **No AI-produced number reaches a money column without a human confirming it**
   (AI-41). Amounts are extractions, re-validated in C# against stored balances.
3. **No AI-authored numerals reach a customer**: drafts use placeholders and a
   post-generation guard rejects bare numerals (AI-60); briefing narratives are checked
   against the computed metrics and discarded if a numeral is untraceable (AI-81).
4. Consequential classifications (`payment_claimed`, `dispute_raised`, `refusal_to_pay`,
   `complaint_or_escalation`) always require human review (AI-40).
5. Below the confidence threshold, the answer is `unclassified` — a human queue item
   with no pre-selected guess (AI-05, A-21).
6. The AI service has **no database access and no tools** (AI-11, AI-23). The maximum
   damage from a perfect prompt injection is a wrong suggestion shown to a human.

## Consequences

**Positive:** the product is safe to ship with a small, imperfect model; a model upgrade
is a quality improvement, never a new class of risk; every AI-influenced action is
auditable; injection is contained by capability starvation rather than by prompt
wording, which is the only defence that actually holds.

**Negative:** more human clicks than a fully-autonomous competitor; the perceived
"magic" is lower; some users will ask for auto-send — the answer is a documented no for
v1, revisited only with measured evaluation results and per-tenant opt-in with caps.
