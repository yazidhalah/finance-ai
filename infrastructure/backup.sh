#!/usr/bin/env bash
# Backup (PRD-23, SEC-94): a pg_dump of the whole database in custom format, encrypted at rest with AES-256-CBC
# (PBKDF2, 600k iterations) under BACKUP_PASSPHRASE from .env — a backup holds every tenant's data.
#   infrastructure/backup.sh [output-file]
# Writes backups/<db>-<UTC timestamp>.dump.enc by default and prints the path. The drill and a real restore decrypt
# with the same passphrase (restore-drill.sh; runbook §5). Custom format carries every table, policy, function and
# trigger the migrations created — the restore drill relies on that. Refuses to run without the passphrase: an
# unencrypted backup is not a backup this product makes.
set -euo pipefail
cd "$(dirname "$0")/.."
. infrastructure/load-env.sh ./.env
trap 'rc=$?; if [ $rc -ne 0 ]; then infrastructure/alert.sh backup_failed "pg_dump of ${POSTGRES_DB:-?} failed (exit $rc)"; fi' EXIT   # SEC-102
: "${POSTGRES_HOST:=127.0.0.1}" "${POSTGRES_PORT:=5432}"
[ -n "${BACKUP_PASSPHRASE:-}" ] || { echo "BACKUP_PASSPHRASE is not set (runbook §1): refusing to write an unencrypted backup (SEC-94)" >&2; exit 1; }
out="${1:-backups/${POSTGRES_DB}-$(date -u +%Y%m%dT%H%M%SZ).dump.enc}"
mkdir -p "$(dirname "$out")"
umask 077
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc --no-owner \
  | openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt -pass env:BACKUP_PASSPHRASE -out "$out"
echo "$out"
