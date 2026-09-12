# Slice 26 — Per-operation AI enablement: self-review

Per `CLAUDE.md` → Slice Self-Review. Risk tier: **low**.

1. **Endpoints.** None added; route pin 160. `GET/PATCH /organization/ai-settings` and `GET /ai/health` gain fields; all three stay behind `TenantScopeMiddleware` with their existing permissions (`tenant.read`, `ai.settings.write`, `cases.read`). The OpenAPI snapshot is regenerated; `EndpointAuthorizationSweepTests` unchanged.
2. **Tables.** None new; `tenant_settings` gains two boolean columns (migration 0020, default true). RLS unchanged. `Switches_AreTenantScoped` checks a PATCH by one tenant leaves the other's row alone.
3. **Pre-auth paths.** None; nothing here runs before tenant scope.
4. **Money.** None.
5. **AI.** The slice removes AI calls, never adds one: both gates are checked before any request to the service (the tests count requests). The output path is untouched — schema validation, the numeric guard and the human gates of slices 6–10 stand as before.
6. **Dependencies.** None added.

## Flagged

- **The kill switch off makes the operation toggles inert in the UI** but the API still accepts them (a setting can be prepared while paused). Deliberate; stated in the card's hint.
- **A third AI operation** means a third column and a third pair of fields — a migration plus contract change, not a config entry (D-1).
