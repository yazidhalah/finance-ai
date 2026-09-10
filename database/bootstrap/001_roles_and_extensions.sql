-- Bootstrap: cluster/database objects that require an administrative connection.
-- Run by `dotnet run --project apps/api/FinanceAi.Migrator -- bootstrap`, which connects with
-- the POSTGRES_* administrative credentials from the environment. Migrations themselves never
-- run with these credentials (SEC-21).
--
-- Dollar-brace placeholders are substituted by the migrator with a correctly escaped SQL literal,
-- read from environment variables. No credential is ever written into this file (SEC-67):
--   app_password        <- POSTGRES_APP_PASSWORD
--   migrator_password   <- POSTGRES_MIGRATOR_PASSWORD
--   reporting_password  <- POSTGRES_REPORTING_PASSWORD

-- citext gives case-insensitive email comparison in the database rather than in every query.
CREATE EXTENSION IF NOT EXISTS citext;

-- Least-privilege roles (SEC-100). Three separate roles, three separate connection strings.
DO $$
BEGIN
  -- migrator: owns the schema and performs DDL. Held only by the migration job.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'finance_migrator') THEN
    EXECUTE 'CREATE ROLE finance_migrator LOGIN PASSWORD ' || quote_literal(${migrator_password});
  ELSE
    EXECUTE 'ALTER ROLE finance_migrator LOGIN PASSWORD ' || quote_literal(${migrator_password});
  END IF;

  -- app: the application. DML only. No DDL, no BYPASSRLS, and NOT the table owner (DM-03),
  -- so FORCE ROW LEVEL SECURITY genuinely binds it.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'finance_app') THEN
    EXECUTE 'CREATE ROLE finance_app LOGIN PASSWORD ' || quote_literal(${app_password});
  ELSE
    EXECUTE 'ALTER ROLE finance_app LOGIN PASSWORD ' || quote_literal(${app_password});
  END IF;

  -- readonly_reporting: SELECT only, still RLS-bound (SEC-100).
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'finance_reporting') THEN
    EXECUTE 'CREATE ROLE finance_reporting LOGIN PASSWORD ' || quote_literal(${reporting_password});
  ELSE
    EXECUTE 'ALTER ROLE finance_reporting LOGIN PASSWORD ' || quote_literal(${reporting_password});
  END IF;
END
$$;

-- None of these may bypass row-level security. Asserted by AC-34.
ALTER ROLE finance_app        NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT;
ALTER ROLE finance_migrator   NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
ALTER ROLE finance_reporting  NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOINHERIT;

-- Interactive requests get a bounded statement timeout (doc 04 §7).
ALTER ROLE finance_app SET statement_timeout = '5s';
ALTER ROLE finance_reporting SET statement_timeout = '30s';

-- The migrator owns the schema; the application only uses it.
ALTER SCHEMA public OWNER TO finance_migrator;
GRANT USAGE ON SCHEMA public TO finance_app, finance_reporting;

-- Revoke the PostgreSQL default that lets any role create objects in public.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT CREATE ON SCHEMA public TO finance_migrator;
