using System.Text;
using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Cases;

/// <summary>Doc 02 §4, the pure half: the machine (T-10), SM-45/46 amounts, SM-48 clocks, SEC-44 sniffing.</summary>
public sealed class DisputeRulesTests
{
    private static readonly (DisputeStatus From, DisputeEvent Event, DisputeStatus To)[] Legal =
    [
        (DisputeStatus.Open, DisputeEvent.Assign, DisputeStatus.UnderReview),
        (DisputeStatus.Open, DisputeEvent.Cancel, DisputeStatus.Cancelled),
        (DisputeStatus.Open, DisputeEvent.CustomerWithdrew, DisputeStatus.Withdrawn),
        (DisputeStatus.UnderReview, DisputeEvent.RequestInfo, DisputeStatus.PendingCustomer),
        (DisputeStatus.PendingCustomer, DisputeEvent.InfoReceived, DisputeStatus.UnderReview),
        (DisputeStatus.PendingCustomer, DisputeEvent.Timeout, DisputeStatus.UnderReview),
        (DisputeStatus.UnderReview, DisputeEvent.Accept, DisputeStatus.Accepted),
        (DisputeStatus.UnderReview, DisputeEvent.PartiallyAccept, DisputeStatus.PartiallyAccepted),
        (DisputeStatus.UnderReview, DisputeEvent.Reject, DisputeStatus.Rejected),
        (DisputeStatus.UnderReview, DisputeEvent.CustomerWithdrew, DisputeStatus.Withdrawn),
    ];

    public static TheoryData<DisputeStatus, DisputeEvent> EveryPair()
    {
        var data = new TheoryData<DisputeStatus, DisputeEvent>();
        foreach (var s in Enum.GetValues<DisputeStatus>())
            foreach (var e in Enum.GetValues<DisputeEvent>())
                data.Add(s, e);
        return data;
    }

    /// <summary>AC-01: 8 × 9.</summary>
    [Theory]
    [MemberData(nameof(EveryPair))]
    public void DisputeMachine_Matrix_IsExhaustive(DisputeStatus from, DisputeEvent @event)
    {
        var expected = Legal.Where(l => l.From == from && l.Event == @event).Select(l => (DisputeStatus?)l.To).SingleOrDefault();
        if (expected is { } to) Assert.Equal(to, DisputeMachine.Next(from, @event));
        else Assert.Throws<InvalidTransitionException>(() => DisputeMachine.Next(from, @event));
    }

    [Fact]
    public void Matrix_CoversTheEnums_AndResolutionsAreSeparate()
    {
        Assert.Equal(8, Enum.GetValues<DisputeStatus>().Length);
        Assert.Equal(9, Enum.GetValues<DisputeEvent>().Length);
        Assert.Equal(Legal.Length, DisputeMachine.Transitions.Count);
        Assert.DoesNotContain(DisputeMachine.Transitions.Keys, k => DisputeMachine.IsTerminal(k.From));
        // SM-47: the resolutions are not among the update events a `disputes.write` holder can name.
        Assert.Equal(["assign", "cancel", "info_received", "request_info", "withdraw"], DisputeMachine.UpdateEvents.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["accepted", "partially_accepted", "rejected"], DisputeMachine.ResolutionOutcomes.Keys.Order(StringComparer.Ordinal));
        Assert.All(DisputeMachine.UpdateEvents.Values, e => Assert.False(DisputeMachine.IsResolution(e)));
        Assert.Equal(13, DisputeReasons.All.Count);
    }

