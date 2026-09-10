# ADR-0002 — Money as `numeric(19,3)` / C# `decimal`, JSON as a string

Status: **Proposed** · Date: 2026-09-10

## Context

`CLAUDE.md` mandates `decimal` in C# and forbids float/double. Two further decisions are
not settled by that rule: the **scale**, and the **wire representation**.

Jordan's currency has three decimal places (1 JOD = 1000 fils). A two-decimal assumption
— the default in most accounting software and most developers' habits — silently
truncates every Jordanian amount. Meanwhile JSON numbers are IEEE-754 doubles in
JavaScript, so `{"amount": 1250.500}` parsed in the browser is already a float, which
would defeat the C# rule at the boundary.

Alternative considered: **minor units as integers** (store 1250500 fils). Exact and
fast, but every read/write needs scaling, multi-currency scales differ (2 vs 3
decimals), and a mis-scaled integer is a 1000× error that looks plausible. Rejected.

## Decision

1. Storage: `numeric(19,3)`; C#: `decimal`. Enforced by a schema test (INV-11) and a
   reflection test (T-23).
2. **Scale 3 for every currency**, with rounding performed at the *currency's own*
   scale (FIN-05). Two-decimal currencies store a trailing zero.
3. Rounding: `MidpointRounding.AwayFromZero`, applied **once at the end** of a
   computation — this matches what a Jordanian accountant expects to see, unlike
   banker's rounding.
4. Wire format: an object `{"amount": "1250.500", "currency": "JOD"}` — a **string**
   with exactly three decimals, never a JSON number. The frontend never does arithmetic
   on it (UI-30).
5. Money inside audit `changes` JSON is serialized as a string too (DM-28).

## Consequences

**Positive:** no representation error anywhere; the three-decimal reality of the market
is structural rather than remembered; the frontend cannot silently compute a wrong
total; audit records survive JSON round-trips exactly.

**Negative:** every client must format strings rather than numbers; a small amount of
ceremony on every money field; developers accustomed to two decimals will be surprised
until the tests catch them (which is the point).
