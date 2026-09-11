# Slice 11 — v1 acceptance: self-review

Per `CLAUDE.md` → Slice Self-Review. A test-and-infrastructure slice: no endpoint, no table, no money
field was added. The six questions, briefly and honestly:

1. **Endpoints:** none added. The E2E suite calls the existing ones; the route pin stays at 131.
2. **Tables:** none added. `tests/e2e/helpers/api.ts` seeds a member with `psql` — through the same
   `users` / `tenant_memberships` columns the .NET seeder uses, with the owner's own Argon2id hash.
3. **Pre-tenant code paths:** none added.
4. **Money:** none added. The suite asserts money through `bdi[data-amount]` — the exact stored string —
   never by parsing rendered text.
5. **AI-touching code:** the fake model (`services/ai/app/fake_model.py`) is the one addition. It is
   opt-in (`AI_FAKE_MODEL=1`), refuses nothing that the real path refuses (the same schemas, the same
   validators, the same guards run on its output), and is unmistakable in every record it produces
   (`model_name = fake-model`, `model_digest = fake`, a warning at service start, `fake: true` on
   `/model-info`). It cannot reach production by accident: the flag is read only in `create_app` and
   `.env.example` does not mention it. `test_fake_model_is_opt_in_and_marked` pins the opt-in.
6. **Dependencies:** `@playwright/test` 1.63.0 (Apache-2.0, test-only) with `playwright`,
   `playwright-core` (Apache-2.0) and `undici-types` (MIT); recorded. The browser it downloads is a test
   tool, not a shipped artifact.

## What passed

- `npm --prefix tests/e2e test`: 24 passed (12 journeys × en/ar) on this machine, ~1 minute, against
  the Release API, the Vite dev server, the fake-model AI service, the local PostgreSQL and Mailpit.
- The Arabic snapshots show RTL layout, Western digits with Arabic separators, isolated money, and the
  "EN" fallback marker on an untranslated customer name (doc 10 §2.5).

## What the first CI runs taught (fixed in this PR)

Four first-run failures, all real: `tsc -b` type-checks the test files (an untyped locale parameter); the
`.env` loader keeps the first value for a key (the CI key was appended after the example's empty line);
`dotnet build` takes one project; and the Vite dev server must bind `127.0.0.1` on the runner. The fifth
was not a bug: the Arabic visual snapshots differ by ~5% in width between my machine and the runner
(fonts). Visual comparison is now opt-in (`PLAYWRIGHT_SNAPSHOTS=1`) with a manual `refresh_snapshots`
workflow input that renders baselines on the canonical environment for committing; everything else the
Arabic run asserts (RTL, isolates, exact amounts) stays on. Flagged below.

## What did not run

- **The CI workflow has not executed.** There is no GitHub Actions runner in this environment; the file
  is written from the commands that pass locally and the service images the repo already uses. Expect a
  first-run fix or two (paths, apt package names, the Chromium fonts).
- The browser's system libraries could not be installed with `apt` here (no root); they were unpacked
  locally and found through `LD_LIBRARY_PATH`. CI uses `playwright install --with-deps`.

## Flagged for your decision

1. **No invitation flow** — T-121's "invite an Accountant → accept" is replaced by seeding a member. If
   invitations are wanted in v1, that is a small slice of its own (slice 12, already on its branch).
1b. **Visual snapshots are not compared in CI yet** — baselines are font-dependent; run the workflow with
   `refresh_snapshots`, commit the artifact, then enable `PLAYWRIGHT_SNAPSHOTS=1` in the e2e job.
2. **Performance tests stay out of CI** (`Category=Performance`), as they need the 50k-invoice seed and
   minutes of runtime; they run locally on demand.
3. The e2e suite shares the developer database in `.env`; each run creates new organizations and never
   deletes anything (nothing in this system deletes). A dedicated e2e database is a one-line `.env`
   change if the clutter bothers you.
