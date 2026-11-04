// Kabuor Intelligence end to end: a salesperson's link brings a visitor, the visitor asks on the web shop, the
// inquiry becomes a lead, a quote and a sale, and the credit lands on the salesperson and the channel. Customers
// sign in to the portal with a one-time code to see what they owe and to order. The insight figures add up.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kabuor.Erp.Tests;

public class IntelligenceTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    async Task<(HttpClient owner, HttpClient sales, Guid salesId, string slug)> Setup(string name)
    {
        var owner = factory.CreateClient();
        var s = await Ok(await owner.PostAsJsonAsync("/api/auth/register", new { companyName = name, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var email = $"{Guid.NewGuid():N}@example.com";
        var made = await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Mercy Achieng", email, role = "Sales", password = "another-long-password" }));
        var sales = factory.CreateClient();
        var login = await TestSignIn.First(sales, email, "another-long-password");
        sales.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
        var portal = await Ok(await owner.PutAsJsonAsync("/api/settings/portal", new { enabled = true }));
        return (owner, sales, login.GetProperty("user").GetProperty("id").GetGuid(), portal.GetProperty("slug").GetString()!);
    }

    [Fact]
    public async Task A_salespersons_TikTok_link_is_credited_with_the_inquiry_the_customer_and_the_sale()
    {
        var (owner, sales, mercyId, slug) = await Setup($"Intel Shop {Guid.NewGuid():N}");
        var juice = Id(await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Mango juice 1L", price = 250_00 })));

        // Mercy makes her own link for her TikTok bio.
        var link = await Ok(await sales.PostAsJsonAsync("/api/links", new { name = "Mercy's TikTok bio", channel = "TikTok", target = "shop" }));
        var code = link.GetProperty("code").GetString()!;

        // A visitor opens it: counted, remembered, sent to the shop.
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var visit = await browser.GetAsync($"/l/{code}");
        Assert.Equal(HttpStatusCode.Redirect, visit.StatusCode);
        Assert.Contains($"/s/{slug}", visit.Headers.Location!.ToString());

        // They ask on the shop: an inquiry and a lead, credited to the link, assigned to Mercy.
        var shop = await Ok(await browser.GetAsync($"/api/public/{slug}"));
        Assert.Contains("Mango juice", shop.ToString());
        await Ok(await browser.PostAsJsonAsync($"/api/public/{slug}/inquiries", new { name = "Achieng Hotel", phone = "0733222111", message = "Do you deliver 40 litres of mango juice to Kisumu?", itemId = juice, @ref = code }));
        var inbox = await Ok(await sales.GetAsync("/api/inquiries?mine=true"));
        var inquiry = inbox.GetProperty("items")[0];
        Assert.Equal("TikTok", inquiry.GetProperty("channel").GetString());
        Assert.Equal("Mercy Achieng", inquiry.GetProperty("assignedName").GetString());

        // Mercy answers and quotes; the lead becomes a customer who keeps the link's credit; the sale wins the inquiry.
        await Ok(await sales.PostAsJsonAsync($"/api/inquiries/{Id(inquiry)}/reply", new { text = "Yes, delivery to Kisumu is KSh 800. I'll send a quote." }));
        var q = await Ok(await sales.PostAsync($"/api/inquiries/{Id(inquiry)}/quote", null));
        var quoteId = q.GetProperty("quoteId").GetGuid();
        var quote = (await Ok(await sales.GetAsync($"/api/sales/{quoteId}"))).GetProperty("document");
        await Ok(await sales.PutAsJsonAsync($"/api/sales/{quoteId}", new { kind = "Quote", customerId = quote.GetProperty("customerId").GetGuid(), lines = new[] { new { itemId = juice, quantity = 40 } } }));
        await Ok(await sales.PostAsync($"/api/sales/{quoteId}/issue", null));
        var invoice = await Ok(await sales.PostAsync($"/api/sales/{quoteId}/to-invoice", null));
        await Ok(await sales.PostAsync($"/api/sales/{Id(invoice)}/issue", null));

        var thread = await Ok(await sales.GetAsync($"/api/inquiries/{Id(inquiry)}"));
        Assert.Equal("Won", thread.GetProperty("status").GetString());

        var people = await Ok(await owner.GetAsync("/api/insights/salespeople"));
        var leader = people.GetProperty("leader");
        Assert.Equal("Mercy Achieng", leader.GetProperty("name").GetString());
        Assert.Equal(40 * 250_00, leader.GetProperty("sold").GetInt64());
        Assert.Equal(1, leader.GetProperty("customersBrought").GetInt32());
        Assert.Equal(40 * 250_00, leader.GetProperty("broughtRevenue").GetInt64());

        var channels = await Ok(await owner.GetAsync("/api/insights/channels"));
        Assert.Equal("TikTok", channels.GetProperty("best").GetString());
        var links = await Ok(await sales.GetAsync("/api/links"));
        var mine = links.GetProperty("links")[0];
        Assert.Equal(1, mine.GetProperty("visits").GetInt32());
        Assert.Equal(1, mine.GetProperty("inquiries").GetInt32());
        Assert.Equal(1, mine.GetProperty("customers").GetInt32());
        Assert.Equal(40 * 250_00, mine.GetProperty("revenue").GetInt64());

        // The owner can follow the customer from the link to the sale.
        var journey = await Ok(await owner.GetAsync($"/api/insights/journey/{quote.GetProperty("customerId").GetGuid()}"));
        var kinds = journey.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Contains("found", kinds);
        Assert.Contains("inquiry", kinds);
        Assert.Contains("reply", kinds);
        Assert.Contains("invoice", kinds);
        Assert.Contains("won", kinds);
    }

    [Fact]
    public async Task Customers_sign_in_to_the_portal_with_a_one_time_code_and_see_their_account_and_order()
    {
        var (owner, _, _, slug) = await Setup($"Portal Shop {Guid.NewGuid():N}");
        var bread = Id(await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Bread 400g", price = 65_00 })));
        var cust = Id(await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Mama Njeri Shop", phone = "0722555444" })));
        var inv = await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = bread, quantity = 20 } } }));
        await Ok(await owner.PostAsync($"/api/sales/{Id(inv)}/issue", null));

        var web = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync($"/api/public/{slug}/me")).StatusCode);
        var sent = await Ok(await web.PostAsJsonAsync($"/api/public/{slug}/auth/code", new { phone = "0722 555 444" }));
        var devCode = sent.GetProperty("devCode").GetString()!; // development servers show the code (no SMS here)
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsJsonAsync($"/api/public/{slug}/auth/verify", new { phone = "0722555444", code = "000000" == devCode ? "111111" : "000000" })).StatusCode);
        var signedIn = await Ok(await web.PostAsJsonAsync($"/api/public/{slug}/auth/verify", new { phone = "0722555444", code = devCode }));
        web.DefaultRequestHeaders.Add("X-Portal-Token", signedIn.GetProperty("token").GetString());

        var me = await Ok(await web.GetAsync($"/api/public/{slug}/me"));
        Assert.Equal("Mama Njeri Shop", me.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal(20 * 65_00, me.GetProperty("owes").GetInt64());
        Assert.Single(me.GetProperty("invoices").EnumerateArray());

        // Ordering again makes a draft quote and an inquiry for staff to confirm.
        var order = await Ok(await web.PostAsJsonAsync($"/api/public/{slug}/orders", new { lines = new[] { new { itemId = bread, quantity = 30 } }, note = "Deliver Friday" }));
        Assert.Equal(30 * 65_00, order.GetProperty("quote").GetProperty("total").GetInt64());
        var inbox = await Ok(await owner.GetAsync("/api/inquiries"));
        Assert.Contains("Deliver Friday", inbox.ToString());

        // They choose not to get offers: recorded, and campaigns skip them.
        await Ok(await web.PutAsJsonAsync($"/api/public/{slug}/me", new { marketingOptOut = true }));
        var customer = (await Ok(await owner.GetAsync($"/api/customers/{cust}"))).GetProperty("customer");
        Assert.True(customer.GetProperty("marketingOptOut").GetBoolean());

        // A token for one business opens nothing at another.
        var (_, _, _, otherSlug) = await Setup($"Other Shop {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await web.GetAsync($"/api/public/{otherSlug}/me")).StatusCode);
    }

    [Fact]
    public async Task Insights_answer_what_sells_what_sits_what_to_buy_and_whether_stock_matches_the_shelf()
    {
        var (owner, _, _, _) = await Setup($"Insight Shop {Guid.NewGuid():N}");
        var fast = Id(await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Soda", price = 70_00, trackStock = true })));
        var dead = Id(await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Umbrella", price = 900_00, trackStock = true })));
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = fast, quantity = 100, unitCost = 40_00 }));
        await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = dead, quantity = 10, unitCost = 500_00 }));
        var cust = Id(await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Kiosk" })));
        for (var d = 1; d <= 6; d++)
        {
            var inv = await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, issueDate = DateTime.UtcNow.AddDays(-d * 5).ToString("yyyy-MM-dd"), lines = new[] { new { itemId = fast, quantity = 15 } } }));
            await Ok(await owner.PostAsync($"/api/sales/{Id(inv)}/issue", null));
        }

        var selling = await Ok(await owner.GetAsync("/api/insights/selling"));
        Assert.Equal("Soda", selling.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(90, selling.GetProperty("items")[0].GetProperty("quantity").GetDecimal());

        var shelf = await Ok(await owner.GetAsync("/api/insights/shelf"));
        Assert.Equal("Umbrella", shelf.GetProperty("stuck")[0].GetProperty("name").GetString());
        Assert.Equal(10 * 500_00, shelf.GetProperty("tiedUp").GetInt64());

        // 90 soda in 30 days ≈ 1.5 a day; two weeks of cover is 21; 10 left: buy 11.
        var buy = await Ok(await owner.GetAsync("/api/insights/buy"));
        var soda = buy.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "Soda");
        Assert.Equal(11, soda.GetProperty("buy").GetDecimal());

        // A count: soda matches, umbrellas are 2 short.
        await Ok(await owner.PostAsJsonAsync("/api/stock/count", new { counts = new object[] { new { itemId = fast, counted = 10 }, new { itemId = dead, counted = 8 } } }));
        var accuracy = await Ok(await owner.GetAsync("/api/insights/accuracy"));
        Assert.Equal(50.0, accuracy.GetProperty("accuracy").GetDouble());
        Assert.Equal(2 * 500_00, accuracy.GetProperty("missingValue").GetInt64());

        var overview = await Ok(await owner.GetAsync("/api/insights/overview"));
        Assert.Equal("Soda", overview.GetProperty("bestSeller").GetProperty("name").GetString());
        Assert.Equal(50.0, overview.GetProperty("stockAccuracy").GetDouble());
    }

    [Fact]
    public async Task A_lost_inquiry_needs_a_reason_and_the_reasons_are_counted()
    {
        var (owner, sales, _, _) = await Setup($"Lost Shop {Guid.NewGuid():N}");
        var i = await Ok(await sales.PostAsJsonAsync("/api/inquiries", new { name = "Walk-in", phone = "0700111222", message = "How much is a 50kg bag of sugar?", channel = "WalkIn" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await sales.PostAsJsonAsync($"/api/inquiries/{Id(i)}/close", new { won = false })).StatusCode);
        await Ok(await sales.PostAsJsonAsync($"/api/inquiries/{Id(i)}/close", new { won = false, reason = "Price too high" }));
        var report = await Ok(await owner.GetAsync("/api/insights/inquiries"));
        Assert.Equal("Price too high", report.GetProperty("lostReasons")[0].GetProperty("reason").GetString());
        Assert.Equal(0.0, report.GetProperty("conversion").GetDouble());
    }

    [Fact]
    public async Task A_cached_answer_reads_exactly_like_a_fresh_one()
    {
        // The inbox, the insights and the quote audit are cached for a few seconds; the second load used to come back
        // with "Name" and "Subject" instead of "name" and "subject", and the inbox showed empty rows.
        var (owner, _, _, _) = await Setup($"Cache Shop {Guid.NewGuid():N}");
        await Ok(await owner.PostAsJsonAsync("/api/inquiries", new { name = "Walk-in", phone = "0700111222", message = "Do you have 28 gauge sheets in green?", channel = "WalkIn" }));
        foreach (var url in new[] { "/api/inquiries?status=active", "/api/insights/quote-variance" })
        {
            var fresh = (await Ok(await owner.GetAsync(url))).GetRawText();
            var cached = (await Ok(await owner.GetAsync(url))).GetRawText();
            Assert.Equal(fresh, cached);
        }
        var row = (await Ok(await owner.GetAsync("/api/inquiries?status=active"))).GetProperty("items")[0];
        Assert.Equal("Walk-in", row.GetProperty("name").GetString());
        Assert.Equal("Do you have 28 gauge sheets in green?", row.GetProperty("subject").GetString());
        Assert.StartsWith("ENQ-", row.GetProperty("number").GetString());
    }
}
