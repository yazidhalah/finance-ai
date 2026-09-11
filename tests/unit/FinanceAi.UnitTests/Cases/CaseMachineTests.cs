using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Cases;

/// <summary>T-10 for the collection-case machine (doc 02 §2): every (state, event) pair, legal and illegal.</summary>
public sealed class CaseMachineTests
{
    /// <summary>
    /// Doc 02 §2.2 / §2.3 transcribed independently of <see cref="CaseMachine"/>. The test compares the two;
    /// a disagreement is a spec-reading question, not a typo to fix in whichever side is handier.
    /// </summary>
    private static readonly (CaseStatus From, CaseEvent Event, CaseStatus To)[] Legal =
    [
        (CaseStatus.Open, CaseEvent.ContactLogged, CaseStatus.InProgress),
        (CaseStatus.Open, CaseEvent.ReplyReceived, CaseStatus.InProgress),
        (CaseStatus.Open, CaseEvent.FollowUpDue, CaseStatus.InProgress),
        (CaseStatus.AwaitingCustomer, CaseEvent.ContactLogged, CaseStatus.InProgress),
        (CaseStatus.AwaitingCustomer, CaseEvent.ReplyReceived, CaseStatus.InProgress),
        (CaseStatus.AwaitingCustomer, CaseEvent.FollowUpDue, CaseStatus.InProgress),
        (CaseStatus.InProgress, CaseEvent.MessageSent, CaseStatus.AwaitingCustomer),
        (CaseStatus.Open, CaseEvent.PtpRecorded, CaseStatus.PromiseActive),
        (CaseStatus.InProgress, CaseEvent.PtpRecorded, CaseStatus.PromiseActive),
        (CaseStatus.AwaitingCustomer, CaseEvent.PtpRecorded, CaseStatus.PromiseActive),
        (CaseStatus.PromiseActive, CaseEvent.PtpBroken, CaseStatus.InProgress),
        (CaseStatus.PromiseActive, CaseEvent.PtpCancelled, CaseStatus.InProgress),
        (CaseStatus.PromiseActive, CaseEvent.PtpKeptAndBalanceZero, CaseStatus.Resolved),
        (CaseStatus.Open, CaseEvent.DisputeOpened, CaseStatus.Disputed),
        (CaseStatus.InProgress, CaseEvent.DisputeOpened, CaseStatus.Disputed),
        (CaseStatus.AwaitingCustomer, CaseEvent.DisputeOpened, CaseStatus.Disputed),
        (CaseStatus.PromiseActive, CaseEvent.DisputeOpened, CaseStatus.Disputed),
        (CaseStatus.Disputed, CaseEvent.AllDisputesResolved, CaseStatus.InProgress),
        (CaseStatus.Open, CaseEvent.Hold, CaseStatus.OnHold),
        (CaseStatus.InProgress, CaseEvent.Hold, CaseStatus.OnHold),
        (CaseStatus.AwaitingCustomer, CaseEvent.Hold, CaseStatus.OnHold),
        (CaseStatus.PromiseActive, CaseEvent.Hold, CaseStatus.OnHold),
        (CaseStatus.Disputed, CaseEvent.Hold, CaseStatus.OnHold),
        (CaseStatus.OnHold, CaseEvent.Resume, CaseStatus.InProgress),
        (CaseStatus.OnHold, CaseEvent.HoldExpired, CaseStatus.InProgress),
        (CaseStatus.Open, CaseEvent.Escalate, CaseStatus.Escalated),
        (CaseStatus.InProgress, CaseEvent.Escalate, CaseStatus.Escalated),
        (CaseStatus.AwaitingCustomer, CaseEvent.Escalate, CaseStatus.Escalated),
        (CaseStatus.Disputed, CaseEvent.Escalate, CaseStatus.Escalated),
        (CaseStatus.PromiseActive, CaseEvent.Escalate, CaseStatus.Escalated),
        (CaseStatus.OnHold, CaseEvent.Escalate, CaseStatus.Escalated),
        (CaseStatus.Open, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.InProgress, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.AwaitingCustomer, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.PromiseActive, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.Disputed, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.OnHold, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.Escalated, CaseEvent.BalanceZero, CaseStatus.Resolved),
        (CaseStatus.Open, CaseEvent.Abandon, CaseStatus.Abandoned),
        (CaseStatus.InProgress, CaseEvent.Abandon, CaseStatus.Abandoned),
        (CaseStatus.Escalated, CaseEvent.Abandon, CaseStatus.Abandoned),
    ];

