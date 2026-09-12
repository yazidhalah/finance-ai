using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using FinanceAi.TestSupport;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// T-142 (doc 09 §7): a load profile of 20 concurrent users on one tenant, sustained, with no error-rate increase.
/// Twenty distinct members (API-13 limits each user to 600 requests a minute; twenty users each paced under that is
/// the shape the requirement describes), a read mix over the screens they would keep open, and the run split into
/// five windows so a rising error rate shows as a trend, not a total.
/// <c>LOAD_PROFILE_SECONDS</c> sets the duration: the nightly runs the specified ten minutes; locally one is enough to
/// see the shape.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LoadProfileTests(ApiTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Users = 20;
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(150);   // ≤ 400 requests a minute per user: under the 600 limit with headroom

    private sealed record Sample(int Window, int Status, double Milliseconds);

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TwentyConcurrentUsers_Sustained_NoErrorRateIncrease()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("LOAD_PROFILE_SECONDS"), out var configured) && configured > 0 ? configured : 60;
        var duration = TimeSpan.FromSeconds(seconds);
        const int windows = 5;

        var org = await fixture.Api.CreateOrganizationAsync("Load Co.");
        var tenant = org.TenantId;
        await fixture.Database.ExecuteAsync(
            """
            INSERT INTO customers (id, tenant_id, code, name_en, payment_terms_days)
            SELECT gen_random_uuid(), @t, 'LOAD-' || g, 'Load customer ' || g, 30 FROM generate_series(1, 2000) g;
            INSERT INTO invoices (id, tenant_id, customer_id, invoice_number, status, issue_date, due_date, currency, net_amount, tax_amount, total_amount, balance_cache, base_currency)
            SELECT gen_random_uuid(), @t, c.id, 'LOAD-' || c.code, 'Open', current_date - 60 - (row_number() over () % 100)::int, current_date - 30 - (row_number() over () % 100)::int, 'JOD', 1000, 160, 1160, 1160, 'JOD'
            FROM customers c WHERE c.tenant_id = @t AND c.code LIKE 'LOAD-%';
            INSERT INTO collection_cases (id, tenant_id, customer_id, case_number, status, priority_score, max_days_past_due, invoice_count, overdue_balance_base, priority_factors)
            SELECT gen_random_uuid(), @t, c.id, row_number() over (), 'InProgress', (row_number() over ()) % 100, 30 + (row_number() over ()) % 100, 1, 1160, '[{"factor":"amount","contribution":4,"detail":"seed"}]'
            FROM customers c WHERE c.tenant_id = @t AND c.code LIKE 'LOAD-%';
            INSERT INTO case_invoices (tenant_id, case_id, invoice_id)
            SELECT @t, k.id, i.id FROM collection_cases k JOIN invoices i ON i.tenant_id = k.tenant_id AND i.customer_id = k.customer_id WHERE k.tenant_id = @t;
            ANALYZE customers; ANALYZE invoices; ANALYZE collection_cases; ANALYZE case_invoices;
            """, ("t", tenant));

        var customerIds = new List<Guid>();
        var invoiceIds = new List<Guid>();
        var caseIds = new List<Guid>();
        await using (var connection = fixture.Database.OpenAdmin())
        {
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT c.id, i.id, k.id FROM customers c JOIN invoices i ON i.tenant_id = c.tenant_id AND i.customer_id = c.id JOIN collection_cases k ON k.tenant_id = c.tenant_id AND k.customer_id = c.id WHERE c.tenant_id = @t ORDER BY c.code LIMIT 200", connection);
            command.Parameters.AddWithValue("t", tenant);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                customerIds.Add(reader.GetGuid(0));
                invoiceIds.Add(reader.GetGuid(1));
                caseIds.Add(reader.GetGuid(2));
            }
        }

        Assert.Equal(200, customerIds.Count);

        var clients = new List<HttpClient>();
        for (var u = 0; u < Users; u++)
        {
            var member = await fixture.Database.AddMemberAsync(tenant, TenantRole.Accountant);
            var session = await fixture.Api.LoginAsync(member.Email, member.Password);
            clients.Add(fixture.Api.AuthenticatedClient(session));
        }

        // Warm the paths once so the first window measures the steady state, not JIT and connection setup.
        foreach (var path in Mix(customerIds[0], invoiceIds[0], caseIds[0])) Assert.Equal(HttpStatusCode.OK, (await clients[0].GetAsync(path)).StatusCode);

        var samples = new System.Collections.Concurrent.ConcurrentBag<Sample>();
        var clock = Stopwatch.StartNew();
        var workers = clients.Select((client, u) => Task.Run(async () =>
        {
            var n = u;
            while (clock.Elapsed < duration)
            {
                var started = clock.Elapsed;
                var path = Mix(customerIds[n % customerIds.Count], invoiceIds[n % invoiceIds.Count], caseIds[n % caseIds.Count])[n % 6];
                int status;
                try
                {
                    using var response = await client.GetAsync(path);
                    status = (int)response.StatusCode;
                }
                catch (HttpRequestException)
                {
                    status = 0;
                }

                var elapsed = clock.Elapsed - started;
                var window = Math.Min(windows - 1, (int)(started.TotalSeconds * windows / duration.TotalSeconds));
                samples.Add(new Sample(window, status, elapsed.TotalMilliseconds));
                n += 7;
                if (elapsed < MinimumGap) await Task.Delay(MinimumGap - elapsed);
            }
        })).ToArray();
        await Task.WhenAll(workers);
        foreach (var client in clients) client.Dispose();

        var byWindow = samples.GroupBy(x => x.Window).OrderBy(g => g.Key).ToList();
        Assert.Equal(windows, byWindow.Count);
        var rates = new List<double>();
        foreach (var w in byWindow)
        {
            var count = w.Count();
            var errors = w.Count(x => x.Status is < 200 or >= 300);
            var p95 = w.Select(x => x.Milliseconds).Order().ElementAt((int)Math.Ceiling(count * 0.95) - 1);
            rates.Add((double)errors / count);
            output.WriteLine($"T-142 window {w.Key + 1}/{windows}: {count} requests, {errors} errors ({rates[^1]:P2}), P95 {p95:F0} ms, max {w.Max(x => x.Milliseconds):F0} ms");
        }

        var total = samples.Count;
        var totalErrors = samples.Count(x => x.Status is < 200 or >= 300);
        var overallP95 = samples.Select(x => x.Milliseconds).Order().ElementAt((int)Math.Ceiling(total * 0.95) - 1);
        output.WriteLine($"T-142: {Users} users for {duration.TotalSeconds:F0} s — {total} requests ({total / duration.TotalSeconds:F1}/s), {totalErrors} errors, P95 {overallP95:F0} ms; statuses {string.Join(", ", samples.GroupBy(x => x.Status).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"))}");

        Assert.DoesNotContain(samples, x => x.Status >= 500 || x.Status == 0);                          // nothing broke
        Assert.DoesNotContain(samples, x => x.Status == 429);                                           // the pacing keeps every user under API-13
        Assert.True(rates[^1] <= rates[0], $"error rate rose from {rates[0]:P2} in the first window to {rates[^1]:P2} in the last");
        Assert.True(total >= Users * windows, "every user produced samples in every window");
    }

    /// <summary>The read mix: the queue, the aging report, a customer, an invoice, a case, a customer search.</summary>
    private static string[] Mix(Guid customerId, Guid invoiceId, Guid caseId) =>
    [
        "/api/v1/queue?limit=50",
        "/api/v1/reports/aging?groupBy=customer",
        $"/api/v1/customers/{customerId}",
        $"/api/v1/invoices/{invoiceId}",
        $"/api/v1/cases/{caseId}",
        "/api/v1/customers?search=Load%20customer%2017&limit=20",
    ];
}
