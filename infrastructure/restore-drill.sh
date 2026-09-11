#!/usr/bin/env bash
# Restore drill (doc 09 T-150, PRD-23). Takes a backup, restores it into a fresh database, proves every table
# came back with the same number of rows, reports RPO (age of the backup) and RTO (restore wall time), and drops
# the drill database. Exit status is non-zero on any mismatch. Row counts, not checksums (slice 12 D-5).
#   infrastructure/restore-drill.sh [backup-file]      # without an argument, takes a fresh backup first
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
: "${POSTGRES_HOST:=127.0.0.1}" "${POSTGRES_PORT:=5432}"
export PGPASSWORD="$POSTGRES_PASSWORD"
psql_admin() { psql -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -v ON_ERROR_STOP=1 -Atq "$@"; }

dump="${1:-$(infrastructure/backup.sh)}"
taken=$(stat -c %Y "$dump")
drill="${POSTGRES_DB}_drill_$(date -u +%H%M%S)"
echo "backup: $dump ($(( ( $(date +%s) - taken ) ))s old — that is the RPO of this drill)"

psql_admin -d postgres -c "DROP DATABASE IF EXISTS \"$drill\""
psql_admin -d postgres -c "CREATE DATABASE \"$drill\" TEMPLATE template0 ENCODING 'UTF8'"
trap 'rc=$?; psql_admin -d postgres -c "DROP DATABASE IF EXISTS \"$drill\"" >/dev/null; if [ $rc -ne 0 ]; then infrastructure/alert.sh restore_drill_failed "restore drill of ${dump:-?} failed (exit $rc)"; fi' EXIT   # SEC-102

start=$(date +%s)
# The roles (finance_app, finance_migrator, finance_reporting) already exist on the server; --no-owner keeps the
# restore independent of who dumped it. Grants and policies reference the roles by name and come back intact.
# Streamed through psql with ON_ERROR_STOP so any failed statement fails the drill. A newer client's
# "SET transaction_timeout" (PostgreSQL 17+) is dropped for a 16 server; nothing else is filtered.
pg_restore --no-owner -f - "$dump" | grep -v '^SET transaction_timeout' | psql -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$drill" -v ON_ERROR_STOP=1 -q
rto=$(( $(date +%s) - start ))
echo "restore: ${rto}s into $drill (RTO of this drill)"

# Every business table (the ones under forced RLS) plus the platform tables: counts must agree.
tables=$(psql_admin -d "$POSTGRES_DB" -c "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY 1")
mismatch=0
for t in $tables; do
  a=$(psql_admin -d "$POSTGRES_DB" -c "SELECT count(*) FROM \"$t\"")
  b=$(psql_admin -d "$drill" -c "SELECT count(*) FROM \"$t\"")
  if [ "$a" != "$b" ]; then echo "MISMATCH $t: source $a restored $b"; mismatch=1; else echo "ok $t: $a"; fi
done
rls=$(psql_admin -d "$drill" -c "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND rowsecurity")
policies=$(psql_admin -d "$drill" -c "SELECT count(*) FROM pg_policies WHERE schemaname = 'public'")
migrations=$(psql_admin -d "$drill" -c "SELECT count(*) FROM schema_migrations")
echo "restored: $rls tables with RLS, $policies policies, $migrations migrations recorded"
[ "$rls" -gt 0 ] && [ "$policies" -gt 0 ] || { echo "RLS or policies missing after restore"; mismatch=1; }
if [ "$mismatch" -ne 0 ]; then echo "RESTORE DRILL FAILED"; exit 1; fi
echo "RESTORE DRILL PASSED"
