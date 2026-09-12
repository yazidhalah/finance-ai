using Microsoft.AspNetCore.Http.HttpResults;
using System.Globalization;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Api.Contracts;
using FinanceAi.Api.Http;
using FinanceAi.Domain.Authorization;
using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Audit;
using FinanceAi.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FinanceAi.Api.Endpoints;

/// <summary>
/// Doc 05 slice 2. Every handler runs inside the tenant scope opened by <c>TenantScopeMiddleware</c>;
/// none of them mentions a tenant id. The two places that reach past the EF query filter — search
/// and duplicate detection — are raw SQL, and each carries its own explicit tenant predicate on top
/// of the RLS policy that governs every statement on these tables.
/// </summary>
public static class CustomerEndpoints
{
    /// <summary>Trigram similarity floor for "did you mean" search hits and for duplicate candidates.</summary>
    private const double SearchSimilarity = 0.3;

    private const double DuplicateSimilarity = 0.6;

    private static readonly string[] Languages = ["ar", "en"];

    public static RouteGroupBuilder MapCustomerEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var customers = api.MapGroup("/customers");

        customers.MapGet("/", ListAsync).RequiresPermission(Permissions.CustomersRead).WithName("ListCustomers");
        customers.MapPost("/", CreateAsync).RequiresPermission(Permissions.CustomersWrite).WithName("CreateCustomer");
        // Slice 23 (DM-21): preview, then confirm with the token the preview returned. Irreversible.
        customers.MapPost("/{id:guid}/merge", MergeAsync).RequiresPermission(Permissions.CustomersMerge).WithName("MergeCustomer");
        customers.MapGet("/duplicates", DuplicatesAsync).RequiresPermission(Permissions.CustomersRead).WithName("CustomerDuplicates");
        customers.MapGet("/{id:guid}", GetAsync).RequiresPermission(Permissions.CustomersRead).WithName("GetCustomer");
        customers.MapPatch("/{id:guid}", UpdateAsync).RequiresPermission(Permissions.CustomersWrite).WithName("UpdateCustomer");
        customers.MapDelete("/{id:guid}", DeleteAsync).RequiresPermission(Permissions.CustomersWrite).WithName("DeleteCustomer");

        customers.MapGet("/{id:guid}/contacts", ListContactsAsync).RequiresPermission(Permissions.CustomersRead).WithName("ListContacts");
        customers.MapPost("/{id:guid}/contacts", CreateContactAsync).RequiresPermission(Permissions.CustomersWrite).WithName("CreateContact");
        customers.MapPatch("/{id:guid}/contacts/{contactId:guid}", UpdateContactAsync).RequiresPermission(Permissions.CustomersWrite).WithName("UpdateContact");
        customers.MapDelete("/{id:guid}/contacts/{contactId:guid}", DeleteContactAsync).RequiresPermission(Permissions.CustomersWrite).WithName("DeleteContact");
        customers.MapPost("/{id:guid}/contacts/{contactId:guid}/erase", EraseContactAsync).RequiresPermission(Permissions.CustomersWrite).RequiresReauth().WithName("EraseContact");   // SEC-93, slice 31

