using FinanceAi.Infrastructure.Configuration;
using FinanceAi.Infrastructure.Database;

// The migration job (SEC-21). Runs as its own database role, from its own connection string, and is
// the only thing in the system permitted to perform DDL.
//
//   dotnet run --project apps/api/FinanceAi.Migrator -- bootstrap   create roles and extensions (admin)
//   dotnet run --project apps/api/FinanceAi.Migrator -- migrate     apply pending migrations (migrator)
//   dotnet run --project apps/api/FinanceAi.Migrator -- up          both, in order
//   dotnet run --project apps/api/FinanceAi.Migrator -- rotate-mfa-kek   re-seal every TOTP secret under MFA_KEK_BASE64 (slice 17)
//   dotnet run --project apps/api/FinanceAi.Migrator -- review-pack [path]   the native-speaker template review pack (slice 21; no database)
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

        case "review-pack":
            var target = args.Length > 1 ? args[1] : "docs/review/arabic-review-pack.md";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await File.WriteAllTextAsync(target, FinanceAi.Infrastructure.Messaging.ReviewPack.Render(DateOnly.FromDateTime(DateTime.UtcNow)));
            Console.WriteLine($"Review pack written to {target} ({FinanceAi.Domain.Entities.SystemTemplates.All.Count} templates).");
            break;

        case "corpus-proposals":
            // T-109: corpus-proposals --tenant <id> --consent <reference> --out <path.csv> [--since yyyy-MM-dd]
            var options = ParseOptions(args.Skip(1));
            if (!options.TryGetValue("tenant", out var tenantText) || !Guid.TryParse(tenantText, out var tenantId) || !options.TryGetValue("consent", out var consent) || !options.TryGetValue("out", out var outPath))
            {
                Console.Error.WriteLine("Usage: corpus-proposals --tenant <tenant id> --consent <written-consent reference> --out <path.csv> [--since yyyy-MM-dd]");
                return 2;
            }

            DateOnly? since = options.TryGetValue("since", out var sinceText) ? DateOnly.ParseExact(sinceText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
            var (csv, summary) = await FinanceAi.Infrastructure.Ai.CorpusProposals.RenderAsync(PostgresConnections.For(DatabaseRole.Migrator), tenantId, consent, since);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await File.WriteAllTextAsync(outPath, csv);
            Console.WriteLine($"Corpus proposals written to {outPath}: {summary.Proposed} proposed, {summary.AwaitingLabel} rejected and still unlabelled, {summary.Skipped} skipped as too long. Next: services/ai/evaluations/import_corpus.py (redaction, readiness).");
            break;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Expected: bootstrap | migrate | up | rotate-mfa-kek | review-pack | corpus-proposals.");
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

static Dictionary<string, string> ParseOptions(IEnumerable<string> arguments)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    string? key = null;
    foreach (var argument in arguments)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal)) { key = argument[2..]; options[key] = string.Empty; }
        else if (key is not null) { options[key] = argument; key = null; }
    }

    return options;
}

static void Report(IReadOnlyList<string> applied) =>
    Console.WriteLine(applied.Count == 0
        ? "No pending migrations."
        : $"Applied {applied.Count} migration(s): {string.Join(", ", applied)}");
