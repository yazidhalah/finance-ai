using FinanceAi.Infrastructure.Configuration;
using FinanceAi.Infrastructure.Database;

// The migration job (SEC-21). Runs as its own database role, from its own connection string, and is
// the only thing in the system permitted to perform DDL.
//
//   dotnet run --project apps/api/FinanceAi.Migrator -- bootstrap   create roles and extensions (admin)
//   dotnet run --project apps/api/FinanceAi.Migrator -- migrate     apply pending migrations (migrator)
//   dotnet run --project apps/api/FinanceAi.Migrator -- up          both, in order
//   dotnet run --project apps/api/FinanceAi.Migrator -- rotate-mfa-kek   re-seal every TOTP secret under MFA_KEK_BASE64 (slice 17)
DotEnv.Load();

var command = args.Length > 0 ? args[0] : "up";
var runner = new MigrationRunner(Console.Out);

try
{
    switch (command)
    {
        case "bootstrap":
            await runner.BootstrapAsync(PostgresConnections.For(DatabaseRole.Admin));
            break;

        case "migrate":
            Report(await runner.MigrateAsync(PostgresConnections.For(DatabaseRole.Migrator)));
            break;

        case "up":
            await runner.BootstrapAsync(PostgresConnections.For(DatabaseRole.Admin));
            Report(await runner.MigrateAsync(PostgresConnections.For(DatabaseRole.Migrator)));
            break;

        case "rotate-mfa-kek":
            var outcome = await FinanceAi.Infrastructure.Security.KekRotation.RunAsync(PostgresConnections.For(DatabaseRole.Migrator), new FinanceAi.Infrastructure.Security.AesGcmSecretBox());
            Console.WriteLine($"MFA KEK rotation: {outcome.Resealed} re-sealed, {outcome.AlreadyCurrent} already under the current key. MFA_KEK_BASE64_PREVIOUS can be removed.");
            break;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Expected: bootstrap | migrate | up | rotate-mfa-kek.");
            return 2;
    }

    return 0;
}
catch (Exception ex)
{
    // The message only: a connection string or a role password must never reach the console (SEC-67).
    Console.Error.WriteLine($"Migration failed: {ex.Message}");
    return 1;
}

static void Report(IReadOnlyList<string> applied) =>
    Console.WriteLine(applied.Count == 0
        ? "No pending migrations."
        : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}");
