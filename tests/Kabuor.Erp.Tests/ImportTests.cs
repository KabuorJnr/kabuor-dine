// Moving in from QuickBooks / Sage / Excel: the usual column names are understood, a preview shows each row's
// problem before anything is saved, duplicates are skipped, and opening balances land in equity, not in sales or VAT.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class ImportTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Moving In Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X", vatRegistered = true }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    // A QuickBooks Online customer list export.
    const string QuickBooksCustomers = """
        Customer,Phone Numbers,Email,Tax Reg No,Open Balance
        Naivas Westlands,0722 111 333,ap@naivas.test,P000111222A,"21,100.00"
        Wanjiku Stores,0722111444,,,0.00
        Naivas Westlands,0722 111 333,,,"5.00"
        Bad Pin Ltd,,,12345,"1,000.00"
        """;

    [Fact]
    public async Task Customers_from_QuickBooks_preview_first_then_save_with_opening_balances_in_equity()
    {
        var c = await Owner();
        var preview = await Ok(await c.PostAsJsonAsync("/api/import/customers", new { csv = QuickBooksCustomers, commit = false, asAt = "2026-09-30" }));
        Assert.False(preview.GetProperty("saved").GetBoolean());
        Assert.Equal(3, preview.GetProperty("adding").GetInt32());
        Assert.Equal(1, preview.GetProperty("skipping").GetInt32()); // Naivas twice
        Assert.Equal(22_100_00, preview.GetProperty("openingBalances").GetInt64());
        Assert.Equal(0, (await Ok(await c.GetAsync("/api/customers"))).GetProperty("counts").GetProperty("all").GetInt32()); // nothing saved yet

        await Ok(await c.PostAsJsonAsync("/api/import/customers", new { csv = QuickBooksCustomers, commit = true, asAt = "2026-09-30" }));
        var customers = await Ok(await c.GetAsync("/api/customers"));
        Assert.Equal(3, customers.GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(2, customers.GetProperty("counts").GetProperty("owing").GetInt32());
        // Again: everything is already here.
        Assert.Equal(0, (await Ok(await c.PostAsJsonAsync("/api/import/customers", new { csv = QuickBooksCustomers, commit = true }))).GetProperty("adding").GetInt32());

        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.Equal(22_100_00, accounts["AR"]);
        Assert.Equal(-22_100_00, accounts["EQUITY"]);
        Assert.Equal(0, accounts.GetValueOrDefault("SALES"));
        var vat = await Ok(await c.GetAsync("/api/reports/vat?from=2026-09-01&to=2026-09-30"));
        Assert.Equal(0, vat.GetProperty("totalSales").GetInt64());
        // The opening balance can be paid like any invoice.
        var naivas = customers.GetProperty("page").GetProperty("items").EnumerateArray().First(x => x.GetProperty("name").GetString() == "Naivas Westlands");
        Assert.Equal(21_100_00, naivas.GetProperty("owes").GetInt64());
    }

    [Fact]
    public async Task Products_from_a_Sage_export_bring_their_stock_at_cost()
    {
        var c = await Owner();
        const string csv = """
            Item Code,Description,Selling Price,Average Cost,Qty On Hand,Tax,Type
            CS250,Chilli Sauce 250ml,"350.00","180.00",120,VAT 16%,Inventory
            BR,Bread,"70.00","45.00",30,8%,Inventory
            DEL,Delivery Nairobi,"300.00",,,Exempt,Service
            """;
        var r = await Ok(await c.PostAsJsonAsync("/api/import/items", new { csv, commit = true }));
        Assert.Equal(3, r.GetProperty("adding").GetInt32());
        Assert.Equal(120 * 180_00 + 30 * 45_00, r.GetProperty("stockValue").GetInt64());
        var stock = await Ok(await c.GetAsync("/api/stock"));
        var body = stock.ToString();
        Assert.Contains("Chilli Sauce 250ml", body);
        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.Equal(120 * 180_00 + 30 * 45_00, accounts["INVENTORY"]);
        var items = (await Ok(await c.GetAsync("/api/items"))).ToString();
        Assert.Contains("E_Reduced8", items);
        Assert.Contains("A_Exempt", items);
    }
}
