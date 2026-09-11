using System.Globalization;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Ledger;
using FinanceAi.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 4. Read-only. Handlers parse the query and shape the response; every figure comes from
/// <see cref="AgingService"/>. Nothing here adds two amounts.
/// </summary>
public static class ReportEndpoints
{
    public static RouteGroupBuilder MapReportEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var reports = api.MapGroup("/reports");
        reports.MapGet("/aging", AgingAsync).RequiresPermission(Permissions.AgingRead).WithName("AgingReport");
        reports.MapGet("/aging/customers/{id:guid}", AgingCustomerAsync).RequiresPermission(Permissions.AgingRead).WithName("AgingCustomerDetail");
        reports.MapGet("/aging/export", ExportAsync).RequiresPermission(Permissions.ExportRun).WithName("AgingExport");
        reports.MapGet("/dso", DsoAsync).RequiresPermission(Permissions.AgingRead).WithName("Dso");
        reports.MapGet("/reconciliation", ReconciliationAsync).RequiresPermission(Permissions.AuditRead).WithName("Reconciliation");

        return api;
    }

    private sealed record Query(DateOnly? AsOf, string? Basis, string? Currency, Guid? CustomerId, bool ByCustomer);

    /// <summary>Query parsing shared by the report and the export. Invalid input is a 400 with the field named.</summary>
    private static IResult? ParseQuery(HttpContext context, out Query query)
    {
        var q = context.Request.Query;
        var validation = new Validation();
        DateOnly? asOf = null;
        if (q.TryGetValue("asOf", out var asOfText) && asOfText.ToString().Length > 0)
        {
            if (DateOnly.TryParseExact(asOfText.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) asOf = d;
            else validation.Require("asOf", null, "invalid_date");
        }

        string? basis = q.TryGetValue("basis", out var basisText) && basisText.ToString().Length > 0 ? basisText.ToString() : null;
        if (basis is not null && basis is not ("due_date" or "issue_date")) validation.Require("basis", null, "invalid");

        string? currency = q.TryGetValue("currency", out var currencyText) && currencyText.ToString().Length > 0 ? currencyText.ToString() : null;
        validation.Currency("currency", currency);

        Guid? customerId = null;
        if (q.TryGetValue("customerId", out var customerText) && customerText.ToString().Length > 0)
        {
            if (Guid.TryParse(customerText.ToString(), out var g)) customerId = g;
            else validation.Require("customerId", null, "invalid");
        }

        var groupBy = q.TryGetValue("groupBy", out var groupText) ? groupText.ToString() : "bucket";
        if (groupBy is not ("bucket" or "customer" or "")) validation.Require("groupBy", null, "invalid");

        query = new Query(asOf, basis, currency, customerId, groupBy == "customer");
        return validation.HasErrors ? ApiProblems.ValidationProblem(context, validation.Errors) : null;
    }

    private static async Task<IResult> AgingAsync(HttpContext context, AgingService aging, CancellationToken ct)
    {
        if (ParseQuery(context, out var query) is { } problem)
        {
            return problem;
        }

        var report = await aging.ReportAsync(query.AsOf, query.Basis, query.Currency, query.CustomerId, query.ByCustomer, ct);
        return TypedResults.Ok(ToDto(report));
    }

    private static async Task<IResult> AgingCustomerAsync(Guid id, HttpContext context, AgingService aging, CancellationToken ct)
    {
        if (ParseQuery(context, out var query) is { } problem)
        {
            return problem;
        }

        var bucket = context.Request.Query.TryGetValue("bucket", out var b) && b.ToString().Length > 0 ? b.ToString() : null;
        var detail = await aging.CustomerAsync(id, query.AsOf, query.Basis, bucket, ct);
        if (detail is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        return TypedResults.Ok(new AgingCustomerDetailResponse(
            detail.CustomerId, Iso(detail.AsOf), AgingRules.BasisKey(detail.Basis),
            detail.Invoices.Select(d => new AgedInvoiceDto(
                d.Invoice.InvoiceId, d.Invoice.InvoiceNumber, d.Invoice.Currency, Iso(d.Invoice.IssueDate), Iso(d.Invoice.DueDate),
                MoneyDto.From(d.Invoice.TotalAmount, d.Invoice.Currency), MoneyDto.From(d.Invoice.OpenBalance, d.Invoice.Currency),
                d.Invoice.DaysPastDue, d.Bucket)).ToList(),
            detail.AverageDaysToPay?.ToString("0.0", CultureInfo.InvariantCulture), detail.AverageDaysToPaySampleSize));
    }

    private static async Task<IResult> DsoAsync(HttpContext context, AgingService aging, CancellationToken ct)
    {
        if (ParseQuery(context, out var query) is { } problem)
        {
            return problem;
        }

        var figures = await aging.DsoAsync(query.AsOf, query.Currency, ct);
        var asOf = figures.Count > 0 ? figures[0].PeriodEnd : (await aging.ContextAsync(query.AsOf, null, ct)).AsOf;
        return TypedResults.Ok(new DsoResponse(Iso(asOf), figures.Select(f => new DsoDto(
            f.Currency, f.Dso?.ToString("0.0", CultureInfo.InvariantCulture), f.InsufficientHistory,
            MoneyDto.From(f.ArAtPeriodEnd, f.Currency), MoneyDto.From(f.CreditSalesInPeriod, f.Currency),
            f.DaysInPeriod, Iso(f.PeriodStart), Iso(f.PeriodEnd))).ToList(), "reports.dso.disclaimer"));
    }

    private static async Task<IResult> ReconciliationAsync(BalanceReconciliation reconciliation, TimeProvider time, CancellationToken ct)
    {
        var result = await reconciliation.CheckAsync(ct);
        return TypedResults.Ok(new ReconciliationResponse(
            time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), result.InvoicesChecked,
            result.Mismatches.Select(m => new ReconciliationMismatchDto(m.InvoiceId, m.InvoiceNumber, m.Rule,
                MoneyDto.From(m.Cached, m.Currency), MoneyDto.From(m.Derived, m.Currency), m.Status.ToString())).ToList()));
    }

    /// <summary>
    /// SEC-45 escaping and RTL handling live in <see cref="AgingExport"/>. Headers are localized from the
    /// <c>locale</c> query (default: the user's preferred locale); the file name carries the as-of date.
    /// </summary>
    private static async Task<IResult> ExportAsync(HttpContext context, CurrentUser user, TenantDbContext db, AgingService aging, CancellationToken ct)
    {
        if (ParseQuery(context, out var query) is { } problem)
        {
            return problem;
        }

        var format = context.Request.Query.TryGetValue("format", out var f) ? f.ToString() : "xlsx";
        if (format is not ("xlsx" or "csv"))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("format", "invalid", "errors.validation.format.invalid")]);
        }

        var locale = context.Request.Query.TryGetValue("locale", out var l) && l.ToString().Length > 0
            ? l.ToString()
            : await db.Users.Where(u => u.Id == user.UserId).Select(u => u.PreferredLocale).FirstOrDefaultAsync(ct) ?? Locales.Default;
        var arabic = locale.StartsWith("ar", StringComparison.OrdinalIgnoreCase);

        var report = await aging.ReportAsync(query.AsOf, query.Basis, query.Currency, query.CustomerId, byCustomer: true, ct);
        var table = BuildTable(report, arabic);
        var bytes = format == "csv" ? AgingExport.ToCsv(table) : AgingExport.ToXlsx(table);
        var fileName = $"aging-{Iso(report.AsOf)}.{format}";
        var contentType = format == "csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return TypedResults.File(bytes, contentType, fileName);
    }

    /// <summary>One row per customer per currency, then the currency total, unapplied lines, and the basis note.</summary>
    private static AgingExport.Table BuildTable(AgingService.Report report, bool arabic)
    {
        string T(string en, string ar) => arabic ? ar : en;
        string BucketLabel(string key)
        {
            if (key == AgingBuckets.CurrentKey) return T("Current", "غير مستحق");
            var m = System.Text.RegularExpressions.Regex.Match(key, "^Days(\\d+)To(\\d+)$");
            if (m.Success) return T($"{m.Groups[1].Value}–{m.Groups[2].Value} days", $"{m.Groups[1].Value}–{m.Groups[2].Value} يومًا");
            m = System.Text.RegularExpressions.Regex.Match(key, "^Days(\\d+)Plus$");
            return m.Success ? T($"Over {m.Groups[1].Value} days", $"أكثر من {m.Groups[1].Value} يومًا") : key;
        }

        var headers = new List<string> { T("Customer code", "رمز العميل"), T("Customer", "العميل"), T("Currency", "العملة") };
        headers.AddRange(report.BucketKeys.Select(BucketLabel));
        headers.Add(T("Total", "الإجمالي"));
        headers.Add(T("Of which disputed", "منها متنازع عليه"));

        var rows = new List<IReadOnlyList<AgingExport.Cell>>();
        foreach (var section in report.Currencies)
        {
            foreach (var customer in section.Customers ?? [])
            {
                var name = (arabic ? customer.NameAr ?? customer.NameEn : customer.NameEn ?? customer.NameAr) ?? string.Empty;
                var row = new List<AgingExport.Cell> { new(customer.Code ?? string.Empty), new(name), new(section.Currency) };
                row.AddRange(customer.Buckets.Select(b => new AgingExport.Cell(AgingService.F3(b.Amount), IsNumber: true)));
                row.Add(new AgingExport.Cell(AgingService.F3(customer.Total), IsNumber: true));
                row.Add(new AgingExport.Cell(AgingService.F3(customer.DisputedTotal), IsNumber: true));
                rows.Add(row);
            }

            var total = new List<AgingExport.Cell> { new(string.Empty), new(T("Total", "الإجمالي")), new(section.Currency) };
            total.AddRange(section.Buckets.Select(b => new AgingExport.Cell(AgingService.F3(b.Amount), IsNumber: true)));
            total.Add(new AgingExport.Cell(AgingService.F3(section.Total), IsNumber: true));
            total.Add(new AgingExport.Cell(AgingService.F3(section.DisputedTotal), IsNumber: true));
            rows.Add(total);
            rows.Add([new(string.Empty), new(T("Unapplied cash", "نقد غير مخصص")), new(section.Currency), new(AgingService.F3(section.UnappliedCash), IsNumber: true)]);
            rows.Add([new(string.Empty), new(T("Unapplied credit", "رصيد دائن غير مطبق")), new(section.Currency), new(AgingService.F3(section.UnappliedCredit), IsNumber: true)]);
        }

        var basis = report.Basis == AgingBasis.IssueDate ? T("issue date", "تاريخ الإصدار") : T("due date", "تاريخ الاستحقاق");
        rows.Add([new(T($"As of {Iso(report.AsOf)} ({report.Timezone}), aged by {basis}. Indicative total in {report.BaseCurrency}: {AgingService.F3(report.BaseCurrencyTotal)} (converted at document rates; not an accounting figure).",
            $"كما في {Iso(report.AsOf)} ({report.Timezone})، محسوب حسب {basis}. الإجمالي الاسترشادي بعملة {report.BaseCurrency}: {AgingService.F3(report.BaseCurrencyTotal)} (محوّل بأسعار المستندات؛ ليس رقمًا محاسبيًا)."))]);

        return new AgingExport.Table(headers, rows, arabic, T("Aging", "أعمار الذمم"));
    }

    private static AgingReportResponse ToDto(AgingService.Report r) => new(
        Iso(r.AsOf), AgingRules.BasisKey(r.Basis), r.Timezone, r.BucketBoundaries, r.BucketKeys,
        r.Currencies.Select(s => new AgingCurrencyDto(
            s.Currency, s.Buckets.Select(b => Bucket(b, s.Currency)).ToList(), MoneyDto.From(s.Total, s.Currency), MoneyDto.From(s.DisputedTotal, s.Currency), s.InvoiceCount,
            MoneyDto.From(s.UnappliedCash, s.Currency), MoneyDto.From(s.UnappliedCredit, s.Currency),
            s.Customers?.Select(c => new AgingCustomerRowDto(c.CustomerId, c.Code, c.NameAr, c.NameEn,
                c.Buckets.Select(b => Bucket(b, s.Currency)).ToList(), MoneyDto.From(c.Total, s.Currency), MoneyDto.From(c.DisputedTotal, s.Currency), c.InvoiceCount)).ToList())).ToList(),
        new IndicativeMoneyDto(AgingService.F3(r.BaseCurrencyTotal), r.BaseCurrency, Indicative: true),
        r.DisputedAvailable, "reports.aging.explanation");

    private static AgingBucketDto Bucket(AgingService.BucketTotal b, string currency) =>
        new(b.Bucket, MoneyDto.From(b.Amount, currency), b.InvoiceCount, MoneyDto.From(b.DisputedAmount, currency));

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
