# Slice 27 — Performance completion: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **none** (tests and a workflow variable; no product code).

1. **Endpoints.** None added or changed; route pin 160; OpenAPI snapshot unchanged.
2. **Tables.** None. The seeds write through the admin connection into throwaway test databases, as the existing performance tests do.
3. **Pre-auth paths.** None. The load profile signs twenty members in through the normal login (the test collection lifts the auth limiter, as every login-heavy test relies on) and every request afterwards carries a member's token through `TenantScopeMiddleware`.
4. **Money.** The import fixture's amounts are string literals in a CSV; the product's `decimal` path handles them as in any import.
5. **AI.** None touched; the scripted client answers nothing because nothing asks it.
6. **Dependencies.** None added.

## Flagged

- **The load profile shares the API process with the test host** (`WebApplicationFactory`): the twenty clients and the server are one process, so the figure includes no network and the CPU is shared. The host run in the acceptance pass is the number that counts; the nightly is the trend.
- **Ten minutes on the nightly** adds ten minutes to a job that took about five; the schedule is nightly, so it does not matter, but a manual dispatch now takes a quarter of an hour.
- **The import budget covers CSV only**; the XLSX reader shares the pipeline after parsing, and the parse itself is bounded by `ImportLimits.MaxRows`.
