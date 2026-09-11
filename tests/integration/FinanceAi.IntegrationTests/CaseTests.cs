using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Domain.Authorization;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>Slice 5 AC-02 … AC-16 against the real database and API.</summary>
[Collection(ApiCollection.Name)]
public sealed class CaseTests(ApiTestFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman")).DateTime);

    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private static async Task<JsonElement> SweepAsync(HttpClient client) => await client.PostAsync("/api/v1/cases/sweep", new { });

    private static async Task<JsonElement> QueueAsync(HttpClient client, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>("/api/v1/queue" + query, ApiScenario.Json);

    private static async Task<JsonElement> CaseAsync(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{id}", ApiScenario.Json);

    private static async Task<Guid> OpenCaseAsync(ApiTestFixture f, Setup s, int daysOverdue = 10, decimal total = 1_000m, string number = "C-1")
    {
        await f.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, number, total, dueDate: D(daysOverdue), issueDate: D(daysOverdue + 30));
        await SweepAsync(s.Client);
        var queue = await QueueAsync(s.Client);
        return queue.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("customer").GetProperty("id").GetGuid() == s.CustomerId).GetProperty("caseId").GetGuid();
    }

    /// <summary>AC-05 / C1 / T-13.</summary>
    [Fact]
    public async Task Sweep_CreatesCases_PastGrace_Idempotently()
    {
        var s = await fixture.Api.NewCustomerAsync("Grace Co.");
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "G-1", 500m, dueDate: D(2), issueDate: D(32));
        var first = await SweepAsync(s.Client);
        Assert.Equal(0, first.GetProperty("created").GetInt32());   // 2 days overdue, grace 3

        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "G-2", 700m, dueDate: D(4), issueDate: D(34));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "G-3", 100m, dueDate: D(-5), issueDate: D(25));   // not yet due
        var second = await SweepAsync(s.Client);
        Assert.Equal(1, second.GetProperty("created").GetInt32());

        var queue = await QueueAsync(s.Client);
        var item = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal("Open", item.GetProperty("status").GetString());
        Assert.Equal(2, item.GetProperty("invoiceCount").GetInt32());     // G-1 and G-2 in scope; G-3 not due
        Assert.Equal(4, item.GetProperty("maxDaysPastDue").GetInt32());
        Assert.Equal("1200.000", item.GetProperty("overdueBalances")[0].GetProperty("openBalance").GetProperty("amount").GetString());
        Assert.True(item.GetProperty("priorityScore").GetInt32() > 0);
        Assert.Equal(1, item.GetProperty("weightsVersion").GetInt32());
        Assert.Equal("Days1To30", item.GetProperty("bucket").GetString());
        var caseId = item.GetProperty("caseId").GetGuid();

        // Idempotent: nothing created, nothing transitioned, no second audit row for the case.
        var third = await SweepAsync(s.Client);
        Assert.Equal(0, third.GetProperty("created").GetInt32());
        Assert.Equal(0, third.GetProperty("resolved").GetInt32());
        Assert.Equal(1, third.GetProperty("rescored").GetInt32());
        Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM audit_events WHERE entity_id = @c AND event_type = 'collection_case.status_changed'", ("c", caseId)));
        Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM collection_cases WHERE customer_id = @cu", ("cu", s.CustomerId)));
        Assert.Equal(1L, await fixture.Database.ScalarAsync<long>("SELECT case_number FROM collection_cases WHERE id = @c", ("c", caseId)));
    }

    /// <summary>AC-02 / T-11 / INV-06 (service half).</summary>
    [Fact]
    public async Task Guards_HoldEscalateAbandon_AndManualCreation()
    {
        var s = await fixture.Api.NewCustomerAsync("Guard Co.");
        var (noOverdue, _) = await s.Client.TryPostAsync("/api/v1/cases", new { customerId = s.CustomerId });
        Assert.Equal(422, noOverdue);

        var caseId = await OpenCaseAsync(fixture, s);
        var (dup, dupBody) = await s.Client.TryPostAsync("/api/v1/cases", new { customerId = s.CustomerId });
        Assert.Equal(409, dup);
        Assert.Equal("duplicate", dupBody.GetProperty("code").GetString());

        var (h1, h1b) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", reasonCode = "bereavement" });
        Assert.Equal(422, h1);
        Assert.Equal("hold_requires_reason_and_until", h1b.GetProperty("errors")[0].GetProperty("code").GetString());
        var (h2, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", holdUntil = D(-10) });
        Assert.Equal(422, h2);
        var (h3, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", reasonCode = "bereavement", holdUntil = D(0) });
        Assert.Equal(422, h3);   // until must be in the future

        var (e1, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "escalate" });
        Assert.Equal(422, e1);
        var (a1, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "abandon" });
        Assert.Equal(422, a1);

        // Doc 05: escalate and abandon need cases.escalate — an Accountant holds cases.write but not that.
        var accountant = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Accountant);
        using var accountantClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));
        var (forbidden, _) = await accountantClient.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "escalate", reasonCode = "legal" });
        Assert.Equal(403, forbidden);
        var (okHold, held) = await accountantClient.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", reasonCode = "standstill", holdUntil = D(-7) });
        Assert.Equal(200, okHold);
        Assert.Equal("OnHold", held.GetProperty("status").GetString());

        var (bad, badBody) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", reasonCode = "again", holdUntil = D(-7) });
        Assert.Equal(409, bad);
        Assert.Equal("invalid_transition", badBody.GetProperty("code").GetString());
        var (unknown, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "ptp_recorded" });
        Assert.Equal(400, unknown);   // not a user event
    }

    /// <summary>AC-03: the partial unique index, not the service, refuses a second open case.</summary>
    [Fact]
    public async Task OneOpenCasePerCustomer_IsEnforcedByTheIndex()
    {
        var s = await fixture.Api.NewCustomerAsync("Index Co.");
        var caseId = await OpenCaseAsync(fixture, s);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Database.ExecuteAsync(
            "INSERT INTO collection_cases (id, tenant_id, customer_id, case_number, status) VALUES (gen_random_uuid(), @t, @cu, 999, 'InProgress')",
            ("t", s.Organization.TenantId), ("cu", s.CustomerId)));
        Assert.Equal("23505", ex.SqlState);
        Assert.Contains("one_open_case_per_customer", ex.Message, StringComparison.Ordinal);

        // Once the first is terminal, a new one is allowed (SM-05: a new object, not a resurrection).
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "abandon", reasonCode = "immaterial" });
        Assert.Equal(1, await fixture.Database.ExecuteAsync(
            "INSERT INTO collection_cases (id, tenant_id, customer_id, case_number, status) VALUES (gen_random_uuid(), @t, @cu, 999, 'InProgress')",
            ("t", s.Organization.TenantId), ("cu", s.CustomerId)));
    }

    /// <summary>AC-04 / SM-26 / T-12.</summary>
    [Fact]
    public async Task Escalation_StopsAutomationPermanently()
    {
        var s = await fixture.Api.NewCustomerAsync("Escalate Co.");
        var caseId = await OpenCaseAsync(fixture, s);
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/snooze", new { untilDate = D(-3), reason = "later" });

        var escalated = await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "escalate", reasonCode = "lawyer", note = "sent to counsel" });
        Assert.Equal("Escalated", escalated.GetProperty("status").GetString());
        Assert.True(escalated.GetProperty("automationDisabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, escalated.GetProperty("nextActionAt").ValueKind);   // nothing scheduled survives
        Assert.Equal("manual_follow_up", escalated.GetProperty("suggestedAction").GetProperty("kind").GetString());

        Assert.DoesNotContain(caseId, (await QueueAsync(s.Client)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("caseId").GetGuid()));

        // Every event but balance_zero and abandon is refused at the API; snoozing is refused outright.
        foreach (var ev in new[] { "hold", "resume", "contact_logged" })
        {
            var (status, body) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = ev, reasonCode = "x", holdUntil = D(-9) });
            Assert.Equal(409, status);
            Assert.Equal("invalid_transition", body.GetProperty("code").GetString());
        }

        var (snooze, snoozeBody) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/snooze", new { untilDate = D(-3) });
        Assert.Equal(422, snooze);
        Assert.Equal("automation_disabled", snoozeBody.GetProperty("errors")[0].GetProperty("code").GetString());

        // The sweep leaves it alone: no transition, still escalated, still out of the queue.
        await SweepAsync(s.Client);
        var detail = await CaseAsync(s.Client, caseId);
        Assert.Equal("Escalated", detail.GetProperty("case").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("escalatedAt").ValueKind);

        // Resolution does not clear the flag.
        var invoice = await fixture.Database.ScalarAsync<Guid>("SELECT invoice_id FROM case_invoices WHERE case_id = @c", ("c", caseId));
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(1_000m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = invoice, amount = M(1_000m) } } });
        detail = await CaseAsync(s.Client, caseId);
        Assert.Equal("Resolved", detail.GetProperty("case").GetProperty("status").GetString());
        Assert.True(detail.GetProperty("case").GetProperty("automationDisabled").GetBoolean());
    }

    /// <summary>AC-06 / C2 / C3 / C8 / T-12, with the host clock pinned to walk the calendar.</summary>
    [Fact]
    public async Task Suppression_LeavesAndReturnsOnSchedule()
    {
        var s = await fixture.Api.NewCustomerAsync("Snooze Co.");
        var caseId = await OpenCaseAsync(fixture, s);
        bool InQueue(JsonElement q) => q.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("caseId").GetGuid() == caseId);

        try
        {
            // Snooze: gone now, back the morning of the date.
            await s.Client.PostAsync($"/api/v1/cases/{caseId}/snooze", new { untilDate = D(-2), reason = "asked to call back" });
            Assert.False(InQueue(await QueueAsync(s.Client)));
            var summary = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue/summary", ApiScenario.Json);
            Assert.Equal(1, summary.GetProperty("suppressed").GetInt32());

            fixture.Api.Clock.Override = new DateTimeOffset(Today.AddDays(2).ToDateTime(new TimeOnly(6, 0)), TimeSpan.FromHours(3));
            Assert.True(InQueue(await QueueAsync(s.Client)));

            // Hold: a state, needs the sweep to expire.
            fixture.Api.ResetClock();
            await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", reasonCode = "ramadan_courtesy", holdUntil = D(-3) });
            Assert.False(InQueue(await QueueAsync(s.Client)));
            await SweepAsync(s.Client);
            Assert.Equal("OnHold", (await CaseAsync(s.Client, caseId)).GetProperty("case").GetProperty("status").GetString());

            fixture.Api.Clock.Override = new DateTimeOffset(Today.AddDays(3).ToDateTime(new TimeOnly(6, 0)), TimeSpan.FromHours(3));
            var swept = await SweepAsync(s.Client);
            Assert.Equal(1, swept.GetProperty("resumed").GetInt32());
            Assert.Equal("InProgress", (await CaseAsync(s.Client, caseId)).GetProperty("case").GetProperty("status").GetString());
            Assert.True(InQueue(await QueueAsync(s.Client)));

            // AwaitingCustomer with a follow-up date: no user event reaches it before slice 8, so set it as the send path will.
            await fixture.Database.ExecuteAsync("UPDATE collection_cases SET status = 'AwaitingCustomer', next_action_at = @at WHERE id = @c",
                ("at", new DateTimeOffset(Today.AddDays(5).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)), ("c", caseId));
            Assert.False(InQueue(await QueueAsync(s.Client)));
            fixture.Api.Clock.Override = new DateTimeOffset(Today.AddDays(5).ToDateTime(new TimeOnly(6, 0)), TimeSpan.FromHours(3));
            swept = await SweepAsync(s.Client);
            Assert.Equal(1, swept.GetProperty("followedUp").GetInt32());
            Assert.Equal("InProgress", (await CaseAsync(s.Client, caseId)).GetProperty("case").GetProperty("status").GetString());
            Assert.True(InQueue(await QueueAsync(s.Client)));
        }
        finally
        {
            fixture.Api.ResetClock();
        }
    }

    /// <summary>AC-07 / C10 / SM-50.</summary>
    [Fact]
    public async Task SettlingEverything_ResolvesTheCase()
    {
        var s = await fixture.Api.NewCustomerAsync("Settle Co.");
        var a = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "S-1", 1_000m, dueDate: D(10), issueDate: D(40));
        var b = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "S-2", 500m, dueDate: D(20), issueDate: D(50));
        await SweepAsync(s.Client);
        var caseId = (await QueueAsync(s.Client)).GetProperty("items")[0].GetProperty("caseId").GetGuid();
        var before = (await CaseAsync(s.Client, caseId)).GetProperty("case").GetProperty("priorityScore").GetInt32();

        // Partial: still open, rescored, scope unchanged.
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(400m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = a, amount = M(400m) } } });
        var mid = await CaseAsync(s.Client, caseId);
        Assert.Equal("Open", mid.GetProperty("case").GetProperty("status").GetString());
        Assert.True(mid.GetProperty("case").GetProperty("priorityScore").GetInt32() <= before);
        Assert.Equal("1100.000", mid.GetProperty("case").GetProperty("overdueBalances")[0].GetProperty("openBalance").GetProperty("amount").GetString());

        // Settle S-1 fully and write off S-2: the second event resolves the case in its own request.
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(600m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = a, amount = M(600m) } } });
        Assert.Equal("Open", (await CaseAsync(s.Client, caseId)).GetProperty("case").GetProperty("status").GetString());
        var proposal = await s.Client.PostAsync($"/api/v1/invoices/{b}/write-off", new { reasonCode = "uncollectible" });
        await s.Client.ReauthAsync();   // SEC-09 (slice 13)
        await s.Client.PostAsync($"/api/v1/write-offs/{proposal.GetProperty("id").GetGuid()}/approve", new { selfApproved = true });

        var after = await CaseAsync(s.Client, caseId);
        Assert.Equal("Resolved", after.GetProperty("case").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("closedAt").ValueKind);
        Assert.Equal("balance_zero", after.GetProperty("closeReason").GetString());
        Assert.All(after.GetProperty("invoices").EnumerateArray(), i => Assert.NotEqual(JsonValueKind.Null, i.GetProperty("removedAt").ValueKind));
        Assert.Contains(after.GetProperty("invoices").EnumerateArray(), i => i.GetProperty("removedReason").GetString() == "writtenoff");
        Assert.Empty((await QueueAsync(s.Client)).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await SweepAsync(s.Client)).GetProperty("created").GetInt32());
    }

    /// <summary>AC-09 / FIN-80 / FIN-81.</summary>
    [Fact]
    public async Task CaseDetail_FactorsSumToScore()
    {
        var s = await fixture.Api.NewCustomerAsync("Factor Co.");
        await fixture.Database.ExecuteAsync("UPDATE customers SET risk_flag = 'Watch', bounced_cheque_count_12m = 1 WHERE id = @c", ("c", s.CustomerId));
        var caseId = await OpenCaseAsync(fixture, s, daysOverdue: 62, total: 8_200m);
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/activities", new { kind = "call", summary = "Spoke to the accountant" });

        var item = (await CaseAsync(s.Client, caseId)).GetProperty("case");
        var factors = item.GetProperty("priorityFactors").EnumerateArray().ToList();
        Assert.Equal(item.GetProperty("priorityScore").GetInt32(), factors.Sum(f => f.GetProperty("contribution").GetInt32()));
        Assert.Equal(1, item.GetProperty("weightsVersion").GetInt32());
        Assert.Equal(["amount", "days_past_due", "broken_promises", "bounced_cheques", "customer_value", "recent_contact"], factors.Select(f => f.GetProperty("factor").GetString()));
        Assert.Equal(29, factors[0].GetProperty("contribution").GetInt32());    // 35 × 8200/10000 = 28.7 → 29
        Assert.Equal(16, factors[1].GetProperty("contribution").GetInt32());    // 30 × 62/120 = 15.5 → 16
        Assert.Equal(5, factors[3].GetProperty("contribution").GetInt32());     // 10 × 1/2
        Assert.Equal(5, factors[4].GetProperty("contribution").GetInt32());     // Watch
        Assert.Equal(-10, factors[5].GetProperty("contribution").GetInt32());   // contacted today
        Assert.Equal(45, item.GetProperty("priorityScore").GetInt32());
        Assert.Equal("InProgress", item.GetProperty("status").GetString());     // C2 on the call
        Assert.Equal("dunning_30", item.GetProperty("suggestedAction").GetProperty("templateKey").GetString());
    }

    /// <summary>AC-10 / PRD-14.</summary>
    [Fact]
    public async Task CollectorScoping_IsServerSide()
    {
        var s = await fixture.Api.NewCustomerAsync("Scope A");
        var other = (await s.Client.PostAsync("/api/v1/customers", new { nameEn = "Scope B", defaultCurrency = "JOD" })).GetProperty("id").GetGuid();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "P-A", 100m, dueDate: D(10), issueDate: D(40));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, other, "P-B", 200m, dueDate: D(10), issueDate: D(40));
        await SweepAsync(s.Client);
        var items = (await QueueAsync(s.Client)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var mine = items.Single(i => i.GetProperty("customer").GetProperty("id").GetGuid() == s.CustomerId).GetProperty("caseId").GetGuid();
        var theirs = items.Single(i => i.GetProperty("customer").GetProperty("id").GetGuid() == other).GetProperty("caseId").GetGuid();

        var collector = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Collector);
        using var collectorClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(collector.Email, collector.Password));
        await s.Client.PostAsync($"/api/v1/cases/{mine}/assign", new { userId = collector.UserId });

        // Off (default): a Collector sees everything.
        Assert.Equal(2, (await QueueAsync(collectorClient)).GetProperty("items").GetArrayLength());
        Assert.False((await QueueAsync(collectorClient)).GetProperty("scopedToAssignee").GetBoolean());

        await fixture.Database.ExecuteAsync("UPDATE tenant_settings SET collector_sees_only_assigned = true WHERE tenant_id = @t", ("t", s.Organization.TenantId));
        var scoped = await QueueAsync(collectorClient, "?assignedTo=all");   // the query cannot widen it
        Assert.True(scoped.GetProperty("scopedToAssignee").GetBoolean());
        Assert.Equal([mine], scoped.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("caseId").GetGuid()));
        var list = await collectorClient.GetFromJsonAsync<JsonElement>("/api/v1/cases", ApiScenario.Json);
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        var summary = await collectorClient.GetFromJsonAsync<JsonElement>("/api/v1/queue/summary", ApiScenario.Json);
        Assert.Equal(1, summary.GetProperty("queueSize").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await collectorClient.GetAsync($"/api/v1/cases/{theirs}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await collectorClient.GetAsync($"/api/v1/cases/{mine}")).StatusCode);

        // An Accountant is not scoped.
        var accountant = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Accountant);
        using var accountantClient = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(accountant.Email, accountant.Password));
        Assert.Equal(2, (await QueueAsync(accountantClient)).GetProperty("items").GetArrayLength());
    }

    /// <summary>AC-11.</summary>
    [Fact]
    public async Task Assign_ValidatesMembership()
    {
        var s = await fixture.Api.NewCustomerAsync("Assign Co.");
        var caseId = await OpenCaseAsync(fixture, s);
        var member = await fixture.Database.AddMemberAsync(s.Organization.TenantId, TenantRole.Collector);
        var assigned = await s.Client.PostAsync($"/api/v1/cases/{caseId}/assign", new { userId = member.UserId });
        Assert.Equal(member.UserId, assigned.GetProperty("assignedTo").GetGuid());

        var stranger = await fixture.Api.CreateOrganizationAsync();
        var (foreign, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/assign", new { userId = stranger.OwnerUserId });
        Assert.Equal(404, foreign);

        await fixture.Database.ExecuteAsync("UPDATE tenant_memberships SET status = 'Disabled' WHERE user_id = @u", ("u", member.UserId));
        var (disabled, body) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/assign", new { userId = member.UserId });
        Assert.Equal(422, disabled);
        Assert.Equal("member_not_active", body.GetProperty("errors")[0].GetProperty("code").GetString());

        var unassigned = await s.Client.PostAsync($"/api/v1/cases/{caseId}/assign", new { userId = (Guid?)null });
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("assignedTo").ValueKind);
        var timeline = await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{caseId}/timeline", ApiScenario.Json);
        Assert.Equal(2, timeline.GetProperty("items").EnumerateArray().Count(e => e.GetProperty("summary").GetString() is "Assigned" or "Unassigned"));
    }

    /// <summary>AC-12 / SM-03 / INV-12.</summary>
    [Fact]
    public async Task Transitions_AreAudited_OneToOne()
    {
        var s = await fixture.Api.NewCustomerAsync("Audit Co.");
        var caseId = await OpenCaseAsync(fixture, s);
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/activities", new { kind = "call", summary = "called" });              // C2
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "hold", reasonCode = "standstill", holdUntil = D(-30) });
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "resume" });
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "escalate", reasonCode = "legal" });
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/transitions", new { @event = "abandon", reasonCode = "unreachable" });

        var audit = await fixture.Database.ScalarAsync<string>(
            "SELECT string_agg(coalesce(from_state, '-') || '>' || to_state || ':' || actor_kind, ',' ORDER BY id) FROM audit_events WHERE entity_id = @c AND event_type = 'collection_case.status_changed'", ("c", caseId));
        Assert.Equal("->Open:system,Open>InProgress:user,InProgress>OnHold:user,OnHold>InProgress:user,InProgress>Escalated:user,Escalated>Abandoned:user", audit);
        var activities = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM case_activities WHERE case_id = @c AND kind = 'status_change'", ("c", caseId));
        Assert.Equal(6L, activities);
        var detail = await CaseAsync(s.Client, caseId);
        Assert.Equal("Abandoned", detail.GetProperty("case").GetProperty("status").GetString());
        // FIN-33: abandonment is not a write-off; the invoice is still open.
        Assert.Equal("Open", detail.GetProperty("invoices")[0].GetProperty("status").GetString());
    }

    /// <summary>AC-13.</summary>
    [Fact]
    public async Task Queue_OrdersAndFilters()
    {
        var s = await fixture.Api.NewCustomerAsync("Order A");
        var b = (await s.Client.PostAsync("/api/v1/customers", new { nameEn = "Order B", defaultCurrency = "JOD" })).GetProperty("id").GetGuid();
        var c = (await s.Client.PostAsync("/api/v1/customers", new { nameEn = "Order C", defaultCurrency = "JOD" })).GetProperty("id").GetGuid();
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, "O-A", 100m, dueDate: D(10), issueDate: D(40));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, b, "O-B", 9_000m, dueDate: D(95), issueDate: D(125));
        await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, c, "O-C", 3_000m, dueDate: D(40), issueDate: D(70));
        await SweepAsync(s.Client);

        var items = (await QueueAsync(s.Client)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([b, c, s.CustomerId], items.Select(i => i.GetProperty("customer").GetProperty("id").GetGuid()));
        Assert.True(items[0].GetProperty("priorityScore").GetInt32() > items[1].GetProperty("priorityScore").GetInt32());
        Assert.Equal(["Days90Plus", "Days31To60", "Days1To30"], items.Select(i => i.GetProperty("bucket").GetString()));

        var bucket = await QueueAsync(s.Client, "?bucket=Days31To60");
        Assert.Equal([c], bucket.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("customer").GetProperty("id").GetGuid()));
        var min = await QueueAsync(s.Client, "?minAmount=2500");
        Assert.Equal(2, min.GetProperty("totalCount").GetInt32());
        var mine = await QueueAsync(s.Client, "?assignedTo=me");
        Assert.Equal(0, mine.GetProperty("totalCount").GetInt32());
        var summary = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/queue/summary", ApiScenario.Json);
        Assert.Equal(3, summary.GetProperty("queueSize").GetInt32());
        Assert.Equal(3, summary.GetProperty("byStatus").GetProperty("Open").GetInt32());
        Assert.Equal(1, summary.GetProperty("byBucket").GetProperty("Days90Plus").GetInt32());
    }

    /// <summary>AC-14.</summary>
    [Fact]
    public async Task Timeline_MergesActivitiesAndPayments()
    {
        var s = await fixture.Api.NewCustomerAsync("Timeline Co.");
        var caseId = await OpenCaseAsync(fixture, s, total: 1_000m);
        var invoice = await fixture.Database.ScalarAsync<Guid>("SELECT invoice_id FROM case_invoices WHERE case_id = @c", ("c", caseId));
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/activities", new { kind = "note", summary = "Promised to check with the bank", detail = new { channel = "phone" } });
        await s.Client.PostAsync("/api/v1/payments", new { customerId = s.CustomerId, amount = M(250m), method = "Cash", receivedDate = D(0), allocations = new[] { new { invoiceId = invoice, amount = M(250m) } } });
        await s.Client.PostAsync($"/api/v1/cases/{caseId}/activities", new { kind = "meeting", summary = "Met the owner" });

        var timeline = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/v1/cases/{caseId}/timeline", ApiScenario.Json)).GetProperty("items").EnumerateArray().ToList();
        var kinds = timeline.Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Equal("status_change", kinds[0]);                         // opened
        Assert.Contains("note", kinds);
        Assert.Contains("payment", kinds);
        Assert.Contains("meeting", kinds);
        Assert.True(kinds.IndexOf("note") < kinds.IndexOf("payment") && kinds.IndexOf("payment") < kinds.IndexOf("meeting"));
        var payment = timeline.Single(e => e.GetProperty("kind").GetString() == "payment");
        Assert.Equal("250.000", payment.GetProperty("detail").GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal(timeline.Select(e => e.GetProperty("occurredAt").GetString()).Order(StringComparer.Ordinal), timeline.Select(e => e.GetProperty("occurredAt").GetString()));
        // A note is not contact (C2 fires on the meeting, not the note).
        Assert.Equal("InProgress", (await CaseAsync(s.Client, caseId)).GetProperty("case").GetProperty("status").GetString());
        var (bad, _) = await s.Client.TryPostAsync($"/api/v1/cases/{caseId}/activities", new { kind = "status_change", summary = "nope" });
        Assert.Equal(422, bad);
    }

    /// <summary>AC-16 / doc 10 acceptance 8.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public async Task Queue_P95_Under800ms()
    {
        var s = await fixture.Api.NewCustomerAsync("Queue Perf");
        var tenant = s.Organization.TenantId;
        await fixture.Database.ExecuteAsync(
            """
            INSERT INTO customers (id, tenant_id, code, name_en, payment_terms_days)
            SELECT gen_random_uuid(), @t, 'Q-' || g, 'Queue customer ' || g, 30 FROM generate_series(1, 5000) g;
            INSERT INTO invoices (id, tenant_id, customer_id, invoice_number, status, issue_date, due_date, currency, net_amount, tax_amount, total_amount, balance_cache, base_currency)
            SELECT gen_random_uuid(), @t, c.id, 'Q-' || c.code, 'Open', current_date - 60 - (row_number() over () % 100)::int, current_date - 30 - (row_number() over () % 100)::int, 'JOD', 1000, 160, 1160, 1160, 'JOD'
            FROM customers c WHERE c.tenant_id = @t AND c.code LIKE 'Q-%';
            INSERT INTO collection_cases (id, tenant_id, customer_id, case_number, status, priority_score, max_days_past_due, invoice_count, overdue_balance_base, priority_factors)
            SELECT gen_random_uuid(), @t, c.id, row_number() over (), 'InProgress', (row_number() over ()) % 100, 30 + (row_number() over ()) % 100, 1, 1160, '[{"factor":"amount","contribution":4,"detail":"seed"}]'
            FROM customers c WHERE c.tenant_id = @t AND c.code LIKE 'Q-%';
            INSERT INTO case_invoices (tenant_id, case_id, invoice_id)
            SELECT @t, k.id, i.id FROM collection_cases k JOIN invoices i ON i.tenant_id = k.tenant_id AND i.customer_id = k.customer_id WHERE k.tenant_id = @t;
            ANALYZE collection_cases; ANALYZE case_invoices;
            """, ("t", tenant));

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await s.Client.GetAsync("/api/v1/queue?limit=50");
            watch.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var p95 = timings.Order().ElementAt((int)Math.Ceiling(timings.Count * 0.95) - 1);
        output.WriteLine($"queue P95 {p95:F0} ms, min {timings.Min():F0}, max {timings.Max():F0} over {timings.Count} calls at 5,000 cases");
        Assert.True(p95 < 800, $"queue P95 was {p95:F0} ms");
    }
}
