using System.Globalization;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Database;
using FinanceAi.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 3: the import pipeline and read-only invoices. Every handler runs inside the
/// tenant scope; commit relies on the request transaction that <c>TenantScopeMiddleware</c> opened,
/// which is what makes "all accepted rows or none" true without any code here saying so.
/// </summary>
public static class ImportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapImportEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var imports = api.MapGroup("/imports");

        // Bearer authentication, not cookies, so the antiforgery token that ASP.NET expects on a
        // multipart form has nothing to protect against (SEC-63). Disabled explicitly rather than
        // left as a runtime surprise.
        imports.MapPost("/", UploadAsync).Produces<ImportBatchResponse>(201).RequiresPermission(Permissions.InvoicesImport).WithName("UploadImport").DisableAntiforgery();
        imports.MapGet("/", ListBatchesAsync).Produces<ImportBatchListResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("ListImports");
        imports.MapGet("/{id:guid}", GetBatchAsync).Produces<ImportBatchResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("GetImport");
        imports.MapGet("/{id:guid}/rows", ListRowsAsync).Produces<ImportRowListResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("ListImportRows");
        imports.MapPost("/{id:guid}/mapping", ApplyMappingAsync).Produces<ImportBatchResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("ApplyImportMapping");
        imports.MapPost("/{id:guid}/rows/{rowId:guid}/resolve", ResolveRowAsync).Produces<ImportRowResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("ResolveImportRow");
        imports.MapPost("/{id:guid}/commit", CommitAsync).Produces<CommitResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("CommitImport");
        imports.MapPost("/{id:guid}/cancel", CancelAsync).Produces(204).RequiresPermission(Permissions.InvoicesImport).WithName("CancelImport");

        var mappings = api.MapGroup("/import-mappings");
        mappings.MapGet("/", ListMappingsAsync).Produces<ImportMappingListResponse>(200).RequiresPermission(Permissions.InvoicesImport).WithName("ListImportMappings");
        mappings.MapPost("/", CreateMappingAsync).Produces<ImportMappingResponse>(201).RequiresPermission(Permissions.InvoicesImport).WithName("CreateImportMapping");
        mappings.MapDelete("/{id:guid}", DeleteMappingAsync).Produces(204).RequiresPermission(Permissions.InvoicesImport).WithName("DeleteImportMapping");

        var invoices = api.MapGroup("/invoices");
        invoices.MapGet("/", ListInvoicesAsync).Produces<InvoiceListResponse>(200).RequiresPermission(Permissions.InvoicesRead).WithName("ListInvoices");
        invoices.MapGet("/{id:guid}", GetInvoiceAsync).Produces<InvoiceDetailResponse>(200).RequiresPermission(Permissions.InvoicesRead).WithName("GetInvoice");

        return api;
    }

    // ---------------------------------------------------------------------------------------
    // Batches
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> UploadAsync(
        IFormFile? file, HttpContext context, CurrentUser currentUser, TenantDbContext db, ImportService imports, CancellationToken ct, bool force = false)
    {
        if (file is null || file.Length == 0)
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("file", "required", "errors.file.required")]);
        }

        if (file.Length > ImportLimits.MaxFileBytes)
        {
            return ApiProblems.Create(context, StatusCodes.Status413PayloadTooLarge, "file_too_large",
                "The upload exceeds 10 MB.", "errors.import.file_too_large");
        }

        byte[] content;
        await using (var stream = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        var result = await imports.UploadAsync(file.FileName, content, force, currentUser.TenantId, currentUser.UserId, ct);

        if (result.Batch is null)
        {
            return result.DuplicateFile
                ? ApiProblems.Create(context, StatusCodes.Status409Conflict, "duplicate_file",
                    "This file has already been imported. Re-upload with ?force=true to import it again.", "errors.import.duplicate_file")
                : ApiProblems.Create(context, StatusCodes.Status400BadRequest, result.ErrorCode!,
                    result.ErrorDetail ?? "The file could not be read.", $"errors.import.{result.ErrorCode}");
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);
        return TypedResults.Created($"/api/v1/imports/{result.Batch.Id}", ToResponse(result.Batch, [], tenant.BaseCurrency));
    }

    private static async Task<IResult> ListBatchesAsync(TenantDbContext db, CurrentUser currentUser, CancellationToken ct, int limit = 25, Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 100);
        var query = db.ImportBatches.AsQueryable();
        var totalCount = await query.CountAsync(ct);

        if (cursor is not null)
        {
            query = query.Where(b => b.Id.CompareTo(cursor.Value) < 0);
        }

        // Newest first: UUIDv7 descends with time.
        var page = await query.OrderByDescending(b => b.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);

        var items = (hasMore ? page[..pageSize] : page).Select(b => ToResponse(b, [], tenant.BaseCurrency)).ToList();
        return TypedResults.Ok(new ImportBatchListResponse(items, hasMore ? items[^1].Id.ToString() : null, totalCount));
    }

    private static async Task<IResult> GetBatchAsync(Guid id, HttpContext context, TenantDbContext db, CurrentUser currentUser, CancellationToken ct)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var rows = await db.ImportRows.Where(r => r.BatchId == id).ToListAsync(ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);
        return TypedResults.Ok(ToResponse(batch, rows, tenant.BaseCurrency));
    }

    private static async Task<IResult> ListRowsAsync(
        Guid id, HttpContext context, TenantDbContext db, CancellationToken ct, string? outcome = null, int limit = 100, int? cursor = null)
    {
        if (!await db.ImportBatches.AnyAsync(b => b.Id == id, ct))
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var pageSize = Math.Clamp(limit, 1, 500);
        var query = db.ImportRows.Where(r => r.BatchId == id);

        if (outcome is not null && Enum.TryParse<ImportRowOutcome>(outcome, ignoreCase: true, out var filter))
        {
            query = query.Where(r => r.Outcome == filter);
        }

        var totalCount = await query.CountAsync(ct);
        if (cursor is not null)
        {
            query = query.Where(r => r.RowNo > cursor.Value);
        }

        var page = await query.OrderBy(r => r.RowNo).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page[..pageSize] : page).Select(ToResponse).ToList();

        return TypedResults.Ok(new ImportRowListResponse(items, hasMore ? items[^1].RowNo.ToString(CultureInfo.InvariantCulture) : null, totalCount));
    }

    private static async Task<IResult> ApplyMappingAsync(
        Guid id, ApplyMappingRequest request, HttpContext context, TenantDbContext db, CurrentUser currentUser, ImportService imports, CancellationToken ct)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        if (batch.Status is ImportBatchStatus.Committed or ImportBatchStatus.Committing or ImportBatchStatus.Cancelled)
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "invalid_transition",
                $"A batch in status {batch.Status} cannot be re-mapped.", "errors.import.invalid_transition");
        }

        // A saved mapping supplies the defaults; explicit fields override it.
        ImportMapping? saved = null;
        if (request.MappingId is { } mappingId)
        {
            saved = await db.ImportMappings.FirstOrDefaultAsync(m => m.Id == mappingId, ct);
            if (saved is null)
            {
                return ApiProblems.NotFoundProblem(context);
            }
        }

        var columnMap = request.ColumnMap
            ?? (saved is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(saved.ColumnMap, Json));

        if (columnMap is null || columnMap.Count == 0)
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("columnMap", "required", "errors.columnMap.required")]);
        }

        var spec = new ImportService.MappingSpec(
            columnMap,
            request.DateFormat ?? saved?.DateFormat ?? "yyyy-MM-dd",
            (request.DecimalSeparator ?? saved?.DecimalSeparator.ToString() ?? ".").FirstOrDefault('.'));

        var headers = JsonSerializer.Deserialize<List<string>>(batch.Headers, Json) ?? [];
        var errors = ImportService.ValidateMapping(spec, headers);
        if (errors.Count > 0)
        {
            return ApiProblems.ValidationProblem(context, errors.Select(e => new ApiProblems.FieldError(e.Field, e.Code, $"errors.mapping.{e.Code}")).ToList());
        }

        if (!string.IsNullOrWhiteSpace(request.SaveAs))
        {
            var name = request.SaveAs.Trim();
            var existing = await db.ImportMappings.FirstOrDefaultAsync(m => m.Name == name, ct);
            if (existing is null)
            {
                existing = new ImportMapping { TenantId = currentUser.TenantId, Name = name, ColumnMap = "{}" };
                db.ImportMappings.Add(existing);
            }

            existing.ColumnMap = JsonSerializer.Serialize(spec.ColumnMap, Json);
            existing.DateFormat = spec.DateFormat;
            existing.DecimalSeparator = spec.DecimalSeparator;
            await db.SaveChangesAsync(ct);
            saved = existing;
        }

        await imports.ApplyMappingAsync(batch, spec, saved?.Id, currentUser.UserId, ct);

        var rows = await db.ImportRows.Where(r => r.BatchId == id).ToListAsync(ct);
        var tenant = await db.Tenants.FirstAsync(t => t.Id == currentUser.TenantId, ct);
        return TypedResults.Ok(ToResponse(batch, rows, tenant.BaseCurrency));
    }

    private static async Task<IResult> ResolveRowAsync(
        Guid id, Guid rowId, ResolveRowRequest request, HttpContext context, TenantDbContext db, CurrentUser currentUser, ImportService imports, CancellationToken ct)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        var row = batch is null ? null : await db.ImportRows.FirstOrDefaultAsync(r => r.Id == rowId && r.BatchId == id, ct);
        if (batch is null || row is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("action", "required", "errors.action.required")]);
        }

        var outcome = await imports.ResolveAsync(batch, row, request.Action, request.CustomerId, currentUser.UserId, ct);
        if (!outcome.Ok)
        {
            return outcome.ErrorCode == "customer_not_found"
                ? ApiProblems.NotFoundProblem(context)
                : ApiProblems.Create(context, StatusCodes.Status422UnprocessableEntity, outcome.ErrorCode!,
                    "The row could not be resolved this way.", $"errors.import.{outcome.ErrorCode}");
        }

        return TypedResults.Ok(ToResponse(row));
    }

    private static async Task<IResult> CommitAsync(Guid id, HttpContext context, TenantDbContext db, CurrentUser currentUser, ImportService imports, CancellationToken ct)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var result = await imports.CommitAsync(batch, currentUser.UserId, ct);

        if (!result.Ok)
        {
            // Any non-2xx makes TenantScopeMiddleware roll the request back: nothing was imported.
            var status = result.ErrorCode is "unresolved_exceptions" or "batch_not_in_preview"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status409Conflict;

            return ApiProblems.Create(context, status, result.ErrorCode!,
                "The batch could not be committed; nothing was imported.", $"errors.import.{result.ErrorCode}");
        }

        return TypedResults.Ok(new CommitResponse(batch.Id, batch.Status.ToString(), result.Created, ToDto(result.Totals)));
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext context, TenantDbContext db, CurrentUser currentUser, ImportService imports, CancellationToken ct)
    {
        var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        if (batch.Status is ImportBatchStatus.Committed or ImportBatchStatus.Committing)
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "invalid_transition",
                "A committed batch cannot be cancelled.", "errors.import.invalid_transition");
        }

        await imports.CancelAsync(batch, currentUser.UserId, ct);
        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------------------------------
    // Saved mappings
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> ListMappingsAsync(TenantDbContext db, CancellationToken ct)
    {
        var mappings = await db.ImportMappings.OrderBy(m => m.Name).ToListAsync(ct);
        return TypedResults.Ok(new ImportMappingListResponse(mappings.Select(ToResponse).ToList()));
    }

    private static async Task<IResult> CreateMappingAsync(ImportMappingRequest request, HttpContext context, TenantDbContext db, CurrentUser currentUser, CancellationToken ct)
    {
        var validation = new Validation().Require("name", request.Name).MaxLength("name", request.Name, 100);
        if (request.ColumnMap is null || request.ColumnMap.Count == 0)
        {
            validation.Require("columnMap", null);
        }

        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var spec = new ImportService.MappingSpec(request.ColumnMap!, request.DateFormat ?? "yyyy-MM-dd", (request.DecimalSeparator ?? ".").FirstOrDefault('.'));

        // Validate targets and format, but not headers: a saved mapping is not tied to one file.
        var errors = ImportService.ValidateMapping(spec, [.. spec.ColumnMap.Keys]);
        if (errors.Count > 0)
        {
            return ApiProblems.ValidationProblem(context, errors.Select(e => new ApiProblems.FieldError(e.Field, e.Code, $"errors.mapping.{e.Code}")).ToList());
        }

        if (await db.ImportMappings.AnyAsync(m => m.Name == request.Name!.Trim(), ct))
        {
            return ApiProblems.Create(context, StatusCodes.Status409Conflict, "duplicate", "A mapping with this name exists.", "errors.mapping.duplicate_name");
        }

        var mapping = new ImportMapping
        {
            TenantId = currentUser.TenantId,
            Name = request.Name!.Trim(),
            ColumnMap = JsonSerializer.Serialize(spec.ColumnMap, Json),
            DateFormat = spec.DateFormat,
            DecimalSeparator = spec.DecimalSeparator,
        };
        db.ImportMappings.Add(mapping);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/import-mappings/{mapping.Id}", ToResponse(mapping));
    }

    private static async Task<IResult> DeleteMappingAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var mapping = await db.ImportMappings.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mapping is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        // Batches keep their own copy of the applied map, so removing the template loses nothing.
        await db.ImportBatches.Where(b => b.MappingId == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.MappingId, (Guid?)null), ct);
        db.ImportMappings.Remove(mapping);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------------------------------
    // Invoices (read-only in 3a)
    // ---------------------------------------------------------------------------------------

    private static async Task<IResult> ListInvoicesAsync(
        TenantDbContext db, CancellationToken ct, Guid? customerId = null, string? status = null, string? currency = null,
        string? q = null, DateOnly? dueBefore = null, DateOnly? dueAfter = null, int limit = 50, Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);
        var query = db.Invoices.AsQueryable();

        if (customerId is not null)
        {
            query = query.Where(i => i.CustomerId == customerId);
        }

        if (status is not null && Enum.TryParse<InvoiceStatus>(status, ignoreCase: true, out var s))
        {
            query = query.Where(i => i.Status == s);
        }

        if (currency is not null)
        {
            query = query.Where(i => i.Currency == currency);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToUpperInvariant();
            query = query.Where(i => i.InvoiceNumber.ToUpper().Contains(needle));
        }

        if (dueBefore is not null)
        {
            query = query.Where(i => i.DueDate <= dueBefore);
        }

        if (dueAfter is not null)
        {
            query = query.Where(i => i.DueDate >= dueAfter);
        }

        var totalCount = await query.CountAsync(ct);
        if (cursor is not null)
        {
            query = query.Where(i => i.Id.CompareTo(cursor.Value) > 0);
        }

        var page = await query.OrderBy(i => i.Id).Take(pageSize + 1).ToListAsync(ct);
        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page[..pageSize] : page).Select(ToResponse).ToList();

        return TypedResults.Ok(new InvoiceListResponse(items, hasMore ? items[^1].Id.ToString() : null, totalCount));
    }

    private static async Task<IResult> GetInvoiceAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        return invoice is null ? ApiProblems.NotFoundProblem(context) : TypedResults.Ok(await LedgerEndpoints.InvoiceDetailAsync(invoice, db, ct));
    }

    // ---------------------------------------------------------------------------------------
    // Mapping
    // ---------------------------------------------------------------------------------------

    private static ImportBatchResponse ToResponse(ImportBatch b, IReadOnlyCollection<ImportRow> rows, string baseCurrency) => new(
        b.Id, b.FileName, b.FileKind.ToString().ToLowerInvariant(), b.FileSize, b.Status.ToString(),
        JsonSerializer.Deserialize<List<string>>(b.Headers, Json) ?? [],
        b.ColumnMap is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(b.ColumnMap, Json),
        b.DateFormat, b.DecimalSeparator.ToString(), b.MappingId,
        b.RowCount, b.AcceptedCount, b.RejectedCount, b.DuplicateCount, b.WarningCount, b.Forced,
        ToDto(ImportService.ControlTotals(rows, baseCurrency)),
        b.UploadedAt, b.CommittedAt, b.RowVersion.ToString(CultureInfo.InvariantCulture));

    private static ImportRowResponse ToResponse(ImportRow r) => new(
        r.Id, r.RowNo,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.Raw, Json) ?? [],
        r.Parsed is null ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(r.Parsed, Json),
        r.Outcome.ToString(), r.ErrorCode, r.ErrorDetail, r.CustomerId, r.InvoiceId);

    private static ImportMappingResponse ToResponse(ImportMapping m) => new(
        m.Id, m.Name, JsonSerializer.Deserialize<Dictionary<string, string>>(m.ColumnMap, Json) ?? [], m.DateFormat, m.DecimalSeparator.ToString());

    public static InvoiceResponse ToResponse(Invoice i) => new(
        i.Id, i.CustomerId, i.InvoiceNumber, i.Status.ToString(),
        i.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), i.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        i.Currency, MoneyDto.From(i.NetAmount, i.Currency), MoneyDto.From(i.TaxAmount, i.Currency), MoneyDto.From(i.TotalAmount, i.Currency),
        MoneyDto.From(i.BalanceCache, i.Currency), i.FxRateToBase.ToString("0.########", CultureInfo.InvariantCulture), i.BaseCurrency,
        i.PoReference, i.ExternalId, i.Notes, i.Source.ToString().ToLowerInvariant(), i.ImportBatchId, i.CreatedAt,
        i.RowVersion.ToString(CultureInfo.InvariantCulture));

    private static List<ControlTotalDto> ToDto(MoneyTotals totals) =>
        totals.PerCurrency.Select(t => new ControlTotalDto(t.Currency, MoneyDto.From(t.Amount, t.Currency), t.Count)).ToList();
}
