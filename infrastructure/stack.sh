#!/usr/bin/env bash
# The one-line stack (A-15, slice 14). Runs Podman Compose against infrastructure/compose.yml with the repo `.env`
# as the interpolation and secret source, from any working directory:
#
#   infrastructure/stack.sh up -d                       # dev: PostgreSQL + Mailpit
#   infrastructure/stack.sh --profile full up -d --build  # everything, images built from this clone
#   infrastructure/stack.sh --profile full logs -f api
#   infrastructure/stack.sh --profile full down          # add -v to drop the volumes (data!)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ ! -f "$ROOT/.env" ]; then
  echo "No $ROOT/.env — copy .env.example and fill in the secrets first (docs/ops/runbook.md §1)." >&2
  exit 1
fi
exec podman compose --env-file "$ROOT/.env" -f "$ROOT/infrastructure/compose.yml" "$@"
