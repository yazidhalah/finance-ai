using FinanceAi.Domain.Abstractions;

namespace FinanceAi.Domain.Entities;

public enum ImportBatchStatus { Uploaded, Parsing, Preview, Committing, Committed, Failed, Cancelled }

public enum ImportRowOutcome { Pending, Accepted, Rejected, Duplicate, Warning, Skipped }

public enum ImportFileKind { Csv, Xlsx }

/// <summary>
/// The target fields a column can be mapped to. The import never invents a field: a column mapped
/// to anything else is an error, and unmapped columns are kept in <c>raw</c> but ignored.
/// </summary>
public static class ImportFields
{
    public const string InvoiceNumber = "invoice_number";
    public const string CustomerCode = "customer_code";
    public const string CustomerName = "customer_name";
    public const string IssueDate = "issue_date";
    public const string DueDate = "due_date";
    public const string Currency = "currency";
    public const string NetAmount = "net_amount";
    public const string TaxAmount = "tax_amount";
    public const string TotalAmount = "total_amount";
    public const string PoReference = "po_reference";
    public const string ExternalId = "external_id";
    public const string FxRateToBase = "fx_rate_to_base";
    public const string Notes = "notes";

    public static readonly IReadOnlyList<string> All =
    [
        InvoiceNumber, CustomerCode, CustomerName, IssueDate, DueDate, Currency,
        NetAmount, TaxAmount, TotalAmount, PoReference, ExternalId, FxRateToBase, Notes,
    ];
}

/// <summary>The machine-readable reasons a row is not accepted. Rendered by the client from a messageKey.</summary>
public static class ImportErrorCodes
{
    public const string MissingInvoiceNumber = "missing_invoice_number";
    public const string MissingCustomer = "missing_customer";
    public const string CustomerNotFound = "customer_not_found";
    public const string MissingIssueDate = "missing_issue_date";
    public const string InvalidDate = "invalid_date";
    public const string DueBeforeIssue = "due_before_issue";
    public const string MissingAmount = "missing_amount";
    public const string InvalidAmount = "invalid_amount";
    public const string NegativeAmount = "negative_amount";
    public const string TooManyDecimals = "too_many_decimals";
    public const string TotalsDoNotReconcile = "totals_do_not_reconcile";
    public const string InvalidCurrency = "invalid_currency";
    public const string MissingFxRate = "missing_fx_rate";
    public const string InvalidFxRate = "invalid_fx_rate";
    public const string DuplicateInvoiceNumber = "duplicate_invoice_number";
    public const string DuplicateInBatch = "duplicate_in_batch";
    public const string UnmappedRequiredField = "unmapped_required_field";
}

public sealed class ImportMapping : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>JSON: {"Invoice No": "invoice_number", ...}. Header → target field.</summary>
    public required string ColumnMap { get; set; }

    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public char DecimalSeparator { get; set; } = '.';
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ImportBatch : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public long FileSize { get; set; }
    public ImportFileKind FileKind { get; set; }
    public required byte[] FileContent { get; set; }
    public Guid? MappingId { get; set; }
    public string? ColumnMap { get; set; }
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public char DecimalSeparator { get; set; } = '.';
    public required string Headers { get; set; }
    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Uploaded;
    public int RowCount { get; set; }
    public int AcceptedCount { get; set; }
    public int RejectedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int WarningCount { get; set; }
    public bool Forced { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CommittedAt { get; set; }
    public long RowVersion { get; set; } = 1;
}

public sealed class ImportRow : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid BatchId { get; set; }
    public int RowNo { get; set; }

    /// <summary>The row exactly as it arrived, as JSON. Data, never instruction (SEC-40, SEC-48).</summary>
    public required string Raw { get; set; }

    public string? Parsed { get; set; }
    public ImportRowOutcome Outcome { get; set; } = ImportRowOutcome.Pending;
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? InvoiceId { get; set; }
}
