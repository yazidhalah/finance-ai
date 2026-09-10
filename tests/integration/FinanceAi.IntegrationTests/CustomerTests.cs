using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAi.IntegrationTests;

public sealed record MoneyDto(string Amount, string Currency);

public sealed record CustomerDto(
    Guid Id, string? Code, string? NameAr, string? NameEn, string? LegalName, string PreferredLanguage,
    int PaymentTermsDays, MoneyDto? CreditLimit, string DefaultCurrency, string RiskFlag, string Status,
    IReadOnlyList<object> Balances, string RowVersion);

public sealed record CustomerListDto(IReadOnlyList<CustomerDto> Items, string? NextCursor, int TotalCount);

public sealed record ContactDto(Guid Id, Guid CustomerId, string Name, string? Email, bool IsPrimary, string RowVersion);

public sealed record ContactListDto(IReadOnlyList<ContactDto> Items);

public sealed record DuplicateDto(Guid CustomerId, Guid OtherCustomerId, string? NameA, string? NameB, double Similarity);

public sealed record DuplicateListDto(IReadOnlyList<DuplicateDto> Items);

/// <summary>Slice 2 AC-01 … AC-12, AC-18, AC-19.</summary>
[Collection(ApiCollection.Name)]
public sealed class CustomerTests(ApiTestFixture fixture)
{
    /// <summary>AC-01 / DM-20 / doc 10 §2.1 — the acceptance criterion, verbatim.</summary>
    [Fact]
    public async Task Search_IsArabicAware()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var created = await CreateAsync(client, new { nameAr = "شركة الأمل التجارية", nameEn = "Al Amal Trading Co." });
        await CreateAsync(client, new { nameEn = "Petra Supplies" });

