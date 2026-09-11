using System.Globalization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Reports;

namespace FinanceAi.UnitTests.Reports;

/// <summary>Doc 03 §5, the pure half: buckets from settings, boundaries, day arithmetic, the two advisory metrics.</summary>
public sealed class AgingRulesTests
{
    /// <summary>AC-01 / T-24: boundary b belongs to the bucket that ends at b.</summary>
    [Theory]
    [InlineData(-5, "Current")]
    [InlineData(0, "Current")]
    [InlineData(1, "Days1To30")]
    [InlineData(30, "Days1To30")]
    [InlineData(31, "Days31To60")]
    [InlineData(60, "Days31To60")]
    [InlineData(61, "Days61To90")]
    [InlineData(90, "Days61To90")]
    [InlineData(91, "Days90Plus")]
    [InlineData(4000, "Days90Plus")]
    public void Buckets_BoundariesAreInclusiveOnTheRight(int daysPastDue, string expected)
    {
        var buckets = AgingBuckets.FromBoundaries([30, 60, 90]);
        Assert.Equal(expected, buckets.Classify(daysPastDue).Key);
    }

    /// <summary>AC-02: nothing is hardcoded — the keys and edges follow the tenant's boundaries.</summary>
    [Fact]
    public void Buckets_ComeFromSettings()
    {
        var buckets = AgingBuckets.FromBoundaries([15, 45]);
        Assert.Equal(["Current", "Days1To15", "Days16To45", "Days45Plus"], buckets.All.Select(b => b.Key));
        Assert.Equal("Days1To15", buckets.Classify(15).Key);
        Assert.Equal("Days16To45", buckets.Classify(16).Key);
        Assert.Equal("Days45Plus", buckets.Classify(46).Key);

        var single = AgingBuckets.FromBoundaries([7]);
        Assert.Equal(["Current", "Days1To7", "Days7Plus"], single.All.Select(b => b.Key));

        Assert.Throws<ArgumentException>(() => AgingBuckets.FromBoundaries([]));
        Assert.Throws<ArgumentException>(() => AgingBuckets.FromBoundaries([30, 30]));
        Assert.Throws<ArgumentException>(() => AgingBuckets.FromBoundaries([60, 30]));
        Assert.Throws<ArgumentException>(() => AgingBuckets.FromBoundaries([0, 30]));
    }

    /// <summary>AC-03 / INV-08 / FIN-52: over random boundaries and random invoices, Σ buckets == Σ AR and every row lands once.</summary>
    [Fact]
    public void BucketSums_EqualTotalAr()
    {
        var seed = Environment.TickCount;
        var random = new Random(seed);
        for (var round = 0; round < 500; round++)
        {
            var count = random.Next(1, 5);
            var boundaries = new List<int>();
            var last = 0;
            for (var i = 0; i < count; i++)
            {
                last += random.Next(1, 60);
                boundaries.Add(last);
            }

            var buckets = AgingBuckets.FromBoundaries(boundaries);
            var rows = Enumerable.Range(0, random.Next(0, 40)).Select(_ => Row(random.Next(-40, 400), random.Next(1, 1_000_000) / 1000m)).ToList();
            var totals = AgingService.Bucketize(buckets, rows);

            Assert.True(rows.Sum(r => r.OpenBalance) == totals.Sum(t => t.Amount), $"seed {seed}: bucket sums differ from AR");
            Assert.True(rows.Count == totals.Sum(t => t.InvoiceCount), $"seed {seed}: an invoice landed in zero or two buckets");
            Assert.Equal(buckets.All.Count, totals.Count);                       // every bucket present, even empty
            foreach (var row in rows)
            {
                Assert.Equal(1, buckets.All.Count(b => b.Contains(row.DaysPastDue)));
            }
        }
    }

    [Fact]
    public void DaysPastDue_UsesTheBasis_InCalendarDays()
    {
        var asOf = new DateOnly(2026, 9, 11);
        Assert.Equal(1, AgingRules.DaysPastDue(asOf, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 10), AgingBasis.DueDate));
        Assert.Equal(41, AgingRules.DaysPastDue(asOf, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 10), AgingBasis.IssueDate));
        Assert.Equal(-19, AgingRules.DaysPastDue(asOf, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30), AgingBasis.DueDate));
    }

    /// <summary>FIN-58 / T-25: the calendar day is the tenant's, across a DST transition too.</summary>
    [Theory]
    [InlineData("Asia/Amman", "2026-09-10T20:59:00Z", "2026-09-10")]
    [InlineData("Asia/Amman", "2026-09-10T21:01:00Z", "2026-09-11")]
    [InlineData("Europe/London", "2026-10-24T23:30:00Z", "2026-10-25")]    // BST, UTC+1
    [InlineData("Europe/London", "2026-10-25T23:30:00Z", "2026-10-25")]    // GMT after the switch, UTC+0
    [InlineData("UTC", "2026-09-10T23:59:00Z", "2026-09-10")]
    public void TodayIn_IsTheTenantCalendarDate(string tz, string utc, string expected)
    {
        var now = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
        Assert.Equal(DateOnly.Parse(expected, CultureInfo.InvariantCulture), AgingRules.TodayIn(tz, now));
    }

    /// <summary>The one money rounding in the slice: per invoice, three decimals, away from zero.</summary>
    [Fact]
    public void ToBaseIndicative_RoundsOncePerInvoice()
    {
        Assert.Equal(709.000m, AgingRules.ToBaseIndicative(1000m, 0.709m));
        Assert.Equal(0.001m, AgingRules.ToBaseIndicative(0.001m, 1m));
        Assert.Equal(0.709m, AgingRules.ToBaseIndicative(1m, 0.70850m));         // .7085 → .709 away from zero
        Assert.Equal(7.085m, AgingRules.ToBaseIndicative(10m, 0.7085m));
    }

    /// <summary>FIN-60: days, one decimal; null without sales.</summary>
    [Fact]
    public void Dso_IsTheDocumentedFormula()
    {
        Assert.Equal(45.0m, AgingRules.Dso(50_000m, 100_000m, 90));
        Assert.Equal(30.9m, AgingRules.Dso(12_345.678m, 36_000m, 90));   // 30.864… → 30.9
        Assert.Null(AgingRules.Dso(1m, 0m, 90));
    }

    /// <summary>FIN-61 / AC-13.</summary>
    [Fact]
    public void AverageDaysToPay_StatesSampleSize()
    {
        Assert.Equal(10.0m, AgingRules.AverageDaysToPay([5, 15]));
        Assert.Equal(-2.5m, AgingRules.AverageDaysToPay([-5, 0]));      // early payers are negative days
        Assert.Null(AgingRules.AverageDaysToPay([]));
    }

    [Fact]
    public void ParseBasis_AcceptsOnlyTheTwoValues()
    {
        Assert.Equal(AgingBasis.DueDate, AgingRules.ParseBasis(null));
        Assert.Equal(AgingBasis.IssueDate, AgingRules.ParseBasis("issue_date"));
        Assert.Throws<ArgumentException>(() => AgingRules.ParseBasis("invoice_date"));
    }

    private static AgingService.AgedInvoice Row(int daysPastDue, decimal open) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "INV", "JOD", new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), open + 1m, open, daysPastDue, 1m, "JOD");
}
