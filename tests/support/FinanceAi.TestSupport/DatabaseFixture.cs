using FinanceAi.Infrastructure.Configuration;
using FinanceAi.Infrastructure.Database;
using Npgsql;

namespace FinanceAi.TestSupport;

/// <summary>
/// Provisions a throwaway PostgreSQL database per test assembly, applies the <b>real</b> migrations
/// to it, and drops it afterwards.
/// <para>
/// T-03 requires tests to run against real PostgreSQL, never SQLite or an in-memory provider: row
/// level security, forced RLS, composite foreign keys, <c>citext</c> and partial unique indexes do
/// not exist in a fake database, and those are precisely the controls this slice is made of.
/// </para>
/// <para>
/// Deviation D-1: doc 09 names Testcontainers. This environment has no Docker socket, so the local
/// instance from <c>.env</c> is used instead. Everything that matters — real PostgreSQL, real
/// migrations, a database no other test run shares — is unchanged, and swapping the provisioning in
/// this one file is all that adopting Testcontainers would take.
/// </para>
/// </summary>
public sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly string adminConnectionString;

    private DatabaseFixture(string databaseName, string adminConnectionString)
    {
        this.DatabaseName = databaseName;
        this.adminConnectionString = adminConnectionString;
    }

    public string DatabaseName { get; }

    /// <summary>The application's own connection: DML only, no BYPASSRLS, not the table owner.</summary>
    public string AppConnectionString => PostgresConnections.For(DatabaseRole.App, this.DatabaseName);

    /// <summary>Owns the schema. Used by tests that need to inspect or deliberately bypass a layer.</summary>
    public string MigratorConnectionString => PostgresConnections.For(DatabaseRole.Migrator, this.DatabaseName);

    /// <summary>Superuser. Used only to simulate an attacker who already has database access.</summary>
    public string AdminConnectionString => PostgresConnections.For(DatabaseRole.Admin, this.DatabaseName);

    public static async Task<DatabaseFixture> CreateAsync(string label)
    {
        DotEnv.Load(AppContext.BaseDirectory);

        var databaseName = $"finance_ai_test_{label}_{Guid.NewGuid():N}"[..Math.Min(60, $"finance_ai_test_{label}_{Guid.NewGuid():N}".Length)];
        var maintenanceConnectionString = PostgresConnections.For(DatabaseRole.Admin, "postgres");

        await using (var connection = new NpgsqlConnection(maintenanceConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        var fixture = new DatabaseFixture(databaseName, PostgresConnections.For(DatabaseRole.Admin, databaseName));

        var runner = new MigrationRunner();
        await runner.BootstrapAsync(fixture.adminConnectionString);
        await runner.MigrateAsync(fixture.MigratorConnectionString);

        // The API host and anything else reading the environment now points at this database.
        Environment.SetEnvironmentVariable("POSTGRES_DB", databaseName);

        return fixture;
    }

    public NpgsqlConnection OpenApp() => Open(this.AppConnectionString);

    public NpgsqlConnection OpenMigrator() => Open(this.MigratorConnectionString);

    public NpgsqlConnection OpenAdmin() => Open(this.AdminConnectionString);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        var maintenanceConnectionString = PostgresConnections.For(DatabaseRole.Admin, "postgres");

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync();

        await using (var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()",
            connection))
        {
            terminate.Parameters.AddWithValue("name", this.DatabaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{this.DatabaseName}\"", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static NpgsqlConnection Open(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }
}
