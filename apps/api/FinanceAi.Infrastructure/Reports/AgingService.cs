using System.Globalization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Reports;

/// <summary>
/// Doc 03 §5. Reads only. The balance as of any date — including today — comes from history through
/// <c>fn_aging</c> (FIN-57, slice 4 D-2); bucketing happens here from the tenant's settings; nothing is
/// summed across currencies (FIN-04). The single rounding is <see cref="AgingRules.ToBaseIndicative"/>.
/// </summary>
public sealed class AgingService(TenantDbContext db, TimeProvider time)
{
    /// <summary>A row of <c>fn_aging</c>: one invoice with an open balance as of the date.</summary>
    public sealed record AgedInvoice(
        Guid InvoiceId, Guid CustomerId, string InvoiceNumber, string Currency, DateOnly IssueDate, DateOnly DueDate,
        decimal TotalAmount, decimal OpenBalance, int DaysPastDue, decimal FxRateToBase, string BaseCurrency);

    public sealed record BucketTotal(string Bucket, decimal Amount, int InvoiceCount, decimal DisputedAmount);

    public sealed record CustomerRow(Guid CustomerId, string? Code, string? NameAr, string? NameEn, IReadOnlyList<BucketTotal> Buckets, decimal Total, decimal DisputedTotal, int InvoiceCount);

    public sealed record CurrencySection(
        string Currency, IReadOnlyList<BucketTotal> Buckets, decimal Total, decimal DisputedTotal, int InvoiceCount,
        decimal UnappliedCash, decimal UnappliedCredit, IReadOnlyList<CustomerRow>? Customers);

    public sealed record Report(
        DateOnly AsOf, AgingBasis Basis, string Timezone, IReadOnlyList<int> BucketBoundaries, IReadOnlyList<string> BucketKeys,
        IReadOnlyList<CurrencySection> Currencies, string BaseCurrency, decimal BaseCurrencyTotal, bool DisputedAvailable);

    public sealed record AgedInvoiceDetail(AgedInvoice Invoice, string Bucket, decimal DisputedAmount);

    public sealed record CustomerDetail(
        Guid CustomerId, DateOnly AsOf, AgingBasis Basis, IReadOnlyList<AgedInvoiceDetail> Invoices,
        decimal? AverageDaysToPay, int AverageDaysToPaySampleSize);

    public sealed record DsoFigure(string Currency, decimal? Dso, bool InsufficientHistory, decimal ArAtPeriodEnd, decimal CreditSalesInPeriod, int DaysInPeriod, DateOnly PeriodStart, DateOnly PeriodEnd);

    public sealed record Context(DateOnly AsOf, string Timezone, AgingBasis Basis, AgingBuckets Buckets, string BaseCurrency);

    public async Task<Context> ContextAsync(DateOnly? asOf, string? basisOverride, CancellationToken ct)
    {
        var tenant = await db.Tenants.Select(t => new { t.Timezone, t.BaseCurrency }).FirstAsync(ct);
        var settings = await db.TenantSettings.Select(s => new { s.AgingBasis, s.AgingBucketDays }).FirstAsync(ct);
        var today = AgingRules.TodayIn(tenant.Timezone, time.GetUtcNow());
        var basis = AgingRules.ParseBasis(basisOverride ?? settings.AgingBasis);
        return new Context(asOf ?? today, tenant.Timezone, basis, AgingBuckets.FromBoundaries(settings.AgingBucketDays), tenant.BaseCurrency);
    }

    /// <summary>
    /// Raw SQL under RLS with the explicit tenant predicate — the slice 2 pattern. The function runs as the
    /// caller, so even a wrong tenant id here can only ever narrow the result to nothing (AC-17).
    /// </summary>
    public Task<List<AgedInvoice>> AgedInvoicesAsync(Context context, Guid? customerId, string? currency, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        var basis = AgingRules.BasisKey(context.Basis);
        return db.Database.SqlQuery<AgedInvoice>(
            $"""
             SELECT invoice_id AS "InvoiceId", customer_id AS "CustomerId", invoice_number AS "InvoiceNumber",
                    currency AS "Currency", issue_date AS "IssueDate", due_date AS "DueDate",
                    total_amount AS "TotalAmount", open_balance AS "OpenBalance", days_past_due AS "DaysPastDue",
                    fx_rate_to_base AS "FxRateToBase", base_currency AS "BaseCurrency"
             FROM fn_aging({tenantId}, {context.AsOf}, {basis}, {context.Timezone})
             WHERE ({customerId}::uuid IS NULL OR customer_id = {customerId}::uuid)
               AND ({currency}::text IS NULL OR currency = {currency}::text)
             """).ToListAsync(ct);
    }

