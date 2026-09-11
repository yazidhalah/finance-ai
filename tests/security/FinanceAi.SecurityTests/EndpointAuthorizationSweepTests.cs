using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FinanceAi.Api.Authorization;
using FinanceAi.Domain.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceAi.SecurityTests;

/// <summary>
/// AC-23 … AC-26 / SEC-14 / T-40, T-60, T-71.
/// <para>
/// These sweeps are <b>generated from the route table</b>, not hand-written per endpoint. Doc 09
/// asks for that explicitly, and the reason is simple: a hand-written list is a list someone forgets
/// to extend. Add an endpoint in slice 2 and it is tested for 401, 403 and cross-tenant 404 the
/// moment it is mapped, or the build fails.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointAuthorizationSweepTests(ApiTestFixture fixture)
{
    private sealed record EndpointUnderTest(
        string Method, string Template, RequiredPermission? Permission, DeclaredAccess? Access, bool Multipart);

    /// <summary>AC-24 / SEC-14: no token, no access — for every endpoint that is not anonymous.</summary>
    [Fact]
    public async Task EveryProtectedEndpoint_Returns401WithoutToken()
    {
        var protectedEndpoints = this.Endpoints()
            .Where(e => e.Access?.Kind != DeclaredAccessKind.Anonymous)
            .ToList();

        Assert.NotEmpty(protectedEndpoints);

        using var client = fixture.Api.CreateCookieClient();

        foreach (var endpoint in protectedEndpoints)
        {
            var response = await SendAsync(client, endpoint, Guid.CreateVersion7());

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} without a token; expected 401.");
        }
    }

    /// <summary>
    /// AC-25 / SEC-14. For each endpoint, a real user holding a real role that lacks the declared
    /// permission — chosen from doc 01 §5.1 rather than assumed — must be refused.
    /// </summary>
    [Fact]
    public async Task EveryProtectedEndpoint_Returns403ForRoleWithoutPermission()
    {
        var permissioned = this.Endpoints().Where(e => e.Permission is not null).ToList();

        Assert.NotEmpty(permissioned);

        var organization = await fixture.Api.CreateOrganizationAsync();
        var universal = new List<string>();

        foreach (var endpoint in permissioned)
        {
            var permission = endpoint.Permission!.Permission;

            var roleWithout = Enum.GetValues<TenantRole>()
                .Cast<TenantRole?>()
                .FirstOrDefault(r => !RolePermissions.Grants(r!.Value, permission));

            // Some permissions in doc 01 §5.1 are held by every role — tenant.read, customers.read,
            // invoices.read and the other baseline reads. For those there is no role to refuse, so
            // no 403 exists to assert. PRD-13 asks for "a 403 for at least one role that lacks it",
            // which for a universal permission is unsatisfiable by construction rather than by
            // oversight; the boundary that protects them is tenancy, and AC-26/AC-27 test that.
            if (roleWithout is null)
            {
                universal.Add(permission);
                continue;
            }

            var member = await fixture.Database.AddMemberAsync(organization.TenantId, roleWithout.Value);
            var session = await fixture.Api.LoginAsync(member.Email, member.Password);

            using var client = fixture.Api.AuthenticatedClient(session);
            var response = await SendAsync(client, endpoint, Guid.CreateVersion7());

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} for a " +
                $"{roleWithout} who lacks '{permission}'; expected 403.");
        }

        // Pinned so that a permission becoming universal is a deliberate, visible change rather than
        // a quiet loss of test coverage.
        Assert.Equal(
            ["aging.read", "cases.read", "customers.read", "invoices.read", "payments.read", "tenant.read"],
            universal.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The other half of PRD-13: a permission that <i>is</i> refusable must actually be refused, and
    /// the same endpoint must be allowed for a role that holds it. Without the positive case, a
    /// blanket "deny everything" would pass the 403 sweep.
    /// </summary>
    [Fact]
    public async Task EveryProtectedEndpoint_IsAllowedForARoleThatHoldsThePermission()
    {
        var organization = await fixture.Api.CreateOrganizationAsync();

        foreach (var endpoint in this.Endpoints().Where(e => e.Permission is not null && e.Method == "GET"))
        {
            var permission = endpoint.Permission!.Permission;

            var roleWith = Enum.GetValues<TenantRole>()
                .First(r => RolePermissions.Grants(r, permission));

            var member = roleWith == TenantRole.Owner
                ? null
                : await fixture.Database.AddMemberAsync(organization.TenantId, roleWith);

            var session = member is null
                ? organization.OwnerSession
                : await fixture.Api.LoginAsync(member.Email, member.Password);

            using var client = fixture.Api.AuthenticatedClient(session);
            var response = await SendAsync(client, endpoint, Guid.CreateVersion7());

            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} for a " +
                $"{roleWith} who holds '{permission}'; expected 200, or 404 for an id that does not exist.");
        }
    }

    /// <summary>
    /// AC-26 / SEC-13 / T-71. Another organization's id must look missing, not forbidden. A 403
    /// would confirm the row exists, and for a receivables product that is itself a disclosure:
    /// it tells one SME that another one has a particular customer, invoice or user.
    /// </summary>
    [Fact]
    public async Task EveryEndpointWithId_Returns404ForCrossTenantId()
    {
        var withIdParameter = this.Endpoints()
            .Where(e => e.Template.Contains("{id", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(withIdParameter);

        var organizationA = await fixture.Api.CreateOrganizationAsync("Sweep A");
        var organizationB = await fixture.Api.CreateOrganizationAsync("Sweep B");

        // Real, existing ids in B — not random guids, so a 404 proves scoping rather than absence.
        // Each slice that adds an entity with an {id} route registers how to create one in B here;
        // a route whose entity has no registered id falls back to B's membership id.
        var idsInB = await CreateEntitiesInAsync(organizationB);

        using var clientA = fixture.Api.AuthenticatedClient(organizationA.OwnerSession);

        foreach (var endpoint in withIdParameter)
        {
            var secondary = endpoint.Template.Contains("{rowId", StringComparison.Ordinal) ? idsInB.RowId : idsInB.ContactId;
            var response = await SendAsync(clientA, endpoint, IdFor(endpoint, idsInB), secondary);

            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{endpoint.Method} {endpoint.Template} returned {(int)response.StatusCode} for a real id " +
                "belonging to another organization; expected 404 (SEC-13).");
        }
    }

    /// <summary>
    /// AC-23 / SEC-10 / T-61. An endpoint that declares nothing must prevent the application from
    /// booting. The whole point is that the failure mode of a forgotten declaration is a crash at
    /// deploy time, not an open endpoint discovered later.
    /// </summary>
    [Fact]
    public void Startup_FailsWhenAnEndpointDeclaresNoPermission()
    {
        var declared = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/v1/declared"),
            0,
            new EndpointMetadataCollection(new RequiredPermission(Permissions.TenantRead)),
            "declared");

        var undeclared = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/v1/forgotten"),
            0,
            new EndpointMetadataCollection(),
            "forgotten");

        // The declared one alone is fine.
        EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess([declared]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess([declared, undeclared]));

        Assert.Contains("SEC-10", ex.Message, StringComparison.Ordinal);
        Assert.Contains("forgotten", ex.Message, StringComparison.Ordinal);

        // ...and the running application satisfies the same assertion, which is why it started.
        EndpointDeclarationAssertion.AssertEveryEndpointDeclaresAccess(
            fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints);
    }

    /// <summary>Every endpoint this slice ships is accounted for, so the sweeps above are not
    /// silently sweeping an empty set.</summary>
    [Fact]
    public void EveryEndpoint_DeclaresEitherAPermissionOrAnExplicitAccessLevel()
    {
        var endpoints = this.Endpoints();

        Assert.Equal(76, endpoints.Count);
        Assert.All(endpoints, e => Assert.True(e.Permission is not null || e.Access is not null));

        // The anonymous set is exactly registration, login and refresh — nothing has drifted into it.
        var anonymous = endpoints
            .Where(e => e.Access?.Kind == DeclaredAccessKind.Anonymous)
            .Select(e => e.Template)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["api/v1/auth/login", "api/v1/auth/refresh", "api/v1/auth/register"],
            anonymous);
    }

    /// <summary>
    /// The live route table of the running application. Not a list maintained by hand — that is the
    /// point: a new endpoint joins these sweeps by existing.
    /// </summary>
    private List<EndpointUnderTest> Endpoints() =>
        fixture.Api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (Endpoint: e, Template: Normalize(e.RoutePattern.RawText)))
            .Where(e => e.Template.StartsWith("api/v1/", StringComparison.Ordinal))
            .SelectMany(e => (e.Endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Where(m => m is not ("OPTIONS" or "HEAD"))
                .Select(m => new EndpointUnderTest(
                    m,
                    e.Template,
                    e.Endpoint.Metadata.GetMetadata<RequiredPermission>(),
                    e.Endpoint.Metadata.GetMetadata<DeclaredAccess>(),
                    e.Endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes.Any(c => c.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)) == true)))
            .OrderBy(e => e.Template, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();

    private static string Normalize(string? template) =>
        (template ?? string.Empty).Trim('/');

    private sealed record ForeignIds(Guid MembershipId, Guid CustomerId, Guid ContactId, Guid BatchId, Guid RowId, Guid MappingId, Guid InvoiceId, Guid PaymentId, Guid AllocationId, Guid ChequeId, Guid CreditNoteId, Guid WriteOffId, Guid CaseId);

    /// <summary>One real row of every {id}-addressed entity, inside organization B.</summary>
    private async Task<ForeignIds> CreateEntitiesInAsync(ApiScenario.Organization organization)
    {
        var membershipId = await fixture.Database.ScalarAsync<Guid>(
            "SELECT id FROM tenant_memberships WHERE tenant_id = @t LIMIT 1", ("t", organization.TenantId));

        using var client = fixture.Api.AuthenticatedClient(organization.OwnerSession);

        var customer = await (await client.PostAsJsonAsync("/api/v1/customers", new { nameEn = "Sweep target" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json);
        var customerId = customer.GetProperty("id").GetGuid();

        var contact = await (await client.PostAsJsonAsync($"/api/v1/customers/{customerId}/contacts", new { name = "Sweep contact" }, ApiScenario.Json))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json);

        // Slice 3: a committed import, so batch, row, mapping and invoice ids are all real.
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Invoice No,Customer,Issue Date,Total\nSW-1,Sweep target,2026-09-01,1\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "sweep.csv");
        var batch = await (await client.PostAsync("/api/v1/imports", form)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json);
        var batchId = batch.GetProperty("id").GetGuid();

        var mapped = await (await client.PostAsJsonAsync($"/api/v1/imports/{batchId}/mapping", new
        {
            columnMap = new Dictionary<string, string> { ["Invoice No"] = "invoice_number", ["Customer"] = "customer_name", ["Issue Date"] = "issue_date", ["Total"] = "total_amount" },
            saveAs = "sweep mapping",
        }, ApiScenario.Json)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ApiScenario.Json);
        var mappingId = mapped.GetProperty("mappingId").GetGuid();

        var rows = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/v1/imports/{batchId}/rows", ApiScenario.Json);
        var rowId = rows.GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.PostAsync($"/api/v1/imports/{batchId}/commit", null)).StatusCode);
        var invoices = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/invoices", ApiScenario.Json);
        var invoiceId = invoices.GetProperty("items")[0].GetProperty("id").GetGuid();

        // Slice 3b: money against that invoice, so payment, allocation, cheque, credit note and
        // write-off ids are all real. The invoice is 1.000 (the sweep CSV); allocate 0.500 so a
        // write-off proposal still has a balance to name.
        JsonElement Post(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ApiScenario.Json) };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
            var response = client.SendAsync(request).GetAwaiter().GetResult();
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        var payment = Post("/api/v1/payments", new
        {
            customerId,
            amount = new { amount = "0.500", currency = "JOD" },
            method = "Cash",
            receivedDate = "2026-09-10",
            allocations = new[] { new { invoiceId, amount = new { amount = "0.500", currency = "JOD" } } },
        });
        var cheque = Post("/api/v1/cheques", new { customerId, chequeNumber = "SWEEP-1", amount = new { amount = "1.000", currency = "JOD" }, chequeDate = "2026-10-01", receivedDate = "2026-09-10" });
        var note = Post("/api/v1/credit-notes", new { customerId, amount = new { amount = "0.100", currency = "JOD" }, issueDate = "2026-09-10", reasonCode = "other" });
        var writeOff = Post($"/api/v1/invoices/{invoiceId}/write-off", new { reasonCode = "sweep" });

        // Slice 5: an overdue invoice so a case can be opened for the customer.
        await fixture.Database.OpenInvoiceAsync(organization.TenantId, customerId, "SW-OVERDUE", 5m, dueDate: "2026-08-01", issueDate: "2026-07-01");
        var collectionCase = Post("/api/v1/cases", new { customerId });

        Assert.NotEqual(Guid.Empty, membershipId);
        return new ForeignIds(membershipId, customerId, contact.GetProperty("id").GetGuid(), batchId, rowId, mappingId, invoiceId,
            payment.GetProperty("id").GetGuid(), payment.GetProperty("allocations")[0].GetProperty("id").GetGuid(),
            cheque.GetProperty("id").GetGuid(), note.GetProperty("id").GetGuid(), writeOff.GetProperty("id").GetGuid(), collectionCase.GetProperty("caseId").GetGuid());
    }

    /// <summary>Which of B's real ids a route is addressed with. A new entity with an {id} route registers here.</summary>
    private static Guid IdFor(EndpointUnderTest endpoint, ForeignIds ids) => endpoint.Template switch
    {
        var t when t.Contains("import-mappings", StringComparison.Ordinal) => ids.MappingId,
        var t when t.Contains("cases", StringComparison.Ordinal) => ids.CaseId,
        var t when t.Contains("payments", StringComparison.Ordinal) => ids.PaymentId,
        var t when t.Contains("allocations/{id", StringComparison.Ordinal) => ids.AllocationId,
        var t when t.Contains("cheques", StringComparison.Ordinal) => ids.ChequeId,
        var t when t.Contains("credit-notes", StringComparison.Ordinal) => ids.CreditNoteId,
        var t when t.Contains("write-offs", StringComparison.Ordinal) => ids.WriteOffId,
        var t when t.Contains("imports", StringComparison.Ordinal) => ids.BatchId,
        var t when t.Contains("invoices", StringComparison.Ordinal) => ids.InvoiceId,
        var t when t.Contains("customers", StringComparison.Ordinal) => ids.CustomerId,
        _ => ids.MembershipId,
    };

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, EndpointUnderTest endpoint, Guid id, Guid? secondaryId = null)
    {
        var path = "/" + endpoint.Template
            .Replace("{id:guid}", id.ToString(), StringComparison.Ordinal)
            .Replace("{id}", id.ToString(), StringComparison.Ordinal)
            .Replace("{contactId:guid}", (secondaryId ?? Guid.CreateVersion7()).ToString(), StringComparison.Ordinal)
            .Replace("{rowId:guid}", (secondaryId ?? Guid.CreateVersion7()).ToString(), StringComparison.Ordinal);

        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), path);

        if (endpoint.Multipart)
        {
            // Routing answers 415 to the wrong content type before any middleware runs, which would
            // mask the 401/403 this sweep is here to prove. Speak the endpoint's language.
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes("a,b\n1,2\n"));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "sweep.csv");
            request.Content = form;
        }
        else if (endpoint.Method is "POST" or "PATCH" or "PUT")
        {
            request.Content = JsonContent.Create(new { }, options: ApiScenario.Json);
        }

        return client.SendAsync(request);
    }
}
