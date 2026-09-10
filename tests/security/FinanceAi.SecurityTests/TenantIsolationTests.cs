using Npgsql;

namespace FinanceAi.SecurityTests;

/// <summary>
/// AC-29 … AC-34 / SEC-29 / T-70 … T-75. The isolation suite doc 09 §4.2 calls "the suite that must
/// never be skipped".
/// <para>
/// ADR-0001 defends tenancy with three independent layers. A suite that only proved the application
/// filters correctly would prove one of them. These tests deliberately disable each layer in turn
/// and assert the ones underneath still hold — because the whole argument for shared-schema
/// multi-tenancy is that all three must fail at once for a breach to happen.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(ApiTestFixture fixture)
{
    /// <summary>
    /// AC-29 / DM-01, DM-02, DM-10 / T-70. Enumerated from <c>pg_catalog</c>, so a table added in a
    /// later slice without <c>tenant_id</c>, without forced RLS, without a policy, or with a
    /// single-column foreign key to another tenant-scoped table fails this build.
    /// </summary>
    [Fact]
    public async Task EveryTenantScopedTable_HasTenantIdRlsForcedPolicyAndCompositeKeys()
    {
        await using var connection = fixture.Database.OpenAdmin();

        var tenantScoped = await QueryAsync(connection, """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            ORDER BY c.relname
            """);

        Assert.NotEmpty(tenantScoped);

        foreach (var table in tenantScoped)
        {
            var notNull = await ScalarAsync<bool>(connection, """
                SELECT a.attnotnull FROM pg_attribute a
                JOIN pg_class c ON c.oid = a.attrelid
                WHERE c.relname = @t AND a.attname = 'tenant_id'
                """, ("t", table));
            Assert.True(notNull, $"{table}.tenant_id must be NOT NULL (DM-01).");

            var rlsEnabled = await ScalarAsync<bool>(connection,
                "SELECT relrowsecurity FROM pg_class WHERE relname = @t", ("t", table));
            Assert.True(rlsEnabled, $"{table} must have RLS enabled (DM-02).");

            // FORCE is the half people forget. Without it the table owner is exempt, and since the
            // owner is who migrations and any future admin tooling connect as, the control is theatre.
            var rlsForced = await ScalarAsync<bool>(connection,
                "SELECT relforcerowsecurity FROM pg_class WHERE relname = @t", ("t", table));
            Assert.True(rlsForced, $"{table} must have RLS FORCED, not merely enabled (DM-02).");

            var policies = await ScalarAsync<long>(connection, """
                SELECT count(*) FROM pg_policy p
                JOIN pg_class c ON c.oid = p.polrelid WHERE c.relname = @t
                """, ("t", table));
            Assert.True(policies > 0, $"{table} has RLS enabled but no policy, so it returns nothing to anyone.");

            await AssertTenantQualifiedKeyAsync(connection, table);
        }
    }

    /// <summary>
    /// AC-29 / DM-10. Layer 3: a foreign key between two tenant-scoped tables must carry
    /// <c>tenant_id</c>, so that a child in tenant A referencing a parent in tenant B is not merely
    /// prevented but unrepresentable.
    /// </summary>
    [Fact]
    public async Task NoSingleColumnForeignKey_JoinsTwoTenantScopedTables()
    {
        await using var connection = fixture.Database.OpenAdmin();

        var offenders = await QueryAsync(connection, """
            SELECT child.relname || '.' || con.conname
            FROM pg_constraint con
            JOIN pg_class child  ON child.oid  = con.conrelid
            JOIN pg_class parent ON parent.oid = con.confrelid
            WHERE con.contype = 'f'
              AND array_length(con.conkey, 1) = 1
              AND EXISTS (SELECT 1 FROM pg_attribute a
                          WHERE a.attrelid = child.oid  AND a.attname = 'tenant_id' AND NOT a.attisdropped)
              AND EXISTS (SELECT 1 FROM pg_attribute a
                          WHERE a.attrelid = parent.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
            """);

        Assert.True(
            offenders.Count == 0,
            "DM-10: a foreign key between two tenant-scoped tables must be composite on tenant_id. " +
            "Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// AC-30 / DM-05 / T-72. The most important negative test in the system: with no tenant bound,
    /// every tenant-scoped table returns <b>zero rows</b>. A policy that failed open here would turn
    /// every forgotten <c>set_config</c> — a background job, a report, a console — into a full
    /// cross-tenant disclosure.
    /// </summary>
    [Fact]
    public async Task ConnectionWithoutTenantGuc_ReturnsZeroRows()
    {
        // Real data exists to be leaked, in two organizations.
        var organizationA = await fixture.Api.CreateOrganizationAsync("No GUC A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("No GUC B");
        Assert.NotEqual(organizationA.TenantId, organizationB.TenantId);

        await using var connection = fixture.Database.OpenApp();

        foreach (var table in await TenantScopedTablesAsync())
        {
            var visible = await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}");

            Assert.True(
                visible == 0,
                $"{table} returned {visible} rows to a connection with no app.tenant_id set. " +
                "Policies must fail closed (DM-05).");
        }

        // ...and the platform tables are equally closed without either GUC.
        Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM tenants"));
        Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM users"));
    }

    /// <summary>
    /// AC-31 / SEC-23 / T-73. The classic RLS-plus-pooling bug: a GUC set for one request surviving
    /// into the next borrower of the same physical connection, which would serve one tenant's data
    /// under another tenant's request. <c>is_local =&gt; true</c> is what prevents it, and this is
    /// the test that says so.
    /// </summary>
    [Fact]
    public async Task PooledConnection_DoesNotLeakTenantGuc()
    {
        var organization = await fixture.Api.CreateOrganizationAsync("Pool");

        var connectionString = fixture.Database.AppConnectionString;

        // Borrow, set the tenant inside a transaction, use it, return to the pool.
        await using (var first = new NpgsqlConnection(connectionString))
        {
            await first.OpenAsync();

            await using var transaction = await first.BeginTransactionAsync();

            await using (var set = new NpgsqlCommand(
                "SELECT set_config('app.tenant_id', @t, true)", first, transaction))
            {
                set.Parameters.AddWithValue("t", organization.TenantId.ToString());
                await set.ExecuteScalarAsync();
            }

            var visibleInside = await ScalarAsync<long>(first, "SELECT count(*) FROM tenant_settings", transaction);
            Assert.Equal(1, visibleInside);

            await transaction.CommitAsync();
        }

        // Borrow again — very likely the same physical connection — and assert the tenant is gone.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var second = new NpgsqlConnection(connectionString);
            await second.OpenAsync();

            var leaked = await ScalarAsync<string>(second, "SELECT current_setting('app.tenant_id', true)");
            Assert.True(
                string.IsNullOrEmpty(leaked),
                $"app.tenant_id leaked across a pooled connection as '{leaked}' (SEC-23).");

            Assert.Equal(0, await ScalarAsync<long>(second, "SELECT count(*) FROM tenant_settings"));
        }
    }

    /// <summary>
    /// AC-32 / T-74. Layer 2, proved with layer 1 switched off. This query is written exactly as a
    /// future bug would write it — no <c>WHERE tenant_id</c>, straight to the database, bypassing EF
    /// entirely — and it still cannot see another organization's rows.
    /// </summary>
    [Fact]
    public async Task UnfilteredRawSql_ReturnsOnlyCurrentTenantRows()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("Raw SQL A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Raw SQL B");

        await using var connection = fixture.Database.OpenApp();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("t", organizationA.TenantId.ToString());
            await set.ExecuteScalarAsync();
        }

        // "SELECT * FROM tenant_memberships" — the forgotten-filter bug, in full.
        var visibleTenantIds = new List<Guid>();
        await using (var read = new NpgsqlCommand("SELECT DISTINCT tenant_id FROM tenant_memberships", connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visibleTenantIds.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal(organizationA.TenantId, Assert.Single(visibleTenantIds));

        // The same for the audit log, which has no platform escape at all.
        var auditTenants = await ScalarAsync<long>(connection,
            "SELECT count(DISTINCT tenant_id) FROM audit_events", transaction);
        Assert.Equal(1, auditTenants);

        // B's rows exist; they are simply not reachable from inside A's scope.
        var bExists = await fixture.Database.ScalarAsync<long>(
            "SELECT count(*) FROM tenant_memberships WHERE tenant_id = @t", ("t", organizationB.TenantId));
        Assert.True(bExists > 0);
    }

    /// <summary>
    /// AC-33 / DM-10 / T-75. Layer 3, proved with layers 1 and 2 both out of the way: this runs as
    /// the superuser, who bypasses RLS entirely. A refresh token for organization B belonging to a
    /// user who is only a member of organization A cannot be written at all — the composite foreign
    /// key makes the cross-tenant reference unrepresentable rather than merely rejected.
    /// </summary>
    [Fact]
    public async Task CrossTenantChildRow_IsRejectedByCompositeForeignKey()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync("FK A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("FK B");

        await using var connection = fixture.Database.OpenAdmin();

        // Sanity: as superuser, RLS is not in play, so only the schema can stop this.
        var canSeeEverything = await ScalarAsync<long>(connection, "SELECT count(DISTINCT tenant_id) FROM tenant_memberships");
        Assert.True(canSeeEverything >= 2, "This test is only meaningful if RLS is genuinely bypassed here.");

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO refresh_tokens (id, tenant_id, user_id, family_id, token_hash, expires_at)
            VALUES (@id, @tenant, @user, @family, @hash, now() + interval '14 days')
            """, connection);

        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("tenant", organizationB.TenantId);      // tenant B...
        insert.Parameters.AddWithValue("user", organizationA.OwnerUserId);     // ...user of tenant A
        insert.Parameters.AddWithValue("family", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("hash", Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());

        Assert.Equal("23503", ex.SqlState);   // foreign_key_violation
        Assert.Contains("membership", ex.ConstraintName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC-34 / SEC-21, DM-03. Forced RLS binds the table owner, but the application must still not
    /// <i>be</i> the owner and must not hold <c>BYPASSRLS</c>: either would let a single mistake in
    /// a connection string undo the whole of layer 2.
    /// </summary>
    [Fact]
    public async Task ApplicationRole_HasNoBypassRlsAndOwnsNothing()
    {
        await using var connection = fixture.Database.OpenAdmin();

        foreach (var role in new[] { "finance_app", "finance_reporting" })
        {
            var bypassRls = await ScalarAsync<bool>(connection,
                "SELECT rolbypassrls FROM pg_roles WHERE rolname = @r", ("r", role));
            Assert.False(bypassRls, $"{role} must not have BYPASSRLS (SEC-21).");

            var superuser = await ScalarAsync<bool>(connection,
                "SELECT rolsuper FROM pg_roles WHERE rolname = @r", ("r", role));
            Assert.False(superuser, $"{role} must not be a superuser — superusers bypass RLS unconditionally.");

            var owned = await ScalarAsync<long>(connection, """
                SELECT count(*) FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) = @r
                """, ("r", role));
            Assert.Equal(0, owned);
        }

        // The migrator owns the schema, and it is a separate login from the application (SEC-100).
        var migratorOwns = await ScalarAsync<long>(connection, """
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND pg_get_userbyid(c.relowner) = 'finance_migrator'
            """);
        Assert.True(migratorOwns > 0);

        // The application cannot perform DDL, so it cannot disable a policy even if compromised.
        await using var appConnection = fixture.Database.OpenApp();
        var ddl = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var attempt = new NpgsqlCommand("ALTER TABLE audit_events DISABLE ROW LEVEL SECURITY", appConnection);
            await attempt.ExecuteNonQueryAsync();
        });

        Assert.Equal("42501", ddl.SqlState);   // insufficient_privilege
    }

    private async Task<IReadOnlyList<string>> TenantScopedTablesAsync()
    {
        await using var connection = fixture.Database.OpenAdmin();

        return await QueryAsync(connection, """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            ORDER BY c.relname
            """);
    }

    /// <summary>
    /// DM-10 requires a <c>(tenant_id, id)</c> unique key so children can reference their parent
    /// tenant-qualified. Tables keyed directly by <c>tenant_id</c> (one row per tenant, like
    /// <c>tenant_settings</c>) satisfy the same requirement through their primary key.
    /// </summary>
    private static async Task AssertTenantQualifiedKeyAsync(NpgsqlConnection connection, string table)
    {
        var hasCompositeKey = await ScalarAsync<bool>(connection, """
            SELECT EXISTS (
              SELECT 1 FROM pg_constraint con
              JOIN pg_class c ON c.oid = con.conrelid
              WHERE c.relname = @t
                AND con.contype IN ('u','p')
                AND (SELECT array_agg(a.attname ORDER BY a.attname)
                     FROM pg_attribute a
                     WHERE a.attrelid = c.oid AND a.attnum = ANY (con.conkey))
                    = ARRAY['id','tenant_id']::name[])
            """, ("t", table));

        var keyedByTenant = await ScalarAsync<bool>(connection, """
            SELECT EXISTS (
              SELECT 1 FROM pg_constraint con
              JOIN pg_class c ON c.oid = con.conrelid
              WHERE c.relname = @t
                AND con.contype = 'p'
                AND (SELECT array_agg(a.attname ORDER BY a.attname)
                     FROM pg_attribute a
                     WHERE a.attrelid = c.oid AND a.attnum = ANY (con.conkey))
                    = ARRAY['tenant_id']::name[])
            """, ("t", table));

        Assert.True(
            hasCompositeKey || keyedByTenant,
            $"{table} needs a UNIQUE (tenant_id, id) key so children can reference it tenant-qualified " +
            "(DM-10), or a primary key of tenant_id alone if it holds one row per tenant.");
    }

    private static async Task<List<string>> QueryAsync(NpgsqlConnection connection, string sql)
    {
        var results = new List<string>();

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private static async Task<T?> ScalarAsync<T>(
        NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters ?? [])
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }
}
