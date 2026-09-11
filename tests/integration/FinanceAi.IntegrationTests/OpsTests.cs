using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.TestSupport;
using Npgsql;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 15 AC-01 … AC-08: the invariant job, the SEC-102 detectors and the alert path (T-153).</summary>
[Collection(ApiCollection.Name)]
public sealed class OpsTests(ApiTestFixture fixture)
{
    private static readonly HttpClient Mailpit = new();

    private static string D(int daysFromToday) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(daysFromToday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<JsonElement> RunAsync(HttpClient client) => await client.PostAsync("/api/v1/organization/invariants/run", new { });

    private static JsonElement Check(JsonElement run, string id) => run.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == id);

    private async Task<JsonElement> AlertsAsync(HttpClient client, bool all = false) => await client.GetFromJsonAsync<JsonElement>($"/api/v1/organization/alerts{(all ? "?all=1" : string.Empty)}", ApiScenario.Json);

    /// <summary>AC-01: the ledger scenario — invoices, a payment, an allocation, a case — is clean, and the run says so.</summary>
    [Fact]
    public async Task Invariants_AreClean_OnTheLedgerScenario()
    {
        var s = await fixture.Api.NewCustomerAsync("Clean Co.");
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "CLEAN-1", 1_000.000m, dueDate: D(-30), issueDate: D(-60));
        var payment = (await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(400.000m), method = "Cash", receivedDate = D(0) })).GetProperty("id").GetGuid();
        await s.Client.PostAsync($"/api/v1/payments/{payment}/allocations", new { lines = new[] { new { invoiceId = invoice, amount = M(400.000m) } } });
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });

        var run = await RunAsync(s.Client);
        Assert.True(run.GetProperty("status").GetString() == "ok", run.GetRawText());
        Assert.Equal("manual", run.GetProperty("trigger").GetString());
        var ids = run.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        foreach (var id in new[] { "INV-01", "INV-02", "INV-03", "INV-04", "INV-06", "INV-07", "INV-08", "INV-09", "INV-10", "INV-13", "SEC-53" })
        {
            Assert.Contains(id, ids);
            Assert.Equal(0, Check(run, id).GetProperty("violations").GetInt64());
        }

        // The run is what GET returns, and nothing was alerted.
        var latest = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/invariants", ApiScenario.Json);
        Assert.Equal(run.GetProperty("id").GetGuid(), latest.GetProperty("run").GetProperty("id").GetGuid());
        Assert.Equal(0, (await AlertsAsync(s.Client, all: true)).GetProperty("items").GetArrayLength());
    }

    /// <summary>AC-02 / T-153: an injected violation reaches a human by every leg, once.</summary>
    [Fact]
    public async Task InjectedViolation_FiresTheAlertPath_Once()
    {
        var s = await fixture.Api.NewCustomerAsync("Broken Co.");
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "BRK-1", 500.000m, dueDate: D(-10), issueDate: D(-40));
        // Straight past the ledger: the cache no longer matches what the allocations say (INV-09). (INV-01 is also a check
        // constraint — balance_in_range — so a cache outside [0, total] cannot even be written; the runtime check stays for the day it is dropped.)
        await fixture.Database.ExecuteAsync("UPDATE invoices SET balance_cache = balance_cache - 1 WHERE id = @i", ("i", invoice));

        var mailBefore = fixture.Api.Mail.Sent;
        var hooksBefore = fixture.Api.Webhook.Delivered.Count;

        var run = await RunAsync(s.Client);
        Assert.Equal("violations", run.GetProperty("status").GetString());
        Assert.Equal(0, Check(run, "INV-01").GetProperty("violations").GetInt64());
        Assert.Equal(1, Check(run, "INV-09").GetProperty("violations").GetInt64());
        Assert.Contains(invoice.ToString(), Check(run, "INV-09").GetProperty("samples").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(0, Check(run, "SEC-53").GetProperty("violations").GetInt64());

        var alerts = await AlertsAsync(s.Client);
        var alert = Assert.Single(alerts.GetProperty("items").EnumerateArray());
        Assert.Equal("invariant_violation", alert.GetProperty("kind").GetString());
        Assert.Equal("critical", alert.GetProperty("severity").GetString());
        Assert.Equal("sent", alert.GetProperty("emailDelivery").GetString());
        Assert.Equal("sent", alert.GetProperty("webhookDelivery").GetString());
        Assert.Equal(1, alerts.GetProperty("openCount").GetInt32());

        // The email leg, through the real transport to Mailpit; the webhook leg, captured.
        Assert.Equal(mailBefore + 1, fixture.Api.Mail.Sent);
        var mail = await MailAsync("ops@finance-ai.test", alert.GetProperty("id").GetGuid());
        Assert.Contains("invariant_violation", mail);
        Assert.Contains(s.Organization.Name, mail, StringComparison.Ordinal);   // the organization is named …
        Assert.DoesNotContain("Broken Co.", mail, StringComparison.Ordinal);  // … the customer never is (SEC-41)
        Assert.DoesNotContain("500.000", mail, StringComparison.Ordinal);     // and neither is an amount
        Assert.Equal(hooksBefore + 1, fixture.Api.Webhook.Delivered.Count);
        var hook = fixture.Api.Webhook.Delivered[^1];
        Assert.Equal(alert.GetProperty("id").GetGuid(), hook.AlertId);
        Assert.Equal(s.Organization.TenantId, hook.TenantId);
        Assert.Equal("invariant_violation", hook.Kind);

        // Once a day: a second run records the violation again but pages nobody.
        var second = await RunAsync(s.Client);
        Assert.Equal("violations", second.GetProperty("status").GetString());
        Assert.Equal(1, (await AlertsAsync(s.Client, all: true)).GetProperty("items").GetArrayLength());
        Assert.Equal(mailBefore + 1, fixture.Api.Mail.Sent);
        Assert.Equal(hooksBefore + 1, fixture.Api.Webhook.Delivered.Count);

        // Acknowledge: audited, leaves the open list, stays in the full list.
        var acked = await s.Client.PostAsync($"/api/v1/organization/alerts/{alert.GetProperty("id").GetGuid()}/acknowledge", new { });
        Assert.NotEqual(JsonValueKind.Null, acked.GetProperty("acknowledgedAt").ValueKind);
        Assert.Equal(0, (await AlertsAsync(s.Client)).GetProperty("openCount").GetInt32());
        var audit = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?entityType=alert", ApiScenario.Json);
        Assert.Contains(audit.GetProperty("items").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "alert.acknowledged");
        var runAudit = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?entityType=invariant_run", ApiScenario.Json);
        Assert.Contains(runAudit.GetProperty("items").EnumerateArray(), e => e.GetProperty("eventType").GetString() == "invariants.run");
    }

    /// <summary>AC-03: a rewritten audit row is a broken chain, and that is its own alert.</summary>
    [Fact]
    public async Task AuditChainBreak_Alerts()
    {
        var s = await fixture.Api.NewCustomerAsync("Tamper Co.");
        var targetId = await fixture.Database.ScalarAsync<long>("SELECT id FROM audit_events WHERE tenant_id = @t ORDER BY id LIMIT 1", ("t", s.Organization.TenantId));
        await using (var owner = fixture.Database.OpenMigrator())
        {
            await using (var bind = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", owner))
            {
                bind.Parameters.AddWithValue("t", s.Organization.TenantId.ToString());
                await bind.ExecuteScalarAsync();
            }

            await using (var off = new NpgsqlCommand("ALTER TABLE audit_events DISABLE TRIGGER audit_events_append_only_trg", owner)) await off.ExecuteNonQueryAsync();
            await using (var tamper = new NpgsqlCommand("UPDATE audit_events SET event_type = 'auth.login_failed' WHERE id = @id", owner))
            {
                tamper.Parameters.AddWithValue("id", targetId);
                Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
            }

            await using (var on = new NpgsqlCommand("ALTER TABLE audit_events ENABLE TRIGGER audit_events_append_only_trg", owner)) await on.ExecuteNonQueryAsync();
        }

        var run = await RunAsync(s.Client);
        Assert.Equal("violations", run.GetProperty("status").GetString());
        var chain = Check(run, "SEC-53");
        Assert.Equal(1, chain.GetProperty("violations").GetInt64());
        Assert.Equal(targetId.ToString(System.Globalization.CultureInfo.InvariantCulture), chain.GetProperty("samples")[0].GetString());

        var alerts = await AlertsAsync(s.Client);
        var alert = Assert.Single(alerts.GetProperty("items").EnumerateArray());
        Assert.Equal("audit_chain_break", alert.GetProperty("kind").GetString());
        Assert.Equal("critical", alert.GetProperty("severity").GetString());
    }

    /// <summary>AC-04: ten suggestions, six rejected → a warning; the same run's business checks stay clean.</summary>
    [Fact]
    public async Task GuardSpike_Alerts()
    {
        var s = await fixture.Api.NewCustomerAsync("Spiky Co.");
        for (var i = 0; i < 10; i++)
        {
            await SeedSuggestionAsync(s.Organization.TenantId, i < 6 ? "rejected_by_guard" : "valid");
        }

        await RunAsync(s.Client);
        var alerts = await AlertsAsync(s.Client);
        var alert = Assert.Single(alerts.GetProperty("items").EnumerateArray());
        Assert.Equal("ai_guard_rejection_spike", alert.GetProperty("kind").GetString());
        Assert.Equal("warning", alert.GetProperty("severity").GetString());
        Assert.Equal(10, alert.GetProperty("details").GetProperty("suggestions").GetInt32());
        Assert.Equal(6, alert.GetProperty("details").GetProperty("rejected").GetInt32());
    }

    /// <summary>AC-05: twenty-one sends today against five a day before → a warning.</summary>
    [Fact]
    public async Task SendAnomaly_Alerts()
    {
        var s = await fixture.Api.NewCustomerAsync("Bursty Co.");
        var owner = s.Organization.OwnerUserId;
        for (var i = 0; i < 21; i++) await SeedSentAsync(s.Organization.TenantId, s.CustomerId, owner, "2 hours");
        for (var i = 0; i < 35; i++) await SeedSentAsync(s.Organization.TenantId, s.CustomerId, owner, "3 days");   // five a day over seven days

        await RunAsync(s.Client);
        var alert = Assert.Single((await AlertsAsync(s.Client)).GetProperty("items").EnumerateArray());
        Assert.Equal("send_volume_anomaly", alert.GetProperty("kind").GetString());
        Assert.Equal(21, alert.GetProperty("details").GetProperty("sentToday").GetInt32());
        Assert.Equal(35, alert.GetProperty("details").GetProperty("sentPriorSevenDays").GetInt32());
    }

    /// <summary>AC-06: the alert path is independent of the outbound switch; a failing webhook is recorded, not fatal.</summary>
    [Fact]
    public async Task AlertDelivery_IsIndependentOfTheOutboundSwitch()
    {
        var s = await fixture.Api.NewCustomerAsync("Silent Co.");
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "SIL-1", 100.000m, dueDate: D(-10), issueDate: D(-40));
        await fixture.Database.ExecuteAsync("UPDATE invoices SET balance_cache = balance_cache - 1 WHERE id = @i", ("i", invoice));
        await s.Client.PutAsJsonAsync("/api/v1/organization/outbound", new { outboundSendingEnabled = false }, ApiScenario.Json);

        var mailBefore = fixture.Api.Mail.Sent;
        fixture.Api.Webhook.FailNext = 1;
        var previous = Environment.GetEnvironmentVariable("OUTBOUND_SENDING_ENABLED");
        Environment.SetEnvironmentVariable("OUTBOUND_SENDING_ENABLED", "false");
        try
        {
            var run = await RunAsync(s.Client);
            Assert.Equal("violations", run.GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OUTBOUND_SENDING_ENABLED", previous);
        }

        var alert = Assert.Single((await AlertsAsync(s.Client)).GetProperty("items").EnumerateArray());
        Assert.Equal("sent", alert.GetProperty("emailDelivery").GetString());
        Assert.Equal("failed", alert.GetProperty("webhookDelivery").GetString());
        Assert.Equal(mailBefore + 1, fixture.Api.Mail.Sent);
    }

    /// <summary>AC-07: the sweep runs the job; a Collector can neither run it nor read it.</summary>
    [Fact]
    public async Task Sweep_RunsTheJob_AndPermissionsHold()
    {
        var s = await fixture.Api.NewCustomerAsync("Swept Co.");
        var none = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/invariants", ApiScenario.Json);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("run").ValueKind);

        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        var latest = (await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/organization/invariants", ApiScenario.Json)).GetProperty("run");
        Assert.Equal("sweep", latest.GetProperty("trigger").GetString());
        Assert.Equal("ok", latest.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, latest.GetProperty("actorUserId").ValueKind);

        var collector = await fixture.Database.AddMemberAsync(s.Organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Collector);
        using var client = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(collector.Email, collector.Password));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/organization/invariants")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/organization/invariants/run", new { }, ApiScenario.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/organization/alerts")).StatusCode);
    }

    /// <summary>AC-08: B's broken invoice is B's problem; A's run, alerts and acknowledgements are A's.</summary>
    [Fact]
    public async Task Ops_IsTenantScoped()
    {
        var a = await fixture.Api.NewCustomerAsync("Tenant A Ops");
        var b = await fixture.Api.NewCustomerAsync("Tenant B Ops");
        var invoiceB = await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "B-1", 100.000m, dueDate: D(-10), issueDate: D(-40));
        await fixture.Database.ExecuteAsync("UPDATE invoices SET balance_cache = balance_cache - 1 WHERE id = @i", ("i", invoiceB));

        var runA = await RunAsync(a.Client);
        var runB = await RunAsync(b.Client);
        Assert.Equal("ok", runA.GetProperty("status").GetString());
        Assert.Equal("violations", runB.GetProperty("status").GetString());

        var alertB = Assert.Single((await AlertsAsync(b.Client)).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal(0, (await AlertsAsync(a.Client, all: true)).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsJsonAsync($"/api/v1/organization/alerts/{alertB}/acknowledge", new { }, ApiScenario.Json)).StatusCode);

        // RLS: A's scope sees none of B's rows, and a row written into A's scope with B's tenant id is refused.
        await using var app = fixture.Database.OpenApp();
        await using var tx = await app.BeginTransactionAsync();
        await using (var bind = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", app, tx))
        {
            bind.Parameters.AddWithValue("t", a.Organization.TenantId.ToString());
            await bind.ExecuteNonQueryAsync();
        }

        await using (var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM alerts) + (SELECT count(*) FROM invariant_runs WHERE tenant_id = @b)", app, tx))
        {
            count.Parameters.AddWithValue("b", b.Organization.TenantId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using (var smuggle = new NpgsqlCommand("INSERT INTO alerts (id, tenant_id, kind, severity, summary, dedupe_key) VALUES (gen_random_uuid(), @b, 'invariant_violation', 'critical', 'x', 'smuggled')", app, tx))
        {
            smuggle.Parameters.AddWithValue("b", b.Organization.TenantId);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => smuggle.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState);   // new row violates row-level security policy
        }
    }

    private Task SeedSuggestionAsync(Guid tenantId, string validationStatus) =>
        fixture.Database.ExecuteAsync(
            """
            INSERT INTO ai_suggestions (id, tenant_id, operation, subject_type, subject_id, model_name, model_digest, prompt_version, schema_version,
                                        input_ref, input_hash, output_json, confidence, validation_status, latency_ms)
            VALUES (gen_random_uuid(), @t, 'classify_customer_reply', 'tenant', @t, 'test-model', 'digest', 'v1', 'v1',
                    '{}', repeat('0', 64), '{}', 0.5, @s, 10)
            """, ("t", tenantId), ("s", validationStatus));

    private Task SeedSentAsync(Guid tenantId, Guid customerId, Guid approver, string ago) =>
        fixture.Database.ExecuteAsync(
            $"INSERT INTO messages (id, tenant_id, customer_id, channel, language, body, status, approval_required, approval_kind, approved_by, sent_at) VALUES (gen_random_uuid(), @t, @c, 'email', 'en', 'x', 'Sent', false, 'message', @u, now() - interval '{ago}')",
            ("t", tenantId), ("c", customerId), ("u", approver));

    private static async Task<string> MailAsync(string to, Guid alertId)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var search = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/search?query={Uri.EscapeDataString("to:" + to)}");
            foreach (var m in search.GetProperty("messages").EnumerateArray())
            {
                var message = await Mailpit.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:8025/api/v1/message/{m.GetProperty("ID").GetString()}");
                var text = message.GetProperty("Text").GetString() ?? string.Empty;
                if (text.Contains(alertId.ToString(), StringComparison.OrdinalIgnoreCase)) return text;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException($"no alert mail for {alertId}");
    }
}
