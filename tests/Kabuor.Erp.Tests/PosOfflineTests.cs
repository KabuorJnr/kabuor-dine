// An offline till syncs its queue when the network is back: a sale sent twice (the reply was lost) is kept once,
// and it keeps the day it was really made.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class PosOfflineTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task A_queued_sale_sent_twice_is_kept_once_on_the_day_it_happened()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Offline Shop", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var bread = (await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Bread", price = 70_00 }))).GetProperty("id").GetGuid();
        await Ok(await c.PostAsJsonAsync("/api/pos/session/open", new { openingFloat = 1_000_00, notes = "" }));

        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var sale = new { paymentMethod = "Cash", amountPaid = 100_00, changeGiven = 30_00, lines = new[] { new { itemId = bread, quantity = 1 } }, clientRef = "till1-0001", soldAt = yesterday };
        var first = await Ok(await c.PostAsJsonAsync("/api/pos/checkout", sale));
        var again = await Ok(await c.PostAsJsonAsync("/api/pos/checkout", sale));
        Assert.Equal(first.GetProperty("invoiceId").GetGuid(), again.GetProperty("invoiceId").GetGuid());
        Assert.True(again.GetProperty("alreadyRecorded").GetBoolean());

        var invoices = await Ok(await c.GetAsync("/api/sales?kind=Invoice"));
        Assert.Equal(1, invoices.GetProperty("counts").GetProperty("all").GetInt32());
        var doc = (await Ok(await c.GetAsync($"/api/sales/{first.GetProperty("invoiceId")}"))).GetProperty("document");
        Assert.Equal(DateOnly.FromDateTime(yesterday.ToOffset(TimeSpan.FromHours(3)).DateTime).ToString("yyyy-MM-dd"), doc.GetProperty("issueDate").GetString());
    }
}
