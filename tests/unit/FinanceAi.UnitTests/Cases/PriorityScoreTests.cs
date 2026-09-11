using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Cases;

/// <summary>T-31 / FIN-80 / AC-08: deterministic, bounded, explainable — the breakdown is the score.</summary>
public sealed class PriorityScoreTests
{
    [Fact]
    public void PriorityScore_IsDeterministic_AndExplainable()
    {
        var seed = Environment.TickCount;
        var random = new Random(seed);
        for (var i = 0; i < 2_000; i++)
        {
            var inputs = new PriorityInputs(
                random.Next(0, 3_000_000) / 100m, random.Next(-10, 400), random.Next(0, 6), random.Next(0, 4),
                (RiskFlag)random.Next(0, 4), random.Next(0, 3) == 0 ? null : random.Next(0, 30), random.Next(0, 5) == 0);

            var a = PriorityScore.Compute(inputs, PriorityWeights.Version1);
            var b = PriorityScore.Compute(inputs, PriorityWeights.For(1));

            Assert.True(a == b || (a.Score == b.Score && a.Factors.SequenceEqual(b.Factors)), $"seed {seed}: not deterministic");
            Assert.InRange(a.Score, 0, 100);
            Assert.Equal(1, a.WeightsVersion);
            // D-3: contributions are integers and, before clamping, sum to the score.
            var sum = a.Factors.Sum(f => f.Contribution);
            Assert.Equal(Math.Clamp(sum, 0, 100), a.Score);
            Assert.All(a.Factors, f => Assert.False(string.IsNullOrWhiteSpace(f.Detail)));
        }
    }

    [Fact]
    public void Weights_SaturateAndDampen_AsDocumented()
    {
        var w = PriorityWeights.Version1;
        var max = PriorityScore.Compute(new PriorityInputs(1_000_000m, 500, 10, 10, RiskFlag.Legal, null, false), w);
        Assert.Equal(100, max.Score);   // 35 + 30 + 15 + 10 + 10

        var fresh = PriorityScore.Compute(new PriorityInputs(0m, 0, 0, 0, RiskFlag.None, null, false), w);
        Assert.Equal(0, fresh.Score);

        var half = PriorityScore.Compute(new PriorityInputs(5_000m, 60, 0, 0, RiskFlag.None, null, false), w);
        Assert.Equal(18 + 15, half.Score);   // round(35 × 0.5) = 18 (away from zero), round(30 × 0.5) = 15
        Assert.Equal(18, half.Factors.Single(f => f.Factor == "amount").Contribution);

        var contactedToday = PriorityScore.Compute(new PriorityInputs(5_000m, 60, 0, 0, RiskFlag.None, 0, false), w);
        Assert.Equal(-10, contactedToday.Factors.Single(f => f.Factor == "recent_contact").Contribution);
        Assert.Equal(23, contactedToday.Score);

        var contactedLastWeek = PriorityScore.Compute(new PriorityInputs(5_000m, 60, 0, 0, RiskFlag.None, 7, false), w);
        Assert.DoesNotContain(contactedLastWeek.Factors, f => f.Factor == "recent_contact");

        var disputed = PriorityScore.Compute(new PriorityInputs(5_000m, 60, 0, 0, RiskFlag.None, null, true), w);
        Assert.Equal(-20, disputed.Factors.Single(f => f.Factor == "dispute").Contribution);
        Assert.Equal(13, disputed.Score);

        // Never below zero, never a monetary figure: the amount detail is a ranking input string, the score an int.
        var floor = PriorityScore.Compute(new PriorityInputs(0m, 0, 0, 0, RiskFlag.None, 0, true), w);
        Assert.Equal(0, floor.Score);
    }

    [Fact]
    public void UnknownWeightsVersion_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorityWeights.For(99));
    }

    [Theory]
    [InlineData(CaseStatus.Disputed, 10, null, "resolve_dispute", null)]
    [InlineData(CaseStatus.PromiseActive, 10, null, "await_promise", null)]
    [InlineData(CaseStatus.Escalated, 10, null, "manual_follow_up", null)]
    [InlineData(CaseStatus.InProgress, 3, null, "send_reminder", "dunning_0")]
    [InlineData(CaseStatus.InProgress, 10, null, "send_reminder", "dunning_7")]
    [InlineData(CaseStatus.InProgress, 20, 2, "send_reminder", "dunning_14")]
    [InlineData(CaseStatus.InProgress, 45, 2, "send_reminder", "dunning_30")]
    [InlineData(CaseStatus.InProgress, 45, null, "call", null)]
    [InlineData(CaseStatus.Open, 90, 30, "call", null)]
    public void SuggestedAction_IsRuleBased(CaseStatus status, int dpd, int? sinceContact, string kind, string? template)
    {
        var action = SuggestedActions.For(status, dpd, sinceContact, [0, 7, 14, 30], "ar");
        Assert.Equal(kind, action.Kind);
        Assert.Equal(template, action.TemplateKey);
        Assert.Equal("ar", action.Language);
    }
}
