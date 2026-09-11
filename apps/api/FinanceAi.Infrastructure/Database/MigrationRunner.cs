using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FinanceAi.Infrastructure.Configuration;
using Npgsql;

namespace FinanceAi.Infrastructure.Database;

/// <summary>
/// Applies the forward-only SQL migrations in <c>database/migrations</c> (DM-34).
/// <para>
/// Raw SQL rather than EF Core migrations, deliberately: this slice's migrations must express
/// <c>FORCE ROW LEVEL SECURITY</c>, policies, partial unique indexes, composite foreign keys and
/// an append-only trigger. Those are the security controls, and they should be reviewable as the
/// exact DDL that runs — not as generated output.
/// </para>
/// Scripts are embedded in this assembly so the migrator, the API and the test fixture all apply
/// byte-identical SQL.
/// </summary>
public sealed class MigrationRunner(TextWriter? log = null)
{
    private readonly TextWriter log = log ?? TextWriter.Null;

    /// <summary>
    /// Creates the least-privilege roles and extensions. Requires an administrative connection,
    /// and is the only step that has one (SEC-21).
    /// </summary>
    public async Task BootstrapAsync(string adminConnectionString, CancellationToken ct = default)
    {
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_password"] = PostgresConnections.Require("POSTGRES_APP_PASSWORD"),
            ["migrator_password"] = PostgresConnections.Require("POSTGRES_MIGRATOR_PASSWORD"),
            ["reporting_password"] = PostgresConnections.Require("POSTGRES_REPORTING_PASSWORD"),
        };

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(ct);

        // Roles are cluster-wide. Two bootstraps at once — two test assemblies, two deploys — would
        // race on pg_authid ("tuple concurrently updated"). One transaction under one cluster-wide
        // advisory lock makes the bootstrap serial and idempotent.
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(0x46494E41, 0x424F4F54)", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        foreach (var (name, sql) in SqlScripts.Bootstrap)
        {
            var rendered = Substitute(sql, passwords);
            await using var command = new NpgsqlCommand(rendered, connection, transaction);
            await command.ExecuteNonQueryAsync(ct);
            this.log.WriteLine($"bootstrap: applied {name}");
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Applies every not-yet-applied migration, each in its own transaction, in file-name order.
    /// A migration whose content changed after it was applied is a hard failure: forward-only
    /// means the file that ran is the file in the repository.
    /// </summary>
    public async Task<IReadOnlyList<string>> MigrateAsync(
        string migratorConnectionString, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(ct);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version    text PRIMARY KEY,
              checksum   text NOT NULL,
              applied_at timestamptz NOT NULL DEFAULT now(),
              applied_by text NOT NULL DEFAULT current_user
            );
            """, ct);

        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var read = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations", connection))
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                applied[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var newlyApplied = new List<string>();

        foreach (var (version, sql) in SqlScripts.Migrations)
        {
            var checksum = Checksum(sql);

            if (applied.TryGetValue(version, out var recorded))
            {
                if (recorded != checksum)
                {
                    throw new InvalidOperationException(
                        $"Migration '{version}' was modified after it was applied " +
                        $"(recorded {recorded}, found {checksum}). Migrations are forward-only (DM-34): " +
                        "add a new migration instead of editing an applied one.");
                }

                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(ct);

            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(ct);
            }

            await using (var record = new NpgsqlCommand(
                "INSERT INTO schema_migrations (version, checksum) VALUES (@v, @c)", connection, transaction))
            {
                record.Parameters.AddWithValue("v", version);
                record.Parameters.AddWithValue("c", checksum);
                await record.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            newlyApplied.Add(version);
            this.log.WriteLine($"migrate: applied {version}");
        }

        return newlyApplied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Checksum(string sql)
    {
        // Normalize line endings so a checkout on another platform is not a false tamper alarm.
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>
    /// Replaces <c>${name}</c> with a correctly escaped PostgreSQL string literal. Values come
    /// from the operator's environment, never from a request; escaping is belt-and-braces so a
    /// password containing a quote cannot alter the statement.
    /// </summary>
    private static string Substitute(string sql, IReadOnlyDictionary<string, string> values)
    {
        var result = new StringBuilder(sql);
        foreach (var (key, value) in values)
        {
            result.Replace($"${{{key}}}", QuoteLiteral(value));
        }

        var remaining = result.ToString();
        if (remaining.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Bootstrap script has an unsubstituted ${...} placeholder.");
        }

        return remaining;
    }

    private static string QuoteLiteral(string value)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Value contains a NUL character.", nameof(value));
        }

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}

/// <summary>The SQL shipped inside this assembly, in application order.</summary>
public static class SqlScripts
{
    private const string BootstrapPrefix = "FinanceAi.Infrastructure.Sql.bootstrap.";
    private const string MigrationsPrefix = "FinanceAi.Infrastructure.Sql.migrations.";

    public static IReadOnlyList<(string Name, string Sql)> Bootstrap { get; } = Load(BootstrapPrefix);

    public static IReadOnlyList<(string Version, string Sql)> Migrations { get; } = Load(MigrationsPrefix);

    private static IReadOnlyList<(string, string)> Load(string prefix)
    {
        var assembly = Assembly.GetExecutingAssembly();

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) &&
                        n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n[prefix.Length..^4], reader.ReadToEnd());
            })
            .ToList();
    }
}
