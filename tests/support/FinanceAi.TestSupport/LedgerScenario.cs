using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace FinanceAi.TestSupport;

/// <summary>
/// Builds the state the worked examples start from. Invoices are inserted directly as `Open`
/// with `balance_cache = total` — the state the import commit produces — rather than driving the
/// four-step import for every example. The import path is proven in its own suite.
/// </summary>
public static class LedgerScenario
{
    public sealed record Setup(ApiScenario.Organization Organization, HttpClient Client, Guid CustomerId);

    public static async Task<Setup> NewCustomerAsync(this ApiFactory api, string name = "Ledger Co.", string currency = "JOD")
    {
        ArgumentNullException.ThrowIfNull(api);
        var organization = await api.CreateOrganizationAsync();
        var client = api.AuthenticatedClient(organization.OwnerSession);
        var response = await client.PostAsJsonAsync("/api/v1/customers", new { nameEn = name, defaultCurrency = currency }, ApiScenario.Json);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("id").GetGuid();
        return new Setup(organization, client, id);
    }

    public static async Task<Guid> OpenInvoiceAsync(this DatabaseFixture db, Guid tenantId, Guid customerId, string number, decimal total, string currency = "JOD", string dueDate = "2026-09-01", string issueDate = "2026-08-01", decimal fxRateToBase = 1m)
    {
        ArgumentNullException.ThrowIfNull(db);
        var id = Guid.CreateVersion7();
        await using var connection = db.OpenAdmin();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO invoices (id, tenant_id, customer_id, invoice_number, status, issue_date, due_date, currency,
                                  net_amount, tax_amount, total_amount, balance_cache, base_currency, fx_rate_to_base)
            VALUES (@id, @t, @c, @n, 'Open', @issue::date, @due::date, @cur, @total, 0, @total, @total, 'JOD', @fx)
            """, connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("t", tenantId);
        insert.Parameters.AddWithValue("c", customerId);
        insert.Parameters.AddWithValue("n", number);
        insert.Parameters.AddWithValue("issue", issueDate);
        insert.Parameters.AddWithValue("due", dueDate);
        insert.Parameters.AddWithValue("cur", currency);
        insert.Parameters.AddWithValue("total", total);
        insert.Parameters.AddWithValue("fx", fxRateToBase);
        await insert.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task<JsonElement> PostAsync(this HttpClient client, string path, object body, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ApiScenario.Json) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{path} -> {(int)response.StatusCode}: {text}");
        }

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<(int Status, JsonElement Body)> TryPostAsync(this HttpClient client, string path, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ApiScenario.Json) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static async Task<JsonElement> InvoiceAsync(this HttpClient client, Guid id)
    {
        ArgumentNullException.ThrowIfNull(client);
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/invoices/{id}", ApiScenario.Json);
    }

    /// <summary>Slice 13 (SEC-09): re-authenticates and pins the five-minute proof on the client for the sensitive endpoints.</summary>
    public static async Task ReauthAsync(this HttpClient client, string password = ApiScenario.ValidPassword, string? totp = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.PostAsJsonAsync("/api/v1/auth/reauthenticate", new { password, totp }, ApiScenario.Json);
        response.EnsureSuccessStatusCode();
        var proof = (await response.Content.ReadFromJsonAsync<JsonElement>(ApiScenario.Json)).GetProperty("reauthToken").GetString();
        client.DefaultRequestHeaders.Remove("X-Reauth");
        client.DefaultRequestHeaders.Add("X-Reauth", proof);
    }

    public static object M(decimal amount, string currency = "JOD") => new { amount = amount.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), currency };

    public static string Open(this JsonElement invoiceDetail) => invoiceDetail.GetProperty("invoice").GetProperty("openBalance").GetProperty("amount").GetString()!;

    public static string Status(this JsonElement invoiceDetail) => invoiceDetail.GetProperty("invoice").GetProperty("status").GetString()!;

    public static string Settlement(this JsonElement invoiceDetail) => invoiceDetail.GetProperty("settlement").GetString()!;
}