    /// <summary>SM-45 / SM-46 / D-6.</summary>
    [Theory]
    [InlineData(DisputeEvent.Accept, null, 2000, 2000, null)]
    [InlineData(DisputeEvent.Accept, null, 2000, 1999.999, "exceeds_open_balance")]     // a payment since raising
    [InlineData(DisputeEvent.PartiallyAccept, 500.0, 2000, 2000, null)]
    [InlineData(DisputeEvent.PartiallyAccept, 2000.0, 2000, 2000, "partial_must_be_below_disputed")]
    [InlineData(DisputeEvent.PartiallyAccept, 1999.999, 2000, 1000, "exceeds_open_balance")]
    [InlineData(DisputeEvent.PartiallyAccept, null, 2000, 2000, "amount_required")]
    [InlineData(DisputeEvent.PartiallyAccept, 0.0, 2000, 2000, "amount_required")]
    [InlineData(DisputeEvent.Reject, null, 2000, 2000, null)]
    [InlineData(DisputeEvent.Reject, 1.0, 2000, 2000, "amount_not_allowed")]
    [InlineData(DisputeEvent.Assign, null, 2000, 2000, "not_a_resolution")]
    public void ResolutionAmount_IsChecked(DisputeEvent outcome, double? amount, decimal disputed, decimal open, string? expected)
    {
        Assert.Equal(expected, DisputeRules.CheckResolutionAmount(outcome, amount is null ? null : (decimal)amount, disputed, open));
    }

    [Fact]
    public void DisputedForAging_IsCappedAtTheOpenBalance()
    {
        Assert.Equal(2000m, DisputeRules.DisputedForAging(2000m, 6000m));
        Assert.Equal(1500m, DisputeRules.DisputedForAging(2000m, 1500m));
        Assert.Equal(0m, DisputeRules.DisputedForAging(2000m, 0m));
    }

    /// <summary>SM-48: business days, end of day, tenant zone.</summary>
    [Fact]
    public void SlaDueAt_IsBusinessDaysAtEndOfDay()
    {
        var amman = TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
        var thursday = new DateOnly(2026, 9, 10);
        var due = DisputeSla.DueAt(thursday, 2, new HashSet<DateOnly>(), amman);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 15, 0, 0, TimeSpan.Zero), due);   // Monday 18:00 Amman = 15:00 UTC
        var ten = DisputeSla.DueAt(thursday, 10, new HashSet<DateOnly>(), amman);
        Assert.Equal(new DateOnly(2026, 9, 24), DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ten, amman).DateTime));

        var dispute = new Dispute { ReasonCode = "other", Currency = "JOD", FirstResponseDueAt = due, ResolutionDueAt = ten };
        Assert.False(dispute.IsSlaBreached(due.AddMinutes(-1)));
        Assert.True(dispute.IsSlaBreached(due.AddMinutes(1)));            // no first response yet
        dispute.FirstResponseAt = due;
        Assert.False(dispute.IsSlaBreached(ten.AddMinutes(-1)));
        Assert.True(dispute.IsSlaBreached(ten.AddMinutes(1)));
        dispute.PendingSince = ten;                                        // paused: not breached while waiting on the customer
        Assert.False(dispute.IsSlaBreached(ten.AddDays(30)));
        dispute.Status = DisputeStatus.Rejected;
        dispute.PendingSince = null;
        Assert.False(dispute.IsSlaBreached(ten.AddDays(30)));             // closed disputes have no clock
    }

    /// <summary>SEC-44: bytes decide, the name must agree.</summary>
    [Fact]
    public void EvidenceInspector_SniffsAndMatchesExtensions()
    {
        Assert.Equal("application/pdf", EvidenceInspector.SniffContentType(Encoding.ASCII.GetBytes("%PDF-1.7 ...")));
        Assert.Equal("image/png", EvidenceInspector.SniffContentType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("image/jpeg", EvidenceInspector.SniffContentType([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(EvidenceInspector.SniffContentType(Encoding.ASCII.GetBytes("<html><script>alert(1)</script>")));
        Assert.Null(EvidenceInspector.SniffContentType([0x4D, 0x5A, 0x90, 0x00]));   // MZ
        Assert.True(EvidenceInspector.ExtensionMatches("note.PDF", "application/pdf"));
        Assert.False(EvidenceInspector.ExtensionMatches("note.pdf", "image/png"));
        Assert.True(EvidenceInspector.ExtensionMatches("photo.jpeg", "image/jpeg"));
        var safe = EvidenceInspector.SafeFileName("../../etc\\passwd.pdf");
        Assert.DoesNotContain("/", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", safe, StringComparison.Ordinal);
        Assert.EndsWith("passwd.pdf", safe, StringComparison.Ordinal);
        Assert.Equal("evidence", EvidenceInspector.SafeFileName("///"));
    }
}
