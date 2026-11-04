// The whole first slice through the real HTTP API, on a throwaway SQLite database:
// sign up → customer → item → invoice → issue (books + eTIMS) → M-Pesa arrives and matches by itself →
// the books balance; plus company isolation and role permissions.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kabuor.Erp.Tests;

/// <summary>The app on a throwaway database: a SQLite file by default, or a fresh PostgreSQL database when
/// ERP_TEST_PG is set (e.g. "Host=localhost;Username=postgres;Password=postgres"), as in CI. On PostgreSQL the
/// schema comes from the EF migrations, so every test also checks the migrations.</summary>
public class ErpFactory : WebApplicationFactory<Program>
{
    public static readonly string? Pg = Environment.GetEnvironmentVariable("ERP_TEST_PG") is { Length: > 0 } pg ? pg : null;
    readonly string _name = $"kabuor_erp_test_{Guid.NewGuid():N}";
    string Sqlite => Path.Combine(Path.GetTempPath(), $"{_name}.db");
    public string ConnectionString => Pg is null ? $"Data Source={Sqlite}" : $"{Pg};Database={_name}";
    public const string JwtKey = "test-key-test-key-test-key-test-key-0123456789";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Erp", ConnectionString);
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Agents:Disabled", "true"); // tests run agents explicitly
        builder.UseSetting("Owners:OversightByDefault", "false"); // most tests have the owner do the work; OwnerOversightTests turns it on
        builder.UseSetting("Retention:Disabled", "true"); // and clean-ups
        builder.UseSetting("Backfill:Disabled", "true"); // and one-off backfills
        // Most tests start working straight after signing up; paying to start has its own tests (PayToStartTests).
        builder.UseSetting("Billing:PayToStart", PayToStart ? "true" : "false");
    }

    protected virtual bool PayToStart => false;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Pg is null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(Sqlite); } catch (IOException) { }
            return;
        }
        Npgsql.NpgsqlConnection.ClearAllPools();
        using var conn = new Npgsql.NpgsqlConnection($"{Pg};Database=postgres");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}

