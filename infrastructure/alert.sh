#!/usr/bin/env bash
# The alert path from shell (SEC-102, slice 15): the same JSON envelope the API posts, from the scripts that run
# outside it — backup.sh and restore-drill.sh call this on failure. Posts to ALERT_WEBHOOK_URL when it is set
# (with ALERT_WEBHOOK_TOKEN as a bearer when that is set), always prints to stderr, and exits 0 either way: an
# alert that cannot be delivered must not mask the failure that raised it.
#   infrastructure/alert.sh <kind> <summary>
#   kinds: backup_failed | restore_drill_failed
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ -f "$ROOT/.env" ]; then set -a; . "$ROOT/.env"; set +a; fi
kind="${1:?kind}"; summary="${2:?summary}"
now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "ALERT [$kind] $now: $summary" >&2
if [ -z "${ALERT_WEBHOOK_URL:-}" ]; then
  echo "alert.sh: ALERT_WEBHOOK_URL is not set; the alert is on stderr only" >&2
  exit 0
fi
payload="$(python3 - "$kind" "$summary" "$now" "$(hostname)" <<'PY'
import json, sys
kind, summary, now, host = sys.argv[1:5]
print(json.dumps({"alertId": None, "kind": kind, "severity": "critical", "tenantId": None, "tenantName": None,
                  "summary": summary, "details": {"host": host, "source": "infrastructure/alert.sh"}, "raisedAt": now}))
PY
)"
auth=()
[ -n "${ALERT_WEBHOOK_TOKEN:-}" ] && auth=(-H "Authorization: Bearer $ALERT_WEBHOOK_TOKEN")
if curl -fsS -m 5 -X POST -H 'content-type: application/json' "${auth[@]}" --data "$payload" "$ALERT_WEBHOOK_URL" -o /dev/null; then
  echo "alert.sh: delivered to the webhook" >&2
else
  echo "alert.sh: the webhook did not accept the alert" >&2
fi
exit 0