        return api;
    }

    // ---------------------------------------------------------------------------------------
    // Customers
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Ok<CustomerListResponse>, ProblemHttpResult>> ListAsync(
        TenantDbContext db,
        CurrentUser currentUser,
        CancellationToken ct,
        string? q = null,
        string? riskFlag = null,
        string? status = null,
        string? currency = null,
        int limit = 50,
        Guid? cursor = null)
    {
        var pageSize = Math.Clamp(limit, 1, 200);

        // DM-20: the search term goes through the same app_normalize_arabic as the stored column,
        // so "شركه الامل" and "شركة الأمل" meet in the middle. Substring catches near-exact input,
        // trigram similarity catches typos. The EF tenant and soft-delete filters compose on top of
        // FromSql, and the explicit tenant predicate keeps layer 1 present even in the raw text.
        IQueryable<Customer> query = string.IsNullOrWhiteSpace(q)
            ? db.Customers
            : db.Customers.FromSql(
                $"""
                 SELECT * FROM customers
                 WHERE tenant_id = {currentUser.TenantId}
                   AND (normalized_name ILIKE '%' || app_normalize_arabic({EscapeLike(q)}) || '%' ESCAPE '\'
                        OR similarity(normalized_name, app_normalize_arabic({q})) > {SearchSimilarity})
                 """);

        if (riskFlag is not null && Enum.TryParse<RiskFlag>(riskFlag, ignoreCase: true, out var flag))
        {
            query = query.Where(c => c.RiskFlag == flag);
        }

        if (status is not null && Enum.TryParse<CustomerStatus>(status, ignoreCase: true, out var customerStatus))
        {
            query = query.Where(c => c.Status == customerStatus);
        }

        if (currency is not null)
        {
            query = query.Where(c => c.DefaultCurrency == currency);
        }

        var totalCount = await query.CountAsync(ct);

        if (cursor is not null)
        {
            query = query.Where(c => c.Id.CompareTo(cursor.Value) > 0);
        }

        // UUIDv7 ids are creation-ordered, so ordering by id is stable under concurrent inserts and
        // needs no composite cursor (D-4).
        var page = await query.OrderBy(c => c.Id).Take(pageSize + 1).ToListAsync(ct);

        var hasMore = page.Count > pageSize;
        var items = (hasMore ? page[..pageSize] : page).Select(ToResponse).ToList();

        return TypedResults.Ok(new CustomerListResponse(
            items,
            hasMore ? items[^1].Id.ToString() : null,
            totalCount));
    }

    private static async Task<Results<Created<CustomerResponse>, ProblemHttpResult>> CreateAsync(
        CustomerRequest request,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        CancellationToken ct)
    {
        var validation = Validate(request, isCreate: true);
        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var now = DateTimeOffset.UtcNow;
        var customer = new Customer
        {
            TenantId = currentUser.TenantId,
            CreatedAt = now,
            CreatedBy = currentUser.UserId,
            UpdatedAt = now,
            UpdatedBy = currentUser.UserId,
        };

        Apply(customer, request);

        if (await CodeIsTakenAsync(db, customer.Code, exceptId: null, ct))
        {
            return ApiProblems.Create(
                context, StatusCodes.Status409Conflict, "duplicate",
                "A live customer with this code already exists in this organization.", "errors.customers.duplicate_code",
                [new ApiProblems.FieldError("code", "duplicate", "errors.code.duplicate")]);
        }

        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditFor(currentUser, context, "customer.created", customer.Id, Snapshot(customer)), ct);

        // The generated normalized_name is read back so the response reflects what was stored.
        await db.Entry(customer).ReloadAsync(ct);

        return TypedResults.Created($"/api/v1/customers/{customer.Id}", ToResponse(customer));
    }

    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> GetAsync(Guid id, HttpContext context, TenantDbContext db, FinanceAi.Infrastructure.Ledger.LedgerService ledger, CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);

        // Another tenant's customer, a deleted customer and a random id are the same answer (SEC-13).
        if (customer is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        // FIN-15 / FIN-42: open balance, unapplied cash and unapplied credit — per currency, three
        // separate figures. Nothing here nets them and nothing sums across currencies (FIN-04).
        var position = await ledger.CustomerPositionAsync(id, ct);

        return TypedResults.Ok(ToResponse(customer) with
        {
            Balances = position.Select(b => new CustomerPositionDto(
                b.Currency, MoneyDto.From(b.OpenBalance, b.Currency), b.OpenInvoiceCount,
                MoneyDto.From(b.UnappliedCash, b.Currency), MoneyDto.From(b.UnappliedCredit, b.Currency))).ToList(),
        });
    }

    private static async Task<Results<Ok<CustomerResponse>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        CustomerRequest request,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var validation = Validate(request, isCreate: false);
        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        // API-09: If-Match is required on a mutation, not merely honoured when present.
        var expected = IfMatch(context);
        if (expected is null)
        {
            return ApiProblems.Create(
                context, StatusCodes.Status428PreconditionRequired, "precondition_required",
                "PATCH requires an If-Match header carrying the rowVersion that was read.", "errors.precondition_required");
        }

        if (expected != customer.RowVersion)
        {
            return ApiProblems.Create(
                context, StatusCodes.Status409Conflict, ApiProblems.ConcurrencyConflict,
                "The customer was modified since it was read.", "errors.concurrency_conflict");
        }

        var before = Snapshot(customer);
        Apply(customer, request);

        if (!customer.HasAName)
        {
            return ApiProblems.ValidationProblem(
                context, [new ApiProblems.FieldError("nameAr", "required", "errors.customers.name_required")]);
        }

        if (await CodeIsTakenAsync(db, customer.Code, exceptId: customer.Id, ct))
        {
            return ApiProblems.Create(
                context, StatusCodes.Status409Conflict, "duplicate",
                "A live customer with this code already exists in this organization.", "errors.customers.duplicate_code",
                [new ApiProblems.FieldError("code", "duplicate", "errors.code.duplicate")]);
        }

        var after = Snapshot(customer);
        var changes = Diff(before, after);

        if (changes.Count > 0)
        {
            customer.RowVersion++;
            customer.UpdatedAt = DateTimeOffset.UtcNow;
            customer.UpdatedBy = currentUser.UserId;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(AuditFor(currentUser, context, "customer.updated", customer.Id, changes), ct);
            await db.Entry(customer).ReloadAsync(ct);
        }

        return TypedResults.Ok(ToResponse(customer));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        Guid id,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        ICustomerBalanceGuard balanceGuard,
        CancellationToken ct)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        // Doc 10 §2.4: money owed does not disappear because the debtor's record did.
        if (await balanceGuard.HasOpenBalanceAsync(customer.Id, ct))
        {
            return ApiProblems.Create(
                context, StatusCodes.Status422UnprocessableEntity, "has_open_balance",
                "A customer with an open balance cannot be deleted.", "errors.customers.has_open_balance");
        }

        customer.DeletedAt = DateTimeOffset.UtcNow;
        customer.UpdatedAt = customer.DeletedAt.Value;
        customer.UpdatedBy = currentUser.UserId;
        customer.RowVersion++;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditFor(currentUser, context, "customer.deleted", customer.Id, null), ct);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// DM-20 duplicate detection: pairs of live customers whose normalized names are near-identical.
    /// A pairwise self-join is not expressible through the EF filter, so this is raw SQL with an
    /// explicit tenant predicate (layer 1 restated by hand) under the RLS policy (layer 2).
    /// </summary>
    private static async Task<Results<Ok<DuplicateListResponse>, ProblemHttpResult>> DuplicatesAsync(
        TenantDbContext db, CurrentUser currentUser, CancellationToken ct, int limit = 50)
    {
        var pageSize = Math.Clamp(limit, 1, 200);

        var rows = await db.Database.SqlQuery<DuplicateRow>(
            $"""
             SELECT a.id AS "CustomerId", b.id AS "OtherCustomerId",
                    coalesce(a.name_ar, a.name_en) AS "NameA",
                    coalesce(b.name_ar, b.name_en) AS "NameB",
                    similarity(a.normalized_name, b.normalized_name)::float8 AS "Similarity"
             FROM customers a
             JOIN customers b ON b.tenant_id = a.tenant_id AND b.id > a.id
             WHERE a.tenant_id = {currentUser.TenantId}
               AND a.deleted_at IS NULL AND b.deleted_at IS NULL
               AND similarity(a.normalized_name, b.normalized_name) > {DuplicateSimilarity}
             ORDER BY "Similarity" DESC, a.id, b.id
             LIMIT {pageSize}
             """).ToListAsync(ct);

        return TypedResults.Ok(new DuplicateListResponse(
            rows.Select(r => new DuplicateCandidate(r.CustomerId, r.OtherCustomerId, r.NameA, r.NameB, Math.Round(r.Similarity, 3))).ToList()));
    }

    private sealed record DuplicateRow(Guid CustomerId, Guid OtherCustomerId, string? NameA, string? NameB, double Similarity);

    // ---------------------------------------------------------------------------------------
    // Contacts
    // ---------------------------------------------------------------------------------------

    private static async Task<Results<Ok<ContactListResponse>, ProblemHttpResult>> ListContactsAsync(Guid id, HttpContext context, TenantDbContext db, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct))
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var contacts = await db.CustomerContacts
            .Where(c => c.CustomerId == id)
            .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name)
            .ToListAsync(ct);

        return TypedResults.Ok(new ContactListResponse(contacts.Select(ToResponse).ToList()));
    }

    private static async Task<Results<Created<ContactResponse>, ProblemHttpResult>> CreateContactAsync(
        Guid id,
        ContactRequest request,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        CancellationToken ct)
    {
        // Existence before validation, deliberately: a foreign or missing id answers 404 whatever the
        // body says, so the shape of a request can never be used to tell the two apart (SEC-13).
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct))
        {
            return ApiProblems.NotFoundProblem(context);
        }

        var validation = ValidateContact(request, isCreate: true);
        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        var contact = new CustomerContact { TenantId = currentUser.TenantId, CustomerId = id, Name = request.Name!.Trim() };
        ApplyContact(contact, request);

        // The first contact is primary whether or not the caller said so: a customer with contacts
        // but no primary would have nobody to write to.
        if (!await db.CustomerContacts.AnyAsync(c => c.CustomerId == id, ct))
        {
            contact.IsPrimary = true;
        }

        if (contact.IsPrimary)
        {
            await DemoteCurrentPrimaryAsync(db, id, ct);
        }

        db.CustomerContacts.Add(contact);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditFor(currentUser, context, "customer.contact_added", id, new Dictionary<string, object?> { ["contactId"] = contact.Id, ["name"] = contact.Name }), ct);

        return TypedResults.Created($"/api/v1/customers/{id}/contacts/{contact.Id}", ToResponse(contact));
    }

    private static async Task<Results<Ok<ContactResponse>, ProblemHttpResult>> UpdateContactAsync(
        Guid id,
        Guid contactId,
        ContactRequest request,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        CancellationToken ct)
    {
        var contact = await db.CustomerContacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CustomerId == id, ct);
        if (contact is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        if (contact.ErasedAt is not null)
        {
            return ApiProblems.BusinessRuleProblem(context, "contact_erased", null, null);   // SEC-93: an erased contact is read-only
        }

        var validation = ValidateContact(request, isCreate: false);
        if (validation.HasErrors)
        {
            return ApiProblems.ValidationProblem(context, validation.Errors);
        }

        if (IfMatch(context) is { } expected && expected != contact.RowVersion)
        {
            return ApiProblems.Create(
                context, StatusCodes.Status409Conflict, ApiProblems.ConcurrencyConflict,
                "The contact was modified since it was read.", "errors.concurrency_conflict");
        }

        var wasPrimary = contact.IsPrimary;
        var emailBefore = contact.Email;
        ApplyContact(contact, request);
        if (contact.BouncedAt is not null && !string.Equals(emailBefore, contact.Email, StringComparison.OrdinalIgnoreCase))
        {
            contact.BouncedAt = null;   // slice 25: a corrected address gets a fresh chance
            contact.BounceReason = null;
        }

        // Demoting the only primary is refused for the same reason the first contact is promoted.
        if (wasPrimary && !contact.IsPrimary)
        {
            return ApiProblems.ValidationProblem(
                context, [new ApiProblems.FieldError("isPrimary", "last_primary", "errors.contacts.last_primary")]);
        }

        if (contact.IsPrimary && !wasPrimary)
        {
            await DemoteCurrentPrimaryAsync(db, id, ct);
        }

        contact.RowVersion++;
        contact.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditFor(currentUser, context, "customer.contact_updated", id, new Dictionary<string, object?> { ["contactId"] = contact.Id }), ct);

        return TypedResults.Ok(ToResponse(contact));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteContactAsync(
        Guid id,
        Guid contactId,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        CancellationToken ct)
    {
        var contact = await db.CustomerContacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CustomerId == id, ct);
        if (contact is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        db.CustomerContacts.Remove(contact);
        await db.SaveChangesAsync(ct);

        // If the primary was removed, the oldest remaining contact takes over, so there is always
        // someone to write to while any contact exists.
        if (contact.IsPrimary)
        {
            var successor = await db.CustomerContacts
                .Where(c => c.CustomerId == id)
                .OrderBy(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (successor is not null)
            {
                successor.IsPrimary = true;
                await db.SaveChangesAsync(ct);
            }
        }

        await audit.WriteAsync(AuditFor(currentUser, context, "customer.contact_removed", id, new Dictionary<string, object?> { ["contactId"] = contactId }), ct);

        return TypedResults.NoContent();
    }

    /// <summary>Runs inside the request transaction, so the partial unique index never sees two primaries.</summary>
    private static Task<int> DemoteCurrentPrimaryAsync(TenantDbContext db, Guid customerId, CancellationToken ct) =>
        db.CustomerContacts
            .Where(c => c.CustomerId == customerId && c.IsPrimary)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsPrimary, false), ct);

    // ---------------------------------------------------------------------------------------
    // Validation and mapping
    // ---------------------------------------------------------------------------------------

    private static Validation Validate(CustomerRequest request, bool isCreate)
    {
        var validation = new Validation()
            .MaxLength("code", request.Code, 50)
            .MaxLength("nameAr", request.NameAr, 200)
            .MaxLength("nameEn", request.NameEn, 200)
            .MaxLength("legalName", request.LegalName, 200)
            .MaxLength("taxRegistrationNo", request.TaxRegistrationNo, 50)
            .MaxLength("notes", request.Notes, 4000)
            .Currency("defaultCurrency", request.DefaultCurrency);

        if (isCreate && string.IsNullOrWhiteSpace(request.NameAr) && string.IsNullOrWhiteSpace(request.NameEn))
        {
            validation.Require("nameAr", null, "required");
        }

        if (request.PreferredLanguage is not null && !Languages.Contains(request.PreferredLanguage))
        {
            validation.Require("preferredLanguage", null, "unsupported_language");
        }

        if (request.PaymentTermsDays is < 0 or > 365)
        {
            validation.Require("paymentTermsDays", null, "out_of_range");
        }

        if (request.RiskFlag is not null && !Enum.TryParse<RiskFlag>(request.RiskFlag, out _))
        {
            validation.Require("riskFlag", null, "invalid");
        }

        if (request.Status is not null && !Enum.TryParse<CustomerStatus>(request.Status, out _))
        {
            validation.Require("status", null, "invalid");
        }

        if (request.CreditLimit is { } limit)
        {
            if (!limit.TryParse(out _))
            {
                validation.Require("creditLimit.amount", null, "invalid_amount");
            }

            validation.Require("creditLimit.currency", limit.Currency).Currency("creditLimit.currency", limit.Currency);
        }

        return validation;
    }

    private static Validation ValidateContact(ContactRequest request, bool isCreate)
    {
        var validation = new Validation()
            .MaxLength("name", request.Name, 200)
            .MaxLength("roleTitle", request.RoleTitle, 100)
            .Email("email", request.Email);

        if (isCreate)
        {
            validation.Require("name", request.Name);
        }

        if (request.PhoneE164 is not null && !System.Text.RegularExpressions.Regex.IsMatch(request.PhoneE164, @"^\+[1-9][0-9]{6,14}$"))
        {
            validation.Require("phoneE164", null, "invalid_e164");
        }

        if (request.PreferredLanguage is not null && !Languages.Contains(request.PreferredLanguage))
        {
            validation.Require("preferredLanguage", null, "unsupported_language");
        }

        return validation;
    }

    /// <summary>PATCH semantics: a null field is "unchanged". The credit limit is cleared by explicit flag.</summary>
    private static void Apply(Customer customer, CustomerRequest request)
    {
        customer.Code = request.Code is null ? customer.Code : NullIfBlank(request.Code);
        customer.NameAr = request.NameAr is null ? customer.NameAr : NullIfBlank(request.NameAr);
        customer.NameEn = request.NameEn is null ? customer.NameEn : NullIfBlank(request.NameEn);
        customer.LegalName = request.LegalName is null ? customer.LegalName : NullIfBlank(request.LegalName);
        customer.TaxRegistrationNo = request.TaxRegistrationNo is null ? customer.TaxRegistrationNo : NullIfBlank(request.TaxRegistrationNo);
        customer.PreferredLanguage = request.PreferredLanguage ?? customer.PreferredLanguage;
        customer.PaymentTermsDays = request.PaymentTermsDays ?? customer.PaymentTermsDays;
        customer.DefaultCurrency = request.DefaultCurrency ?? customer.DefaultCurrency;
        customer.Notes = request.Notes is null ? customer.Notes : NullIfBlank(request.Notes);

        if (request.RiskFlag is not null)
        {
            customer.RiskFlag = Enum.Parse<RiskFlag>(request.RiskFlag);
        }

        if (request.Status is not null)
        {
            customer.Status = Enum.Parse<CustomerStatus>(request.Status);
        }

        if (request.ClearCreditLimit == true)
        {
            customer.CreditLimitAmount = null;
            customer.CreditLimitCurrency = null;
        }
        else if (request.CreditLimit is { } limit && limit.TryParse(out var amount))
        {
            customer.CreditLimitAmount = amount;
            customer.CreditLimitCurrency = limit.Currency;
        }
    }

    private static void ApplyContact(CustomerContact contact, ContactRequest request)
    {
        contact.Name = request.Name is null ? contact.Name : request.Name.Trim();
        contact.RoleTitle = request.RoleTitle is null ? contact.RoleTitle : NullIfBlank(request.RoleTitle);
        contact.Email = request.Email is null ? contact.Email : NullIfBlank(request.Email)?.Trim();
        contact.PhoneE164 = request.PhoneE164 is null ? contact.PhoneE164 : NullIfBlank(request.PhoneE164);
        contact.IsPrimary = request.IsPrimary ?? contact.IsPrimary;
        contact.IsBilling = request.IsBilling ?? contact.IsBilling;
        contact.PreferredLanguage = request.PreferredLanguage ?? contact.PreferredLanguage;
    }

    private static Task<bool> CodeIsTakenAsync(TenantDbContext db, string? code, Guid? exceptId, CancellationToken ct) =>
        code is null
            ? Task.FromResult(false)
            : db.Customers.AnyAsync(c => c.Code != null && c.Code.ToLower() == code.ToLower() && c.Id != exceptId, ct);

    private static async Task<Results<Ok<CustomerMergeResponse>, ProblemHttpResult>> MergeAsync(Guid id, CustomerMergeRequest request, HttpContext context, CurrentUser user, TenantDbContext db, FinanceAi.Infrastructure.Customers.CustomerMergeService merges, CancellationToken ct)
    {
        // SEC-13: the row in the route is looked up before the body is judged, so a foreign id is a 404 and nothing else.
        if (!await db.Customers.AnyAsync(c => c.Id == id, ct)) return ApiProblems.NotFoundProblem(context);
        if (request.SourceCustomerId is null) return ApiProblems.ValidationProblem(context, [new ApiProblems.FieldError("sourceCustomerId", "required", "errors.validation.sourceCustomerId.required")]);
        var confirming = !string.IsNullOrWhiteSpace(request.ConfirmToken);
        var (preview, refusal, notFound) = confirming
            ? await merges.ConfirmAsync(id, request.SourceCustomerId.Value, request.ConfirmToken!.Trim(), user.UserId, ct)
            : await merges.PreviewAsync(id, request.SourceCustomerId.Value, ct);
        if (notFound) return ApiProblems.NotFoundProblem(context);
        if (refusal is not null) return ApiProblems.BusinessRuleProblem(context, refusal.Code, null, refusal.Meta);
        return TypedResults.Ok(new CustomerMergeResponse(preview!.TargetId, preview.SourceId, confirming, preview.Moves.Select(m => new MergeTableDto(m.Name, m.Rows)).ToList(),
            preview.ConflictingInvoiceNumbers, preview.BothHaveOpenCases, confirming ? null : preview.ConfirmToken));
    }

    private static CustomerResponse ToResponse(Customer c) => new(
        c.Id, c.Code, c.NameAr, c.NameEn, c.LegalName, c.TaxRegistrationNo, c.PreferredLanguage, c.PaymentTermsDays,
        c.CreditLimitAmount is { } amount && c.CreditLimitCurrency is { } currency ? MoneyDto.From(amount, currency) : null,
        c.DefaultCurrency, c.RiskFlag.ToString(), c.Status.ToString(), c.BrokenPromiseCount12m, c.BouncedChequeCount12m,
        c.Notes, [], c.CreatedAt, c.UpdatedAt, c.RowVersion.ToString(CultureInfo.InvariantCulture),
        c.MergedIntoId);

    /// <summary>
    /// SEC-93 (slice 31): anonymize a contact's personal data while every financial and audit record that referenced
    /// the contact keeps its meaning. The row stays (messages point at it); name, role, email and phone go; every copy
    /// of the address on outbound and inbound messages goes with it; later edits are refused. Irreversible, so it
    /// needs a fresh re-authentication proof (SEC-09). The audit event carries the contact id only.
    /// </summary>
    private static async Task<Results<Ok<ContactResponse>, ProblemHttpResult>> EraseContactAsync(
        Guid id,
        Guid contactId,
        HttpContext context,
        CurrentUser currentUser,
        TenantDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var contact = await db.CustomerContacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CustomerId == id, ct);
        if (contact is null)
        {
            return ApiProblems.NotFoundProblem(context);
        }

        if (contact.ErasedAt is not null)
        {
            return ApiProblems.BusinessRuleProblem(context, "contact_erased", null, null);
        }

        var email = contact.Email;
        var phone = contact.PhoneE164;
        var now = time.GetUtcNow();
        // The request's tenant-scope transaction (TenantScopeMiddleware) makes the contact, the messages and the audit row one unit.

        contact.Name = CustomerContact.ErasedName;
        contact.RoleTitle = null;
        contact.Email = null;
        contact.PhoneE164 = null;
        contact.BouncedAt = null;
        contact.BounceReason = null;
        contact.ErasedAt = now;
        contact.ErasedBy = currentUser.UserId;
        contact.UpdatedAt = now;
        contact.RowVersion++;

        // Every copy of the address, whether it was written through the contact or matched by address alone.
        var outbound = await db.Messages.Where(m => m.ContactId == contactId || (m.ToAddress != null && (m.ToAddress == email || m.ToAddress == phone))).ToListAsync(ct);
        foreach (var m in outbound) m.ToAddress = null;
        var inbound = await db.InboundMessages.Where(m => m.FromAddress != null && (m.FromAddress == email || m.FromAddress == phone)).ToListAsync(ct);
        foreach (var m in inbound) m.FromAddress = null;

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(AuditFor(currentUser, context, "customer.contact_erased", id, new Dictionary<string, object?>
        {
            ["contactId"] = contactId,
            ["outboundAddressesCleared"] = outbound.Count,
            ["inboundAddressesCleared"] = inbound.Count,
        }), ct);

        return TypedResults.Ok(ToResponse(contact));
    }

    private static ContactResponse ToResponse(CustomerContact c) => new(
        c.Id, c.CustomerId, c.Name, c.RoleTitle, c.Email, c.PhoneE164, c.IsPrimary, c.IsBilling, c.PreferredLanguage,
        c.RowVersion.ToString(CultureInfo.InvariantCulture), c.BouncedAt, c.BounceReason, c.ErasedAt);

    /// <summary>The audited projection. Money is a string (DM-28), never a JSON number.</summary>
    private static Dictionary<string, object?> Snapshot(Customer c) => new(StringComparer.Ordinal)
    {
        ["code"] = c.Code,
        ["nameAr"] = c.NameAr,
        ["nameEn"] = c.NameEn,
        ["legalName"] = c.LegalName,
        ["taxRegistrationNo"] = c.TaxRegistrationNo,
        ["preferredLanguage"] = c.PreferredLanguage,
        ["paymentTermsDays"] = c.PaymentTermsDays,
        ["creditLimitAmount"] = c.CreditLimitAmount?.ToString("F3", CultureInfo.InvariantCulture),
        ["creditLimitCurrency"] = c.CreditLimitCurrency,
        ["defaultCurrency"] = c.DefaultCurrency,
        ["riskFlag"] = c.RiskFlag.ToString(),
        ["status"] = c.Status.ToString(),
        ["notes"] = c.Notes,
    };

    private static Dictionary<string, object?> Diff(Dictionary<string, object?> before, Dictionary<string, object?> after) =>
        after.Where(kv => !Equals(kv.Value, before[kv.Key]))
             .ToDictionary(kv => kv.Key, kv => (object?)new { old = before[kv.Key], @new = kv.Value }, StringComparer.Ordinal);

    private static AuditEvent AuditFor(CurrentUser user, HttpContext context, string eventType, Guid customerId, Dictionary<string, object?>? changes) => new()
    {
        TenantId = user.TenantId,
        ActorUserId = user.UserId,
        ActorKind = ActorKinds.User,
        ActorIp = context.ClientIp(),
        EventType = eventType,
        EntityType = "customer",
        EntityId = customerId,
        Changes = changes is null ? null : JsonSerializer.Serialize(changes),
        RequestId = context.RequestId(),
    };

    private static long? IfMatch(HttpContext context) =>
        long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"', 'W', '/'), out var version) ? version : null;

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}
