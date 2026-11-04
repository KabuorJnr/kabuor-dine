// Printed documents carry the business's letterhead: its address and logo come from Settings, anyone who prints an
// invoice or waybill can read them, and only a real PNG/JPEG/WebP is kept as the logo. Waybills list the goods.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class PrintedDocumentsTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    // The smallest valid PNG (1×1, transparent).
    const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Amani Store", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    [Fact]
    public async Task The_letterhead_carries_the_address_and_logo_and_every_role_that_prints_can_read_it()
    {
        var owner = await Owner();
        await Ok(await owner.PutAsJsonAsync("/api/settings/company", new { name = "Amani Store", kraPin = "P051234567X", phone = "0712345678", email = "hello@amani.co.ke", county = "Nairobi", vatRegistered = true, mpesaShortcode = "", address = "Amani House, Moi Avenue" }));
        await Ok(await owner.PutAsJsonAsync("/api/settings/company/logo", new { dataUrl = Png }));

        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Store Keeper", email, phone = "0700 333 444", role = "Storekeeper", password = "storekeeper-pass" }));
        var sk = factory.CreateClient();
        var login = await TestSignIn.First(sk, email, "storekeeper-pass");
        sk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());

        var lh = await Ok(await sk.GetAsync("/api/company/letterhead"));
        Assert.Equal("Amani Store", lh.GetProperty("name").GetString());
        Assert.Equal("Amani House, Moi Avenue", lh.GetProperty("address").GetString());
        Assert.Equal("P051234567X", lh.GetProperty("kraPin").GetString());
        Assert.Equal(Png, lh.GetProperty("logo").GetString());
        // The settings themselves (with the M-Pesa confirmation secret) stay the owner's.
        Assert.Equal(HttpStatusCode.Forbidden, (await sk.GetAsync("/api/settings/company")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sk.PutAsJsonAsync("/api/settings/company/logo", new { dataUrl = "" })).StatusCode);

        await Ok(await owner.PutAsJsonAsync("/api/settings/company/logo", new { dataUrl = "" }));
        Assert.Equal("", (await Ok(await owner.GetAsync("/api/company/letterhead"))).GetProperty("logo").GetString());
    }

    [Theory]
    [InlineData("data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciLz4=")] // SVG can carry script
    [InlineData("data:image/png;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")] // says PNG, isn't one
    [InlineData("https://example.com/logo.png")]
    [InlineData("data:image/png;base64,not base64!")]
    public async Task Only_a_real_picture_is_kept_as_the_logo(string dataUrl)
    {
        var owner = await Owner();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/settings/company/logo", new { dataUrl })).StatusCode);
        Assert.Equal("", (await Ok(await owner.GetAsync("/api/company/letterhead"))).GetProperty("logo").GetString());
    }

    [Fact]
    public async Task A_logo_too_large_to_print_quickly_is_refused()
    {
        var owner = await Owner();
        var big = new byte[400 * 1024];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(big, 0);
        var res = await owner.PutAsJsonAsync("/api/settings/company/logo", new { dataUrl = "data:image/png;base64," + Convert.ToBase64String(big) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_waybill_lists_the_goods_on_its_invoice()
    {
        var c = await Owner();
        var cust = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Rift Valley Supermarket", phone = "0733112233", address = "Nakuru Town CBD" }))).GetProperty("id").GetGuid();
        var item = (await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Rice 25kg Pack", price = 3_200_00 }))).GetProperty("id").GetGuid();
        var inv = (await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust, lines = new[] { new { itemId = item, quantity = 20 } } }))).GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{inv}/issue", null));
        var trip = await Ok(await c.PostAsJsonAsync("/api/dispatch/trips", new
        {
            vehiclePlate = "KBZ 456T", driverName = "John Kamau", driverPhone = "0722123456", routeName = "Nairobi - Nakuru",
            deliveries = new[] { new { salesDocId = inv, customerName = "Rift Valley Supermarket", deliveryAddress = "Nakuru Town CBD", contactPhone = "0733112233" } },
        }));

        var detail = await Ok(await c.GetAsync($"/api/dispatch/trips/{trip.GetProperty("id").GetGuid()}"));
        var goods = Assert.Single(detail.GetProperty("deliveries")[0].GetProperty("items").EnumerateArray());
        Assert.Equal("Rice 25kg Pack", goods.GetProperty("description").GetString());
        Assert.Equal(20m, goods.GetProperty("quantity").GetDecimal());
    }
}
