// Slice 2 through the real API: suppliers, orders, bills, paying suppliers, stock with average cost,
// cost of sales on invoices, counts, transfers, voids, VAT payable, roles and company isolation.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class BuyingAndStockTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : Json(body);
    }

    async Task<HttpClient> SignUp(string company)
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static async Task<Dictionary<string, long>> Balances(HttpClient c) =>
        (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .ToDictionary(a => a.GetProperty("systemKey").GetString() ?? a.GetProperty("code").GetString()!, a => a.GetProperty("balance").GetInt64());

    static async Task<JsonElement> StockOf(HttpClient c, Guid itemId) =>
        (await Ok(await c.GetAsync("/api/stock"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == itemId);

    static async Task<(Guid Supplier, Guid Item)> Basics(HttpClient c, decimal reorder = 10)
    {
        var supplier = await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Kibuye Bottles Ltd", phone = "0722 111 222", kraPin = "P059999999Z" }));
        var item = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Garden Chilli 250g", price = 40000, taxType = "B_Standard16", trackStock = true, reorderLevel = reorder }));
        return (supplier.GetProperty("id").GetGuid(), item.GetProperty("id").GetGuid());
    }

    static async Task<JsonElement> PostedBill(HttpClient c, Guid supplier, Guid item, decimal qty, long unitCostInclVat, string supplierRef = "KB-001")
    {
        var draft = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier, supplierRef, dueInDays = 30, lines = new[] { new { itemId = item, quantity = qty, unitCost = unitCostInclVat } } }));
        return await Ok(await c.PostAsync($"/api/purchases/{draft.GetProperty("id")}/post", null));
    }

    [Fact]
    public async Task Buying_selling_and_counting_keep_stock_cost_and_books_right()
    {
        var c = await SignUp("Stock Ltd");
        var (supplier, item) = await Basics(c);

        // 100 at KSh 116 incl. VAT = KSh 100 net each.
        var bill = await PostedBill(c, supplier, item, 100, 11600);
        Assert.Equal("Open", bill.GetProperty("status").GetString());
        Assert.Equal(1160000, bill.GetProperty("total").GetInt64());
        var s = await StockOf(c, item);
        Assert.Equal(100, s.GetProperty("total").GetDecimal());
        Assert.Equal(10000, s.GetProperty("averageCost").GetInt64());
        var b = await Balances(c);
        Assert.Equal(1000000, b["INVENTORY"]);
        Assert.Equal(160000, b["VAT_IN"]);
        Assert.Equal(-1160000, b["AP"]);

        // 50 more at KSh 200 net: average becomes (100×100 + 50×200) / 150 = 133.33.
        await PostedBill(c, supplier, item, 50, 23200, "KB-002");
        s = await StockOf(c, item);
        Assert.Equal(150, s.GetProperty("total").GetDecimal());
        Assert.Equal(13333, s.GetProperty("averageCost").GetInt64());

        // Pay part of the first bill by M-Pesa.
        var paid = await Ok(await c.PostAsJsonAsync($"/api/purchases/{bill.GetProperty("id")}/pay", new { method = "Mpesa", amount = 500000, reference = "SKP1Q2W3E4" }));
        Assert.Equal("PartPaid", paid.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/purchases/{bill.GetProperty("id")}/pay", new { method = "Mpesa", amount = 999999999 })).StatusCode);

        // Sell 30: stock goes down and 30 × 133.33 = KSh 4,000 moves to cost of sales.
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Naivas" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer.GetProperty("id").GetGuid(), lines = new[] { new { itemId = item, quantity = 30 } } }));
        await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));
        Assert.Equal(120, (await StockOf(c, item)).GetProperty("total").GetDecimal());
        b = await Balances(c);
        Assert.Equal(400000, b["COGS"]);
        Assert.Equal(1600000, b["INVENTORY"]);

        // Void it: the 30 come back and cost of sales returns to zero.
        await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/void", null));
        Assert.Equal(150, (await StockOf(c, item)).GetProperty("total").GetDecimal());
        b = await Balances(c);
        Assert.Equal(0, b["COGS"]);
        Assert.Equal(2000000, b["INVENTORY"]);

        // Count 145 in the main store: 5 lost, at average cost, to stock adjustments.
        var count = await Ok(await c.PostAsJsonAsync("/api/stock/count", new { counts = new[] { new { itemId = item, counted = 145 } }, note = "Month-end count" }));
        Assert.Equal(-5, Assert.Single(count.GetProperty("changes").EnumerateArray()).GetProperty("difference").GetDecimal());
        b = await Balances(c);
        Assert.Equal(66667, b["STOCK_ADJ"]);
        Assert.True((await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());

        // Moves history tells the whole story.
        var moves = await Ok(await c.GetAsync($"/api/stock/{item}/moves"));
        Assert.Equal(["Adjusted", "Returned", "Sold", "Received", "Received"], moves.EnumerateArray().Select(m => m.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Transfers_move_stock_between_stores_without_touching_the_books()
    {
        var c = await SignUp("Branches Ltd");
        var (supplier, item) = await Basics(c);
        await PostedBill(c, supplier, item, 40, 11600);
        var main = (await Ok(await c.GetAsync("/api/locations"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        var thika = (await Ok(await c.PostAsJsonAsync("/api/locations", new { name = "Thika branch" }))).GetProperty("id").GetGuid();
        var before = await Balances(c);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/stock/transfer", new { itemId = item, from = main, to = thika, quantity = 41 })).StatusCode);
        await Ok(await c.PostAsJsonAsync("/api/stock/transfer", new { itemId = item, from = main, to = thika, quantity = 15 }));

        var s = await StockOf(c, item);
        Assert.Equal(25, s.GetProperty("byLocation").GetProperty(main.ToString()).GetDecimal());
        Assert.Equal(15, s.GetProperty("byLocation").GetProperty(thika.ToString()).GetDecimal());
        Assert.Equal(before["INVENTORY"], (await Balances(c))["INVENTORY"]);
    }

    [Fact]
    public async Task Low_stock_shows_on_today_and_in_the_stock_list()
    {
        var c = await SignUp("Low Ltd");
        var (supplier, item) = await Basics(c, reorder: 20);
        await PostedBill(c, supplier, item, 12, 11600);
        var today = await Ok(await c.GetAsync("/api/today"));
        Assert.Equal(1, today.GetProperty("lowStock").GetProperty("count").GetInt32());
        var stock = await Ok(await c.GetAsync("/api/stock?filter=low"));
        Assert.Equal(1, stock.GetProperty("counts").GetProperty("low").GetInt32());
        Assert.Equal("low", Assert.Single(stock.GetProperty("items").EnumerateArray()).GetProperty("state").GetString());
        Assert.Equal(1, today.GetProperty("billsDue").GetProperty("count").GetInt32() + (today.GetProperty("weOwe").GetProperty("count").GetInt32() - 1));
    }

    [Fact]
    public async Task Vat_payable_is_vat_on_sales_less_vat_on_purchases()
    {
        var c = await SignUp("VAT Ltd");
        var (supplier, item) = await Basics(c);
        await PostedBill(c, supplier, item, 10, 11600); // KSh 160 input VAT
        await PostedBill(c, supplier, item, 1, 11600, supplierRef: ""); // claimable only with their invoice no.
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Buyer" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer.GetProperty("id").GetGuid(), lines = new[] { new { itemId = item, quantity = 5, unitPrice = 46400 } } }));
        await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null)); // KSh 320 output VAT
        var vat = await Ok(await c.GetAsync("/api/reports/vat?from=2000-01-01&to=2100-01-01"));
        Assert.Equal(32000, vat.GetProperty("totalTax").GetInt64());
        Assert.Equal(17600, vat.GetProperty("inputTax").GetInt64());
        Assert.Equal(14400, vat.GetProperty("payable").GetInt64());
        Assert.Single(vat.GetProperty("billsMissingSupplierInvoiceNo").EnumerateArray());
    }

    [Fact]
    public async Task Orders_become_bills_and_voided_bills_undo_stock_and_books()
    {
        var c = await SignUp("Orders Ltd");
        var (supplier, item) = await Basics(c);
        var order = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Order", supplierId = supplier, lines = new[] { new { itemId = item, quantity = 24, unitCost = 11600 } } }));
        Assert.StartsWith("PO-", order.GetProperty("number").GetString());
        await Ok(await c.PostAsync($"/api/purchases/{order.GetProperty("id")}/post", null));
        Assert.Equal(0, (await StockOf(c, item)).GetProperty("total").GetDecimal()); // an order moves nothing

        var bill = await Ok(await c.PostAsync($"/api/purchases/{order.GetProperty("id")}/receive", null));
        Assert.Equal("Draft", bill.GetProperty("status").GetString());
        Assert.Equal(278400, bill.GetProperty("total").GetInt64());
        await Ok(await c.PostAsync($"/api/purchases/{bill.GetProperty("id")}/post", null));
        Assert.Equal(24, (await StockOf(c, item)).GetProperty("total").GetDecimal());

        await Ok(await c.PostAsync($"/api/purchases/{bill.GetProperty("id")}/void", null));
        Assert.Equal(0, (await StockOf(c, item)).GetProperty("total").GetDecimal());
        var b = await Balances(c);
        Assert.Equal(0, b["INVENTORY"]);
        Assert.Equal(0, b["AP"]);
        Assert.Equal(0, b["VAT_IN"]);
    }

    [Fact]
    public async Task Bills_without_stock_go_to_the_chosen_expense()
    {
        var c = await SignUp("Expenses Ltd");
        var supplier = await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Kenya Power" }));
        var power = (await Ok(await c.GetAsync("/api/expense-accounts"))).EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Electricity and water").GetProperty("id").GetGuid();
        var draft = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier.GetProperty("id").GetGuid(), supplierRef = "KPLC-77", lines = new[] { new { accountId = power, description = "Tokens, October", quantity = 1, unitCost = 580000 } } }));
        await Ok(await c.PostAsync($"/api/purchases/{draft.GetProperty("id")}/post", null));
        var b = await Balances(c);
        Assert.Equal(500000, b["6500"]);
        Assert.Equal(80000, b["VAT_IN"]);
        var stockAccount = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray().Single(a => a.GetProperty("systemKey").GetString() == "AR").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier.GetProperty("id").GetGuid(), lines = new[] { new { accountId = stockAccount, description = "x", quantity = 1, unitCost = 100 } } })).StatusCode);
    }

    [Fact]
    public async Task Storekeepers_order_and_count_but_dont_enter_or_pay_bills()
    {
        var owner = await SignUp("Roles Stock Ltd");
        var (supplier, item) = await Basics(owner);
        var bill = await PostedBill(owner, supplier, item, 5, 11600);
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Store Keeper", email, phone = "0700 333 444", role = "Storekeeper", password = "storekeeper-pass" }));
        var sk = factory.CreateClient();
        var login = await TestSignIn.First(sk, email, "storekeeper-pass");
        sk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());

        await Ok(await sk.PostAsJsonAsync("/api/purchases", new { kind = "Order", supplierId = supplier, lines = new[] { new { itemId = item, quantity = 5, unitCost = 11600 } } }));
        await Ok(await sk.PostAsJsonAsync("/api/stock/count", new { counts = new[] { new { itemId = item, counted = 5 } } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await sk.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier, lines = new[] { new { itemId = item, quantity = 1, unitCost = 100 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sk.PostAsJsonAsync($"/api/purchases/{bill.GetProperty("id")}/pay", new { method = "Cash", amount = 100 })).StatusCode);
    }

    [Fact]
    public async Task Another_company_sees_none_of_it()
    {
        var a = await SignUp("Company A Stock");
        var (supplier, item) = await Basics(a);
        var bill = await PostedBill(a, supplier, item, 10, 11600);
        var b = await SignUp("Company B Stock");
        Assert.Equal(0, (await Ok(await b.GetAsync("/api/suppliers"))).GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(0, (await Ok(await b.GetAsync("/api/purchases"))).GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(0, (await Ok(await b.GetAsync("/api/stock"))).GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/purchases/{bill.GetProperty("id")}")).StatusCode);
        Assert.Empty((await Ok(await b.GetAsync($"/api/stock/{item}/moves"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/stock/count", new { counts = new[] { new { itemId = item, counted = 0 } } })).StatusCode);
    }
}