    public async Task<Report> ReportAsync(DateOnly? asOf, string? basis, string? currency, Guid? customerId, bool byCustomer, CancellationToken ct)
    {
        var context = await ContextAsync(asOf, basis, ct);
        var rows = await AgedInvoicesAsync(context, customerId, currency, ct);
        var unapplied = await UnappliedAsync(context.AsOf, customerId, ct);
        var disputed = await DisputedByInvoiceAsync(ct);

        var customerIds = rows.Select(r => r.CustomerId).Distinct().ToList();
        var customers = byCustomer
            ? await db.Customers
                .Where(c => customerIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Code, c.NameAr, c.NameEn })
                .ToDictionaryAsync(c => c.Id, ct)
            : null;

        var sections = new List<CurrencySection>();
        foreach (var group in rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = group.ToList();
            IReadOnlyList<CustomerRow>? customerRows = null;
            if (customers is not null)
            {
                customerRows = list.GroupBy(r => r.CustomerId)
                    .Select(g =>
                    {
                        customers.TryGetValue(g.Key, out var c);
                        var buckets = Bucketize(context.Buckets, g.ToList(), disputed);
                        return new CustomerRow(g.Key, c?.Code, c?.NameAr, c?.NameEn, buckets, g.Sum(r => r.OpenBalance), buckets.Sum(b => b.DisputedAmount), g.Count());
                    })
                    .OrderByDescending(r => r.Total)
                    .ToList();
            }

            unapplied.TryGetValue(group.Key, out var lines);
            var sectionBuckets = Bucketize(context.Buckets, list, disputed);
            sections.Add(new CurrencySection(group.Key, sectionBuckets, list.Sum(r => r.OpenBalance), sectionBuckets.Sum(b => b.DisputedAmount), list.Count,
                lines.Cash, lines.Credit, customerRows));
        }

        // FIN-59: unapplied cash or credit in a currency with no open invoice is still reported — it is money the
        // customer is owed back or has on account, and hiding it is how "the report doesn't match the bank" starts.
        foreach (var (cur, lines) in unapplied.Where(u => sections.All(s => s.Currency != u.Key)).OrderBy(u => u.Key, StringComparer.Ordinal))
        {
            sections.Add(new CurrencySection(cur, Bucketize(context.Buckets, []), 0m, 0m, 0, lines.Cash, lines.Credit, customers is null ? null : []));
        }

        // FIN-55: per-invoice conversion, rounded once each, then summed. The only rounding in the slice.
        var baseTotal = rows.Sum(r => AgingRules.ToBaseIndicative(r.OpenBalance, r.FxRateToBase));

