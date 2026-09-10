using Npgsql;

namespace FinanceAi.Infrastructure.Configuration;

/// <summary>The least-privilege database roles of SEC-100. Each has its own connection string.</summary>
public enum DatabaseRole
{
    /// <summary>Administrative bootstrap connection. Creates roles and extensions only.</summary>
    Admin,

    /// <summary>Owns the schema and performs DDL. Held only by the migration job (SEC-21).</summary>
    Migrator,

    /// <summary>The application. DML only, no BYPASSRLS, not the table owner (DM-03).</summary>
    App,

    /// <summary>SELECT only, still RLS-bound.</summary>
    Reporting,
}

/// <summary>
/// Builds connection strings from the environment. No credential is ever compiled in, defaulted,
/// or logged (SEC-67); a missing variable is a startup failure, never a silent fallback.
/// </summary>
public static class PostgresConnections
{
    public const string AppRoleName = "finance_app";
    public const string MigratorRoleName = "finance_migrator";
    public const string ReportingRoleName = "finance_reporting";

    public static string For(DatabaseRole role, string? databaseOverride = null)
    {
        var (user, password) = role switch
        {
            DatabaseRole.Admin => (Require("POSTGRES_USER"), Require("POSTGRES_PASSWORD")),
            DatabaseRole.Migrator => (MigratorRoleName, Require("POSTGRES_MIGRATOR_PASSWORD")),
            DatabaseRole.App => (AppRoleName, Require("POSTGRES_APP_PASSWORD")),
            DatabaseRole.Reporting => (ReportingRoleName, Require("POSTGRES_REPORTING_PASSWORD")),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Require("POSTGRES_HOST"),
            Port = int.Parse(Require("POSTGRES_PORT")),
            Database = databaseOverride ?? Require("POSTGRES_DB"),
            Username = user,
            Password = password,

            // The tenant GUC is set with is_local => true inside a transaction, so it cannot
            // survive a return to the pool (SEC-23). Pooling is safe, and AC-31 proves it.
            Pooling = true,
            MaxPoolSize = 20,
            IncludeErrorDetail = false,
        };

        return builder.ConnectionString;
    }

    public static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Required environment variable '{name}' is not set. " +
                "Copy .env.example to .env for local development; inject it at deploy time otherwise.");
}