        foreach (var term in new[] { "شركه الامل", "شركة الأمل", "الامل", "Al Amal", "al amal trading", "AMAL" })
        {
            var page = await client.GetFromJsonAsync<CustomerListDto>(
                $"/api/v1/customers?q={Uri.EscapeDataString(term)}", ApiScenario.Json);

            Assert.True(page!.Items.Any(c => c.Id == created.Id), $"'{term}' did not find the customer.");
            Assert.DoesNotContain(page.Items, c => c.NameEn == "Petra Supplies");
        }
    }

    /// <summary>AC-02: each normalization rule of DM-20, checked against the database function itself.</summary>
    [Theory]
    [InlineData("شركة", "شركه")]              // teh marbuta -> heh
    [InlineData("الأمل", "الامل")]            // alef with hamza above -> alef
    [InlineData("إبراهيم", "ابراهيم")]        // alef with hamza below -> alef
    [InlineData("آمنة", "امنه")]              // alef with madda -> alef, teh marbuta -> heh
    [InlineData("مصطفى", "مصطفي")]            // alef maqsura -> yeh
    [InlineData("مــحــمــد", "محمد")]         // tatweel removed
    [InlineData("مُحَمَّد", "محمد")]            // diacritics removed
    [InlineData("  Al   Amal  ", "al amal")]  // Latin lower-cased, whitespace collapsed
    public async Task Normalize_UnifiesArabicForms(string input, string expected)
    {
        var normalized = await fixture.Database.ScalarAsync<string>(
            "SELECT app_normalize_arabic(@i)", ("i", input));

        Assert.Equal(expected, normalized);
    }

    /// <summary>AC-03 / doc 10 §2.2.</summary>
    [Fact]
    public async Task Duplicates_SurfaceNearIdenticalNames()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var a = await CreateAsync(client, new { nameAr = "شركة الأمل التجارية" });
        var b = await CreateAsync(client, new { nameAr = "شركه الامل التجاريه" });
        await CreateAsync(client, new { nameEn = "Completely Different Ltd" });

        var duplicates = await client.GetFromJsonAsync<DuplicateListDto>("/api/v1/customers/duplicates", ApiScenario.Json);

        var pair = Assert.Single(duplicates!.Items);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), new[] { pair.CustomerId, pair.OtherCustomerId }.Order());
        Assert.Equal(1.0, pair.Similarity);
    }

    /// <summary>AC-04.</summary>
    [Fact]
    public async Task Create_WithoutAnyName_Returns400()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PostAsJsonAsync("/api/v1/customers", new { code = "C-1", legalName = "Nameless" }, ApiScenario.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>AC-04: the database is the last line, not the endpoint.</summary>
    [Fact]
    public async Task Database_RejectsNamelessCustomer()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        await using var connection = fixture.Database.OpenAdmin();
        await using var insert = new Npgsql.NpgsqlCommand(
            "INSERT INTO customers (id, tenant_id) VALUES (@id, @t)", connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("t", organization.TenantId);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("customer_has_a_name", ex.ConstraintName);
    }

    /// <summary>AC-05, AC-06 / API-05, DM-09, FIN-01.</summary>
    [Fact]
    public async Task Money_IsDecimalAndSerializedAsString()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var created = await CreateAsync(client, new
        {
            nameEn = "Credit Co.",
            creditLimit = new { amount = "12500.5", currency = "JOD" },
        });

        Assert.Equal("12500.500", created.CreditLimit!.Amount);
        Assert.Equal("JOD", created.CreditLimit.Currency);

        // The wire format is a string, whatever the client's JSON parser would have made of a number.
        using var raw = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/customers/{created.Id}"));
        Assert.Equal(JsonValueKind.String, raw.RootElement.GetProperty("creditLimit").GetProperty("amount").ValueKind);

        // Stored as numeric(19,3), read back exactly — no float in the path.
        var stored = await fixture.Database.ScalarAsync<decimal>(
            "SELECT credit_limit_amount FROM customers WHERE id = @id", ("id", created.Id));
        Assert.Equal(12500.500m, stored);

        // More than three decimals is refused rather than rounded: rounding is a financial decision
        // that belongs to a documented rule (FIN-05), not to a form field.
        var tooPrecise = await client.PostAsJsonAsync("/api/v1/customers",
            new { nameEn = "Precise Co.", creditLimit = new { amount = "1.2345", currency = "JOD" } }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooPrecise.StatusCode);

        // A JSON number is not accepted as an amount at all.
        var asNumber = await client.PostAsync("/api/v1/customers",
            new StringContent("""{"nameEn":"Number Co.","creditLimit":{"amount":100,"currency":"JOD"}}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, asNumber.StatusCode);
    }

    /// <summary>AC-05: amount and currency together or not at all.</summary>
    [Fact]
    public async Task CreditLimit_RequiresCurrency()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PostAsJsonAsync("/api/v1/customers",
            new { nameEn = "Half Co.", creditLimit = new { amount = "100.000" } }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The CHECK constraint holds even for a writer that skips the API.
        var organizationId = organization.TenantId;
        await using var connection = fixture.Database.OpenAdmin();
        await using var insert = new Npgsql.NpgsqlCommand(
            "INSERT INTO customers (id, tenant_id, name_en, credit_limit_amount) VALUES (@id, @t, 'X', 1)", connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("t", organizationId);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("credit_limit_pair", ex.ConstraintName);
    }

    /// <summary>AC-07.</summary>
    [Fact]
    public async Task Code_IsUniquePerTenant_CaseInsensitive()
    {
        var organizationA = await fixture.Api.CreateOrganizationAsync();
        var organizationB = await fixture.Api.CreateOrganizationAsync();
        using var clientA = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);
        using var clientB = fixture.Api.AuthenticatedClient(organizationB.OwnerSession);

        await CreateAsync(clientA, new { code = "CUST-001", nameEn = "First" });

        var duplicate = await clientA.PostAsJsonAsync("/api/v1/customers", new { code = "cust-001", nameEn = "Second" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // The same code in another organization is not a conflict: codes are the tenant's own.
        var elsewhere = await clientB.PostAsJsonAsync("/api/v1/customers", new { code = "CUST-001", nameEn = "Theirs" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    /// <summary>AC-08 / API-09.</summary>
    [Fact]
    public async Task Update_WithStaleIfMatch_Returns409()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var created = await CreateAsync(client, new { nameEn = "Versioned Co." });

        var withoutIfMatch = await client.PatchAsync($"/api/v1/customers/{created.Id}",
            JsonContent.Create(new { legalName = "No header" }, options: ApiScenario.Json));
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);

        var first = await PatchAsync(client, created.Id, created.RowVersion, new { legalName = "First edit" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await PatchAsync(client, created.Id, created.RowVersion, new { legalName = "Second edit" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await client.GetFromJsonAsync<CustomerDto>($"/api/v1/customers/{created.Id}", ApiScenario.Json);
        Assert.Equal("First edit", current!.LegalName);
        Assert.Equal("2", current.RowVersion);
    }

    /// <summary>AC-09 / DM-08.</summary>
    [Fact]
    public async Task Delete_IsSoft()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var created = await CreateAsync(client, new { code = "GONE", nameEn = "Soon Deleted" });

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/customers/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/customers/{created.Id}")).StatusCode);

        var page = await client.GetFromJsonAsync<CustomerListDto>("/api/v1/customers", ApiScenario.Json);
        Assert.DoesNotContain(page!.Items, c => c.Id == created.Id);

        // The row is still there, with its history.
        var deletedAt = await fixture.Database.ScalarAsync<DateTime?>(
            "SELECT deleted_at FROM customers WHERE id = @id", ("id", created.Id));
        Assert.NotNull(deletedAt);

        // ...and its code is free for reuse, since the partial index ignores deleted rows.
        var reuse = await client.PostAsJsonAsync("/api/v1/customers", new { code = "GONE", nameEn = "Reused Code" }, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Created, reuse.StatusCode);
    }

    /// <summary>AC-11 / API-07.</summary>
    [Fact]
    public async Task List_IsCursorPaginated()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(client, new { nameEn = $"Customer {i:D2}" });
        }

        var first = await client.GetFromJsonAsync<CustomerListDto>("/api/v1/customers?limit=2", ApiScenario.Json);
        Assert.Equal(2, first!.Items.Count);
        Assert.Equal(5, first.TotalCount);
        Assert.NotNull(first.NextCursor);

        var second = await client.GetFromJsonAsync<CustomerListDto>($"/api/v1/customers?limit=2&cursor={first.NextCursor}", ApiScenario.Json);
        var third = await client.GetFromJsonAsync<CustomerListDto>($"/api/v1/customers?limit=2&cursor={second!.NextCursor}", ApiScenario.Json);

        Assert.Single(third!.Items);
        Assert.Null(third.NextCursor);

        var all = first.Items.Concat(second.Items).Concat(third.Items).Select(c => c.Id).ToList();
        Assert.Equal(5, all.Distinct().Count());
    }

    /// <summary>AC-12.</summary>
    [Fact]
    public async Task Contacts_ExactlyOnePrimary()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var customer = await CreateAsync(client, new { nameEn = "Contacts Co." });
        var url = $"/api/v1/customers/{customer.Id}/contacts";

        // The first contact becomes primary whether or not asked.
        var first = await (await client.PostAsJsonAsync(url, new { name = "Layla", email = "layla@example.jo" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<ContactDto>(ApiScenario.Json);
        Assert.True(first!.IsPrimary);

        var second = await (await client.PostAsJsonAsync(url, new { name = "Sami" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<ContactDto>(ApiScenario.Json);
        Assert.False(second!.IsPrimary);

        // Promoting the second demotes the first, in one transaction.
        var promote = new HttpRequestMessage(HttpMethod.Patch, $"{url}/{second.Id}")
        {
            Content = JsonContent.Create(new { isPrimary = true }, options: ApiScenario.Json),
        };
        promote.Headers.TryAddWithoutValidation("If-Match", second.RowVersion);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(promote)).StatusCode);

        var contacts = await client.GetFromJsonAsync<ContactListDto>(url, ApiScenario.Json);
        Assert.Single(contacts!.Items, c => c.IsPrimary);
        Assert.True(contacts.Items.Single(c => c.Id == second.Id).IsPrimary);

        // Removing the primary hands the role to the survivor.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{url}/{second.Id}")).StatusCode);
        contacts = await client.GetFromJsonAsync<ContactListDto>(url, ApiScenario.Json);
        Assert.True(Assert.Single(contacts!.Items).IsPrimary);
    }

    /// <summary>AC-12: the partial unique index is the last line.</summary>
    [Fact]
    public async Task Database_RejectsTwoPrimaryContacts()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);
        var customer = await CreateAsync(client, new { nameEn = "Two Primaries Co." });
        await client.PostAsJsonAsync($"/api/v1/customers/{customer.Id}/contacts", new { name = "One" }, ApiScenario.Json);

        await using var connection = fixture.Database.OpenAdmin();
        await using var insert = new Npgsql.NpgsqlCommand(
            "INSERT INTO customer_contacts (id, tenant_id, customer_id, name, is_primary) VALUES (@id, @t, @c, 'Two', true)", connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("t", organization.TenantId);
        insert.Parameters.AddWithValue("c", customer.Id);

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("one_primary_contact", ex.ConstraintName);
    }

    /// <summary>AC-18 / SEC-17.</summary>
    [Theory]
    [InlineData("tenantId")]
    [InlineData("rowVersion")]
    [InlineData("normalizedName")]
    [InlineData("balances")]
    [InlineData("brokenPromiseCount12m")]
    public async Task Body_WithUnknownField_Returns400(string field)
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var response = await client.PostAsync("/api/v1/customers",
            new StringContent($$"""{"nameEn":"X","{{field}}":"1"}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("unexpected_field", problem!.Code);
    }

    /// <summary>AC-19 / SEC-50.</summary>
    [Fact]
    public async Task CustomerMutations_AreAudited()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var created = await CreateAsync(client, new { nameEn = "Audited Co.", creditLimit = new { amount = "500.000", currency = "JOD" } });
        await PatchAsync(client, created.Id, created.RowVersion, new { paymentTermsDays = 45 });

        var events = await fixture.Database.ScalarAsync<string>(
            "SELECT string_agg(event_type, ',' ORDER BY id) FROM audit_events WHERE tenant_id = @t AND entity_id = @c",
            ("t", organization.TenantId), ("c", created.Id));
        Assert.Equal("customer.created,customer.updated", events);

        var changes = await fixture.Database.ScalarAsync<string>(
            "SELECT changes::text FROM audit_events WHERE entity_id = @c AND event_type = 'customer.updated'", ("c", created.Id));
        Assert.Contains("\"paymentTermsDays\"", changes, StringComparison.Ordinal);
        Assert.Contains("45", changes, StringComparison.Ordinal);

        // DM-28: money in the audit trail is a string.
        var createdChanges = await fixture.Database.ScalarAsync<string>(
            "SELECT changes::text FROM audit_events WHERE entity_id = @c AND event_type = 'customer.created'", ("c", created.Id));
        Assert.Contains("\"creditLimitAmount\": \"500.000\"", createdChanges, StringComparison.Ordinal);

        var actor = await fixture.Database.ScalarAsync<Guid>(
            "SELECT actor_user_id FROM audit_events WHERE entity_id = @c AND event_type = 'customer.created'", ("c", created.Id));
        Assert.Equal(organization.OwnerUserId, actor);
    }

    [Fact]
    public async Task Viewer_CanReadButNotWrite()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();
        var viewer = await fixture.Database.AddMemberAsync(organization.TenantId, FinanceAi.Domain.Authorization.TenantRole.Viewer);
        using var client = fixture.Api.AuthenticatedClient(await fixture.Api.LoginAsync(viewer.Email, viewer.Password));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/v1/customers", new { nameEn = "X" }, ApiScenario.Json)).StatusCode);
    }

    private static async Task<CustomerDto> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/v1/customers", body, ApiScenario.Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CustomerDto>(ApiScenario.Json))!;
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string rowVersion, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/customers/{id}")
        {
            Content = JsonContent.Create(body, options: ApiScenario.Json),
        };
        request.Headers.TryAddWithoutValidation("If-Match", rowVersion);
        return client.SendAsync(request);
    }
}

