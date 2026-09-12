using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Infrastructure.Cases;
using Microsoft.Extensions.DependencyInjection;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// Slice 32: the daily job runs by itself. One pass enters every active tenant in its own scope, sweeps it as the
/// system, skips tenants that are not active, and keeps going past a tenant that fails.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SweepSchedulerTests(ApiTestFixture fixture)
{
    private static string D(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task OnePass_SweepsEveryActiveTenant_AsTheSystem()
    {
        var a = await fixture.Api.NewCustomerAsync("Sched A");
        var b = await fixture.Api.NewCustomerAsync("Sched B");
        var suspended = await fixture.Api.NewCustomerAsync("Sched Suspended");
        await fixture.Database.OpenInvoiceAsync(a.Organization.TenantId, a.CustomerId, "SCHED-A-1", 500m, dueDate: D(-20), issueDate: D(-50));
        await fixture.Database.OpenInvoiceAsync(b.Organization.TenantId, b.CustomerId, "SCHED-B-1", 700m, dueDate: D(-20), issueDate: D(-50));
        await fixture.Database.ExecuteAsync("UPDATE tenants SET status = 'Suspended' WHERE id = @t", ("t", suspended.Organization.TenantId));

        Assert.Equal(0, SweepRunner.IntervalMinutes);   // the hosted scheduler is idle in the test host; the pass is driven here
        var runner = fixture.Api.Services.GetRequiredService<SweepRunner>();
        var outcomes = await runner.RunOnceAsync(CancellationToken.None);

        var ids = outcomes.Select(o => o.TenantId).ToHashSet();
        Assert.Contains(a.Organization.TenantId, ids);
        Assert.Contains(b.Organization.TenantId, ids);
        Assert.DoesNotContain(suspended.Organization.TenantId, ids);
        var mine = new[] { a.Organization.TenantId, b.Organization.TenantId };
        Assert.All(outcomes.Where(o => mine.Contains(o.TenantId)), o => Assert.True(o.Succeeded, o.Error));   // other tests' tenants may be deliberately broken

        // The sweep did its work in each tenant — a case per overdue customer — and signed as the system.
        foreach (var s in new[] { a, b })
        {
            var cases = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={s.CustomerId}", ApiScenario.Json);
            Assert.Equal(1, cases.GetProperty("items").GetArrayLength());
            var run = await fixture.Database.ScalarAsync<string>("SELECT actor_kind FROM audit_events WHERE tenant_id = @t AND event_type = 'collection_case.sweep_run' ORDER BY id DESC LIMIT 1", ("t", s.Organization.TenantId));
            Assert.Equal("system", run);
        }

        Assert.Equal(0L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'collection_case.sweep_run'", ("t", suspended.Organization.TenantId)));

        // Idempotent: a second pass creates nothing new.
        await runner.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, (await a.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases?customerId={a.CustomerId}", ApiScenario.Json)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task OnePass_KeepsGoing_WhenOneTenantFails()
    {
        var ok = await fixture.Api.NewCustomerAsync("Sched OK");
        var broken = await fixture.Api.NewCustomerAsync("Sched Broken");
        // A tenant whose settings row is gone cannot be swept; the pass records the failure and still sweeps the others.
        // (Other tests assert no tenant is ever without settings, so the row is moved aside and put back.)
        var aside = await fixture.Database.ScalarAsync<string>("SELECT row_to_json(s)::text FROM tenant_settings s WHERE tenant_id = @t", ("t", broken.Organization.TenantId));
        await fixture.Database.ExecuteAsync("DELETE FROM tenant_settings WHERE tenant_id = @t", ("t", broken.Organization.TenantId));
        try
        {
            var outcomes = await fixture.Api.Services.GetRequiredService<SweepRunner>().RunOnceAsync(CancellationToken.None);
            var failed = Assert.Single(outcomes, o => o.TenantId == broken.Organization.TenantId);
            Assert.False(failed.Succeeded);
            Assert.NotNull(failed.Error);
            Assert.True(Assert.Single(outcomes, o => o.TenantId == ok.Organization.TenantId).Succeeded);
            Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE tenant_id = @t AND event_type = 'collection_case.sweep_run'", ("t", ok.Organization.TenantId)));
        }
        finally
        {
            await fixture.Database.ExecuteAsync("INSERT INTO tenant_settings SELECT * FROM json_populate_record(NULL::tenant_settings, @j::json)", ("j", aside!));
        }
    }
}
