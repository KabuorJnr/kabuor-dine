// The KPI scorecard: values come from the books, a running total is judged against the share of the period gone,
// "lower is better" measures are judged the other way, and targets are the owner's to set.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class KpiTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task The_scorecard_measures_sales_margin_and_collection_against_the_owners_targets()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"KPI {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var flour = (await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Flour", price = 200_00, trackStock = true, taxType = "A_Exempt" }))).GetProperty("id").GetGuid();
        await Ok(await c.PostAsJsonAsync("/api/stock/opening", new { itemId = flour, quantity = 100, unitCost = 150_00 }));
        var shop = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Duka" }))).GetProperty("id").GetGuid();
        var today = DateTime.UtcNow.AddHours(3);
        foreach (var qty in new[] { 10, 10 })
        {
            var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = shop, issueDate = today.ToString("yyyy-MM-dd"), lines = new[] { new { itemId = flour, quantity = qty } } }));
            await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/insights/kpis/targets", new Dictionary<string, decimal?> { ["margin"] = 150 })).StatusCode);
        // Targets in shillings: KSh 1,000 of sales this month is easily met; a 40% margin isn't (flour makes 25%).
        await Ok(await c.PutAsJsonAsync("/api/insights/kpis/targets", new Dictionary<string, decimal?> { ["sales"] = 1_000, ["margin"] = 40, ["overdue"] = 15 }));

        var card = await Ok(await c.GetAsync("/api/insights/kpis?period=month"));
        var k = card.GetProperty("kpis").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!);
        Assert.Equal(4_000_00, k["sales"].GetProperty("value").GetDecimal());
        Assert.Equal("met", k["sales"].GetProperty("status").GetString());
        Assert.True(k["sales"].GetProperty("dueByNow").GetDecimal() <= 1_000_00); // pro-rated to the days gone
        Assert.Equal(25.0m, k["margin"].GetProperty("value").GetDecimal());
        Assert.Equal("behind", k["margin"].GetProperty("status").GetString());
        Assert.Equal(1, k["repeat"].GetProperty("value").GetDecimal() / 100); // one buyer, bought twice: 100%
        Assert.Equal(1, k["newCustomers"].GetProperty("value").GetDecimal());
        Assert.Equal(6, k["sales"].GetProperty("trend").GetArrayLength());
        // Nothing paid yet, nothing overdue: within the 15% limit (lower is better).
        Assert.Equal("met", k["overdue"].GetProperty("status").GetString());

        // The charts: twelve months ending this one, this month's sales against the target, what's owed by age.
        var ch = await Ok(await c.GetAsync("/api/insights/charts?months=12"));
        var months = ch.GetProperty("monthly").EnumerateArray().ToList();
        Assert.Equal(12, months.Count);
        Assert.Equal(4_000_00, months[^1].GetProperty("sales").GetInt64());
        Assert.Equal(1_000_00, months[^1].GetProperty("target").GetDecimal());
        Assert.Equal(1_000_00, months[^1].GetProperty("grossProfit").GetInt64());
        Assert.Equal(4_000_00, ch.GetProperty("owed").GetInt64());
        Assert.Equal(4_000_00, ch.GetProperty("ageing")[0].GetProperty("amount").GetInt64()); // not yet due
        Assert.Equal("Flour", ch.GetProperty("products")[0].GetProperty("name").GetString());
        Assert.Equal(5, ch.GetProperty("funnel").GetArrayLength());
    }
}
