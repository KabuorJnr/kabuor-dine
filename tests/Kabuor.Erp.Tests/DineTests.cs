// Kabuor Dine: a restaurant signs up into its own product, a guest orders from a table's QR, a waiter confirms the
// table, the kitchen and bar each mark their part ready, and the bill (an ordinary invoice, in the books) is paid at
// the table. Pay-first restaurants only cook what's paid for, and one restaurant's tables never see another's.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class DineTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient Owner, JsonElement Session)> Restaurant(string name = "Ziwa Grill")
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"{name} {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", product = "dine" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    HttpClient Guest(string? token = null)
    {
        var g = factory.CreateClient();
        if (token is not null) g.DefaultRequestHeaders.Add("X-Dine-Token", token);
        return g;
    }

    static Guid Dish(JsonElement menu, string name) =>
        menu.GetProperty("menu").EnumerateArray().First(i => i.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    static JsonElement TableOnFloor(JsonElement floor, string name) =>
        floor.GetProperty("tables").EnumerateArray().First(t => t.GetProperty("name").GetString() == name);

    [Fact]
    public async Task A_guest_orders_from_the_table_the_kitchen_makes_it_and_the_bill_lands_in_the_books()
    {
        var (owner, session) = await Restaurant();
        var company = session.GetProperty("company");
        Assert.Equal("dine", company.GetProperty("product").GetString());
        Assert.False(company.GetProperty("ownerOversight").GetBoolean()); // the owner runs the floor too
        Assert.Contains("dine", session.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // Six tables with QR codes from the start, and a sample menu in one click.
        var tables = (await Ok(await owner.GetAsync("/api/dine/tables"))).EnumerateArray().ToList();
        Assert.Equal(6, tables.Count);
        var qr = tables[0].GetProperty("token").GetString()!;
        Assert.True((await Ok(await owner.PostAsync("/api/dine/menu/sample", null))).GetProperty("added").GetInt32() >= 10);

        // The guest scans the QR: the restaurant, the table and the menu, no sign-in.
        var guest = Guest();
        var menu = await Ok(await guest.GetAsync($"/api/dine/m/{qr}"));
        Assert.Equal("Table 1", menu.GetProperty("table").GetProperty("name").GetString());
        Assert.Equal("Tab", menu.GetProperty("payMode").GetString());
        var choma = Dish(menu, "Nyama choma (½ kg)");
        var juice = Dish(menu, "Fresh juice");

        // They order, with their preferences. It's the table's first order, so it waits for a waiter.
        var placed = await Ok(await guest.PostAsJsonAsync($"/api/dine/m/{qr}/orders", new
        {
            guestName = "Achieng",
            lines = new object[] { new { itemId = choma, quantity = 1, options = "Doneness: Well done · Side: Ugali", note = "No chilli" }, new { itemId = juice, quantity = 2, options = "Fruit: Passion" } },
        }));
        Assert.Equal("AwaitingApproval", placed.GetProperty("order").GetProperty("status").GetString());
        var token = placed.GetProperty("token").GetString()!;
        Assert.Equal(0, (await Ok(await owner.GetAsync("/api/dine/kitchen"))).GetProperty("orders").GetArrayLength());

        // The floor shows the table waiting; the waiter confirms it and the order goes to the kitchen and the bar.
        var t1 = TableOnFloor(await Ok(await owner.GetAsync("/api/dine/floor")), "Table 1");
        Assert.Equal(1, t1.GetProperty("session").GetProperty("awaitingApproval").GetInt32());
        var sessionId = t1.GetProperty("session").GetProperty("id").GetGuid();
        await Ok(await owner.PostAsync($"/api/dine/sessions/{sessionId}/approve", null));

        var kitchen = await Ok(await owner.GetAsync("/api/dine/kitchen?station=Kitchen"));
        var ticket = kitchen.GetProperty("orders").EnumerateArray().Single();
        Assert.Equal("Table 1", ticket.GetProperty("table").GetString());
        var line = ticket.GetProperty("lines").EnumerateArray().Single(); // the kitchen sees the choma, not the juice
        Assert.Equal("Doneness: Well done · Side: Ugali", line.GetProperty("options").GetString());
        Assert.Equal("No chilli", line.GetProperty("note").GetString());
        Assert.Single((await Ok(await owner.GetAsync("/api/dine/kitchen?station=Bar"))).GetProperty("orders").EnumerateArray());

        // The bar finishes first: still preparing. The kitchen finishes: ready. The waiter serves it.
        var orderId = ticket.GetProperty("id").GetGuid();
        Assert.Equal("Preparing", (await Ok(await owner.PostAsJsonAsync($"/api/dine/orders/{orderId}/station-ready", new { station = "Bar" }))).GetProperty("status").GetString());
        Assert.Equal("Ready", (await Ok(await owner.PostAsJsonAsync($"/api/dine/orders/{orderId}/station-ready", new { station = "Kitchen" }))).GetProperty("status").GetString());
        await Ok(await owner.PostAsJsonAsync($"/api/dine/orders/{orderId}/status", new { status = "Served" }));

        // The guest follows along with their token, calls for the bill, and asks to pay (no M-Pesa prompt set up here).
        var mine = Guest(token);
        var visit = await Ok(await mine.GetAsync($"/api/dine/m/{qr}/visit"));
        Assert.Equal("Served", visit.GetProperty("orders").EnumerateArray().Single().GetProperty("status").GetString());
        Assert.Equal(950_00 + 2 * 250_00, visit.GetProperty("due").GetInt64());
        await Ok(await mine.PostAsJsonAsync($"/api/dine/m/{qr}/call", new { kind = "bill" }));
        var pay = await Ok(await mine.PostAsJsonAsync($"/api/dine/m/{qr}/pay", new { phone = "0722000111" }));
        Assert.False(pay.GetProperty("prompt").GetBoolean());
        Assert.Equal(1_450_00, pay.GetProperty("due").GetInt64());
        // Someone without the table's token can't see the bill or pay it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Guest().GetAsync($"/api/dine/m/{qr}/visit")).StatusCode);

        // The waiter takes the M-Pesa code at the table: the bill is paid and the table shows as paid.
        var paid = await Ok(await owner.PostAsJsonAsync($"/api/dine/sessions/{sessionId}/pay", new { method = "Mpesa", reference = "SFG3H5KLM2" }));
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        visit = await Ok(await mine.GetAsync($"/api/dine/m/{qr}/visit"));
        Assert.Equal(0, visit.GetProperty("due").GetInt64());
        var receipt = visit.GetProperty("receipts").EnumerateArray().Single();
        Assert.Equal("Paid", receipt.GetProperty("status").GetString());

        // The bill is an ordinary invoice with the guest's choices on it, and the money is in money-in.
        var invoice = (await Ok(await owner.GetAsync($"/api/sales/{receipt.GetProperty("id").GetGuid()}"))).GetProperty("document");
        Assert.Equal(1_450_00, invoice.GetProperty("total").GetInt64());
        Assert.Contains(invoice.GetProperty("lines").EnumerateArray(), l => l.GetProperty("description").GetString()!.Contains("Well done"));
        Assert.Contains((await Ok(await owner.GetAsync("/api/payments"))).GetProperty("page").GetProperty("items").EnumerateArray(), p => p.GetProperty("reference").GetString() == "SFG3H5KLM2");

        // They rate the visit; the day's report has the sale, the best seller and the rating.
        await Ok(await mine.PostAsJsonAsync($"/api/dine/m/{qr}/rate", new { stars = 5, comment = "Choma was perfect" }));
        var report = await Ok(await owner.GetAsync("/api/dine/report"));
        Assert.Equal(1_450_00, report.GetProperty("sales").GetInt64());
        Assert.Equal(1_450_00, report.GetProperty("paid").GetInt64());
        Assert.Equal("Fresh juice", report.GetProperty("top").EnumerateArray().First().GetProperty("name").GetString());
        Assert.Equal(5, report.GetProperty("rating").GetDouble());

        // Clearing the table frees it for the next party.
        await Ok(await owner.PostAsync($"/api/dine/sessions/{sessionId}/clear", null));
        Assert.Equal(JsonValueKind.Null, TableOnFloor(await Ok(await owner.GetAsync("/api/dine/floor")), "Table 1").GetProperty("session").ValueKind);
    }

    [Fact]
    public async Task Pay_first_restaurants_only_cook_what_is_paid_for()
    {
        var (owner, _) = await Restaurant("Java Corner");
        await Ok(await owner.PutAsJsonAsync("/api/dine/settings", new { payMode = "PayFirst", approveFirstOrder = false }));
        await Ok(await owner.PostAsync("/api/dine/menu/sample", null));
        var qr = (await Ok(await owner.GetAsync("/api/dine/tables"))).EnumerateArray().First().GetProperty("token").GetString()!;
        var guest = Guest();
        var menu = await Ok(await guest.GetAsync($"/api/dine/m/{qr}"));

        var placed = await Ok(await guest.PostAsJsonAsync($"/api/dine/m/{qr}/orders", new { lines = new[] { new { itemId = Dish(menu, "Kenyan tea"), quantity = 2 } } }));
        Assert.Equal("AwaitingPayment", placed.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(0, (await Ok(await owner.GetAsync("/api/dine/kitchen"))).GetProperty("orders").GetArrayLength());

        // Paid in cash at the counter: now the bar makes it.
        var sessionId = placed.GetProperty("visit").GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync($"/api/dine/sessions/{sessionId}/pay", new { method = "Cash" }));
        var bar = await Ok(await owner.GetAsync("/api/dine/kitchen?station=Bar"));
        Assert.Equal("New", bar.GetProperty("orders").EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sold_out_and_hidden_dishes_cannot_be_ordered_and_tables_belong_to_their_restaurant()
    {
        var (a, _) = await Restaurant("Carnivore Corner");
        var (b, _) = await Restaurant("Other Place");
        await Ok(await a.PostAsync("/api/dine/menu/sample", null));
        var qrA = (await Ok(await a.GetAsync("/api/dine/tables"))).EnumerateArray().First().GetProperty("token").GetString()!;
        var guest = Guest();
        var menu = await Ok(await guest.GetAsync($"/api/dine/m/{qrA}"));
        var tilapia = Dish(menu, "Tilapia (whole, fried)");
        var samosa = Dish(menu, "Samosa (3 pcs)");

        // The kitchen runs out of tilapia; the samosas come off the guest menu altogether.
        await Ok(await a.PostAsJsonAsync($"/api/dine/menu/{tilapia}/sold-out", new { soldOut = true }));
        await Ok(await a.PutAsJsonAsync($"/api/dine/menu/{samosa}", new { hidden = true }));
        menu = await Ok(await guest.GetAsync($"/api/dine/m/{qrA}"));
        Assert.True(menu.GetProperty("menu").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == tilapia).GetProperty("soldOut").GetBoolean());
        Assert.DoesNotContain(menu.GetProperty("menu").EnumerateArray(), i => i.GetProperty("id").GetGuid() == samosa);
        Assert.Equal(HttpStatusCode.Conflict, (await guest.PostAsJsonAsync($"/api/dine/m/{qrA}/orders", new { lines = new[] { new { itemId = tilapia, quantity = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsJsonAsync($"/api/dine/m/{qrA}/orders", new { lines = new[] { new { itemId = samosa, quantity = 1 } } })).StatusCode);

        // A made-up QR finds nothing; a new code retires the old one.
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/api/dine/m/0123456789abcdef01")).StatusCode);
        var tableA = (await Ok(await a.GetAsync("/api/dine/tables"))).EnumerateArray().First().GetProperty("id").GetGuid();
        await Ok(await a.PostAsync($"/api/dine/tables/{tableA}/new-code", null));
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/dine/m/{qrA}")).StatusCode);

        // Another restaurant's team can't reach this one's tables or visits.
        var qrNew = (await Ok(await a.GetAsync("/api/dine/tables"))).EnumerateArray().First(t => t.GetProperty("id").GetGuid() == tableA).GetProperty("token").GetString()!;
        var placed = await Ok(await guest.PostAsJsonAsync($"/api/dine/m/{qrNew}/orders", new { lines = new[] { new { itemId = Dish(menu, "Chapati (2 pcs)"), quantity = 1 } } }));
        var visit = placed.GetProperty("visit").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/dine/sessions/{visit}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/dine/sessions/{visit}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/dine/tables/{tableA}/new-code", null)).StatusCode);
        Assert.DoesNotContain((await Ok(await b.GetAsync("/api/dine/floor"))).GetProperty("tables").EnumerateArray(), t => t.GetProperty("id").GetGuid() == tableA);

        // A waiter (Sales) runs the floor but can't change the menu; the menu and tables are the owner's.
        var waiterEmail = $"{Guid.NewGuid():N}@example.com";
        await Ok(await a.PostAsJsonAsync("/api/settings/users", new { name = "Waiter One", email = waiterEmail, phone = "0700 222 333", role = "Sales", password = "role-password-1" }));
        var waiter = factory.CreateClient();
        var ws = await TestSignIn.First(waiter, waiterEmail, "role-password-1");
        waiter.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ws.GetProperty("token").GetString());
        await Ok(await waiter.GetAsync("/api/dine/floor"));
        await Ok(await waiter.PostAsync($"/api/dine/sessions/{visit}/approve", null));
        Assert.Equal(HttpStatusCode.Forbidden, (await waiter.PostAsJsonAsync("/api/dine/menu", new { name = "Secret dish", price = 100_00 })).StatusCode);
    }

    [Fact]
    public async Task An_erp_business_does_not_see_the_dine_plan_and_a_restaurant_sees_it_first()
    {
        var (restaurant, _) = await Restaurant("Plan Check");
        var plans = (await Ok(await restaurant.GetAsync("/api/billing"))).GetProperty("plans").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal("dine", plans[0]);

        var erp = factory.CreateClient();
        var s = await Ok(await erp.PostAsJsonAsync("/api/auth/register", new { companyName = $"Shop {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        Assert.Equal("erp", s.GetProperty("company").GetProperty("product").GetString());
        erp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        Assert.DoesNotContain("dine", (await Ok(await erp.GetAsync("/api/billing"))).GetProperty("plans").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
    }
}
