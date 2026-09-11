# 0007 — Coverage: what the suites cover today, and the floor proposed for CI

Date: 2026-09-12 · Status: **accepted** by the owner on 2026-09-12; enforced in the `api` CI job from slice 19

Doc 10 slice 0 lists "coverage" among the CI gates; slice 16 left it as the one unenforced item because a floor
needs a baseline. This note is the baseline.

## Measurement

`dotnet test … --collect:"XPlat Code Coverage"` (coverlet, already a dependency) for the unit, security and
integration suites (Performance excluded), merged by `infrastructure/coverage-report.py` — a line is covered when
any suite covered it; source-generated code under `obj/` (the regex and OpenAPI-comment generators) is excluded
because it is not ours.

| Assembly | Lines | Covered | Line coverage |
|----------|------:|--------:|--------------:|
| `FinanceAi.Domain` | 1,691 | 1,653 | **97.8 %** |
| `FinanceAi.Infrastructure` | 5,849 | 5,521 | **94.4 %** |
| `FinanceAi.Api` | 3,301 | 3,084 | **93.4 %** |

What is uncovered is what one would expect: defensive branches in the import readers (`XlsxTableReader`,
`ImportEndpoints`), the live-Ollama path of `AiClient` (the suites use the scripted client; the Python suite and
the evaluation harness cover the real one), and a handful of `PlatformIdentityStore` edge paths.

## Proposed floor

| Assembly | Floor | Headroom today |
|----------|------:|---------------:|
| `Domain` | 95 % | 2.8 pts |
| `Infrastructure` | 90 % | 4.4 pts |
| `Api` | 85 % | 8.4 pts |

Line coverage, per assembly, on the merged result. The floors sit below today's numbers by enough that a normal
slice does not trip them, and high enough that a slice which ships a feature without tests does. T-02 (money,
state machines, tenancy get the deepest coverage) is why Domain carries the highest floor.

## How it is enforced

One step in the `api` job after the three test runs (each collecting `XPlat Code Coverage`):

```
infrastructure/coverage-report.py $(find TestResults -name coverage.cobertura.xml) Domain=95 Infrastructure=90 Api=85
```

The script exits 1 naming the assembly and its number. Nothing else changes; no new dependency.

## Not proposed

A branch-coverage floor (coverlet reports it, but the numbers move with refactors that change nothing), and any
floor on the web (`vitest` has no coverage collection configured; UI coverage is the e2e journeys' job).
