namespace FinanceAi.Domain.Entities;

public enum AgingBasis { DueDate, IssueDate }

/// <summary>One aging bucket, generated from the tenant's boundaries (FIN-51). Half-open and exhaustive (FIN-52).</summary>
public sealed record AgingBucket(string Key, int? FromDays, int? ToDays)
{
    /// <summary>Boundary <c>b</c> belongs to the bucket that ends at <c>b</c>: <c>1 ≤ dpd ≤ 30</c>.</summary>
    public bool Contains(int daysPastDue) =>
        (this.FromDays is null || daysPastDue >= this.FromDays) && (this.ToDays is null || daysPastDue <= this.ToDays);
}

/// <summary>
/// The bucket set for a tenant. Boundaries come from <c>tenant_settings.aging_bucket_days</c> and are never
/// hardcoded; the default <c>{30, 60, 90}</c> yields <c>Current, Days1To30, Days31To60, Days61To90, Days90Plus</c>.
/// </summary>
public sealed class AgingBuckets
{
    public const string CurrentKey = "Current";

    private AgingBuckets(IReadOnlyList<AgingBucket> buckets, IReadOnlyList<int> boundaries)
    {
        this.All = buckets;
        this.Boundaries = boundaries;
    }

    public IReadOnlyList<AgingBucket> All { get; }

    public IReadOnlyList<int> Boundaries { get; }

    public static AgingBuckets FromBoundaries(IReadOnlyList<int> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.Count == 0)
        {
            throw new ArgumentException("At least one bucket boundary is required.", nameof(boundaries));
        }

        for (var i = 0; i < boundaries.Count; i++)
        {
            if (boundaries[i] <= 0 || (i > 0 && boundaries[i] <= boundaries[i - 1]))
            {
                throw new ArgumentException("Bucket boundaries must be positive and strictly ascending.", nameof(boundaries));
            }
        }

        var buckets = new List<AgingBucket> { new(CurrentKey, null, 0) };
        var from = 1;
        foreach (var boundary in boundaries)
        {
            buckets.Add(new AgingBucket($"Days{from}To{boundary}", from, boundary));
            from = boundary + 1;
        }

        buckets.Add(new AgingBucket($"Days{boundaries[^1]}Plus", from, null));
        return new AgingBuckets(buckets, boundaries);
    }

    /// <summary>Exactly one bucket for any integer (FIN-52).</summary>
    public AgingBucket Classify(int daysPastDue)
    {
        foreach (var bucket in this.All)
        {
            if (bucket.Contains(daysPastDue))
            {
                return bucket;
            }
        }

        throw new InvalidOperationException("FIN-52 violated: buckets are not exhaustive.");
    }
}

/// <summary>Pure aging arithmetic. Everything with a date or a division lives here so it can be unit-tested (FIN-62).</summary>
public static class AgingRules
{
    public static AgingBasis ParseBasis(string? value) => value switch
    {
        null or "" or "due_date" => AgingBasis.DueDate,
        "issue_date" => AgingBasis.IssueDate,
        _ => throw new ArgumentException($"Unknown aging basis '{value}'.", nameof(value)),
    };

    public static string BasisKey(AgingBasis basis) => basis == AgingBasis.IssueDate ? "issue_date" : "due_date";

    /// <summary>FIN-58: "today" is a calendar date in the tenant's timezone, never the UTC date.</summary>
    public static DateOnly TodayIn(string timezoneId, DateTimeOffset utcNow)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
    }

    /// <summary>FIN-50 / FIN-71: calendar days from the basis date. Negative means not yet due.</summary>
    public static int DaysPastDue(DateOnly asOf, DateOnly issueDate, DateOnly dueDate, AgingBasis basis) =>
        asOf.DayNumber - (basis == AgingBasis.IssueDate ? issueDate : dueDate).DayNumber;

    /// <summary>
    /// FIN-55 / FIN-06: the indicative base-currency figure. <b>The one place money is rounded in this
    /// slice</b>: per invoice, to the currency scale, away from zero, before any summing.
    /// </summary>
    public static decimal ToBaseIndicative(decimal amount, decimal fxRateToBase) =>
        Math.Round(amount * fxRateToBase, 3, MidpointRounding.AwayFromZero);

    /// <summary>
    /// FIN-60: <c>DSO = AR_at_period_end / credit_sales_in_period × days_in_period</c>. Days, not money —
    /// rounded to one decimal. <c>null</c> when there are no sales to divide by.
    /// </summary>
    public static decimal? Dso(decimal arAtPeriodEnd, decimal creditSalesInPeriod, int daysInPeriod) =>
        creditSalesInPeriod <= 0m ? null : Math.Round(arAtPeriodEnd / creditSalesInPeriod * daysInPeriod, 1, MidpointRounding.AwayFromZero);

    /// <summary>FIN-61: mean of (settled − due) in days over the sample; <c>null</c> for an empty sample.</summary>
    public static decimal? AverageDaysToPay(IReadOnlyList<int> daysLate) =>
        daysLate.Count == 0 ? null : Math.Round((decimal)daysLate.Sum() / daysLate.Count, 1, MidpointRounding.AwayFromZero);
}
