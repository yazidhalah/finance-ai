#!/usr/bin/env bash
# Backup (PRD-23): a pg_dump of the whole database in custom format, using the admin credentials in .env.
#   infrastructure/backup.sh [output-file]
# Writes backups/<db>-<UTC timestamp>.dump by default and prints the path. Custom format restores with pg_restore
# and carries every table, policy, function and trigger the migrations created — the restore drill relies on that.
set -euo pipefail
cd "$(dirname "$0")/.."
. infrastructure/load-env.sh ./.env
trap 'rc=$?; if [ $rc -ne 0 ]; then infrastructure/alert.sh backup_failed "pg_dump of ${POSTGRES_DB:-?} failed (exit $rc)"; fi' EXIT   # SEC-102
: "${POSTGRES_HOST:=127.0.0.1}" "${POSTGRES_PORT:=5432}"
out="${1:-backups/${POSTGRES_DB}-$(date -u +%Y%m%dT%H%M%SZ).dump}"
mkdir -p "$(dirname "$out")"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc --no-owner -f "$out"
echo "$out"
