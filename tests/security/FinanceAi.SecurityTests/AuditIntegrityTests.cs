using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinanceAi.SecurityTests;

/// <summary>AC-36, AC-37 / SEC-51, SEC-53 / T-87. The audit log has to be worth trusting, or the
/// whole "why does it say that?" promise of PRD-04 is decoration.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuditIntegrityTests(ApiTestFixture fixture)
{
    /// <summary>
    /// AC-36 / SEC-51, DM-28. Two independent mechanisms: the application role is never granted
    /// <c>UPDATE</c> or <c>DELETE</c>, and a trigger raises for anyone who is — including the table
    /// owner. Rewriting history therefore requires DDL privileges and a deliberate act.
    /// </summary>
    [Fact]
    public async Task AuditEvents_UpdateAndDelete_Raise()
    {
        var organization = await fixture.Api.CreateOrganizationAsync("Append Only");

        var rowId = await fixture.Database.ScalarAsync<long>(
            "SELECT id FROM audit_events WHERE tenant_id = @t ORDER BY id LIMIT 1", ("t", organization.TenantId));
        Assert.NotEqual(0, rowId);

        // As the application: refused for lack of privilege, before the trigger is even reached.
        await using (var app = fixture.Database.OpenApp())
        {
            var update = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand("UPDATE audit_events SET note = 'edited'", app);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Equal("42501", update.SqlState);

            var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand("DELETE FROM audit_events", app);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Equal("42501", delete.SqlState);
        }

        // As the owner of the table, with every privilege there is: the trigger still refuses.
        //
        // The tenant is bound first, because FORCE ROW LEVEL SECURITY binds the owner too — without
        // it the row is simply invisible and the UPDATE would match nothing, which would prove RLS
        // rather than the trigger. Both controls are real; this test is about the second one.
        await using (var owner = fixture.Database.OpenMigrator())
        {
            await BindTenantAsync(owner, organization.TenantId);

            var update = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand(
                    "UPDATE audit_events SET note = 'edited' WHERE id = @id", owner);
                command.Parameters.AddWithValue("id", rowId);
                await command.ExecuteNonQueryAsync();
            });

            Assert.Contains("append-only", update.MessageText, StringComparison.OrdinalIgnoreCase);

            var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand("DELETE FROM audit_events WHERE id = @id", owner);
                command.Parameters.AddWithValue("id", rowId);
                await command.ExecuteNonQueryAsync();
            });

            Assert.Contains("append-only", delete.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        // The row is untouched.
        var note = await fixture.Database.ScalarAsync<string>(
            "SELECT coalesce(note, '') FROM audit_events WHERE id = @id", ("id", rowId));
        Assert.Equal(string.Empty, note);
    }

    /// <summary>
    /// AC-37 / SEC-53. The chain is the answer to "someone with database access edited a row".
    /// Here that someone disables the trigger, edits, and re-enables it — and the chain still says so.
    /// </summary>
    [Fact]
    public async Task AuditChain_Verifies_AndDetectsTampering()
    {
        var organization = await fixture.Api.CreateOrganizationAsync("Chain");

        Assert.True((await this.VerifyAsync(organization.TenantId)).IsIntact);

        // Every row after the first links to its predecessor, and the first links to nothing.
        var orphaned = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit_events a
            WHERE a.tenant_id = @t
              AND a.prev_hash IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM audit_events b WHERE b.tenant_id = a.tenant_id AND b.hash = a.prev_hash)
            """, ("t", organization.TenantId));
        Assert.Equal(0, orphaned);

        var targetId = await fixture.Database.ScalarAsync<long>(
            "SELECT id FROM audit_events WHERE tenant_id = @t ORDER BY id LIMIT 1", ("t", organization.TenantId));

        // An attacker with DDL rights turns the guard off and rewrites what happened.
        await using (var owner = fixture.Database.OpenMigrator())
        {
            await BindTenantAsync(owner, organization.TenantId);
            await ExecuteAsync(owner, "ALTER TABLE audit_events DISABLE TRIGGER audit_events_append_only_trg");

            await using (var tamper = new NpgsqlCommand(
                "UPDATE audit_events SET event_type = 'auth.login_failed' WHERE id = @id", owner))
            {
                tamper.Parameters.AddWithValue("id", targetId);
                Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
            }

            await ExecuteAsync(owner, "ALTER TABLE audit_events ENABLE TRIGGER audit_events_append_only_trg");
        }

        var afterTampering = await this.VerifyAsync(organization.TenantId);

        Assert.False(afterTampering.IsIntact, "A rewritten audit row must break the chain (SEC-53).");
        Assert.Equal(targetId, afterTampering.FirstBrokenId);
    }

    /// <summary>Each organization has its own chain, so one tenant's activity cannot affect the
    /// verifiability of another's.</summary>
    [Fact]
    public async Task AuditChains_ArePerTenant()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Chain A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Chain B");

        Assert.True((await this.VerifyAsync(organizationA.TenantId)).IsIntact);
        Assert.True((await this.VerifyAsync(organizationB.TenantId)).IsIntact);

        // No row in one chain links to a hash from the other.
        var crossLinked = await fixture.Database.ScalarAsync<long>(
            """
            SELECT count(*) FROM audit_events a
            JOIN audit_events b ON b.hash = a.prev_hash
            WHERE a.tenant_id <> b.tenant_id
            """);
        Assert.Equal(0, crossLinked);

        // Exactly one genesis row per tenant.
        foreach (var tenantId in new[] { organizationA.TenantId, organizationB.TenantId })
        {
            var genesis = await fixture.Database.ScalarAsync<long>(
                "SELECT count(*) FROM audit_events WHERE tenant_id = @t AND prev_hash IS NULL", ("t", tenantId));
            Assert.Equal(1, genesis);
        }
    }

    private async Task<AuditChainVerifier.Result> VerifyAsync(Guid tenantId)
    {
        // Verification is a privileged, tenant-declared operation: it runs through the same
        // set_config path any background job must use (SEC-22).
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(fixture.Database.AppConnectionString)
            .Options;

        var tenantContext = new TenantContext();
        tenantContext.Set(tenantId, null);

        await using var db = new TenantDbContext(options, tenantContext);
        await using var scope = await DatabaseScope.EnterTenantAsync(db, tenantId, null);

        var result = await new AuditChainVerifier(db).VerifyAsync(tenantId);

        await scope.CompleteAsync();
        return result;
    }

    /// <summary>
    /// Binds the tenant for the whole session rather than a transaction. Only a test does this: the
    /// application always uses <c>is_local =&gt; true</c> inside a transaction (SEC-23), which is
    /// what AC-31 exists to prove.
    /// </summary>
    private static async Task BindTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection);
        command.Parameters.AddWithValue("t", tenantId.ToString());
        await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
