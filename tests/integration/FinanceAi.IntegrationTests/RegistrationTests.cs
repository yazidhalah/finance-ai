using System.Net;
using System.Net.Http.Json;
using FinanceAi.TestSupport;

namespace FinanceAi.IntegrationTests;

/// <summary>AC-01 … AC-06: registration creates a whole organization, atomically, and tells an
/// attacker nothing.</summary>
[Collection(ApiCollection.Name)]
public sealed class RegistrationTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task Register_CreatesUserTenantOwnerMembershipAndSettings_Atomically()
    {
        var email = $"rana-{Guid.NewGuid():N}@example.jo";

        using var client = fixture.Api.CreateCookieClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = ApiScenario.ValidPassword,
            fullName = "Rana Owner",
            organizationName = "Al Amal Trading Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "ar-JO",
        }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var userId = await fixture.Database.ScalarAsync<Guid>(
            "SELECT id FROM users WHERE email = @e", ("e", email));
        Assert.NotEqual(Guid.Empty, userId);

        // Exactly one membership, and it is an active Owner.
        var role = await fixture.Database.ScalarAsync<string>(
            "SELECT role FROM tenant_memberships WHERE user_id = @u", ("u", userId));
        Assert.Equal("Owner", role);

        var status = await fixture.Database.ScalarAsync<string>(
            "SELECT status FROM tenant_memberships WHERE user_id = @u", ("u", userId));
        Assert.Equal("Active", status);

        var tenantId = await fixture.Database.ScalarAsync<Guid>(
            "SELECT tenant_id FROM tenant_memberships WHERE user_id = @u", ("u", userId));

        var tenantName = await fixture.Database.ScalarAsync<string>(
            "SELECT name FROM tenants WHERE id = @t", ("t", tenantId));
        Assert.Equal("Al Amal Trading Co.", tenantName);

        // The settings row exists with the documented defaults (doc 04 §4), so no later slice has
        // to cope with a tenant that has none.
        var settingsCount = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM tenant_settings WHERE tenant_id = @t", ("t", tenantId));
        Assert.Equal(1, settingsCount);

        var requireApproval = await fixture.Database.ScalarAsync<bool>(
            "SELECT require_approval_before_send FROM tenant_settings WHERE tenant_id = @t", ("t", tenantId));
        Assert.True(requireApproval, "PRD-15: approval before send defaults to true.");
    }

    /// <summary>
    /// AC-02. Registration writes four rows plus three audit rows; a failure anywhere must leave
    /// none of them. Two concurrent registrations of the same address force exactly that: the unique
    /// index on <c>users.email</c> rejects the loser after it has already inserted its organization.
    /// If the transaction were not atomic, an orphan organization with no Owner would survive.
    /// </summary>
    [Fact]
    public async Task Register_WhenCommitFails_LeavesNoRows()
    {
        var email = $"race-{Guid.NewGuid():N}@example.jo";

        var body = new
        {
            email,
            password = ApiScenario.ValidPassword,
            fullName = "Rana Owner",
            organizationName = "Race Condition Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "en-JO",
        };

        using var first = fixture.Api.CreateCookieClient();
        using var second = fixture.Api.CreateCookieClient();

        await Task.WhenAll(
            first.PostAsJsonAsync("/api/v1/auth/register", body, ApiScenario.Json),
            second.PostAsJsonAsync("/api/v1/auth/register", body, ApiScenario.Json));

        var users = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM users WHERE email = @e", ("e", email));
        Assert.Equal(1, users);

        var organizations = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM tenants WHERE name = 'Race Condition Co.'");
        Assert.Equal(1, organizations);

        // No organization anywhere is left without an Owner.
        var ownerless = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM tenants t
            WHERE NOT EXISTS (
              SELECT 1 FROM tenant_memberships m
              WHERE m.tenant_id = t.id AND m.role = 'Owner' AND m.status = 'Active')
            """);
        Assert.Equal(0, ownerless);

        // ...and no organization is left without its settings row.
        var settingsless = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM tenants t WHERE NOT EXISTS (SELECT 1 FROM tenant_settings s WHERE s.tenant_id = t.id)");
        Assert.Equal(0, settingsless);
    }

    /// <summary>AC-04 / SEC-07: registration is not an account-existence oracle.</summary>
    [Fact]
    public async Task Register_WithExistingEmail_ReturnsSameShapeAsSuccess()
    {
        var email = $"taken-{Guid.NewGuid():N}@example.jo";

        var body = new
        {
            email,
            password = ApiScenario.ValidPassword,
            fullName = "Rana Owner",
            organizationName = "First Org",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "en-JO",
        };

        using var client = fixture.Api.CreateCookieClient();

        var firstResponse = await client.PostAsJsonAsync("/api/v1/auth/register", body, ApiScenario.Json);
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        var secondResponse = await client.PostAsJsonAsync("/api/v1/auth/register", body, ApiScenario.Json);
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(firstResponse.StatusCode, secondResponse.StatusCode);
        Assert.Equal(firstBody, secondBody);

        // The second attempt created nothing.
        var organizations = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM tenants WHERE name = 'First Org'");
        Assert.Equal(1, organizations);
    }

    /// <summary>AC-05: what is stored is an Argon2id hash, not the password and not anything reversible.</summary>
    [Fact]
    public async Task Register_StoresNoPlaintextPassword()
    {
        var email = $"hash-{Guid.NewGuid():N}@example.jo";

        using var client = fixture.Api.CreateCookieClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = ApiScenario.ValidPassword,
            fullName = "Rana Owner",
            organizationName = "Hash Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "en-JO",
        }, ApiScenario.Json);

        var stored = await fixture.Database.ScalarAsync<string>(
            "SELECT password_hash FROM users WHERE email = @e", ("e", email));

        Assert.NotNull(stored);
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiScenario.ValidPassword, stored, StringComparison.Ordinal);
    }

    /// <summary>AC-06 / SEC-01: a length floor, and no composition rules.</summary>
    [Fact]
    public async Task Register_WithShortPassword_Returns400()
    {
        using var client = fixture.Api.CreateCookieClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"short-{Guid.NewGuid():N}@example.jo",
            password = "short11chr",
            fullName = "Rana Owner",
            organizationName = "Short Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "en-JO",
        }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("validation_failed", problem!.Code);
    }

    [Fact]
    public async Task Register_AcceptsALongPassphraseWithNoSpecialCharacters()
    {
        using var client = fixture.Api.CreateCookieClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"phrase-{Guid.NewGuid():N}@example.jo",
            password = "seven yellow camels walked to aqaba",
            fullName = "Rana Owner",
            organizationName = "Passphrase Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "en-JO",
        }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.jo")]
    public async Task Register_WithAnInvalidEmail_Returns400(string email)
    {
        using var client = fixture.Api.CreateCookieClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = ApiScenario.ValidPassword,
            fullName = "Rana Owner",
            organizationName = "Invalid Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "en-JO",
        }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithAnUnsupportedLocale_Returns400()
    {
        using var client = fixture.Api.CreateCookieClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"locale-{Guid.NewGuid():N}@example.jo",
            password = ApiScenario.ValidPassword,
            fullName = "Rana Owner",
            organizationName = "Locale Co.",
            baseCurrency = "JOD",
            timezone = "Asia/Amman",
            locale = "fr-FR",
        }, ApiScenario.Json);

        // PRD-20: two locales in v1. A third would ship an untranslated UI.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
