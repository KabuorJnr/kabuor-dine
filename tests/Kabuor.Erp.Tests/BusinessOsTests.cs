// The business as one system: stock for sale and stores kept apart in the books and on the till; a budget only finance
// can approve, and only someone other than the one who prepared it; tools that can be out on one job at a time; the
// chain and foresight read across the modules; and the forecaster that picks the model its own history favours.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Intelligence;

namespace Kabuor.Erp.Tests;

public class BusinessOsTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"OS {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    async Task<HttpClient> As(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, phone = "0700 111 222", role, password = "role-password-1" }));
        var c = factory.CreateClient();
        var login = await TestSignIn.First(c, email, "role-password-1");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
        return c;
    }

    static async Task<Dictionary<string, long>> Balances(HttpClient c) =>
        (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());

    [Fact]
    public async Task Stores_are_kept_apart_from_stock_for_sale_and_never_sold()
    {
        var owner = await Owner();
        var bearing = (await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Bearing 6205", unit = "pc", price = 0, trackStock = true, use = "Stores", storesCategory = "Spares" }))).GetProperty("id").GetGuid();
        var soap = (await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Bar soap", unit = "pc", price = 100_00, trackStock = true }))).GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = bearing, quantity = 10, unitCost = 800_00 }));
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = soap, quantity = 20, unitCost = 50_00 }));
        var books = await Balances(owner);
        Assert.Equal(8_000_00, books["STORES"]);
        Assert.Equal(1_000_00, books["INVENTORY"]);

        // The stock page shows each side on its own.
        var stores = await Ok(await owner.GetAsync("/api/stock?use=stores"));
        Assert.Equal("Bearing 6205", stores.GetProperty("items").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal(8_000_00, stores.GetProperty("storesValue").GetInt64());
        Assert.Equal(1, stores.GetProperty("uses").GetProperty("sale").GetInt32());

        // A spare can't go on an invoice, and the till doesn't offer it.
        var customer = (await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Walk-in" }))).GetProperty("id").GetGuid();
        var invoice = await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { itemId = bearing, description = "Bearing", quantity = 1, unitPrice = 1_000_00 } } });
        Assert.Equal(HttpStatusCode.BadRequest, invoice.StatusCode);
        Assert.Contains("own use", await invoice.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Bearing 6205", (await owner.GetStringAsync("/api/items?use=Sale")));

        // Turning soap into stores (the cleaners use it now) moves its value between the accounts.
        await Ok(await owner.PutAsJsonAsync($"/api/items/{soap}", new { name = "Bar soap", unit = "pc", price = 100_00, trackStock = true, use = "Stores", storesCategory = "Cleaning" }));
        books = await Balances(owner);
        Assert.Equal(9_000_00, books["STORES"]);
        Assert.Equal(0, books["INVENTORY"]);
        Assert.True((await Ok(await owner.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }

    [Fact]
    public async Task Whoever_prepares_a_budget_cannot_approve_it_and_a_revision_replaces_the_one_in_force()
    {
        var owner = await Owner();
        var dept = (await Ok(await owner.PostAsJsonAsync("/api/departments", new { name = "Engineering" }))).GetProperty("id").GetGuid();
        var year = DateTime.UtcNow.AddHours(3).Year;
        var accountant = await As(owner, "Accountant");
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.PostAsJsonAsync("/api/budgets", new { departmentId = dept, year, months = new long[12], justification = "Nothing at all here" })).StatusCode);
        var b = (await Ok(await accountant.PostAsJsonAsync("/api/budgets", new { departmentId = dept, year, months = Enumerable.Repeat(50_000_00L, 12), justification = "Spares, tools and contractors", submit = true }))).GetProperty("budget");
        var id = b.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/budgets/{id}/approve", new { })).StatusCode); // maker isn't checker
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/budgets/{id}/reject", new { })).StatusCode); // a reason is needed
        await Ok(await owner.PostAsJsonAsync($"/api/budgets/{id}/approve", new { }));

        // A revision doesn't touch the budget in force until it's approved too.
        var rev = (await Ok(await accountant.PostAsJsonAsync("/api/budgets", new { departmentId = dept, year, months = Enumerable.Repeat(60_000_00L, 12), justification = "Second aircraft tug needs servicing", submit = true }))).GetProperty("budget");
        Assert.True(rev.GetProperty("isRevision").GetBoolean());
        var view = await Ok(await owner.GetAsync($"/api/budgets?year={year}"));
        var row = view.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("departmentId").GetGuid() == dept);
        Assert.Equal(600_000_00, row.GetProperty("annual").GetInt64());
        await Ok(await owner.PostAsJsonAsync($"/api/budgets/{rev.GetProperty("id")}/approve", new { }));
        view = await Ok(await owner.GetAsync($"/api/budgets?year={year}"));
        row = view.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("departmentId").GetGuid() == dept);
        Assert.Equal(720_000_00, row.GetProperty("annual").GetInt64());
        var old = await Ok(await owner.GetAsync($"/api/budgets/{id}"));
        Assert.Equal("Replaced", old.GetProperty("budget").GetProperty("status").GetString());
        Assert.True((await Ok(await owner.GetAsync("/api/approvals/verify"))).GetProperty("intact").GetBoolean());
    }

    [Fact]
    public async Task A_tool_is_out_on_one_job_at_a_time_and_a_damaged_one_is_reported()
    {
        var owner = await Owner();
        var wrench = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Torque wrench 3/4in", category = "TLS" }))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var press = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Hydraulic press", category = "EQP" }))).GetProperty("items")[0].GetProperty("id").GetGuid();
        async Task<Guid> Job(string title) => (await Ok(await owner.PostAsJsonAsync("/api/maintenance/work-orders", new { title, type = "Corrective", assetId = press, assignedTo = "Peter Otieno" }))).GetProperty("id").GetGuid();
        var a = await Job("Replace ram seals");
        var b = await Job("Re-torque base bolts");
        var onA = await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{a}/tools", new { assetId = wrench }));
        var toolA = onA.GetProperty("tools")[0].GetProperty("id").GetGuid();
        var onB = await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{b}/tools", new { assetId = wrench }));
        var toolB = onB.GetProperty("tools")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{a}/tools", new { assetId = press })).StatusCode); // the asset being fixed isn't a tool

        await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{a}/tools/{toolA}/out", new { to = "Peter Otieno" }));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{b}/tools/{toolB}/out", new { to = "Jane Wambui" })).StatusCode);
        var detailB = await Ok(await owner.GetAsync($"/api/maintenance/work-orders/{b}"));
        Assert.False(detailB.GetProperty("readiness").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "tools").GetProperty("ok").GetBoolean());

        // Job A can't be completed with the wrench still out; it comes back damaged and is reported straight away.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{a}/actions", new { kind = "complete", failureMode = "Leaking", remedy = "Seals replaced" })).StatusCode);
        await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{a}/tools/{toolA}/back", new { condition = "Damaged", note = "Ratchet slipping" }));
        await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{a}/actions", new { kind = "complete", failureMode = "Leaking", remedy = "Seals replaced" }));
        var faults = await Ok(await owner.GetAsync("/api/maintenance/faults?status=all"));
        Assert.Contains(faults.GetProperty("items").EnumerateArray(), f => f.GetProperty("title").GetString()!.Contains("Torque wrench"));
    }

    [Fact]
    public async Task The_chain_and_foresight_read_across_the_modules()
    {
        var owner = await Owner();
        await Ok(await owner.PostAsJsonAsync("/api/departments", new { name = "Flight operations" }));
        var chain = await Ok(await owner.GetAsync("/api/os/chain"));
        var keys = chain.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("key").GetString()).ToList();
        Assert.Equal(["assets", "maintenance", "supply", "people", "sales", "marketing", "finance", "decide"], keys);
        var finance = chain.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("key").GetString() == "finance");
        Assert.Contains(finance.GetProperty("blockers").EnumerateArray(), b => b.GetString()!.Contains("no approved budget"));
        var foresight = await Ok(await owner.GetAsync("/api/os/foresight"));
        Assert.Equal(JsonValueKind.Null, foresight.GetProperty("money")[0].GetProperty("forecast").ValueKind); // a new business has no history yet
        // A cashier doesn't see the whole business.
        var cashier = await As(owner, "Sales");
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/os/chain")).StatusCode);
    }

    [Fact]
    public async Task Quotes_promise_only_what_is_or_will_be_in_stock_and_prices_come_from_the_price_list()
    {
        var owner = await Owner();
        var drum = (await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Diesel drum 200L", unit = "drum", price = 30_000_00, trackStock = true }))).GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = drum, quantity = 5, unitCost = 25_000_00 }));
        var customer = (await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Rift Haulage" }))).GetProperty("id").GetGuid();
        // 8 drums: only 5 on hand and nothing coming, so no.
        var refused = await owner.PostAsJsonAsync("/api/sales", new { kind = "Quote", customerId = customer, lines = new[] { new { itemId = drum, quantity = 8 } } });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("5 in stock", await refused.Content.ReadAsStringAsync());
        // 5 more on order: now it can be promised.
        var supplier = (await Ok(await owner.PostAsJsonAsync("/api/suppliers", new { name = "Fuel Depot" }))).GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync("/api/purchases", new { kind = "Order", supplierId = supplier, lines = new[] { new { itemId = drum, quantity = 5, unitCost = 25_000_00 } } }));
        await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Quote", customerId = customer, lines = new[] { new { itemId = drum, quantity = 8 } } }));

        // A salesperson can't type their own price; without one, the price list's price is used.
        var sales = await As(owner, "Sales");
        var cut = await sales.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { itemId = drum, quantity = 1, unitPrice = 20_000_00 } } });
        Assert.Equal(HttpStatusCode.Forbidden, cut.StatusCode);
        var fair = await Ok(await sales.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { itemId = drum, quantity = 1 } } }));
        Assert.Equal(30_000_00, fair.GetProperty("lines")[0].GetProperty("unitPrice").GetInt64());
        // The owner can give a special price.
        var special = await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { itemId = drum, quantity = 1, unitPrice = 28_000_00 } } }));
        Assert.Equal(28_000_00, special.GetProperty("lines")[0].GetProperty("unitPrice").GetInt64());
    }

    [Fact]
    public async Task A_sale_can_be_traced_from_quote_to_invoice_to_stock_and_the_books()
    {
        var owner = await Owner();
        var tyre = (await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Tyre 295/80R22.5", unit = "pc", price = 45_000_00, trackStock = true }))).GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = tyre, quantity = 10, unitCost = 32_000_00 }));
        var customer = (await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Mombasa Movers" }))).GetProperty("id").GetGuid();
        var quote = await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Quote", customerId = customer, lines = new[] { new { itemId = tyre, quantity = 4 } } }));
        var invoice = await Ok(await owner.PostAsync($"/api/sales/{quote.GetProperty("id")}/to-invoice", null));
        await Ok(await owner.PostAsync($"/api/sales/{invoice.GetProperty("id")}/issue", null));
        var trail = await Ok(await owner.GetAsync($"/api/trace/invoice/{invoice.GetProperty("id")}"));
        var stages = trail.GetProperty("stages").EnumerateArray().Select(s => s.GetProperty("stage").GetString()).ToList();
        Assert.Equal(["Quote", "Invoice", "Stock", "Books"], stages);
        // From the quote, the same trail.
        var back = await Ok(await owner.GetAsync($"/api/trace/quote/{quote.GetProperty("id")}"));
        Assert.Equal(trail.GetProperty("count").GetInt32(), back.GetProperty("count").GetInt32());
    }

    [Fact]
    public void The_forecaster_picks_the_model_the_history_favours_and_says_how_accurate_it_was()
    {
        // A steady climb: the trend wins and is nearly exact.
        var climb = Enumerable.Range(1, 14).Select(i => 100_000.0 + i * 10_000).ToArray();
        var f = Forecaster.Run(climb)!;
        Assert.Contains(f.Model, new[] { "Trend", "Smoothed trend" });
        Assert.True(f.AccuracyPct >= 95);
        Assert.InRange(f.Forecast[0], 235_000, 255_000);
        // A flat line with noise: no model is fooled into a trend that isn't there.
        var flat = new[] { 50.0, 52, 49, 51, 50, 48, 52, 50, 51, 49 };
        var g = Forecaster.Run(flat)!;
        Assert.InRange(g.Forecast[0], 46, 54);
        // Too little history: no forecast rather than a made-up one.
        Assert.Null(Forecaster.Run([0, 0, 10, 12, 11]));
    }
}
