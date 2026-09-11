#!/usr/bin/env bash
# Brings the `full` profile up in explicit stages with `--no-deps`, waiting on each stage from the outside.
# Podman 4.x (the GitHub runner, Ubuntu 24.04) cannot start a chain of `--requires` dependencies that
# podman-compose derives from `depends_on` ("depends on container … not found in input list"); Podman 5 can, and
# `stack.sh --profile full up -d` is the normal path. CI uses this; so can an operator on an older Podman.
#   infrastructure/stack-staged.sh [extra compose args, e.g. -f infrastructure/compose.ci.yml] [-- --build]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_args=(); up_args=()
while [ $# -gt 0 ]; do
  if [ "$1" = "--" ]; then shift; up_args=("$@"); break; fi
  compose_args+=("$1"); shift
done
stack() { "$ROOT/infrastructure/stack.sh" "${compose_args[@]}" --profile full "$@"; }
project="$(basename "$ROOT/infrastructure")"    # podman-compose names containers <dir>_<service>_1

build=0
for a in "${up_args[@]:-}"; do [ "$a" = "--build" ] && build=1; done
if [ "$build" = 1 ]; then
  echo "stage 0: build every image"
  stack build
fi

echo "stage 1: postgres, mailpit, ai"
stack up -d --no-deps --no-build postgres mailpit ai
for i in $(seq 1 60); do
  podman exec "${project}_postgres_1" pg_isready -q -U "${POSTGRES_USER:-finance}" && break
  [ "$i" = 60 ] && { echo "postgres never became ready" >&2; exit 1; }
  sleep 2
done

echo "stage 2: migrate (one-shot)"
stack up -d --no-deps --no-build migrate
rc="$(podman wait "${project}_migrate_1")"
podman logs "${project}_migrate_1" 2>&1 | tail -20
[ "$rc" = "0" ] || { echo "migrate exited $rc" >&2; exit 1; }

echo "stage 3: api, web"
stack up -d --no-deps --no-build api web
podman ps --format '{{.Names}} {{.Status}} {{.Ports}}'
