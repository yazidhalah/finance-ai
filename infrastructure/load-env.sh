#!/usr/bin/env bash
# Sourced by the operator scripts: loads the repo .env without overriding variables already in the environment —
# the same precedence as the API's loader, so CI can inject ALERT_WEBHOOK_URL (or anything) over an empty .env line.
#   . "$(dirname "$0")/load-env.sh" [path-to-.env]
load_env() {
  local file="${1:-.env}" line key value
  [ -f "$file" ] || return 0
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|'#'*) continue ;; esac
    key="${line%%=*}"; value="${line#*=}"
    case "$key" in *[!A-Za-z0-9_]*) continue ;; esac
    if [ -z "${!key+x}" ]; then export "$key=$value"; fi
  done < "$file"
}
load_env "${1:-.env}"