    public static TheoryData<CaseStatus, CaseEvent> EveryPair()
    {
        var data = new TheoryData<CaseStatus, CaseEvent>();
        foreach (var state in Enum.GetValues<CaseStatus>())
        {
            foreach (var @event in Enum.GetValues<CaseEvent>())
            {
                data.Add(state, @event);
            }
        }

        return data;
    }

    /// <summary>AC-01: 9 states × 17 events, each asserted one way or the other.</summary>
    [Theory]
    [MemberData(nameof(EveryPair))]
    public void CaseMachine_Matrix_IsExhaustive(CaseStatus from, CaseEvent @event)
    {
        var expected = Legal.Where(l => l.From == from && l.Event == @event).Select(l => (CaseStatus?)l.To).SingleOrDefault();
        if (expected is { } to)
        {
            Assert.Equal(to, CaseMachine.Next(from, @event));
        }
        else
        {
            var ex = Assert.Throws<InvalidTransitionException>(() => CaseMachine.Next(from, @event));
            Assert.Equal(from.ToString(), ex.From);
            Assert.Equal(@event.ToString(), ex.Event);
        }
    }

    [Fact]
    public void Matrix_CoversTheEnums_AndTheTableHasNoExtraRows()
    {
        Assert.Equal(9, Enum.GetValues<CaseStatus>().Length);
        Assert.Equal(17, Enum.GetValues<CaseEvent>().Length);
        Assert.Equal(Legal.Length, CaseMachine.Transitions.Count);
        // C1 is creation, not a transition; terminal states have no outgoing rows (SM-05).
        Assert.DoesNotContain(CaseMachine.Transitions.Keys, k => k.Event == CaseEvent.InvoiceBecameOverdue);
        Assert.DoesNotContain(CaseMachine.Transitions.Keys, k => CaseMachine.IsTerminal(k.From));
        Assert.True(CaseMachine.IsTerminal(CaseStatus.Resolved));
        Assert.True(CaseMachine.IsTerminal(CaseStatus.Abandoned));
    }

    /// <summary>SM-26: from Escalated, only balance_zero and abandon.</summary>
    [Fact]
    public void Escalated_IsAOneWayDoor()
    {
        var outgoing = CaseMachine.Transitions.Keys.Where(k => k.From == CaseStatus.Escalated).Select(k => k.Event).Order().ToList();
        Assert.Equal([CaseEvent.BalanceZero, CaseEvent.Abandon], outgoing.OrderBy(e => e == CaseEvent.Abandon));
    }

    [Fact]
    public void UserEvents_AreTheFiveOfDoc05_AndOnlyC9C11NeedEscalate()
    {
        Assert.Equal(["abandon", "contact_logged", "escalate", "hold", "resume"], CaseMachine.UserEvents.Keys.Order(StringComparer.Ordinal));
        Assert.True(CaseMachine.RequiresEscalatePermission(CaseEvent.Escalate));
        Assert.True(CaseMachine.RequiresEscalatePermission(CaseEvent.Abandon));
        Assert.False(CaseMachine.RequiresEscalatePermission(CaseEvent.Hold));
        foreach (var e in Enum.GetValues<CaseEvent>())
        {
            Assert.Matches("^[a-z_]+$", CaseMachine.EventName(e));
        }
    }
}
