using System.Reflection;
using System.Text.RegularExpressions;
using FinanceAi.Domain.Authorization;
using FinanceAi.TestSupport;

namespace FinanceAi.UnitTests.SourceRules;

/// <summary>
/// Static checks over the source tree. Some rules cannot be expressed as a behavioural test because
/// the behaviour they forbid does not exist yet — the point is to fail the build the day someone
/// writes it. SEC-20 asks for exactly this ("a grep-based CI check bans ...").
/// </summary>
public sealed class SourceRuleTests
{
    private static readonly string[] ProductionDirectories =
    [
        Path.Combine("apps", "api", "FinanceAi.Api"),
        Path.Combine("apps", "api", "FinanceAi.Domain"),
        Path.Combine("apps", "api", "FinanceAi.Infrastructure"),
        Path.Combine("apps", "api", "FinanceAi.Migrator"),
    ];

    /// <summary>
    /// AC-35 / SEC-20. The tenant comes from the validated token claim and from nowhere else. A
    /// tenant id read out of a header, query string, route value or cookie would defeat all three
    /// isolation layers at once, because they all trust the value the pipeline binds.
    /// </summary>
    [Fact]
    public void NoSourceFile_ReadsTenantIdFromRequestInput()
    {
        var forbidden = new Regex(
            @"(Request\.Headers|Request\.Query|RouteValues|Request\.Cookies|Request\.Form)\s*\[\s*""[^""]*[Tt]enant",
            RegexOptions.None, TimeSpan.FromSeconds(5));

        var offenders = ProductionSourceFiles()
            .Where(file => forbidden.IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "SEC-20: the tenant id must come only from the validated token claim. These files read " +
            "one from request input: " + string.Join(", ", offenders.Select(Relative)));
    }

    /// <summary>
    /// AC-35 / SEC-20. There is also no route that takes a tenant id as a path parameter (API-01);
    /// <c>/auth/switch-tenant</c> takes one in the body, matched against the caller's own membership
    /// list, and is the single documented exception.
    /// </summary>
    [Fact]
    public void NoRoute_TakesATenantIdParameter()
    {
        // A route pattern is a single-line string literal, so the match must not span lines.
        var forbidden = new Regex(@"""[^""\r\n]*\{[ ]*tenantId", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        var offenders = ProductionSourceFiles()
            .Where(file => forbidden.IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "API-01: no endpoint may take a tenant id as a route parameter. Offenders: " +
            string.Join(", ", offenders.Select(Relative)));
    }

    /// <summary>
    /// AC-22 / SEC-12. Authorization checks a permission, never a role name. Role checks scattered
    /// through handlers are how a permission matrix silently stops being the source of truth.
    /// </summary>
    [Fact]
    public void NoEndpoint_AuthorizesOnRoleName()
    {
        var forbidden = new Regex(
            @"(==|!=)\s*TenantRole\.|Role\s*(==|!=)\s*""|""(Owner|Admin|Accountant|Collector|Viewer)""\s*(==|!=)",
            RegexOptions.None, TimeSpan.FromSeconds(5));

        var apiDirectory = Path.Combine(RepositoryPaths.Root, "apps", "api", "FinanceAi.Api");

        var offenders = Directory.EnumerateFiles(apiDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .Where(file => forbidden.IsMatch(File.ReadAllText(file)))
            .ToList();

        Assert.True(offenders.Count == 0,
            "SEC-12: authorize on a permission, not a role. Offenders: " +
            string.Join(", ", offenders.Select(Relative)));
    }

    /// <summary>
    /// The platform scope is the one documented escape from tenant scope (DM-06). It is only
    /// defensible while it stays in one reviewed file; this test is what keeps it there.
    /// </summary>
    [Fact]
    public void PlatformScope_IsOpenedOnlyByPlatformIdentityStore()
    {
        var allowed = new[] { "DatabaseScope.cs", "PlatformIdentityStore.cs" };

        var offenders = ProductionSourceFiles()
            .Where(file => !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("EnterPlatformAsync", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "DM-06: only PlatformIdentityStore may open a platform scope. Offenders: " +
            string.Join(", ", offenders.Select(Relative)));
    }

    /// <summary>
    /// Bypassing the global query filter is layer 1 turned off. It is legitimate in exactly two
    /// places — the identity flows, and the audit writer's explicit-tenant read — and both are still
    /// covered by RLS. Anywhere else it is a bug waiting to leak.
    /// </summary>
    [Fact]
    public void QueryFilters_AreBypassedOnlyWhereDocumented()
    {
        var allowed = new[] { "PlatformIdentityStore.cs", "AuditWriter.cs" };

        var offenders = ProductionSourceFiles()
            .Where(file => !allowed.Contains(Path.GetFileName(file), StringComparer.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Layer 1 may only be bypassed in the documented identity and audit paths. Offenders: " +
            string.Join(", ", offenders.Select(Relative)));
    }

    /// <summary>
    /// AC-46 / FIN-01 / T-23. There is no money in this slice, and this test is here to make sure it
    /// stays that way until slice 3 introduces it in <c>decimal</c>. A <c>double</c> balance is a
    /// wrong balance, and doc 09 rates a single monetary discrepancy a P1 defect.
    /// </summary>
    [Fact]
    public void NoAssemblyType_HasFloatingPointMoneyMember()
    {
        string[] moneyWords = ["amount", "balance", "total", "price", "money", "sum", "value"];

        var assemblies = new[]
        {
            typeof(Permissions).Assembly,
            typeof(FinanceAi.Infrastructure.Database.TenantDbContext).Assembly,
        };

        var offenders = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    var isMoneyName = moneyWords.Any(w => property.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
                    var isFloating = property.PropertyType == typeof(float) ||
                                     property.PropertyType == typeof(double) ||
                                     property.PropertyType == typeof(float?) ||
                                     property.PropertyType == typeof(double?);

                    if (isMoneyName && isFloating)
                    {
                        offenders.Add($"{type.FullName}.{property.Name}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "FIN-01: monetary values are decimal, never float or double. Offenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>SEC-67: no credential is ever compiled in.</summary>
    [Fact]
    public void NoSourceFile_ContainsAHardCodedCredential()
    {
        var forbidden = new Regex(
            @"(Password|Passwd|Secret|ApiKey|Token)\s*=\s*""(?!\s*$)[^""]{6,}""",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            foreach (var match in forbidden.Matches(File.ReadAllText(file)).Cast<Match>())
            {
                var value = match.Value;

                // Environment variable names and connection-string keys read from configuration are
                // not credentials; a literal that looks like one is.
                if (value.Contains("POSTGRES_", StringComparison.Ordinal) ||
                    value.Contains("JWT_", StringComparison.Ordinal) ||
                    value.Contains("Require(", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Relative(file)}: {value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "SEC-67: secrets are injected from the environment, never written in source. Offenders: " +
            string.Join("; ", offenders));
    }

    private static IEnumerable<string> ProductionSourceFiles() =>
        ProductionDirectories
            .Select(d => Path.Combine(RepositoryPaths.Root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(IsSourceFile);

    private static bool IsSourceFile(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryPaths.Root, path);
}
