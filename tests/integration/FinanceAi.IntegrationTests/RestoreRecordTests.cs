using System.Net.Http.Json;
using System.Text.Json;
using FinanceAi.Infrastructure.Audit;
using static FinanceAi.TestSupport.LedgerScenario;

namespace FinanceAi.IntegrationTests;

/// <summary>
/// Slice 31 — SEC-94: a restore is recorded on every tenant's audit chain as <c>instance.restored</c>, hashed like any
/// other event, visible on the Audit screen, and the chain still verifies afterwards.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RestoreRecordTests(ApiTestFixture fixture)
{
    [Fact]
    public async Task RecordRestore_AppendsToEveryChain_AndTheChainsStillVerify()
    {
        var a = await fixture.Api.NewCustomerAsync("Restore A");
        var b = await fixture.Api.NewCustomerAsync("Restore B");
        var dump = Path.Combine(Path.GetTempPath(), $"finance-ai-{Guid.NewGuid():N}.dump.enc");
        await File.WriteAllBytesAsync(dump, [1, 2, 3, 4, 5]);
        try
        {
            var outcome = await RestoreRecord.RunAsync(fixture.Database.MigratorConnectionString, dump, "drill of the 2026-09-12 backup after a disk swap", "operator@example.jo", DateTimeOffset.UtcNow);
            Assert.True(outcome.TenantsRecorded >= 2);
            Assert.Equal("74f81fe167d99b4cb41d6d0ccda82278caee9f3e2f25d5e5a3936ff3dcec60d0", outcome.Sha256);   // sha256 of 01 02 03 04 05

            foreach (var s in new[] { a, b })
            {
                var events = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/audit?eventType=instance.restored", ApiScenario.Json);
                var item = Assert.Single(events.GetProperty("items").EnumerateArray());
                Assert.Equal("system", item.GetProperty("actorKind").GetString());
                Assert.Contains("disk swap", item.GetProperty("note").GetString());
                var changes = item.GetProperty("changes");
                Assert.Equal(Path.GetFileName(dump), changes.GetProperty("dumpFile").GetString());
                Assert.Equal(outcome.Sha256, changes.GetProperty("sha256").GetString());
                Assert.Equal("operator@example.jo", changes.GetProperty("performedBy").GetString());

                // SEC-53: the appended row is part of the chain, not a foreign body.
                var run = await s.Client.PostAsync("/api/v1/organization/invariants/run", new { });
                Assert.Equal("ok", run.GetProperty("status").GetString());
                var chain = run.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "SEC-53");
                Assert.Equal(0, chain.GetProperty("violations").GetInt64());
            }

            await Assert.ThrowsAsync<ArgumentException>(() => RestoreRecord.RunAsync(fixture.Database.MigratorConnectionString, dump, " ", "x", DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<FileNotFoundException>(() => RestoreRecord.RunAsync(fixture.Database.MigratorConnectionString, dump + ".missing", "r", "x", DateTimeOffset.UtcNow));
        }
        finally
        {
            File.Delete(dump);
        }
    }
}