/// <summary>AC-10 / doc 10 §2.4: the guard is exercised through a stub that reports an open balance.</summary>
public sealed class OpenBalanceGuardTests : IAsyncLifetime
{
    private DatabaseFixture? database;
    private GuardedApiFactory? api;

    private sealed class GuardedApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddScoped<FinanceAi.Domain.Entities.ICustomerBalanceGuard, AlwaysOwing>());
        }

        private sealed class AlwaysOwing : FinanceAi.Domain.Entities.ICustomerBalanceGuard
        {
            public Task<bool> HasOpenBalanceAsync(Guid customerId, CancellationToken ct = default) => Task.FromResult(true);
        }
    }

    public async Task InitializeAsync()
    {
        this.database = await DatabaseFixture.CreateAsync("guard");
        Environment.SetEnvironmentVariable("AUTH_RATE_LIMIT_PER_MINUTE", "100000");
        this.api = new GuardedApiFactory();
        _ = this.api.Services;
    }

    public async Task DisposeAsync()
    {
        this.api?.Dispose();
        if (this.database is not null)
        {
            await this.database.DisposeAsync();
        }
    }

    [Fact]
    public async Task Delete_WithOpenBalance_Returns422()
    {
        var organization = await this.api!.CreateOrganizationAsync();
        using var client = this.api!.AuthenticatedClient(organization.OwnerSession);

        var created = await (await client.PostAsJsonAsync("/api/v1/customers", new { nameEn = "Owes Money" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<CustomerDto>(ApiScenario.Json);

        var response = await client.DeleteAsync($"/api/v1/customers/{created!.Id}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(ApiScenario.Json);
        Assert.Equal("has_open_balance", problem!.Code);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/customers/{created.Id}")).StatusCode);
    }
}
