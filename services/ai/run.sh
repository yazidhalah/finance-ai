#!/usr/bin/env bash
# Local runner: source the repo .env (AI_SERVICE_TOKEN, OLLAMA_URL, AI_MODEL) then serve on 127.0.0.1 only (AI-101).
set -euo pipefail
cd "$(dirname "$0")"
if [ -f ../../.env ]; then set -a; . ../../.env; set +a; fi
exec .venv/bin/uvicorn --factory app.main:app_from_env --host 127.0.0.1 --port "${AI_SERVICE_PORT:-8090}"
