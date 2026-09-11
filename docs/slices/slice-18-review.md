# Slice 18 — The typed contract: self-review

Per `CLAUDE.md` → Slice Self-Review.
Risk tier: **low** — metadata and documentation; no handler's behaviour changed.

1. **Endpoints.** None added; route pin 147 and the anonymous set are unchanged (`EveryOperation_DeclaresItsAccess`). Two responses changed from anonymous objects to named records with identical JSON (`TimelineListResponse`, `PlaceholderListResponse`); the web client and e2e journeys read the same `items` key.
2. **Tables.** None.
3. **Pre-auth / cross-tenant paths.** None. The transformer runs at document generation, never on a request.
4. **Money.** No calculation touched. The document shows every money value as `MoneyDto { amount: string, currency: string }`; a grep over the 19k-line snapshot finds zero JSON `number` types — FIN-02 holds at the contract level.
5. **AI.** Nothing changed; the AI responses' shapes are now visible in the contract (`AiSuggestionResponse`, `BriefingResponse`).
6. **Dependencies.** None; `check-notices.py` passes.

## Flagged

- **The declaration is metadata, not the compiler's word.** `.Produces<T>()` beside a handler returning `IResult` can drift from the handler; the guard is that every route is exercised by the integration and security suites, and the two suites read the responses as the declared shapes. A wrong declaration would surface as a test reading a missing property — not as a build error. Typed results remain the stronger fix (deferred).
- **`Created` responses do not document `Location`**; 409 (idempotent replay) and 423-style refusals are not enumerated per endpoint — they are problem-shaped like every other failure, which the `4XX` rule covers only for the statuses listed.