public class ApiFlowTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    async Task<(HttpClient Client, JsonElement Session)> SignUp(string company, string? pin = "P051234567X")
    {
        var c = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.com";
        var res = await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Akinyi Owner", email, phone = "0712345678", password = "a-long-password", kraPin = pin });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        var session = Json(body);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());
        return (c, session);
    }

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : Json(body);
    }

    [Fact]
    public async Task Invoice_paid_by_mpesa_matches_itself_and_the_books_balance()
    {
        var (c, _) = await SignUp("Mama Pili Sauces Ltd");
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Wanjiku Stores", phone = "0722 000 111", kraPin = "P051111111A" }));
        var item = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Garden Chilli 250g", price = 116000, taxType = "B_Standard16" }));

        var draft = await Ok(await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = customer.GetProperty("id").GetGuid(),
            lines = new[] { new { itemId = item.GetProperty("id").GetGuid(), quantity = 2 } },
        }));
        Assert.Equal("INV-0001", draft.GetProperty("number").GetString());
        Assert.Equal(232000, draft.GetProperty("total").GetInt64());
        Assert.Equal(32000, draft.GetProperty("tax").GetInt64());

        var issued = await Ok(await c.PostAsync($"/api/sales/{draft.GetProperty("id")}/issue", null));
        Assert.Equal("Sent", issued.GetProperty("status").GetString());
        Assert.Equal("Signed", issued.GetProperty("etimsStatus").GetString());
        Assert.StartsWith("TEST-", issued.GetProperty("etimsControlCode").GetString());

        // Safaricom confirms a Paybill payment with the invoice number typed as the account.
        var settings = await Ok(await c.GetAsync("/api/settings/company"));
        var url = new Uri(settings.GetProperty("mpesaConfirmationUrl").GetString()!).PathAndQuery;
        var anon = factory.CreateClient();
        var c2b = new { TransID = "SJK4H7L2PQ", TransAmount = "2320.00", TransTime = "20261020103000", BusinessShortCode = "", BillRefNumber = "inv1", MSISDN = "254722000111", FirstName = "WANJIKU" };
        var ack = await Ok(await anon.PostAsJsonAsync(url, c2b));
        Assert.Equal(0, ack.GetProperty("ResultCode").GetInt32());
        await Ok(await anon.PostAsJsonAsync(url, c2b)); // Safaricom retries: must not double count

        var inv = await Ok(await c.GetAsync($"/api/sales/{draft.GetProperty("id")}"));
        Assert.Equal("Paid", inv.GetProperty("document").GetProperty("status").GetString());
        Assert.Equal(1, inv.GetProperty("payments").GetArrayLength());
        var payments = await Ok(await c.GetAsync("/api/payments"));
        Assert.Equal(1, payments.GetProperty("counts").GetProperty("all").GetInt32());

        var tb = await Ok(await c.GetAsync("/api/reports/trial-balance"));
        Assert.True(tb.GetProperty("balanced").GetBoolean());
        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray().ToDictionary(a => a.GetProperty("systemKey").GetString() ?? a.GetProperty("code").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.Equal(232000, accounts["MPESA"]);
        Assert.Equal(0, accounts["AR"]);
        Assert.Equal(0, accounts["UNAPPLIED"]);
        Assert.Equal(-32000, accounts["VAT_OUT"]);
        Assert.Equal(-200000, accounts["SALES"]);

        var vat = await Ok(await c.GetAsync("/api/reports/vat?from=2000-01-01&to=2100-01-01"));
        Assert.Equal(32000, vat.GetProperty("totalTax").GetInt64());

        var audit = await Ok(await c.GetAsync("/api/audit?entity=SalesDoc"));
        Assert.True(audit.GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Money_that_arrives_before_the_invoice_is_matched_when_it_is_issued()
    {
        var (c, _) = await SignUp("Kerio Honey");
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Baraka Grocers", phone = "0733 444 555" }));
        var paid = await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 65000, reference = "SKA1B2C3D4", payerPhone = "0733444555" }));
        Assert.Equal("Unmatched", paid.GetProperty("status").GetString());
        var today = await Ok(await c.GetAsync("/api/today"));
        Assert.Equal(1, today.GetProperty("toMatch").GetProperty("count").GetInt32());

        var draft = await Ok(await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice",
            customerId = customer.GetProperty("id").GetGuid(),
            lines = new[] { new { description = "Raw honey 1kg", quantity = 1, unitPrice = 65000, taxType = "A_Exempt" } },
        }));
        var issued = await Ok(await c.PostAsync($"/api/sales/{draft.GetProperty("id")}/issue", null));
        Assert.Equal("Paid", issued.GetProperty("status").GetString());
        Assert.Equal(0, (await Ok(await c.GetAsync("/api/today"))).GetProperty("toMatch").GetProperty("count").GetInt32());
        Assert.True((await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }

    [Fact]
    public async Task One_company_can_never_see_anothers_data()
    {
        var (a, _) = await SignUp("Company A");
        var (b, _) = await SignUp("Company B");
        var secret = await Ok(await a.PostAsJsonAsync("/api/customers", new { name = "A's best customer", phone = "0711 222 333" }));

        Assert.Equal(0, (await Ok(await b.GetAsync("/api/customers"))).GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/customers/{secret.GetProperty("id")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/customers/{secret.GetProperty("id")}", new { name = "hijacked" })).StatusCode);
        // Same phone in another company is fine: numbers are per company.
        await Ok(await b.PostAsJsonAsync("/api/customers", new { name = "B's own customer", phone = "0711 222 333" }));
    }

    [Fact]
    public async Task Roles_limit_what_people_can_do()
    {
        var (owner, _) = await SignUp("Roles Ltd");
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Juma Sales", email, phone = "0700 111 222", role = "Sales", password = "another-long-password" }));
        var sales = factory.CreateClient();
        var login = await TestSignIn.First(sales, email, "another-long-password");
        sales.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());

        await Ok(await sales.PostAsJsonAsync("/api/customers", new { name = "Walk-in customer" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/reports/trial-balance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/settings/company")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/today")).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_account_for_a_while()
    {
        var (_, session) = await SignUp("Lockout Ltd");
        var c = factory.CreateClient();
        var email = session.GetProperty("user").GetProperty("email").GetString();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = "wrong-password-x" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password" })).StatusCode);
    }

    [Fact]
    public async Task Issued_invoices_cant_be_edited_and_void_reverses_the_books()
    {
        var (c, _) = await SignUp("Void Ltd");
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Some Buyer" }));
        var body = new { kind = "Invoice", customerId = customer.GetProperty("id").GetGuid(), lines = new[] { new { description = "Consulting", quantity = 1, unitPrice = 1160000 } } };
        var draft = await Ok(await c.PostAsJsonAsync("/api/sales", body));
        var id = draft.GetProperty("id");
        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/sales/{id}", body)).StatusCode);
        await Ok(await c.PostAsync($"/api/sales/{id}/void", null));
        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray().ToDictionary(a => a.GetProperty("code").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.All(accounts.Values, v => Assert.Equal(0, v));
    }
}