        return new Report(context.AsOf, context.Basis, context.Timezone, context.Buckets.Boundaries,
            context.Buckets.All.Select(b => b.Key).ToList(), sections, context.BaseCurrency, baseTotal, DisputedAvailable: true);
    }

    public sealed record OverdueTotals(DateOnly AsOf, string BaseCurrency, decimal BaseTotal, IReadOnlyList<(string Currency, decimal Total, int InvoiceCount)> ByCurrency);

    /// <summary>
    /// Slice 10: the overdue position the briefing quotes. The same rows and the same per-invoice
    /// <see cref="AgingRules.ToBaseIndicative"/> as the aging report (FIN-55) — only rows past due are summed.
    /// </summary>
    public async Task<OverdueTotals> OverdueAsync(DateOnly? asOf, CancellationToken ct)
    {
        var context = await ContextAsync(asOf, null, ct);
        var rows = (await AgedInvoicesAsync(context, null, null, ct)).Where(r => r.DaysPastDue > 0).ToList();
        var byCurrency = rows.GroupBy(r => r.Currency).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Currency: g.Key, Total: g.Sum(r => r.OpenBalance), InvoiceCount: g.Count())).ToList();
        return new OverdueTotals(context.AsOf, context.BaseCurrency, rows.Sum(r => AgingRules.ToBaseIndicative(r.OpenBalance, r.FxRateToBase)), byCurrency);
    }

    /// <summary>FIN-52 by construction: every row is classified once; a bucket with nothing in it is still present.</summary>
    public static IReadOnlyList<BucketTotal> Bucketize(AgingBuckets buckets, IReadOnlyList<AgedInvoice> rows, IReadOnlyDictionary<Guid, decimal>? disputedByInvoice = null)
    {
        var totals = buckets.All.ToDictionary(b => b.Key, _ => (Amount: 0m, Count: 0, Disputed: 0m), StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = buckets.Classify(row.DaysPastDue).Key;
            var current = totals[key];
            // FIN-56 / SM-41: the disputed figure is a separate column and is *also* inside the bucket amount;
            // it is capped at what is still owed so a payment since raising cannot show a dispute larger than the debt.
            var disputed = disputedByInvoice is not null && disputedByInvoice.TryGetValue(row.InvoiceId, out var d) ? DisputeRules.DisputedForAging(d, row.OpenBalance) : 0m;
            totals[key] = (current.Amount + row.OpenBalance, current.Count + 1, current.Disputed + disputed);
        }

        return buckets.All.Select(b => new BucketTotal(b.Key, totals[b.Key].Amount, totals[b.Key].Count, totals[b.Key].Disputed)).ToList();
    }

    /// <summary>Open disputes per invoice, today. As-of history for disputes is not kept (slice 7 §2).</summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> DisputedByInvoiceAsync(CancellationToken ct) =>
        await db.Disputes
            .Where(d => d.Status == DisputeStatus.Open || d.Status == DisputeStatus.UnderReview || d.Status == DisputeStatus.PendingCustomer)
            .GroupBy(d => d.InvoiceId)
            .Select(g => new { g.Key, Sum = g.Sum(d => d.DisputedAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

    /// <summary>The invoices behind every cell for one customer (UI-44), plus FIN-61's average days to pay.</summary>
    public async Task<CustomerDetail?> CustomerAsync(Guid customerId, DateOnly? asOf, string? basis, string? bucket, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
        {
            return null;
        }

        var context = await ContextAsync(asOf, basis, ct);
        var rows = await AgedInvoicesAsync(context, customerId, null, ct);
        var disputed = await DisputedByInvoiceAsync(ct);
        var detail = rows
            .Select(r => new AgedInvoiceDetail(r, context.Buckets.Classify(r.DaysPastDue).Key, disputed.TryGetValue(r.InvoiceId, out var d) ? DisputeRules.DisputedForAging(d, r.OpenBalance) : 0m))
            .Where(d => bucket is null || d.Bucket == bucket)
            .OrderBy(d => d.Invoice.DueDate).ThenBy(d => d.Invoice.InvoiceNumber, StringComparer.Ordinal)
            .ToList();

        var (average, sample) = await AverageDaysToPayAsync(customerId, context.AsOf, ct);
        return new CustomerDetail(customerId, context.AsOf, context.Basis, detail, average, sample);
    }

    /// <summary>
    /// FIN-61: over invoices settled in the trailing 12 months, the mean of (settlement date − due date), where the
    /// settlement date is the effective date of the last balance-reducing row. Advisory; the sample size travels with it.
    /// </summary>
    public async Task<(decimal? Average, int SampleSize)> AverageDaysToPayAsync(Guid customerId, DateOnly asOf, CancellationToken ct)
    {
        var from = asOf.AddYears(-1);
        var tenantId = db.CurrentTenantId;
        var rows = await db.Database.SqlQuery<SettledRow>(
            $"""
             SELECT i.due_date AS "DueDate", s.settled_on AS "SettledOn"
             FROM invoices i
             JOIN LATERAL (
               SELECT max(d) AS settled_on FROM (
                 SELECT a.effective_date AS d FROM payment_allocations a WHERE a.tenant_id = i.tenant_id AND a.invoice_id = i.id AND a.is_active
                 UNION ALL
                 SELECT c.effective_date FROM credit_note_applications c WHERE c.tenant_id = i.tenant_id AND c.invoice_id = i.id AND c.is_active
                 UNION ALL
                 SELECT (h.created_at AT TIME ZONE t.timezone)::date FROM withholding_deductions h, tenants t
                 WHERE h.tenant_id = i.tenant_id AND h.invoice_id = i.id AND h.is_active AND t.id = i.tenant_id
               ) x
             ) s ON true
             WHERE i.tenant_id = {tenantId} AND i.customer_id = {customerId} AND i.status = 'Settled'
               AND s.settled_on IS NOT NULL AND s.settled_on > {from} AND s.settled_on <= {asOf}
             """).ToListAsync(ct);

        var days = rows.Select(r => r.SettledOn.DayNumber - r.DueDate.DayNumber).ToList();
        return (AgingRules.AverageDaysToPay(days), days.Count);
    }

    private sealed record SettledRow(DateOnly DueDate, DateOnly SettledOn);

    /// <summary>FIN-60, per currency. The 90-day period ends on the as-of date.</summary>
    public async Task<IReadOnlyList<DsoFigure>> DsoAsync(DateOnly? asOf, string? currency, CancellationToken ct)
    {
        const int days = 90;
        var context = await ContextAsync(asOf, null, ct);
        var end = context.AsOf;
        var start = end.AddDays(-(days - 1));
        var earliest = await db.Invoices.Where(i => i.Status != InvoiceStatus.Void && i.Status != InvoiceStatus.Imported)
            .OrderBy(i => i.IssueDate).Select(i => (DateOnly?)i.IssueDate).FirstOrDefaultAsync(ct);
        var enoughHistory = earliest is not null && earliest.Value <= end.AddDays(-days);

        var sales = await db.Invoices
            .Where(i => i.Status != InvoiceStatus.Void && i.Status != InvoiceStatus.Imported && i.IssueDate >= start && i.IssueDate <= end)
            .Where(i => currency == null || i.Currency == currency)
            .GroupBy(i => i.Currency)
            .Select(g => new { Currency = g.Key, Sales = g.Sum(i => i.TotalAmount) })
            .ToDictionaryAsync(x => x.Currency, x => x.Sales, StringComparer.Ordinal, ct);

        var ar = (await AgedInvoicesAsync(context, null, currency, ct))
            .GroupBy(r => r.Currency).ToDictionary(g => g.Key, g => g.Sum(r => r.OpenBalance), StringComparer.Ordinal);

        return sales.Keys.Union(ar.Keys, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).Select(cur =>
        {
            ar.TryGetValue(cur, out var arAmount);
            sales.TryGetValue(cur, out var salesAmount);
            var dso = enoughHistory ? AgingRules.Dso(arAmount, salesAmount, days) : null;
            return new DsoFigure(cur, dso, InsufficientHistory: !enoughHistory || dso is null, arAmount, salesAmount, days, start, end);
        }).ToList();
    }

    /// <summary>
    /// FIN-59, as of the date: cash received on or before it and not yet allocated by it, and credit issued on or before
    /// it and not yet applied by it. A payment reversed after the date still counted on the date (FIN-57).
    /// </summary>
    private async Task<Dictionary<string, (decimal Cash, decimal Credit)>> UnappliedAsync(DateOnly asOf, Guid? customerId, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        var tz = (await db.Tenants.Select(t => t.Timezone).FirstAsync(ct));
        var rows = await db.Database.SqlQuery<UnappliedRow>(
            $"""
             SELECT p.currency AS "Currency",
                    sum(p.amount) - coalesce((SELECT sum(CASE WHEN a.reversal_of_id IS NULL THEN a.amount ELSE -a.amount END)
                                              FROM payment_allocations a
                                              WHERE a.tenant_id = p.tenant_id AND a.effective_date <= {asOf}
                                                AND a.payment_id IN (SELECT id FROM payments q WHERE q.tenant_id = p.tenant_id AND q.currency = p.currency
                                                                     AND ({customerId}::uuid IS NULL OR q.customer_id = {customerId}::uuid))), 0) AS "Cash",
                    0::numeric AS "Credit"
             FROM payments p
             WHERE p.tenant_id = {tenantId} AND p.received_date <= {asOf}
               AND p.status <> 'Pending'
               AND (p.reversed_at IS NULL OR (p.reversed_at AT TIME ZONE {tz})::date > {asOf})
               AND ({customerId}::uuid IS NULL OR p.customer_id = {customerId}::uuid)
             GROUP BY p.tenant_id, p.currency
             UNION ALL
             SELECT n.currency,
                    0::numeric,
                    sum(n.amount) - coalesce((SELECT sum(CASE WHEN c.reversal_of_id IS NULL THEN c.amount ELSE -c.amount END)
                                              FROM credit_note_applications c
                                              WHERE c.tenant_id = n.tenant_id AND c.effective_date <= {asOf}
                                                AND c.credit_note_id IN (SELECT id FROM credit_notes m WHERE m.tenant_id = n.tenant_id AND m.currency = n.currency
                                                                         AND ({customerId}::uuid IS NULL OR m.customer_id = {customerId}::uuid))), 0)
             FROM credit_notes n
             WHERE n.tenant_id = {tenantId} AND n.issue_date <= {asOf}
               AND (n.voided_at IS NULL OR (n.voided_at AT TIME ZONE {tz})::date > {asOf})
               AND ({customerId}::uuid IS NULL OR n.customer_id = {customerId}::uuid)
             GROUP BY n.tenant_id, n.currency
             """).ToListAsync(ct);

        var result = new Dictionary<string, (decimal Cash, decimal Credit)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result.TryGetValue(row.Currency, out var current);
            result[row.Currency] = (current.Cash + row.Cash, current.Credit + row.Credit);
        }

        // A currency whose every payment is fully allocated reports zero, not absence.
        foreach (var key in result.Keys.ToList())
        {
            var v = result[key];
            result[key] = (v.Cash < 0m ? 0m : v.Cash, v.Credit < 0m ? 0m : v.Credit);
        }

        return result;
    }

    private sealed record UnappliedRow(string Currency, decimal Cash, decimal Credit);

    public static string F3(decimal value) => value.ToString("F3", CultureInfo.InvariantCulture);
}
