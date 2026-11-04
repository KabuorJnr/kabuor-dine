// The till: no sale without an open drawer, a sale is all-or-nothing, and an M-Pesa payment Safaricom already
// sent is used for the sale instead of being counted a second time.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class PosTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient C, Guid Item)> Shop()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Till Shop", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var item = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Chilli Sauce 250ml", price = 35000, taxType = "B_Standard16" }));
        return (c, item.GetProperty("id").GetGuid());
    }

    static object Sale(Guid item, string method, long paid, string? reference = null) => new
    {
        paymentMethod = method, amountPaid = paid, changeGiven = Math.Max(0, paid - 70000), reference,
        lines = new[] { new { itemId = item, quantity = 2 } },
    };

    [Fact]
    public async Task No_sale_until_the_till_is_open_and_a_short_cash_sale_leaves_nothing_behind()
    {
        var (c, item) = await Shop();
        var refused = await c.PostAsJsonAsync("/api/pos/checkout", Sale(item, "Cash", 100000));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Open the till", await refused.Content.ReadAsStringAsync());

        await Ok(await c.PostAsJsonAsync("/api/pos/session/open", new { openingFloat = 200000 }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/pos/checkout", Sale(item, "Cash", 50000))).StatusCode);
        var invoices = await Ok(await c.GetAsync("/api/sales?kind=Invoice"));
        Assert.Equal(0, invoices.GetProperty("page").GetProperty("total").GetInt32()); // rolled back, not a stray unpaid invoice

        var receipt = await Ok(await c.PostAsJsonAsync("/api/pos/checkout", Sale(item, "Cash", 100000)));
        Assert.Equal(70000, receipt.GetProperty("total").GetInt64());
        Assert.Equal(30000, receipt.GetProperty("changeGiven").GetInt64());
        var shift = await Ok(await c.GetAsync("/api/pos/shift"));
        Assert.Equal(1, shift.GetProperty("count").GetInt32());
        Assert.Equal(70000, shift.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task An_mpesa_payment_already_received_pays_the_sale_once()
    {
        var (c, item) = await Shop();
        await Ok(await c.PostAsJsonAsync("/api/pos/session/open", new { openingFloat = 0 }));

        // The customer pays the till; Safaricom tells us before the cashier presses Pay.
        var settings = await Ok(await c.GetAsync("/api/settings/company"));
        var url = new Uri(settings.GetProperty("mpesaConfirmationUrl").GetString()!).PathAndQuery;
        var now = DateTime.UtcNow.AddHours(3).ToString("yyyyMMddHHmmss");
        await Ok(await factory.CreateClient().PostAsJsonAsync(url, new { TransID = "TKA1B2C3D4", TransAmount = "700.00", TransTime = now, BusinessShortCode = "", BillRefNumber = "", MSISDN = "254722000222", FirstName = "AMANI" }));

        var waiting = (await Ok(await c.GetAsync("/api/pos/mpesa-waiting"))).EnumerateArray().ToList();
        Assert.Single(waiting);
        Assert.Equal("TKA1B2C3D4", waiting[0].GetProperty("reference").GetString());

        await Ok(await c.PostAsJsonAsync("/api/pos/checkout", Sale(item, "Mpesa", 70000, "tka1b2c3d4")));
        var payments = await Ok(await c.GetAsync("/api/payments"));
        Assert.Equal(1, payments.GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(0, payments.GetProperty("counts").GetProperty("toMatch").GetInt32());
        Assert.Empty((await Ok(await c.GetAsync("/api/pos/mpesa-waiting"))).EnumerateArray());
        Assert.True((await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }
}
