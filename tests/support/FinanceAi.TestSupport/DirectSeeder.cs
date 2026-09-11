using FinanceAi.Domain.Authorization;
using FinanceAi.Infrastructure.Security;
using Npgsql;

namespace FinanceAi.TestSupport;

/// <summary>
/// Creates additional members of an organization straight in the database.
/// <para>
/// Invitations are deferred to slice 1b, so there is no endpoint that adds a second member yet, but
/// the 403 tests need a Viewer and a Collector to exist today. Seeding directly is honest about
/// that: it builds the state the API will later build, using the same password hasher and the same
/// constraints, and it does not pretend an endpoint exists. When invitations land, these calls are
/// replaced by real API calls and nothing else changes.
/// </para>
/// </summary>
public static class DirectSeeder
{
    public sealed record SeededMember(Guid UserId, Guid MembershipId, string Email, string Password);

    public static async Task<SeededMember> AddMemberAsync(
        this DatabaseFixture fixture,
        Guid tenantId,
        TenantRole role,
        string? email = null,
        string password = ApiScenario.ValidPassword)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var userId = Guid.CreateVersion7();
        var membershipId = Guid.CreateVersion7();
        var address = email ?? $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}"[..24] + "@example.jo";
        var hash = new Argon2idPasswordHasher().Hash(password);

        await using var connection = fixture.OpenAdmin();

        await using (var insertUser = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, full_name, password_hash, preferred_locale, status)
            VALUES (@id, @email, @name, @hash, 'en-JO', 'Active')
            """, connection))
        {
            insertUser.Parameters.AddWithValue("id", userId);
            insertUser.Parameters.AddWithValue("email", address);
            insertUser.Parameters.AddWithValue("name", $"{role} User");
            insertUser.Parameters.AddWithValue("hash", hash);
            await insertUser.ExecuteNonQueryAsync();
        }

        await using (var insertMembership = new NpgsqlCommand(
            """
            INSERT INTO tenant_memberships (id, tenant_id, user_id, role, status)
            VALUES (@id, @tenant, @user, @role, 'Active')
            """, connection))
        {
            insertMembership.Parameters.AddWithValue("id", membershipId);
            insertMembership.Parameters.AddWithValue("tenant", tenantId);
            insertMembership.Parameters.AddWithValue("user", userId);
            insertMembership.Parameters.AddWithValue("role", role.ToString());
            await insertMembership.ExecuteNonQueryAsync();
        }

        return new SeededMember(userId, membershipId, address, password);
    }

    /// <summary>Reads a single scalar as the superuser, for assertions about what is actually stored.</summary>
    public static async Task<T?> ScalarAsync<T>(this DatabaseFixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var connection = fixture.OpenAdmin();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters ?? [])
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Runs a statement as the superuser — for seeding and for corrupting state on purpose.</summary>
    public static async Task<int> ExecuteAsync(this DatabaseFixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var connection = fixture.OpenAdmin();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters ?? [])
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }
}
