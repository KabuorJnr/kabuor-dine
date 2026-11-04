// Distributor depth: a customer's price list and bigger units set invoice prices; batches go out first-to-expire
// and expired stock is never sold; a credit limit stops an invoice unless the owner or accountant allows it.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class TradeTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Trade Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    static async Task<decimal> OnHand(HttpClient c, Guid item) =>
        (await Ok(await c.GetAsync("/api/stock"))).GetProperty("items").EnumerateArray().First(i => Id(i) == item).GetProperty("total").GetDecimal();

    [Fact]
    public async Task Price_lists_and_cartons_set_the_price_and_cartons_move_stock_in_singles()
    {
        var c = await Owner();
        var soda = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Soda 500ml", unit = "bottle", price = 100_00, trackStock = true })));
        await Ok(await c.PutAsJsonAsync($"/api/items/{soda}/units", new[] { new { name = "Crate", factor = 24, price = 2_200_00 } }));
        await Ok(await c.PostAsJsonAsync("/api/stock/opening", new { itemId = soda, quantity = 100, unitCost = 50_00 }));
        var wholesale = Id(await Ok(await c.PostAsJsonAsync("/api/price-lists", new { name = "Wholesale", prices = new[] { new { itemId = soda, price = 90_00 } } })));
        var shop = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Mama Mboga Kiosk", priceListId = wholesale })));
        var walkIn = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Hotel Jambo" })));

        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = shop, lines = new object[] { new { itemId = soda, quantity = 3 }, new { itemId = soda, quantity = 2, unit = "crate" } } }));
        var lines = inv.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(90_00, lines[0].GetProperty("unitPrice").GetInt64());          // the list price
        Assert.Equal(90_00 * 24, lines[1].GetProperty("unitPrice").GetInt64());     // the list price per bottle × 24
        Assert.Equal("Crate", lines[1].GetProperty("unit").GetString());
        await Ok(await c.PostAsync($"/api/sales/{Id(inv)}/issue", null));
        Assert.Equal(100 - 3 - 48, await OnHand(c, soda));

        var hotel = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = walkIn, lines = new[] { new { itemId = soda, quantity = 1, unit = "Crate" } } }));
        Assert.Equal(2_200_00, hotel.GetProperty("lines")[0].GetProperty("unitPrice").GetInt64()); // the crate's own price
        var wrong = await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = walkIn, lines = new[] { new { itemId = soda, quantity = 1, unit = "pallet" } } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task Batches_go_out_first_to_expire_and_expired_stock_is_refused()
    {
        var c = await Owner();
        var milk = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Milk 500ml", unit = "packet", price = 60_00, trackStock = true, trackBatches = true })));
        var supplier = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Dairy Co-op" })));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        // An order doesn't know batches yet; a bill can't be posted without them.
        await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Order", supplierId = supplier, lines = new[] { new { itemId = milk, quantity = 5, unitCost = 40_00 } } }));
        var noBatch = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier, lines = new[] { new { itemId = milk, quantity = 5, unitCost = 40_00 } } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/purchases/{Id(noBatch)}/post", null)).StatusCode);
        var bill = await Ok(await c.PostAsJsonAsync("/api/purchases", new
        {
            kind = "Bill", supplierId = supplier, lines = new object[]
            {
                new { itemId = milk, quantity = 10, unitCost = 40_00, batchNo = "LATE", expiry = today.AddDays(100) },
                new { itemId = milk, quantity = 10, unitCost = 40_00, batchNo = "SOON", expiry = today.AddDays(10) },
                new { itemId = milk, quantity = 5, unitCost = 40_00, batchNo = "OLD", expiry = today.AddDays(-1) },
            },
        }));
        await Ok(await c.PostAsync($"/api/purchases/{Id(bill)}/post", null));

        var expiring = await Ok(await c.GetAsync("/api/stock/expiring?days=30"));
        Assert.Equal(1, expiring.GetProperty("expired").GetInt32());
        Assert.Equal(["OLD", "SOON"], expiring.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("batchNo").GetString()!).ToArray());

        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Corner Shop" })));
        var sale = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = milk, quantity = 15 } } }));
        await Ok(await c.PostAsync($"/api/sales/{Id(sale)}/issue", null));
        var left = (await Ok(await c.GetAsync("/api/stock/expiring?days=365"))).GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("batchNo").GetString()!, r => r.GetProperty("quantity").GetDecimal());
        Assert.False(left.ContainsKey("SOON")); // sold first
        Assert.Equal(5, left["LATE"]);
        Assert.Equal(5, left["OLD"]);           // the expired batch was not sold

        var tooMuch = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = milk, quantity = 6 } } }));
        var refused = await c.PostAsync($"/api/sales/{Id(tooMuch)}/issue", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("expired", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Draft", (await Ok(await c.GetAsync($"/api/sales/{Id(tooMuch)}"))).GetProperty("document").GetProperty("status").GetString());

        // The expired batch is thrown away: it leaves stock and the list.
        var old = (await Ok(await c.GetAsync("/api/stock/expiring?days=365"))).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("batchNo").GetString() == "OLD");
        var off = await Ok(await c.PostAsJsonAsync($"/api/stock/batches/{Id(old)}/write-off", new { }));
        Assert.Equal(5 * 40_00, off.GetProperty("value").GetInt64());
        Assert.Equal(0, (await Ok(await c.GetAsync("/api/stock/expiring?days=365"))).GetProperty("expired").GetInt32());
        Assert.Equal(5, await OnHand(c, milk));

        // Voiding the first sale puts each batch back.
        await Ok(await c.PostAsync($"/api/sales/{Id(sale)}/void", null));
        left = (await Ok(await c.GetAsync("/api/stock/expiring?days=365"))).GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("batchNo").GetString()!, r => r.GetProperty("quantity").GetDecimal());
        Assert.Equal(10, left["SOON"]);
        Assert.Equal(10, left["LATE"]);
        Assert.Equal(20, await OnHand(c, milk));
    }

    [Fact]
    public async Task A_credit_limit_stops_the_invoice_unless_the_owner_allows_it()
    {
        var c = await Owner();
        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Slow Payer Ltd", creditLimit = 1_000_00 })));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { description = "Consulting", quantity = 1, unitPrice = 1_500_00 } } }));
        var blocked = await c.PostAsync($"/api/sales/{Id(inv)}/issue", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("limit", await blocked.Content.ReadAsStringAsync());

        // A salesperson can't wave it through.
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await c.PostAsJsonAsync("/api/settings/users", new { name = "Sales Person", email, role = "Sales", password = "another-long-password" }));
        var sales = factory.CreateClient();
        var s = await TestSignIn.First(sales, email, "another-long-password");
        sales.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsync($"/api/sales/{Id(inv)}/issue?overLimit=true", null)).StatusCode);

        await Ok(await c.PostAsync($"/api/sales/{Id(inv)}/issue?overLimit=true", null));
    }
    [Fact]
    public async Task Expiry_is_judged_on_the_sale_date_and_the_owner_can_forbid_selling_below_zero()
    {
        var c = await Owner();
        var juice = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Juice 1L", unit = "carton", price = 220_00, trackStock = true, trackBatches = true })));
        var supplier = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Juice Co" })));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var bill = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier, issueDate = today.AddDays(-20), lines = new[] { new { itemId = juice, quantity = 10, unitCost = 150_00, batchNo = "J1", expiry = today.AddDays(-5) } } }));
        await Ok(await c.PostAsync($"/api/purchases/{Id(bill)}/post", null));
        var cust = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Cafe" })));

        // Sold ten days ago, when the batch was still good: allowed.
        var past = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, issueDate = today.AddDays(-10), lines = new[] { new { itemId = juice, quantity = 4 } } }));
        await Ok(await c.PostAsync($"/api/sales/{Id(past)}/issue", null));
        // Today it has expired: refused.
        var now = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = juice, quantity = 1 } } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/sales/{Id(now)}/issue", null)).StatusCode);

        // An invoice is for what's on the shelf: selling more than is there is refused, whatever the settings.
        var soap = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Soap", price = 100_00, trackStock = true })));
        await Ok(await c.PostAsJsonAsync("/api/stock/opening", new { itemId = soap, quantity = 2, unitCost = 60_00 }));
        var over = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = soap, quantity = 3 } } }));
        var r = await c.PostAsync($"/api/sales/{Id(over)}/issue", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("in stock", await r.Content.ReadAsStringAsync());
    }
}
