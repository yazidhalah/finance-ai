#!/bin/sh
# `migrate` runs the forward-only migrations and exits; `rotate-mfa-kek` re-seals the TOTP secrets under the current
# key (slice 17); anything else starts the API.
# The content root must be the API's own directory, or appsettings.json (the log-level filters) is not read.
set -eu
case "${1:-}" in
  migrate)        cd /app/migrator; exec dotnet FinanceAi.Migrator.dll up ;;
  rotate-mfa-kek) cd /app/migrator; exec dotnet FinanceAi.Migrator.dll rotate-mfa-kek ;;
esac
cd /app/api
exec dotnet FinanceAi.Api.dll "$@"
