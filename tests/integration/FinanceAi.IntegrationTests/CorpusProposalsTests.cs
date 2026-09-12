using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Infrastructure.Ai;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// T-109 (doc 09 §5.4): human corrections become corpus proposals, one tenant at a time, only with a named consent
/// reference, in the importer's column contract. Slice 30.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CorpusProposalsTests(ApiTestFixture fixture)
{
    private static string D(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(days)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(Setup S, Guid Invoice)> OpenAsync(string name)
    {
        var s = await fixture.Api.NewCustomerAsync(name);
        var email = $"{Guid.NewGuid():N}@example.test";
        await s.Client.PostAsync($"/api/v1/customers/{s.CustomerId}/contacts", new { name = "Accounts", email, isPrimary = true, isBilling = true });
        var invoice = await fixture.Database.OpenInvoiceAsync(s.Organization.TenantId, s.CustomerId, $"{name}-1", 1_500m, dueDate: D(-20), issueDate: D(-50));
        await s.Client.PostAsync("/api/v1/cases/sweep", new { });
        fixture.Api.Ai.Clear();
        return (s, invoice);
    }

    private static async Task<JsonElement> IngestAndClassifyAsync(Setup s, string body, string scripted)
    {
        var message = await s.Client.PostAsync("/api/v1/inbound-messages", new { channel = "email", fromAddress = $"{Guid.NewGuid():N}@example.test", subject = "Re: reminder", body, customerId = s.CustomerId });
        return await s.Client.PostAsync($"/api/v1/inbound-messages/{message.GetProperty("id").GetGuid()}/classify", new { });
    }

    [Fact]
    public async Task Corrections_BecomeProposals_ForTheNamedTenantOnly()
    {
        var (a, _) = await OpenAsync("CorpusA");
        var (b, invoiceB) = await OpenAsync("CorpusB");

        // Tenant A: an edit-and-approve (the human's values), a reject that a person then labelled, and a reject left unlabelled.
        fixture.Api.Ai.Enqueue(AiScript.Response("promise_to_pay", 0.91m, "explicit_future_date_commitment", language: "ar", amountText: "1500", amountNumeric: "1500.000", dateText: "بعد العيد", dateRelative: true));
        var edited = await IngestAndClassifyAsync(a, "رح نحول المبلغ بعد العيد، اتصل على 0791234567", "promise");
        await a.Client.PostAsync($"/api/v1/ai/suggestions/{edited.GetProperty("id").GetGuid()}/edit-and-approve", new { amount = M(1_200m), promisedDate = D(10), note = "agreed by phone" });

        fixture.Api.Ai.Enqueue(AiScript.Response("payment_claimed", 0.85m, "explicit_payment_statement", review: true));
        var rejectedThenLabelled = await IngestAndClassifyAsync(a, "We will look into it, thanks", "ack");
        await a.Client.PostAsync($"/api/v1/ai/suggestions/{rejectedThenLabelled.GetProperty("id").GetGuid()}/reject", new { reason = "not a payment claim" });
        await a.Client.PostAsync($"/api/v1/inbound-messages/{rejectedThenLabelled.GetProperty("subjectId").GetGuid()}/classify-manually", new { classification = "acknowledgement" });

        fixture.Api.Ai.Enqueue(AiScript.Response("dispute_raised", 0.9m, "explicit_goods_or_service_issue"));
        var rejectedOnly = await IngestAndClassifyAsync(a, "The invoice is wrong", "dispute");
        await a.Client.PostAsync($"/api/v1/ai/suggestions/{rejectedOnly.GetProperty("id").GetGuid()}/reject", new { reason = "spam" });

        // Tenant B: a correction that must never appear in A's export.
        fixture.Api.Ai.Enqueue(AiScript.Response("dispute_raised", 0.9m, "explicit_goods_or_service_issue"));
        var other = await IngestAndClassifyAsync(b, "Wrong quantity delivered", "dispute");
        await b.Client.PostAsync($"/api/v1/ai/suggestions/{other.GetProperty("id").GetGuid()}/edit-and-approve", new { disputeReasonCode = "wrong_quantity", invoiceId = invoiceB });

        var (csv, summary) = await CorpusProposals.RenderAsync(fixture.Database.MigratorConnectionString, a.Organization.TenantId, "consent letter 2026-09-01", null);
        Assert.Equal(2, summary.Proposed);
        Assert.Equal(1, summary.AwaitingLabel);
        Assert.Equal(0, summary.Skipped);

        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("id,text,label,language,provenance,note,expect", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.Contains("رح نحول المبلغ بعد العيد", StringComparison.Ordinal) && l.Contains(",promise_to_pay,ar,consented,", StringComparison.Ordinal) && l.Contains("decision: edited; ai: promise_to_pay (0.910)", StringComparison.Ordinal) && l.Contains("\"\"amount_numeric\"\":\"\"1200.000\"\"", StringComparison.Ordinal) && l.Contains($"\"\"date_iso\"\":\"\"{D(10)}\"\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("We will look into it", StringComparison.Ordinal) && l.Contains(",acknowledgement,en,consented,", StringComparison.Ordinal) && l.Contains("decision: rejected; ai: payment_claimed (0.850)", StringComparison.Ordinal));
        Assert.DoesNotContain("Wrong quantity delivered", csv);                    // SEC-13: the other tenant's correction stays where it is
        Assert.Contains("0791234567", csv);                                          // redaction is the importer's job, and it does it; the export does not pretend to

        // Consent is a precondition, not a column default.
        await Assert.ThrowsAsync<ArgumentException>(() => CorpusProposals.RenderAsync(fixture.Database.MigratorConnectionString, a.Organization.TenantId, " ", null));
        await Assert.ThrowsAsync<ArgumentException>(() => CorpusProposals.RenderAsync(fixture.Database.MigratorConnectionString, Guid.Empty, "x", null));

        // `since` filters on the decision time.
        var (later, laterSummary) = await CorpusProposals.RenderAsync(fixture.Database.MigratorConnectionString, a.Organization.TenantId, "consent letter", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        Assert.Equal(0, laterSummary.Proposed);
        Assert.Single(later.TrimEnd('\n').Split('\n'));
    }
}
