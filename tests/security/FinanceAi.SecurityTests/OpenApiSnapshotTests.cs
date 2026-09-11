using System.Text.Json;
using System.Text.Json.Nodes;
using FinanceAi.TestSupport;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace FinanceAi.SecurityTests;

/// <summary>
/// Slice 16 AC-01 / AC-02 — API-14, T-51. The OpenAPI document is generated from the running endpoints and must equal
/// <c>docs/api/openapi.json</c>: an unreviewed contract change (a route, a parameter, a response shape, a permission)
/// fails here with the diff. Accept a reviewed change with <c>UPDATE_OPENAPI=1</c>, which rewrites the snapshot.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiSnapshotTests(ApiTestFixture fixture)
{
    private static string SnapshotPath => RepositoryPaths.Path_("docs", "api", "openapi.json");

    private async Task<string> GenerateAsync()
    {
        var provider = fixture.Api.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();
        var json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1);
        // Re-serialise through System.Text.Json so the text is stable regardless of the writer's whitespace choices.
        var node = JsonNode.Parse(json)!;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    [Fact]
    public async Task Document_MatchesTheCheckedInSnapshot()
    {
        var generated = await GenerateAsync();
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            await File.WriteAllTextAsync(SnapshotPath, generated);
        }

        Assert.True(File.Exists(SnapshotPath), $"No snapshot at {SnapshotPath}. Run the security suite once with UPDATE_OPENAPI=1 and commit docs/api/openapi.json.");
        var expected = await File.ReadAllTextAsync(SnapshotPath);
        if (expected != generated)
        {
            var expectedLines = expected.Split('\n');
            var generatedLines = generated.Split('\n');
            var diff = new List<string>();
            for (var i = 0; i < Math.Max(expectedLines.Length, generatedLines.Length) && diff.Count < 40; i++)
            {
                var e = i < expectedLines.Length ? expectedLines[i] : "<eof>";
                var g = i < generatedLines.Length ? generatedLines[i] : "<eof>";
                if (e != g) diff.Add($"line {i + 1}:\n  snapshot:  {e}\n  generated: {g}");
            }

            Assert.Fail("The API contract changed (API-14). Review the change, then run the security suite with UPDATE_OPENAPI=1 and commit docs/api/openapi.json.\n" + string.Join('\n', diff));
        }
    }

    /// <summary>
    /// Slice 18: every operation documents its success response — a schema (or a declared media type for a download, or
    /// 204) — and the problem responses it can produce, so a response-shape change is a snapshot change.
    /// </summary>
    [Fact]
    public async Task EveryOperation_DocumentsItsResponses()
    {
        var root = JsonNode.Parse(await GenerateAsync())!.AsObject();
        Assert.True(root["components"]!["schemas"]!.AsObject().ContainsKey("Problem"));
        var undocumented = new List<string>();
        foreach (var (path, item) in root["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var responses = operation!["responses"]!.AsObject();
                var success = responses.FirstOrDefault(r => r.Key.StartsWith('2'));
                var ok = success.Value is not null && (success.Key == "204" || success.Value["content"] is JsonObject content && content.Count > 0);
                if (!ok) undocumented.Add($"{method.ToUpperInvariant()} {path}: no typed 2xx");
                var anonymous = operation["x-access"]?.GetValue<string>() == "anonymous";
                if (!anonymous && !responses.ContainsKey("401")) undocumented.Add($"{method.ToUpperInvariant()} {path}: no 401");
                if (path.Contains('{') && !responses.ContainsKey("404")) undocumented.Add($"{method.ToUpperInvariant()} {path}: no 404");
                foreach (var (status, response) in responses)
                {
                    if (status.StartsWith('4') && response!["content"]?["application/problem+json"] is null) undocumented.Add($"{method.ToUpperInvariant()} {path}: {status} is not a problem");
                }
            }
        }

        Assert.Empty(undocumented);
    }

    /// <summary>Every operation says who may call it, and the anonymous set is exactly the middleware's (SEC-10).</summary>
    [Fact]
    public async Task EveryOperation_DeclaresItsAccess()
    {
        var root = JsonNode.Parse(await GenerateAsync())!.AsObject();
        var anonymous = new List<string>();
        var undeclared = new List<string>();
        var count = 0;
        foreach (var (path, item) in root["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                count++;
                var op = operation!.AsObject();
                if (op.ContainsKey("x-permission")) continue;
                var access = op["x-access"]?.GetValue<string>();
                if (access is null) undeclared.Add($"{method.ToUpperInvariant()} {path}");
                else if (access == "anonymous") anonymous.Add($"{method.ToUpperInvariant()} {path}");
            }
        }

        Assert.Equal(148, count);
        Assert.Empty(undeclared);
        Assert.Equal(
            ["POST /api/v1/auth/accept-invitation", "POST /api/v1/auth/forgot-password", "POST /api/v1/auth/login", "POST /api/v1/auth/refresh", "POST /api/v1/auth/register", "POST /api/v1/auth/reset-password"],
            anonymous.Order(StringComparer.Ordinal).ToList());
    }
}
