# Slice 32 — The daily job runs itself: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **medium** (a background writer touching every tenant).

1. **Endpoints.** None; route pin 161; OpenAPI unchanged.
2. **Tables.** None.
3. **Pre-auth paths / cross-tenant by design.** `PlatformIdentityStore.ListActiveTenantsAsync` reads ids and timezones of active tenants on the platform scope (the only new platform-scope query, in the only file allowed to enter it). `SweepRunner` then enters each tenant with `TenantContext.Set` + `DatabaseScope.EnterTenantAsync` — exactly the slice 24 webhook pattern — so RLS applies per tenant and nothing from one tenant is in scope while another runs. Every tenant takes comparable time (the sweep's own work); a failing tenant is caught, logged by id, and does not change what the next tenant sees. The test deletes one tenant's settings row and proves the pass records that failure and still sweeps the others.
4. **Money.** None new — the sweep's existing steps.
5. **AI.** The sweep's briefing step calls the AI service for tenants with the briefing switch on (slice 26), exactly as a manual sweep does; nothing new.
6. **Dependencies.** None (the `BackgroundService` host lives in the API project, which already has Hosting; Infrastructure gained no package).

## Flagged

- **One hourly `collection_case.sweep_run` event per tenant** — the price of an idempotent hourly pass; noted in the slice doc.
- **The first pass after a deploy runs 30 s in**, whatever the hour; harmless by idempotence.
- **No overlap guard across processes** beyond the sweep's own per-tenant advisory lock — which is the guard: two API instances (not A-15) would serialize on it.
