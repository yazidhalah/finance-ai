using System.Globalization;
using System.Text.Json;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Infrastructure.Import;

/// <summary>
/// The import pipeline of doc 05 slice 3: upload → map → preview → resolve → commit. Runs inside the
/// request's tenant scope; every query below is filtered by tenant at layer 1 and layer 2.
/// <para>
/// Nothing here rounds. Amounts are parsed at scale ≤ 3 or rejected; control totals are exact sums of
/// scale-3 decimals per currency (FIN-02, FIN-04, FIN-05).
/// </para>
/// </summary>
public sealed class ImportService(TenantDbContext db, IAuditWriter audit, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The fields a mapping must supply for any row to be accepted.</summary>
    private static readonly string[] RequiredTargets = [ImportFields.InvoiceNumber, ImportFields.IssueDate];

    // ---------------------------------------------------------------------------------------
    // Upload
    // ---------------------------------------------------------------------------------------

    public sealed record UploadResult(ImportBatch? Batch, string? ErrorCode, string? ErrorDetail, bool DuplicateFile);

    public async Task<UploadResult> UploadAsync(string? fileName, byte[] content, bool force, Guid tenantId, Guid userId, CancellationToken ct)
    {
        UploadInspector.Inspected inspected;
        TabularFile table;

        try
        {
            inspected = UploadInspector.Inspect(fileName, content);
            table = inspected.Kind == ImportFileKind.Csv ? CsvTableReader.Read(content) : XlsxTableReader.Read(content);
        }
        catch (ImportFileException ex)
        {
            return new UploadResult(null, ex.Code, ex.Message, false);
        }

        // DM-23: the same file, imported twice, is the classic disaster. Refuse unless overridden.
        var duplicate = await db.ImportBatches.AnyAsync(
            b => b.FileHash == inspected.Sha256Hex && b.Status != ImportBatchStatus.Cancelled && b.Status != ImportBatchStatus.Failed, ct);

        if (duplicate && !force)
        {
            return new UploadResult(null, "duplicate_file", "This file has already been imported.", true);
        }

        var batch = new ImportBatch
        {
            TenantId = tenantId,
            FileName = inspected.SafeFileName,
            FileHash = inspected.Sha256Hex,
            FileSize = content.Length,
            FileKind = inspected.Kind,
            FileContent = content,
            Headers = JsonSerializer.Serialize(table.Headers, Json),
            RowCount = table.Rows.Count,
            Forced = duplicate && force,
            UploadedBy = userId,
            UploadedAt = time.GetUtcNow(),
        };

        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        if (batch.Forced)
        {
            await audit.WriteAsync(Event(tenantId, userId, "import.duplicate_file_overridden", batch.Id, note: batch.FileName), ct);
        }

        return new UploadResult(batch, null, null, duplicate);
    }

    // ---------------------------------------------------------------------------------------
    // Mapping and validation
    // ---------------------------------------------------------------------------------------

    public sealed record MappingSpec(IReadOnlyDictionary<string, string> ColumnMap, string DateFormat, char DecimalSeparator);

    /// <summary>Validates the mapping itself: known targets only, no target mapped twice, required targets present.</summary>
    public static IReadOnlyList<(string Field, string Code)> ValidateMapping(MappingSpec spec, IReadOnlyList<string> headers)
    {
        var errors = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (header, target) in spec.ColumnMap)
        {
            if (!headers.Contains(header, StringComparer.Ordinal))
            {
                errors.Add((header, "unknown_header"));
            }

            if (!ImportFields.All.Contains(target, StringComparer.Ordinal))
            {
                errors.Add((header, "unknown_target"));
            }
            else if (!seen.Add(target))
            {
                errors.Add((header, "target_mapped_twice"));
            }
        }

        foreach (var required in RequiredTargets.Where(r => !seen.Contains(r)))
        {
            errors.Add((required, ImportErrorCodes.UnmappedRequiredField));
        }

        if (!seen.Contains(ImportFields.CustomerCode) && !seen.Contains(ImportFields.CustomerName))
        {
            errors.Add((ImportFields.CustomerName, ImportErrorCodes.UnmappedRequiredField));
        }

        if (!seen.Contains(ImportFields.TotalAmount) && !seen.Contains(ImportFields.NetAmount))
        {
            errors.Add((ImportFields.TotalAmount, ImportErrorCodes.UnmappedRequiredField));
        }

        if (!ImportValueParser.IsValidDateFormat(spec.DateFormat))
        {
            errors.Add(("dateFormat", "invalid_date_format"));
        }

        if (spec.DecimalSeparator is not ('.' or ','))
        {
            errors.Add(("decimalSeparator", "invalid_decimal_separator"));
        }

        return errors;
    }

    /// <summary>
    /// Applies a mapping to a batch: parses every row, validates it, resolves its customer, and stores
    /// the outcome. Re-applying replaces the previous preview; a committed batch is immutable.
    /// </summary>
    public async Task ApplyMappingAsync(ImportBatch batch, MappingSpec spec, Guid? savedMappingId, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Status is ImportBatchStatus.Committed or ImportBatchStatus.Committing or ImportBatchStatus.Cancelled)
        {
            throw new InvalidOperationException($"A batch in status {batch.Status} cannot be re-mapped.");
        }

        var table = batch.FileKind == ImportFileKind.Csv ? CsvTableReader.Read(batch.FileContent) : XlsxTableReader.Read(batch.FileContent);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == batch.TenantId, ct);

        batch.Status = ImportBatchStatus.Parsing;
        batch.ColumnMap = JsonSerializer.Serialize(spec.ColumnMap, Json);
        batch.DateFormat = spec.DateFormat;
        batch.DecimalSeparator = spec.DecimalSeparator;
        batch.MappingId = savedMappingId;

        // Previous preview rows are replaced. Rows of a committed batch are never touched — the guard
        // above is what makes the DELETE grant on import_rows safe.
        await db.ImportRows.Where(r => r.BatchId == batch.Id).ExecuteDeleteAsync(ct);

        var headerIndex = table.Headers.Select((h, i) => (h, i)).GroupBy(x => x.h, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        var targetIndex = spec.ColumnMap.ToDictionary(kv => kv.Value, kv => headerIndex[kv.Key], StringComparer.Ordinal);

        var parsedRows = table.Rows.Select((cells, i) => ParseRow(i + 1, table.Headers, cells, targetIndex, spec, tenant.BaseCurrency)).ToList();

        await ResolveCustomersAsync(parsedRows, ct);
        await FlagDuplicatesAsync(parsedRows, ct);

        var rows = parsedRows.Select(p => new ImportRow
        {
            TenantId = batch.TenantId,
            BatchId = batch.Id,
            RowNo = p.RowNo,
            Raw = p.RawJson,
            Parsed = p.Values is null ? null : JsonSerializer.Serialize(p.Values.ToJson(), Json),
            Outcome = p.Outcome,
            ErrorCode = p.ErrorCode,
            ErrorDetail = p.ErrorDetail,
            CustomerId = p.CustomerId,
        }).ToList();

        db.ImportRows.AddRange(rows);
        RefreshCounts(batch, rows);
        batch.Status = ImportBatchStatus.Preview;
        batch.RowVersion++;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(Event(batch.TenantId, userId, "import.mapped", batch.Id, note: $"{rows.Count} rows"), ct);
    }

    /// <summary>The typed content of one row after parsing. Amounts are decimal; nothing is derived.</summary>
    public sealed record ParsedValues(
        string InvoiceNumber, string? CustomerCode, string? CustomerName, DateOnly IssueDate, DateOnly? DueDate,
        string? Currency, decimal? NetAmount, decimal? TaxAmount, decimal? TotalAmount, decimal? FxRateToBase,
        string? PoReference, string? ExternalId, string? Notes)
    {
        public Dictionary<string, object?> ToJson() => new(StringComparer.Ordinal)
        {
            ["invoiceNumber"] = this.InvoiceNumber,
            ["customerCode"] = this.CustomerCode,
            ["customerName"] = this.CustomerName,
            ["issueDate"] = this.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["dueDate"] = this.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["currency"] = this.Currency,
            ["netAmount"] = this.NetAmount?.ToString("F3", CultureInfo.InvariantCulture),
            ["taxAmount"] = this.TaxAmount?.ToString("F3", CultureInfo.InvariantCulture),
            ["totalAmount"] = this.TotalAmount?.ToString("F3", CultureInfo.InvariantCulture),
            ["fxRateToBase"] = this.FxRateToBase?.ToString(CultureInfo.InvariantCulture),
            ["poReference"] = this.PoReference,
            ["externalId"] = this.ExternalId,
            ["notes"] = this.Notes,
        };
    }

    private sealed class ParsedRow
    {
        public int RowNo { get; init; }
        public required string RawJson { get; init; }
        public ParsedValues? Values { get; set; }
        public ImportRowOutcome Outcome { get; set; } = ImportRowOutcome.Accepted;
        public string? ErrorCode { get; set; }
        public string? ErrorDetail { get; set; }
        public Guid? CustomerId { get; set; }

        public void Reject(string code, string? detail = null)
        {
            if (this.Outcome == ImportRowOutcome.Accepted)
            {
                this.Outcome = ImportRowOutcome.Rejected;
                this.ErrorCode = code;
                this.ErrorDetail = detail;
            }
        }
    }

    private static ParsedRow ParseRow(int rowNo, IReadOnlyList<string> headers, IReadOnlyList<string> cells, Dictionary<string, int> targetIndex, MappingSpec spec, string baseCurrency)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
        {
            raw[headers[i]] = i < cells.Count ? cells[i] : string.Empty;
        }

        var row = new ParsedRow { RowNo = rowNo, RawJson = JsonSerializer.Serialize(raw, Json) };

        string? Cell(string target) =>
            targetIndex.TryGetValue(target, out var index) && index < cells.Count && !string.IsNullOrWhiteSpace(cells[index]) ? cells[index].Trim() : null;

        var invoiceNumber = Cell(ImportFields.InvoiceNumber);
        if (invoiceNumber is null)
        {
            row.Reject(ImportErrorCodes.MissingInvoiceNumber);
        }

        var customerCode = Cell(ImportFields.CustomerCode);
        var customerName = Cell(ImportFields.CustomerName);
        if (customerCode is null && customerName is null)
        {
            row.Reject(ImportErrorCodes.MissingCustomer);
        }

        DateOnly issueDate = default;
        var issueText = Cell(ImportFields.IssueDate);
        if (issueText is null)
        {
            row.Reject(ImportErrorCodes.MissingIssueDate);
        }
        else if (!ImportValueParser.TryParseDate(issueText, spec.DateFormat, out issueDate))
        {
            row.Reject(ImportErrorCodes.InvalidDate, $"issue_date '{issueText}' does not match {spec.DateFormat}");
        }

        DateOnly? dueDate = null;
        var dueText = Cell(ImportFields.DueDate);
        if (dueText is not null)
        {
            if (ImportValueParser.TryParseDate(dueText, spec.DateFormat, out var due))
            {
                dueDate = due;
            }
            else
            {
                row.Reject(ImportErrorCodes.InvalidDate, $"due_date '{dueText}' does not match {spec.DateFormat}");
            }
        }

        decimal? net = ParseMoney(row, Cell(ImportFields.NetAmount), ImportFields.NetAmount, spec.DecimalSeparator);
        decimal? tax = ParseMoney(row, Cell(ImportFields.TaxAmount), ImportFields.TaxAmount, spec.DecimalSeparator);
        decimal? total = ParseMoney(row, Cell(ImportFields.TotalAmount), ImportFields.TotalAmount, spec.DecimalSeparator);

        // Reconciliation, not repair. The three cases: all present (must add up), net only (total is
        // net plus tax, tax defaulting to zero), total only (net is total, tax zero — a header-only
        // export with no tax breakdown). Anything else is the customer's arithmetic to fix.
        if (row.Outcome == ImportRowOutcome.Accepted)
        {
            if (net is null && total is null)
            {
                row.Reject(ImportErrorCodes.MissingAmount);
            }
            else if (net is not null && total is not null)
            {
                var expected = net.Value + (tax ?? 0m);
                if (expected != total.Value)
                {
                    row.Reject(ImportErrorCodes.TotalsDoNotReconcile, $"net {net.Value:F3} + tax {(tax ?? 0m):F3} ≠ total {total.Value:F3}");
                }
            }
            else if (net is not null)
            {
                total = net.Value + (tax ?? 0m);
            }
            else
            {
                net = total!.Value - (tax ?? 0m);
                if (net < 0m)
                {
                    row.Reject(ImportErrorCodes.TotalsDoNotReconcile, "tax exceeds total");
                }
            }

            tax ??= 0m;
        }

        var currency = Cell(ImportFields.Currency)?.ToUpperInvariant();
        if (currency is not null && !System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Z]{3}$"))
        {
            row.Reject(ImportErrorCodes.InvalidCurrency, currency);
        }

        decimal? fxRate = null;
        var rateText = Cell(ImportFields.FxRateToBase);
        if (rateText is not null)
        {
            if (ImportValueParser.TryParseRate(rateText, spec.DecimalSeparator, out var rate))
            {
                fxRate = rate;
            }
            else
            {
                row.Reject(ImportErrorCodes.InvalidFxRate, rateText);
            }
        }

        // FIN-06: a foreign-currency document needs its rate now; a defaulted 1 would be a wrong number.
        if (currency is not null && currency != baseCurrency && fxRate is null)
        {
            row.Reject(ImportErrorCodes.MissingFxRate, $"{currency} invoice without fx_rate_to_base");
        }

        if (row.Outcome == ImportRowOutcome.Accepted && dueDate is { } d && d < issueDate)
        {
            row.Reject(ImportErrorCodes.DueBeforeIssue);
        }

        row.Values = new ParsedValues(
            invoiceNumber ?? string.Empty, customerCode, customerName, issueDate, dueDate,
            currency, net, tax, total, currency is null || currency == baseCurrency ? 1m : fxRate,
            Cell(ImportFields.PoReference), Cell(ImportFields.ExternalId), Cell(ImportFields.Notes));

        return row;
    }

    private static decimal? ParseMoney(ParsedRow row, string? text, string field, char separator)
    {
        if (text is null)
        {
            return null;
        }

        var error = ImportValueParser.TryParseMoney(text, separator, out var value);
        switch (error)
        {
            case ImportValueParser.MoneyError.None:
                return value;
            case ImportValueParser.MoneyError.TooManyDecimals:
                row.Reject(ImportErrorCodes.TooManyDecimals, $"{field} '{text}'");
                return null;
            case ImportValueParser.MoneyError.Negative:
                row.Reject(ImportErrorCodes.NegativeAmount, $"{field} '{text}'");
                return null;
            default:
                row.Reject(ImportErrorCodes.InvalidAmount, $"{field} '{text}'");
                return null;
        }
    }

    /// <summary>Code first, then normalized name — through the same database function the customers table uses (DM-20).</summary>
    private async Task ResolveCustomersAsync(List<ParsedRow> rows, CancellationToken ct)
    {
        var candidates = rows.Where(r => r.Values is not null).ToList();

        var codes = candidates.Select(r => r.Values!.CustomerCode).Where(c => c is not null).Select(c => c!.ToLowerInvariant()).Distinct().ToList();
        var byCode = codes.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : await db.Customers.Where(c => c.Code != null && codes.Contains(c.Code.ToLower()))
                .Select(c => new { Code = c.Code!.ToLower(), c.Id })
                .ToDictionaryAsync(c => c.Code, c => c.Id, StringComparer.Ordinal, ct);

        var names = candidates.Select(r => r.Values!.CustomerName).Where(n => n is not null).Select(n => n!).Distinct().ToList();
        var normalizedByName = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names.Count > 0)
        {
            var pairs = await db.Database.SqlQuery<NamePair>(
                $"SELECT n AS \"Name\", app_normalize_arabic(n) AS \"Normalized\" FROM unnest({names}) AS n").ToListAsync(ct);
            foreach (var pair in pairs)
            {
                normalizedByName[pair.Name] = pair.Normalized;
            }
        }

        // A customer's stored normalized_name covers both names at once (for search); a file names
        // one of them. Match each name individually through the same function. Raw SQL with an
        // explicit tenant predicate, under RLS — the slice 2 pattern for anything the EF filter
        // cannot express.
        var normalizedTargets = normalizedByName.Values.Distinct().ToList();
        var tenantId = db.CurrentTenantId;
        var byNormalizedName = normalizedTargets.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.Ordinal)
            : (await db.Database.SqlQuery<NameMatch>(
                $"""
                 SELECT id AS "Id", app_normalize_arabic(name_ar) AS "Key" FROM customers
                 WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND name_ar IS NOT NULL
                   AND app_normalize_arabic(name_ar) = ANY({normalizedTargets})
                 UNION ALL
                 SELECT id, app_normalize_arabic(name_en) FROM customers
                 WHERE tenant_id = {tenantId} AND deleted_at IS NULL AND name_en IS NOT NULL
                   AND app_normalize_arabic(name_en) = ANY({normalizedTargets})
                 """).ToListAsync(ct))
                .GroupBy(m => m.Key, StringComparer.Ordinal)
                .Where(g => g.Select(m => m.Id).Distinct().Count() == 1)   // an ambiguous name is not a match
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        foreach (var row in candidates)
        {
            var values = row.Values!;

            if (values.CustomerCode is not null && byCode.TryGetValue(values.CustomerCode.ToLowerInvariant(), out var byCodeId))
            {
                row.CustomerId = byCodeId;
            }
            else if (values.CustomerName is not null && normalizedByName.TryGetValue(values.CustomerName, out var normalized) && byNormalizedName.TryGetValue(normalized, out var byNameId))
            {
                row.CustomerId = byNameId;
            }
            else if (row.Outcome == ImportRowOutcome.Accepted)
            {
                row.Reject(ImportErrorCodes.CustomerNotFound, values.CustomerCode ?? values.CustomerName);
            }
        }
    }

    private sealed record NamePair(string Name, string Normalized);

    private sealed record NameMatch(Guid Id, string Key);

    /// <summary>Against existing live invoices of the same customer, then within the batch.</summary>
    private async Task FlagDuplicatesAsync(List<ParsedRow> rows, CancellationToken ct)
    {
        var accepted = rows.Where(r => r.Outcome == ImportRowOutcome.Accepted && r.CustomerId is not null).ToList();
        var numbers = accepted.Select(r => r.Values!.InvoiceNumber.ToUpperInvariant()).Distinct().ToList();

        var existing = numbers.Count == 0
            ? new HashSet<(Guid, string)>()
            : (await db.Invoices.Where(i => i.Status != InvoiceStatus.Void && numbers.Contains(i.InvoiceNumber.ToUpper()))
                .Select(i => new { i.CustomerId, Number = i.InvoiceNumber.ToUpper() }).ToListAsync(ct))
                .Select(i => (i.CustomerId, i.Number)).ToHashSet();

        var seenInBatch = new HashSet<(Guid, string)>();

        foreach (var row in accepted)
        {
            var key = (row.CustomerId!.Value, row.Values!.InvoiceNumber.ToUpperInvariant());

            if (existing.Contains(key))
            {
                row.Outcome = ImportRowOutcome.Duplicate;
                row.ErrorCode = ImportErrorCodes.DuplicateInvoiceNumber;
            }
            else if (!seenInBatch.Add(key))
            {
                row.Outcome = ImportRowOutcome.Duplicate;
                row.ErrorCode = ImportErrorCodes.DuplicateInBatch;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Resolve
    // ---------------------------------------------------------------------------------------

    public sealed record ResolveOutcome(bool Ok, string? ErrorCode);

    public async Task<ResolveOutcome> ResolveAsync(ImportBatch batch, ImportRow row, string action, Guid? customerId, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(row);

        if (batch.Status != ImportBatchStatus.Preview)
        {
            return new ResolveOutcome(false, "batch_not_in_preview");
        }

        switch (action)
        {
            case "skip":
                row.Outcome = ImportRowOutcome.Skipped;
                row.ErrorCode ??= "skipped";
                break;

            case "assign_customer":
                if (customerId is null || !await db.Customers.AnyAsync(c => c.Id == customerId, ct))
                {
                    return new ResolveOutcome(false, "customer_not_found");
                }

                row.CustomerId = customerId;
                break;

            case "create_customer":
                {
                    var values = ReadParsed(row);
                    if (values is null || (values.CustomerName is null && values.CustomerCode is null))
                    {
                        return new ResolveOutcome(false, "no_customer_details");
                    }

                    var name = values.CustomerName ?? values.CustomerCode!;
                    var isArabic = name.Any(ch => ch >= '؀' && ch <= 'ۿ');
                    var customer = new Customer
                    {
                        TenantId = batch.TenantId,
                        Code = values.CustomerCode,
                        NameAr = isArabic ? name : null,
                        NameEn = isArabic ? null : name,
                        DefaultCurrency = values.Currency ?? (await db.Tenants.Select(t => t.BaseCurrency).FirstAsync(ct)),
                        CreatedBy = userId,
                        UpdatedBy = userId,
                    };
                    db.Customers.Add(customer);
                    await db.SaveChangesAsync(ct);
                    await audit.WriteAsync(Event(batch.TenantId, userId, "customer.created", customer.Id, note: "created from import row"), ct);
                    row.CustomerId = customer.Id;
                    break;
                }

            default:
                return new ResolveOutcome(false, "unknown_action");
        }

        if (action != "skip")
        {
            // Re-validate the row now that it has a customer: the duplicate check needs the customer.
            row.Outcome = ImportRowOutcome.Accepted;
            row.ErrorCode = null;
            row.ErrorDetail = null;

            var values = ReadParsed(row);
            if (values is not null)
            {
                var number = values.InvoiceNumber.ToUpperInvariant();
                var exists = await db.Invoices.AnyAsync(i => i.CustomerId == row.CustomerId && i.Status != InvoiceStatus.Void && i.InvoiceNumber.ToUpper() == number, ct);

                var siblings = await db.ImportRows
                    .Where(r => r.BatchId == batch.Id && r.Id != row.Id && r.CustomerId == row.CustomerId && r.Outcome == ImportRowOutcome.Accepted)
                    .ToListAsync(ct);
                var inBatch = siblings.Any(r => ReadParsed(r)?.InvoiceNumber.ToUpperInvariant() == number);

                if (exists || inBatch)
                {
                    row.Outcome = ImportRowOutcome.Duplicate;
                    row.ErrorCode = exists ? ImportErrorCodes.DuplicateInvoiceNumber : ImportErrorCodes.DuplicateInBatch;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        var rows = await db.ImportRows.Where(r => r.BatchId == batch.Id).ToListAsync(ct);
        RefreshCounts(batch, rows);
        batch.RowVersion++;
        await db.SaveChangesAsync(ct);

        return new ResolveOutcome(true, null);
    }

    // ---------------------------------------------------------------------------------------
    // Commit
    // ---------------------------------------------------------------------------------------

    public sealed record CommitResult(bool Ok, string? ErrorCode, int Created, MoneyTotals Totals);

    /// <summary>
    /// Every accepted row becomes an `Open` invoice, or none does. This runs inside the request's
    /// transaction; a failure here rolls the whole request back (TenantScopeMiddleware commits only
    /// on success). Rows in `Rejected`, `Duplicate` and `Skipped` stay exactly as they are — the batch
    /// is the permanent record of what happened to every line of the file.
    /// </summary>
    public async Task<CommitResult> CommitAsync(ImportBatch batch, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var totals = new MoneyTotals();

        // API-08 idempotency: a second commit of a committed batch is a no-op with the same answer.
        if (batch.Status == ImportBatchStatus.Committed)
        {
            foreach (var invoice in await db.Invoices.Where(i => i.ImportBatchId == batch.Id).ToListAsync(ct))
            {
                totals.Add(invoice.Currency, invoice.TotalAmount);
            }

            return new CommitResult(true, null, batch.AcceptedCount, totals);
        }

        if (batch.Status != ImportBatchStatus.Preview)
        {
            return new CommitResult(false, "batch_not_in_preview", 0, totals);
        }

        var rows = await db.ImportRows.Where(r => r.BatchId == batch.Id).OrderBy(r => r.RowNo).ToListAsync(ct);

        // Blocking exceptions must be resolved first (doc 06 §6.4): a Rejected or Duplicate row is a
        // question the user has not answered. Skipped rows are answered.
        if (rows.Any(r => r.Outcome is ImportRowOutcome.Rejected or ImportRowOutcome.Duplicate or ImportRowOutcome.Pending))
        {
            return new CommitResult(false, "unresolved_exceptions", 0, totals);
        }

        var accepted = rows.Where(r => r.Outcome == ImportRowOutcome.Accepted).ToList();
        var customerIds = accepted.Select(r => r.CustomerId!.Value).Distinct().ToList();
        var terms = await db.Customers.Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.PaymentTermsDays, c.DefaultCurrency), ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == batch.TenantId, ct);
        var now = time.GetUtcNow();

        batch.Status = ImportBatchStatus.Committing;

        var invoices = new List<Invoice>(accepted.Count);
        var links = new List<(ImportRow Row, Invoice Invoice)>(accepted.Count);
        foreach (var row in accepted)
        {
            var values = ReadParsed(row) ?? throw new InvalidOperationException($"Row {row.RowNo} is accepted but has no parsed values.");
            var (paymentTermsDays, defaultCurrency) = terms[row.CustomerId!.Value];

            var invoice = new Invoice
            {
                TenantId = batch.TenantId,
                CustomerId = row.CustomerId.Value,
                InvoiceNumber = values.InvoiceNumber,
                Status = InvoiceStatus.Imported,                                   // I1
                IssueDate = values.IssueDate,
                DueDate = values.DueDate ?? values.IssueDate.AddDays(paymentTermsDays),   // FIN-72
                Currency = values.Currency ?? defaultCurrency,
                NetAmount = values.NetAmount!.Value,
                TaxAmount = values.TaxAmount ?? 0m,
                TotalAmount = values.TotalAmount!.Value,
                FxRateToBase = (values.Currency ?? defaultCurrency) == tenant.BaseCurrency ? 1m : values.FxRateToBase!.Value,
                BaseCurrency = tenant.BaseCurrency,
                PoReference = values.PoReference,
                ExternalId = values.ExternalId,
                Notes = values.Notes,
                Source = InvoiceSource.Import,
                ImportBatchId = batch.Id,
                CreatedAt = now,
                CreatedBy = userId,
                UpdatedAt = now,
                UpdatedBy = userId,
            };

            // FIN-10: the one function that writes the cache. No allocations exist yet.
            InvoiceBalance.Recompute(invoice);

            // I2: accept into AR. The guards (FIN-01..06, customer resolved, total ≥ 0, due ≥ issue,
            // no duplicate) were all checked at preview; the database re-checks the structural ones.
            invoice.Status = InvoiceStatus.Open;

            invoices.Add(invoice);
            links.Add((row, invoice));
            totals.Add(invoice.Currency, invoice.TotalAmount);
        }

        db.Invoices.AddRange(invoices);

        try
        {
            // Invoices first, then the rows that point at them: the row→invoice link is a plain
            // column, not an EF relationship, so EF has no dependency to order these by itself.
            await db.SaveChangesAsync(ct);

            foreach (var (row, invoice) in links)
            {
                row.InvoiceId = invoice.Id;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" or "23514" or "23503" } pg)
        {
            // Something changed between preview and commit — an invoice with this number appeared, or a
            // customer vanished. The request rolls back; nothing was imported (doc 06 §6.4 failure state).
            return new CommitResult(false, pg.ConstraintName ?? "commit_failed", 0, new MoneyTotals());
        }

        var events = invoices.Select(i => Event(batch.TenantId, userId, "invoice.status_changed", i.Id, entityType: "invoice", fromState: nameof(InvoiceStatus.Imported), toState: nameof(InvoiceStatus.Open), reasonCode: "import_commit")).ToList();
        events.Add(Event(batch.TenantId, userId, "import.committed", batch.Id, note: $"{invoices.Count} invoices"));
        await audit.WriteManyAsync(events, ct);

        batch.Status = ImportBatchStatus.Committed;
        batch.CommittedAt = now;
        batch.RowVersion++;
        await db.SaveChangesAsync(ct);

        return new CommitResult(true, null, invoices.Count, totals);
    }

    public async Task CancelAsync(ImportBatch batch, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Status is ImportBatchStatus.Committed or ImportBatchStatus.Committing)
        {
            throw new InvalidOperationException("A committed batch cannot be cancelled; void the invoices instead.");
        }

        batch.Status = ImportBatchStatus.Cancelled;
        batch.RowVersion++;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(Event(batch.TenantId, userId, "import.cancelled", batch.Id), ct);
    }

    /// <summary>Per-currency control totals of the rows that would be committed (FIN-04: never mixed).</summary>
    public static MoneyTotals ControlTotals(IEnumerable<ImportRow> rows, string baseCurrency)
    {
        var totals = new MoneyTotals();
        foreach (var row in rows.Where(r => r.Outcome == ImportRowOutcome.Accepted))
        {
            var values = ReadParsed(row);
            if (values?.TotalAmount is { } total)
            {
                totals.Add(values.Currency ?? baseCurrency, total);
            }
        }

        return totals;
    }

    public static ParsedValues? ReadParsed(ImportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Parsed is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(row.Parsed);
        var r = doc.RootElement;

        string? S(string name) => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        decimal? M(string name) => S(name) is { } s ? decimal.Parse(s, CultureInfo.InvariantCulture) : null;
        DateOnly? D(string name) => S(name) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

        return new ParsedValues(
            S("invoiceNumber") ?? string.Empty, S("customerCode"), S("customerName"), D("issueDate") ?? default, D("dueDate"),
            S("currency"), M("netAmount"), M("taxAmount"), M("totalAmount"), M("fxRateToBase"),
            S("poReference"), S("externalId"), S("notes"));
    }

    private static void RefreshCounts(ImportBatch batch, IReadOnlyCollection<ImportRow> rows)
    {
        batch.RowCount = rows.Count;
        batch.AcceptedCount = rows.Count(r => r.Outcome == ImportRowOutcome.Accepted);
        batch.RejectedCount = rows.Count(r => r.Outcome is ImportRowOutcome.Rejected or ImportRowOutcome.Skipped);
        batch.DuplicateCount = rows.Count(r => r.Outcome == ImportRowOutcome.Duplicate);
        batch.WarningCount = rows.Count(r => r.Outcome == ImportRowOutcome.Warning);
    }

    private static AuditEvent Event(Guid tenantId, Guid userId, string eventType, Guid entityId, string entityType = "import_batch", string? fromState = null, string? toState = null, string? reasonCode = null, string? note = null) => new()
    {
        TenantId = tenantId,
        ActorUserId = userId,
        ActorKind = ActorKinds.User,
        EventType = eventType,
        EntityType = entityType,
        EntityId = entityId,
        FromState = fromState,
        ToState = toState,
        ReasonCode = reasonCode,
        Note = note,
    };
}

/// <summary>Doc 10 §2.4, now real: a customer with an <c>Open</c> invoice carrying a balance cannot be deleted.</summary>
public sealed class OpenInvoiceBalanceGuard(TenantDbContext db) : ICustomerBalanceGuard
{
    public Task<bool> HasOpenBalanceAsync(Guid customerId, CancellationToken ct = default) =>
        db.Invoices.AnyAsync(i => i.CustomerId == customerId && i.Status == InvoiceStatus.Open && i.BalanceCache > 0m, ct);
}
