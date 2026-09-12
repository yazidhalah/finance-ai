# Slice 29 — The golden tenant: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **none** (a test, its data file, one test-support helper).

1. **Endpoints.** None; route pin 160; OpenAPI unchanged.
2. **Tables.** None. The test writes only through the API; the two read-only SQL queries (status and event-type counts) run as the superuser in a throwaway database, like every other assertion helper.
3. **Pre-auth paths.** None.
4. **Money.** The script's arithmetic (half, a tenth, 5 % of net) is test-side `decimal` rounded to three places, and what it asserts is the product's own totals. No new product math.
5. **AI.** None; the sweep's briefing runs against the scripted client and its result is not in the file.
6. **Dependencies.** None added.

## Flagged

- **~50 s on every `api` job run.** Acceptable; if the suite grows past patience, the golden run can move to its own job without changing the test.
- **`RowsAsync` reads two columns by position**; it is a test helper and says so.
- **A schema or rule change that legitimately moves a number** is expected to update the file in the same commit; the review should ask why the number moved.
