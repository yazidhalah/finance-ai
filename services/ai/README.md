# finance-ai — local AI service

The Python side of doc 07. Two operations — `classify_customer_reply` (slice 9) and `daily_briefing`
(slice 10) — served by FastAPI and answered by Qwen3 4B through the local Ollama. The service is stateless, has no database and no tools,
and the only thing it can return is an object that validates against
`schemas/classify_customer_reply.response.v1.json`. What happens next is decided in C#.

```
services/ai
├── app/            FastAPI app: auth, framing, redaction, injection signal, validation, threshold
├── prompts/        classify_customer_reply/v1.md — immutable once released (AI-12)
├── schemas/        request/response schemas, verbatim from doc 07 §4 (AI-110)
├── evaluations/    corpus (labelled + injection) and the harness that writes docs/decisions/00XX-ai-evaluation-*.md
├── tests/          pytest, with a fake model; live-model tests are opt-in (AI_LIVE_TESTS=1)
├── requirements.txt
└── run.sh          serves on 127.0.0.1:8090 with the repo .env
```

## Run

```
python3 -m venv .venv && .venv/bin/pip install -r requirements.txt
ollama pull qwen3:4b
./run.sh                      # reads ../../.env: AI_SERVICE_TOKEN (required), OLLAMA_URL, AI_MODEL, AI_TIMEOUT_SECONDS
.venv/bin/python -m pytest    # 75 tests with a fake model; AI_LIVE_TESTS=1 adds the injection corpus against Ollama
```

Environment: `AI_SERVICE_TOKEN` (≥16 chars, shared with the backend), `OLLAMA_URL` (default
`http://127.0.0.1:11434`), `AI_MODEL` (`qwen3:4b`), `AI_TIMEOUT_SECONDS` (20 by spec; raise on a
CPU-only machine), `AI_SEED` (42), `AI_NUM_PREDICT` (700), `AI_NUM_CTX` (8192), `AI_MAX_CONCURRENCY` (5),
`AI_QUEUE_WAIT_SECONDS` (2), `AI_DEFAULT_MIN_CONFIDENCE` (0.70).

## Endpoints

| Route | Auth | What |
|-------|------|------|
| `POST /internal/ai/v1/daily_briefing` | `X-Service-Token` | Narrates already-computed figures (AI-80); the numeral check runs here first and again in C# (AI-81). Header `X-Ai-Validation-Status` adds `rejected_by_guard`. |
| `POST /internal/ai/v1/classify_customer_reply` | `X-Service-Token` | The classification operation. Headers on the answer: `X-Ai-Validation-Status` (`valid` / `repaired` / `schema_invalid`), `X-Ai-Input-Hash`, `X-Ai-Truncated`, `X-Ai-Attempts`. |
| `GET /health` | none | Liveness; no model call. |
| `GET /ready` | none | 200 when the model is pulled, 503 otherwise. |
| `GET /model-info` | `X-Service-Token` | Name, digest, quantization, prompt version + sha, schema version, decoding options. |

## What the pipeline does, in order

1. Validate the request against the request schema (400 with paths, never values).
2. Strip the per-call random delimiter from the text (AI-21), normalise Arabic-Indic digits, scrub
   IBAN / card / phone patterns (AI-31), cap at 4,000 characters (AI-22).
3. Run the injection heuristic (AI-25) — a signal only.
4. Frame: a static system prompt (never contains customer text) and a user turn with the AI-30
   projection followed by the text inside `<DATA_…>` tags.
5. Call Ollama with `temperature 0`, `top_p 1`, fixed seed, capped `num_predict`, thinking off, and
   `format` = the response schema so decoding is grammar-constrained.
6. Validate the output; on failure retry once with the validator's paths; on a second failure return
   the `unclassified` placeholder with `X-Ai-Validation-Status: schema_invalid`.
7. Apply the threshold (below → `unclassified` / `below_confidence_threshold`) and the AI-40 review
   rules; re-validate; stamp `schema_version` and `model`.
8. Log one JSON line: request id, latency, validation status, confidence bucket, counts — no text.

## Evaluation

```
AI_TIMEOUT_SECONDS=180 .venv/bin/python -m evaluations.evaluate --report ../../docs/decisions/0006-ai-evaluation-<date>-qwen3-4b-classify-v1.md --hardware "<what you ran it on>"
```

Re-run it for every prompt or model change and commit the report (AI-111, AI-112).
