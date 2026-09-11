using Xunit;

namespace FinanceAi.TestSupport;

/// <summary>
/// One real database and one API host per test assembly. Every test creates its own tenants inside
/// them (T-04): sharing a tenant between tests is how isolation bugs hide.
/// </summary>
public sealed class ApiTestFixture : IAsyncLifetime
{
    private DatabaseFixture? database;
    private ApiFactory? api;

    public DatabaseFixture Database => this.database
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public ApiFactory Api => this.api
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    /// <summary>The PKCS#8 PEM of the key the host treats as its previous signing key (slice 17).</summary>
    public string RetiredSigningKeyPem { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        this.database = await DatabaseFixture.CreateAsync("api");

        // Auth endpoints are rate limited to 10/minute in production (API-13). A suite that signs in
        // dozens of times would trip that, so the limit is raised here and proved separately by the
        // dedicated rate-limit test, which lowers it deliberately.
        Environment.SetEnvironmentVariable("AUTH_RATE_LIMIT_PER_MINUTE", "100000");
        Environment.SetEnvironmentVariable("ALERT_EMAIL", "ops@finance-ai.test");   // slice 15: the alert path's email leg, captured by Mailpit

        // Slice 13: the TOTP secrets' key-encryption key. Random per run; nothing persists across runs.
        Environment.SetEnvironmentVariable("MFA_KEK_BASE64", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        // Slice 17: a retired signing key the host still validates for tokens that predate it (S4). Random per run.
        using (var retired = System.Security.Cryptography.RSA.Create(2048))
        {
            RetiredSigningKeyPem = retired.ExportPkcs8PrivateKeyPem();
        }

        Environment.SetEnvironmentVariable("JWT_SIGNING_KEY_PEM_BASE64_PREVIOUS", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(RetiredSigningKeyPem)));

        this.api = new ApiFactory();

        // Force the host to build now, so a startup failure surfaces as a fixture error rather than
        // as a confusing failure inside the first test.
        _ = this.api.Services;
    }

    public async Task DisposeAsync()
    {
        this.api?.Dispose();

        if (this.database is not null)
        {
            await this.database.DisposeAsync();
        }
    }
}

/// <summary>
/// The collection name every test assembly uses. xUnit requires the <c>[CollectionDefinition]</c>
/// itself to live in the assembly under test, so each test project declares one that points at this
/// shared fixture.
/// </summary>
public static class ApiCollection
{
    public const string Name = "api";
}
