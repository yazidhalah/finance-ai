namespace FinanceAi.TestSupport;

/// <summary>A clock that does not move, for testing anything expressed in terms of "now".</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
