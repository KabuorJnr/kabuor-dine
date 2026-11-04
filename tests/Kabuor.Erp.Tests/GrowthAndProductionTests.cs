// CRM (leads, pipeline, follow-ups), manufacturing (recipes, capacity, production cost) and marketing
// (segments, opt-outs, sending, results) through the real API.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class GrowthAndProductionTests(ErpFactory factory) : IClassFixture<ErpFactory>
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

    static string Ago(int days) => DateTime.UtcNow.AddHours(3).AddDays(-days).ToString("yyyy-MM-dd");
    static string Today => DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd");

    // ---------- CRM ----------

    [Fact]
    public async Task A_lead_moves_through_the_pipeline_and_becomes_a_customer_with_its_history()
    {
        var c = await SignUp("Pipeline Ltd");
        var lead = await Ok(await c.PostAsJsonAsync("/api/crm/leads", new { name = "Grace Njeri", business = "Njeri Supermarket", phone = "0711 222 333", source = "Instagram", estimatedValue = 120_000_00, interest = "Monthly supply of sauces", followUpOn = Today }));
        Assert.Equal("New", lead.GetProperty("stage").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/crm/leads", new { name = "Duplicate", phone = "0711222333" })).StatusCode);

        var due = await Ok(await c.GetAsync("/api/crm/follow-ups"));
        Assert.Equal(1, due.GetProperty("counts").GetProperty("due").GetInt32());
        Assert.Equal(1, (await Ok(await c.GetAsync("/api/today"))).GetProperty("followUps").GetProperty("due").GetInt32());

        // A call moves a new lead to Contacted by itself.
        await Ok(await c.PostAsJsonAsync("/api/crm/activities", new { leadId = lead.GetProperty("id").GetGuid(), kind = "Call", text = "Wants 200 bottles a month, send prices", followUpOn = Ago(-3) }));
        Assert.Equal("Contacted", (await Ok(await c.GetAsync($"/api/crm/leads/{lead.GetProperty("id")}"))).GetProperty("lead").GetProperty("stage").GetString());
        await Ok(await c.PostAsync($"/api/crm/activities/{due.GetProperty("items")[0].GetProperty("id")}/done", null));
        Assert.Equal(0, (await Ok(await c.GetAsync("/api/crm/follow-ups"))).GetProperty("counts").GetProperty("due").GetInt32());

        await Ok(await c.PostAsJsonAsync($"/api/crm/leads/{lead.GetProperty("id")}/stage", new { stage = "Quoted" }));
        var pipe = await Ok(await c.GetAsync("/api/crm/pipeline"));
        var quoted = pipe.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetString() == "Quoted");
        Assert.Equal(120_000_00, quoted.GetProperty("value").GetInt64());

        var won = await Ok(await c.PostAsync($"/api/crm/leads/{lead.GetProperty("id")}/convert", null));
        var customerId = won.GetProperty("customer").GetProperty("id").GetGuid();
        Assert.Equal("Njeri Supermarket", won.GetProperty("customer").GetProperty("name").GetString());
        var history = await Ok(await c.GetAsync($"/api/crm/activities?customerId={customerId}"));
        Assert.True(history.GetArrayLength() >= 3); // new lead, the call, the stage move, won
        Assert.Equal(1, (await Ok(await c.GetAsync("/api/crm/pipeline"))).GetProperty("thisMonth").GetProperty("won").GetInt32());
    }

    [Fact]
    public async Task Losing_a_lead_needs_a_reason_and_winning_reuses_an_existing_customer()
    {
        var c = await SignUp("Lost Ltd");
        var lost = await Ok(await c.PostAsJsonAsync("/api/crm/leads", new { name = "Price Shopper", phone = "0722 999 888" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/crm/leads/{lost.GetProperty("id")}/stage", new { stage = "Lost" })).StatusCode);
        await Ok(await c.PostAsJsonAsync($"/api/crm/leads/{lost.GetProperty("id")}/stage", new { stage = "Lost", lostReason = "Too expensive" }));

        var existing = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Baraka Mart", phone = "0733 111 000" }));
        var lead = await Ok(await c.PostAsJsonAsync("/api/crm/leads", new { name = "Baraka", phone = "0733111000" }));
        var won = await Ok(await c.PostAsync($"/api/crm/leads/{lead.GetProperty("id")}/convert", null));
        Assert.Equal(existing.GetProperty("id").GetGuid(), won.GetProperty("customer").GetProperty("id").GetGuid());
        var pipe = await Ok(await c.GetAsync("/api/crm/pipeline"));
        Assert.Equal(50, pipe.GetProperty("thisMonth").GetProperty("winRate").GetInt32());
    }

    // ---------- manufacturing ----------

    [Fact]
    public async Task Production_uses_ingredients_and_puts_finished_goods_in_stock_at_cost()
    {
        var c = await SignUp("Factory Ltd");
        var supplier = await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Mwea Growers" }));
        async Task<Guid> Item(string name, long price) => (await Ok(await c.PostAsJsonAsync("/api/items", new { name, price, trackStock = true, unit = name.Contains("Chillies") ? "kg" : "pc" }))).GetProperty("id").GetGuid();
        var chillies = await Item("Fresh Chillies", 0);
        var jars = await Item("Glass jar 250g", 0);
        var sauce = await Item("Chilli Sauce 250g", 116_00);
        var bill = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier.GetProperty("id").GetGuid(), supplierRef = "MG-1", lines = new object[] { new { itemId = chillies, quantity = 10, unitCost = 116_00 }, new { itemId = jars, quantity = 100, unitCost = 23_20 } } }));
        await Ok(await c.PostAsync($"/api/purchases/{bill.GetProperty("id")}/post", null));

        var recipe = await Ok(await c.PostAsJsonAsync("/api/production/recipes", new { name = "Chilli sauce batch", outputItemId = sauce, outputQuantity = 20, conversionCostPerBatch = 400_00, lines = new[] { new { itemId = chillies, quantity = 5m }, new { itemId = jars, quantity = 20m } } }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/production/recipes", new { name = "Loop", outputItemId = sauce, outputQuantity = 1, lines = new[] { new { itemId = sauce, quantity = 1m } } })).StatusCode);

        var cap = await Ok(await c.GetAsync($"/api/production/recipes/{recipe.GetProperty("id")}/capacity"));
        Assert.Equal(2, cap.GetProperty("maxBatches").GetDecimal());
        Assert.Equal("Fresh Chillies", cap.GetProperty("limitedBy").GetString());
        Assert.Equal(1_300_00, cap.GetProperty("costPerBatch").GetInt64());
        Assert.Equal(65_00, cap.GetProperty("unitCost").GetInt64());
        Assert.Equal(35, cap.GetProperty("margin").GetInt32()); // sells at KSh 100 before VAT

        var tooMany = await Ok(await c.PostAsJsonAsync("/api/production/runs", new { recipeId = recipe.GetProperty("id").GetGuid(), batches = 3 }));
        var refused = await c.PostAsJsonAsync($"/api/production/runs/{tooMany.GetProperty("id")}/complete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Fresh Chillies needs 15", await refused.Content.ReadAsStringAsync());
        await Ok(await c.PostAsync($"/api/production/runs/{tooMany.GetProperty("id")}/cancel", null));

        var run = await Ok(await c.PostAsJsonAsync("/api/production/runs", new { recipeId = recipe.GetProperty("id").GetGuid(), batches = 2 }));
        var done = await Ok(await c.PostAsJsonAsync($"/api/production/runs/{run.GetProperty("id")}/complete", new { actualOutput = 38 })); // two jars broke
        Assert.Equal(1_800_00, done.GetProperty("materialCost").GetInt64());
        Assert.Equal(800_00, done.GetProperty("conversionCost").GetInt64());

        var stock = (await Ok(await c.GetAsync("/api/stock"))).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
        Assert.Equal(0, stock["Fresh Chillies"].GetProperty("total").GetDecimal());
        Assert.Equal(60, stock["Glass jar 250g"].GetProperty("total").GetDecimal());
        Assert.Equal(38, stock["Chilli Sauce 250g"].GetProperty("total").GetDecimal());
        Assert.Equal(6842, stock["Chilli Sauce 250g"].GetProperty("averageCost").GetInt64()); // KSh 2,600 / 38

        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray().ToDictionary(a => a.GetProperty("systemKey").GetString() ?? a.GetProperty("code").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.Equal(3_800_00, accounts["INVENTORY"]);
        Assert.Equal(-800_00, accounts["PROD_ABSORBED"]);
        Assert.True((await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }

    // ---------- marketing ----------

    [Fact]
    public async Task Segments_respect_opt_outs_and_campaigns_report_what_sold_after()
    {
        var c = await SignUp("Campaigns Ltd");
        async Task<Guid> Customer(string name, string? phone, bool optOut = false) =>
            (await Ok(await c.PostAsJsonAsync("/api/customers", new { name, phone, marketingOptOut = optOut }))).GetProperty("id").GetGuid();
        var amina = await Customer("Amina Hassan", "0711 000 001");
        var brian = await Customer("Brian Kip", "0711 000 002");
        await Customer("Carol Never", "0711 000 003");
        var dan = await Customer("Dan OptedOut", "0711 000 004", optOut: true);
        await Customer("Esther NoPhone", null);
        async Task Invoice(Guid customer, int daysAgo)
        {
            var d = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, issueDate = Ago(daysAgo), lines = new[] { new { description = "Sauce", quantity = 1, unitPrice = 500_00 } } }));
            await Ok(await c.PostAsync($"/api/sales/{d.GetProperty("id")}/issue", null));
        }
        await Invoice(amina, 10);
        await Invoice(brian, 100);
        await Invoice(dan, 5);

        var counts = await Ok(await c.GetAsync("/api/marketing/audiences?days=60"));
        Assert.Equal(3, counts.GetProperty("all").GetProperty("count").GetInt32());
        Assert.Equal(1, counts.GetProperty("all").GetProperty("noPhone").GetInt32());
        Assert.Equal(1, counts.GetProperty("all").GetProperty("optedOut").GetInt32());
        Assert.Equal(1, counts.GetProperty("recent").GetProperty("count").GetInt32());
        Assert.Equal(1, counts.GetProperty("lapsed").GetProperty("count").GetInt32());
        Assert.Equal(1, counts.GetProperty("never-bought").GetProperty("count").GetInt32());

        var preview = await Ok(await c.GetAsync("/api/marketing/preview?audience=lapsed&value=60&message=" + Uri.EscapeDataString("Hi {first_name}, we miss you!")));
        Assert.Equal("Hi Brian, we miss you!", preview.GetProperty("sample")[0].GetProperty("message").GetString());

        var sms = await Ok(await c.PostAsJsonAsync("/api/marketing/campaigns", new { name = "Come back offer", channel = "Sms", audience = "lapsed", audienceValue = 60, message = "Hi {first_name}, 10% off this week at Campaigns Ltd." }));
        var sent = await Ok(await c.PostAsync($"/api/marketing/campaigns/{sms.GetProperty("id")}/send", null));
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.True(sent.GetProperty("testMode").GetBoolean()); // no SMS keys in tests: nothing leaves the server
        var r = Assert.Single(sent.GetProperty("recipients").EnumerateArray());
        Assert.StartsWith("Hi Brian, 10% off", r.GetProperty("message").GetString());
        Assert.Contains("To stop these messages", r.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/marketing/campaigns/{sms.GetProperty("id")}/send", null)).StatusCode);

        await Invoice(brian, 0); // Brian comes back
        var detail = await Ok(await c.GetAsync($"/api/marketing/campaigns/{sms.GetProperty("id")}"));
        Assert.Equal(1, detail.GetProperty("results").GetProperty("buyers").GetInt32());
        Assert.Equal(500_00, detail.GetProperty("results").GetProperty("sales").GetInt64());

        // WhatsApp: a ready message per person, marked sent as each is opened.
        var wa = await Ok(await c.PostAsJsonAsync("/api/marketing/campaigns", new { name = "WhatsApp all", channel = "WhatsApp", audience = "all", message = "Habari {first_name}! New flavour this week." }));
        var prepared = await Ok(await c.PostAsync($"/api/marketing/campaigns/{wa.GetProperty("id")}/send", null));
        var first = prepared.GetProperty("recipients")[0];
        Assert.Equal("Ready", first.GetProperty("status").GetString());
        Assert.DoesNotContain(prepared.GetProperty("recipients").EnumerateArray(), x => x.GetProperty("name").GetString() == "Dan OptedOut");
        await Ok(await c.PostAsync($"/api/marketing/campaigns/{wa.GetProperty("id")}/recipients/{first.GetProperty("id")}/sent", null));
        Assert.Equal(1, (await Ok(await c.GetAsync($"/api/marketing/campaigns/{wa.GetProperty("id")}"))).GetProperty("results").GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task Another_company_sees_none_of_it()
    {
        var a = await SignUp("Growth A");
        await Ok(await a.PostAsJsonAsync("/api/crm/leads", new { name = "Secret Lead", phone = "0700 123 456" }));
        await Ok(await a.PostAsJsonAsync("/api/marketing/campaigns", new { name = "Secret campaign", channel = "Sms", audience = "all", message = "Hello there" }));
        var b = await SignUp("Growth B");
        Assert.Empty((await Ok(await b.GetAsync("/api/crm/leads"))).EnumerateArray());
        Assert.Empty((await Ok(await b.GetAsync("/api/marketing/campaigns"))).EnumerateArray());
        Assert.Empty((await Ok(await b.GetAsync("/api/production/recipes"))).EnumerateArray());
    }
}
