using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Cases;

/// <summary>Doc 02 §3, the pure half: the machine (T-10), the verdict (T-30), deadlines (A-08), reliability (SM-37).</summary>
public sealed class PromiseRulesTests
{
    private static readonly (PtpStatus From, PtpEvent Event, PtpStatus To)[] Legal =
    [
        (PtpStatus.Proposed, PtpEvent.Confirm, PtpStatus.Active),
        (PtpStatus.Proposed, PtpEvent.Reject, PtpStatus.Rejected),
        (PtpStatus.Active, PtpEvent.PaymentCoversPromise, PtpStatus.Kept),
        (PtpStatus.Active, PtpEvent.PartialPaymentAtDeadline, PtpStatus.PartiallyKept),
        (PtpStatus.Active, PtpEvent.DeadlinePassedUnpaid, PtpStatus.Broken),
        (PtpStatus.Active, PtpEvent.Cancel, PtpStatus.Cancelled),
        (PtpStatus.Active, PtpEvent.SupersededByNewPtp, PtpStatus.Cancelled),
        (PtpStatus.Active, PtpEvent.ChequeBounced, PtpStatus.Broken),
    ];

    public static TheoryData<PtpStatus, PtpEvent> EveryPair()
    {
        var data = new TheoryData<PtpStatus, PtpEvent>();
        foreach (var s in Enum.GetValues<PtpStatus>())
            foreach (var e in Enum.GetValues<PtpEvent>())
                data.Add(s, e);
        return data;
    }

    /// <summary>AC-01: 7 × 8.</summary>
    [Theory]
    [MemberData(nameof(EveryPair))]
    public void PtpMachine_Matrix_IsExhaustive(PtpStatus from, PtpEvent @event)
    {
        var expected = Legal.Where(l => l.From == from && l.Event == @event).Select(l => (PtpStatus?)l.To).SingleOrDefault();
        if (expected is { } to) Assert.Equal(to, PtpMachine.Next(from, @event));
        else Assert.Throws<InvalidTransitionException>(() => PtpMachine.Next(from, @event));
    }

    [Fact]
    public void Matrix_CoversTheEnums_AndTerminalsHaveNoExit()
    {
        Assert.Equal(7, Enum.GetValues<PtpStatus>().Length);
        Assert.Equal(8, Enum.GetValues<PtpEvent>().Length);
        Assert.Equal(Legal.Length, PtpMachine.Transitions.Count);
        Assert.DoesNotContain(PtpMachine.Transitions.Keys, k => PtpMachine.IsTerminal(k.From));
        // Doc 05: only confirm / reject / cancel are a human's; the verdicts are the system's.
        Assert.Equal([PtpEvent.Confirm, PtpEvent.Reject, PtpEvent.Cancel], Enum.GetValues<PtpEvent>().Where(PtpMachine.IsUserEvent));
    }

    /// <summary>AC-02 / T-30, including the exact-threshold case.</summary>
    [Theory]
    [InlineData(1000, 1000, 50, true, PtpEvent.PaymentCoversPromise)]
    [InlineData(1000, 1000, 50, false, PtpEvent.PaymentCoversPromise)]     // before the deadline, Kept is possible
    [InlineData(1000, 1200, 50, false, PtpEvent.PaymentCoversPromise)]
    [InlineData(1000, 999.999, 50, false, null)]                            // before the deadline, nothing else is
    [InlineData(1000, 999.999, 50, true, PtpEvent.PartialPaymentAtDeadline)]
    [InlineData(1000, 500, 50, true, PtpEvent.PartialPaymentAtDeadline)]    // exactly the threshold
    [InlineData(1000, 499.999, 50, true, PtpEvent.DeadlinePassedUnpaid)]
    [InlineData(1000, 0, 50, true, PtpEvent.DeadlinePassedUnpaid)]
    [InlineData(1000, 599.999, 60, true, PtpEvent.DeadlinePassedUnpaid)]    // the threshold is the tenant's
    [InlineData(1000, 600, 60, true, PtpEvent.PartialPaymentAtDeadline)]
    [InlineData(333.335, 166.668, 50, true, PtpEvent.PartialPaymentAtDeadline)]   // 166.6675 threshold, compared exactly
    [InlineData(333.335, 166.667, 50, true, PtpEvent.DeadlinePassedUnpaid)]
    public void Verdict_AtThresholdBoundaries(decimal promised, decimal received, decimal thresholdPct, bool deadlineReached, PtpEvent? expected)
    {
        Assert.Equal(expected, PtpRules.Verdict(promised, received, thresholdPct, deadlineReached));
    }

    /// <summary>AC-03 / A-08: Friday and Saturday are the weekend; holidays are a list.</summary>
    [Fact]
    public void Deadline_SkipsWeekendAndHolidays()
    {
        var none = new HashSet<DateOnly>();
        var thursday = new DateOnly(2026, 9, 10);
        Assert.Equal(DayOfWeek.Thursday, thursday.DayOfWeek);
        Assert.Equal(new DateOnly(2026, 9, 14), PtpRules.Deadline(thursday, 2, none));               // Sun, Mon
        Assert.Equal(new DateOnly(2026, 9, 15), PtpRules.Deadline(thursday, 2, new HashSet<DateOnly> { new DateOnly(2026, 9, 13) }));   // Sunday holiday → Mon, Tue
        Assert.Equal(thursday, PtpRules.Deadline(thursday, 0, none));
        var friday = new DateOnly(2026, 9, 11);
        Assert.Equal(friday, PtpRules.Deadline(friday, 0, none));                                     // zero grace on a weekend stays put
        Assert.Equal(new DateOnly(2026, 9, 13), PtpRules.Deadline(friday, 1, none));                  // Sunday
        Assert.Throws<ArgumentOutOfRangeException>(() => PtpRules.Deadline(friday, -1, none));
    }

    /// <summary>AC-12 / SM-37: the denominator always, the ratio only from three.</summary>
    [Fact]
    public void Reliability_ShowsTheDenominator()
    {
        var two = PtpRules.ReliabilityOf([PtpStatus.Kept, PtpStatus.Broken, PtpStatus.Cancelled, PtpStatus.Active]);
        Assert.Equal((1, 2), (two.Kept, two.Denominator));
        Assert.Null(two.Ratio);

        var three = PtpRules.ReliabilityOf([PtpStatus.Kept, PtpStatus.Kept, PtpStatus.PartiallyKept]);
        Assert.Equal(3, three.Denominator);
        Assert.Equal(0.667m, three.Ratio);
        Assert.Equal(1, three.PartiallyKept);
    }

    [Fact]
    public void CoverFifo_TakesOldestDueUntilTheAmountIsMet()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var candidates = new[]
        {
            (b, new DateOnly(2026, 8, 1), "B", 300m),
            (a, new DateOnly(2026, 7, 1), "A", 500m),
            (c, new DateOnly(2026, 9, 1), "C", 100m),
        };
        Assert.Equal([a, b], PtpRules.CoverFifo(600m, candidates));
        Assert.Equal([a], PtpRules.CoverFifo(500m, candidates));
        Assert.Equal([a, b, c], PtpRules.CoverFifo(5_000m, candidates));
        Assert.Empty(PtpRules.CoverFifo(100m, []));
    }
}
