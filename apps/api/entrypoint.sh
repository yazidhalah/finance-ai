#!/bin/sh
# `migrate` runs the forward-only migrations and exits; anything else starts the API.
# The content root must be the API's own directory, or appsettings.json (the log-level filters) is not read.
set -eu
if [ "${1:-}" = "migrate" ]; then
  cd /app/migrator
  exec dotnet FinanceAi.Migrator.dll up
fi
cd /app/api
exec dotnet FinanceAi.Api.dll "$@"
