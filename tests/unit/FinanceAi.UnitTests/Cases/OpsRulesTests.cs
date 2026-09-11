using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Cases;

/// <summary>Slice 15 AC-04 / AC-05: the two statistical detectors of SEC-102, at their thresholds.</summary>
public sealed class OpsRulesTests
{
    [Theory]
    [InlineData(10, 6, true)]    // ten, sixty percent
    [InlineData(10, 5, true)]    // exactly half
    [InlineData(10, 4, false)]   // under half
    [InlineData(9, 9, false)]    // too few to mean anything, however bad
    [InlineData(0, 0, false)]
    [InlineData(100, 50, true)]
    public void GuardSpike_NeedsVolumeAndShare(int suggestions, int rejected, bool expected) =>
        Assert.Equal(expected, OpsRules.IsGuardRejectionSpike(suggestions, rejected));

    [Theory]
    [InlineData(21, 35, true)]    // 21 against a mean of 5 → 3× reached
    [InlineData(21, 70, false)]   // 21 against a mean of 10 → 2.1×
    [InlineData(19, 0, false)]    // under the floor, no history at all
    [InlineData(20, 0, true)]     // the floor, no history: an anomaly by definition
    [InlineData(30, 70, true)]    // exactly 3×
    [InlineData(29, 70, false)]
    public void SendAnomaly_NeedsVolumeAndMultiple(int today, int priorSevenDays, bool expected) =>
        Assert.Equal(expected, OpsRules.IsSendVolumeAnomaly(today, priorSevenDays));

    [Fact]
    public void DedupeKey_IsTheKindAndTheUtcDay()
    {
        var at = new DateTimeOffset(2026, 9, 11, 23, 30, 0, TimeSpan.FromHours(3));   // 20:30 UTC
        Assert.Equal("invariant_violation:2026-09-11", Alert.DedupeKeyFor(AlertKinds.InvariantViolation, at));
        Assert.Equal("invariant_violation:2026-09-12", Alert.DedupeKeyFor(AlertKinds.InvariantViolation, at.AddHours(4)));
    }
}
