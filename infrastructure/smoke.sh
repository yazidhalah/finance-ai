#!/usr/bin/env bash
# Stack smoke test (slice 14): the published web port answers, the SPA is served with its security headers (SEC-61),
# /api reaches the API and the API reaches PostgreSQL and the AI service, and nothing but `web` publishes a public
# port (SEC-69). Runs against a stack started by infrastructure/stack.sh; needs curl and podman.
#   infrastructure/smoke.sh [base-url]        default http://localhost:${WEB_PORT:-8080}
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
. "$ROOT/infrastructure/load-env.sh" "$ROOT/.env"
BASE="${1:-http://localhost:${WEB_PORT:-8080}}"
fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }

echo "1. waiting for $BASE/healthz"
for i in $(seq 1 90); do
  if curl -fsS -o /dev/null "$BASE/healthz"; then break; fi
  [ "$i" = 90 ] && fail "web never became healthy"
  sleep 2
done

echo "2. the SPA and its headers"
headers="$(curl -fsS -D - -o /dev/null "$BASE/")"
for h in "content-security-policy:" "x-content-type-options: nosniff" "x-frame-options: deny" "referrer-policy: strict-origin-when-cross-origin"; do
  echo "$headers" | tr -d '\r' | grep -qi "^$h" || fail "missing header $h"
done
echo "$headers" | tr -d '\r' | grep -i "^content-security-policy:" | grep -q "unsafe-inline" && fail "CSP allows unsafe-inline"
curl -fsS "$BASE/" | grep -q '<div id="root">' || fail "index.html is not the SPA"
curl -fsS "$BASE/customers" | grep -q '<div id="root">' || fail "SPA fallback for deep links"

echo "3. /api through nginx to the API and the database"
for i in $(seq 1 60); do
  code="$(curl -s -o /dev/null -w '%{http_code}' -H 'content-type: application/json' -d '{"email":"x","password":"x"}' "$BASE/api/v1/auth/login")"
  [ "$code" = "400" ] || [ "$code" = "401" ] && break
  [ "$i" = 60 ] && fail "login probe answered $code"
  sleep 2
done
stamp="$(date +%s)"
email="smoke-$stamp@example.test"
password='Correct-Horse-Battery-9'
curl -fsS -o /dev/null -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"$password\",\"fullName\":\"Smoke Owner\",\"organizationName\":\"Smoke $stamp\",\"baseCurrency\":\"JOD\",\"timezone\":\"Asia/Amman\",\"locale\":\"en-JO\"}" "$BASE/api/v1/auth/register" || fail "register"
# Slice 24: the address must be verified first — the link is in Mailpit (the stack's SMTP host, 127.0.0.1:8025 in dev/CI).
MAILPIT="${MAILPIT_URL:-http://127.0.0.1:8025}"
token=""
for i in $(seq 1 40); do
  token="$(curl -fsS "$MAILPIT/api/v1/search?query=$(python3 -c "import urllib.parse,sys;print(urllib.parse.quote('to:'+sys.argv[1]))" "$email")" | python3 -c '
import json,sys,re,urllib.request
s=json.load(sys.stdin); base=sys.argv[1]
for m in s.get("messages",[]):
    t=json.load(urllib.request.urlopen(base+"/api/v1/message/"+m["ID"])).get("Text","")
    x=re.search(r"verify-email\?token=([0-9a-f]{64})",t)
    if x: print(x.group(1)); break' "$MAILPIT")"
  [ -n "$token" ] && break
  sleep 1
done
[ -n "$token" ] || fail "no verification mail in Mailpit"
curl -fsS -o /dev/null -H 'content-type: application/json' -d "{\"token\":\"$token\"}" "$BASE/api/v1/auth/verify-email" || fail "verify-email"
token="$(curl -fsS -H 'content-type: application/json' -d "{\"email\":\"$email\",\"password\":\"$password\"}" "$BASE/api/v1/auth/login" | python3 -c 'import json,sys; print(json.load(sys.stdin)["accessToken"])')" || fail "login"
[ -n "$token" ] || fail "no access token"
curl -fsS -H "authorization: Bearer $token" "$BASE/api/v1/organization" | grep -q "Smoke $stamp" || fail "organization read through the proxy"

echo "4. the API reaches the AI service"
curl -fsS -H "authorization: Bearer $token" "$BASE/api/v1/ai/health" | tee /dev/stderr | grep -q '"reachable":true' || fail "AI service unreachable from the API"

echo "5. SEC-69: published ports"
published="$(podman ps --format '{{.Names}} {{.Ports}}' | grep -v '^$' || true)"
echo "$published"
echo "$published" | grep -E "_postgres_" | grep -vE "127\.0\.0\.1:[0-9]+->5432" | grep -q -- "->" && fail "PostgreSQL publishes a non-loopback port"
[ "$(echo "$published" | grep -E "_(api|ai|ollama)_" | grep -c -- '->' || true)" = "0" ] || fail "api/ai/ollama publish a port"

echo "SMOKE OK"
