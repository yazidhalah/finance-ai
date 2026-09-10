using System.Text.RegularExpressions;
using FinanceAi.Domain.Authorization;
using FinanceAi.TestSupport;

namespace FinanceAi.UnitTests.Authorization;

/// <summary>
/// AC-21 / T-60. Doc 01 §5.1 is the specification for who may do what; this test parses that table
/// out of the document and compares it with the code.
/// <para>
/// Restating the matrix in the test would prove only that two copies of the code agree. Reading the
/// document means a permission granted in code but not in the specification fails the build, and a
/// specification change that nobody implemented fails it too.
/// </para>
/// </summary>
public sealed class RolePermissionMapTests
{
    private const string SpecificationPath = "docs/product/01-product-requirements.md";

    [Fact]
    public void RolePermissionMap_MatchesProductRequirementsDocument()
    {
        var specified = ParsePermissionMatrix();

        Assert.NotEmpty(specified);

        // Every permission in the document exists in code, with the same role set.
        foreach (var (permission, rolesInSpec) in specified)
        {
            Assert.Contains(permission, Permissions.All);

            foreach (var role in Enum.GetValues<TenantRole>())
            {
                var granted = RolePermissions.Grants(role, permission);
                var shouldGrant = rolesInSpec.Contains(role);

                Assert.True(
                    granted == shouldGrant,
                    $"'{permission}' for {role}: code says {granted}, {SpecificationPath} §5.1 says {shouldGrant}.");
            }
        }

        // ...and the code defines no permission the document does not.
        var undocumented = Permissions.All.Except(specified.Keys, StringComparer.Ordinal).ToList();
        Assert.True(undocumented.Count == 0, $"Permissions absent from {SpecificationPath} §5.1: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void Owner_HoldsEveryPermission()
    {
        // Doc 01 §5: "Full control including billing, users, and deletion."
        Assert.Equal(Permissions.All.Count, RolePermissions.For(TenantRole.Owner).Count);
    }

    [Fact]
    public void Viewer_HoldsNoWritePermission()
    {
        // Doc 01 §5, persona P4: "Must never be able to write." Including notes.
        var writeLike = RolePermissions.OrderedFor(TenantRole.Viewer)
            .Where(p => p.Contains("write", StringComparison.Ordinal)
                        || p.Contains("import", StringComparison.Ordinal)
                        || p.Contains("merge", StringComparison.Ordinal)
                        || p.Contains("approve", StringComparison.Ordinal)
                        || p.Contains("send", StringComparison.Ordinal)
                        || p.Contains("propose", StringComparison.Ordinal)
                        || p.Contains("assign", StringComparison.Ordinal)
                        || p.Contains("escalate", StringComparison.Ordinal)
                        || p.Contains("void", StringComparison.Ordinal)
                        || p.Contains("allocate", StringComparison.Ordinal)
                        || p.Contains("resolve", StringComparison.Ordinal)
                        || p.Contains("delete", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(writeLike);
    }

    [Fact]
    public void WriteOffApproval_IsNotHeldByTheProposingRoleAlone()
    {
        // PRD-11: four-eyes. Accountant proposes; only Admin and Owner approve.
        Assert.True(RolePermissions.Grants(TenantRole.Accountant, Permissions.WriteoffPropose));
        Assert.False(RolePermissions.Grants(TenantRole.Accountant, Permissions.WriteoffApprove));
        Assert.True(RolePermissions.Grants(TenantRole.Admin, Permissions.WriteoffApprove));
    }

    [Fact]
    public void PermissionCatalogue_HasNoDuplicates() =>
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct(StringComparer.Ordinal).Count());

    /// <summary>Reads the "| `permission` | ✅ | — | ... |" rows out of doc 01 §5.1.</summary>
    private static Dictionary<string, HashSet<TenantRole>> ParsePermissionMatrix()
    {
        var lines = File.ReadAllLines(RepositoryPaths.Path_(SpecificationPath.Split('/')));
        var matrix = new Dictionary<string, HashSet<TenantRole>>(StringComparer.Ordinal);

        // Column order is taken from the header rather than assumed, so reordering the table in the
        // document cannot silently invert the assertion.
        TenantRole[]? columns = null;
        var rowPattern = new Regex(@"^\|\s*`(?<permission>[a-z_.]+)`\s*\|(?<cells>.*)\|\s*$");

        foreach (var line in lines)
        {
            if (columns is null && line.StartsWith("| Permission |", StringComparison.Ordinal))
            {
                columns = line.Split('|', StringSplitOptions.TrimEntries)
                    .Skip(2)
                    .Where(c => c.Length > 0)
                    .Select(c => Enum.Parse<TenantRole>(c, ignoreCase: true))
                    .ToArray();
                continue;
            }

            if (columns is null)
            {
                continue;
            }

            var match = rowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var cells = match.Groups["cells"].Value
                .Split('|', StringSplitOptions.TrimEntries)
                .Where(c => c.Length > 0)
                .ToArray();

            Assert.Equal(columns.Length, cells.Length);

            var roles = new HashSet<TenantRole>();
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i] == "✅")
                {
                    roles.Add(columns[i]);
                }
            }

            matrix[match.Groups["permission"].Value] = roles;
        }

        return matrix;
    }
}
