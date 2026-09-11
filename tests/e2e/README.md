# End-to-end journeys (Playwright)

Doc 09 §6: T-121 … T-132, run once per locale (`en`, `ar`; T-120). The Arabic run also asserts
`dir="rtl"`, that every money value sits in its own `<bdi>` isolate with the exact stored amount, and
keeps visual snapshots of the aging table, the empty queue and a rendered Arabic message preview.

Data is set up through the API (the same endpoints the browser calls: register, customers, contacts,
the CSV import path, payments, the sweep); the human gates are walked in the browser. A second member
is seeded straight into the database, the way the .NET suites do it — v1 has no invitation flow.

## Servers

`playwright.config.ts` starts three servers unless they are already running:

| Server | Port | Notes |
|--------|------|-------|
| AI service, **fake model** | 8091 | `AI_FAKE_MODEL=1`: a deterministic stand-in (`services/ai/app/fake_model.py`) so T-129/T-130/T-131 need no GPU. An organization whose name contains `[ai-down]` gets "model unavailable" — that is how T-131 stops the AI container. |
| API | 5080 | Release build of `FinanceAi.Api`, after the migrator; `AUTH_RATE_LIMIT_PER_MINUTE` raised as the .NET suites do. |
| Web | 5173 | `vite` dev server; it proxies `/api` to 5080. |

The database and Mailpit are the ones in the repo `.env` (`podman compose -f infrastructure/compose.yml up -d`).

## Run

```
dotnet build -c Release apps/api/FinanceAi.Api apps/api/FinanceAi.Migrator
npm --prefix tests/e2e ci && npx --prefix tests/e2e playwright install --with-deps chromium
npm --prefix tests/e2e test              # both locales
npm --prefix tests/e2e run test:ar       # one locale
npm --prefix tests/e2e run report        # the HTML report
```

Without root for `install-deps`, the browser's shared libraries can be unpacked locally
(`apt-get download libnspr4 libnss3 libasound2t64 …` then `dpkg -x` into a directory) and pointed at with
`LD_LIBRARY_PATH`; the snapshots were produced on a machine with the Noto fonts.

Snapshots live in `specs/journeys.spec.ts-snapshots/`; regenerate deliberately with `--update-snapshots`.
