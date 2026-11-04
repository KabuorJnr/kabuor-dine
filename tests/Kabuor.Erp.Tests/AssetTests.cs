// The asset register and barcodes: tags are generated in sequence per category, many can be added at once, a scan
// finds an asset by its tag, its label link or the maker's serial number and records the sighting, and a product's
// barcode is unique and searchable.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class AssetTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Assets {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    [Fact]
    public async Task Assets_get_tags_in_sequence_and_a_scan_finds_and_records_them()
    {
        var c = await Owner();
        var store = (await Ok(await c.GetAsync("/api/locations")))[0].GetProperty("id").GetGuid();
        var chairs = await Ok(await c.PostAsJsonAsync("/api/assets", new { name = "Office chair", category = "FUR", quantity = 3, locationId = store, cost = 4_500_00 }));
        Assert.Equal(3, chairs.GetProperty("added").GetInt32());
        Assert.Equal(["AST-FUR-0001", "AST-FUR-0002", "AST-FUR-0003"], chairs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tag").GetString()!).ToArray());
        Assert.Equal("InStore", chairs.GetProperty("items")[0].GetProperty("status").GetString()); // nobody has them yet
        var laptop = (await Ok(await c.PostAsJsonAsync("/api/assets", new { name = "Laptop", category = "ELC", serialNumber = "5CD1234XYZ", assignedTo = "Mercy Achieng", cost = 65_000_00 })))
            .GetProperty("items")[0];
        Assert.Equal("AST-ELC-0001", laptop.GetProperty("tag").GetString());
        Assert.Equal("InUse", laptop.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/assets", new { name = "Drill", category = "TLS", quantity = 2, serialNumber = "X1" })).StatusCode);

        // A scan: the printed tag, the label's link, or the maker's serial number.
        var byTag = await Ok(await c.GetAsync("/api/assets/scan?code=ast-fur-0002&seen=true"));
        Assert.Equal("AST-FUR-0002", byTag.GetProperty("asset").GetProperty("tag").GetString());
        Assert.False(byTag.GetProperty("asset").GetProperty("lastSeenAt").ValueKind == JsonValueKind.Null);
        Assert.Contains(byTag.GetProperty("history").EnumerateArray(), h => h.GetProperty("kind").GetString() == "seen");
        var byLink = await Ok(await c.GetAsync($"/api/assets/scan?code={Uri.EscapeDataString("https://erp.example/assets?tag=AST-ELC-0001")}"));
        Assert.Equal("Laptop", byLink.GetProperty("asset").GetProperty("name").GetString());
        Assert.Equal("AST-ELC-0001", (await Ok(await c.GetAsync("/api/assets/scan?code=5CD1234XYZ"))).GetProperty("asset").GetProperty("tag").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/assets/scan?code=AST-FUR-9999")).StatusCode);

        // Losing one needs a reason; scanning it later finds it again.
        var id = byTag.GetProperty("asset").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/assets/{id}/actions", new { kind = "status", status = "Lost" })).StatusCode);
        await Ok(await c.PostAsJsonAsync($"/api/assets/{id}/actions", new { kind = "status", status = "Lost", note = "Not in the boardroom at the count" }));
        var found = await Ok(await c.GetAsync("/api/assets/scan?code=AST-FUR-0002&seen=true"));
        Assert.Equal("InStore", found.GetProperty("asset").GetProperty("status").GetString());

        var list = await Ok(await c.GetAsync("/api/assets"));
        Assert.Equal(4, list.GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(78_500_00, list.GetProperty("totalCost").GetInt64());
        Assert.Single(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("assignedTo").GetString() == "Mercy Achieng");
    }

    [Fact]
    public async Task A_product_barcode_is_unique_and_found_by_search()
    {
        var c = await Owner();
        var soap = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Bar soap 800g", sku = "SP800", barcode = "6 001087 340052", price = 160_00, taxType = "B_Standard16", trackStock = true }));
        Assert.Equal("6001087340052", soap.GetProperty("barcode").GetString()); // spaces from the pack are dropped
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/items", new { name = "Another soap", barcode = "6001087340052", price = 100_00 })).StatusCode);
        var hits = await Ok(await c.GetAsync("/api/items?q=6001087340052"));
        Assert.Equal("Bar soap 800g", Assert.Single(hits.EnumerateArray()).GetProperty("name").GetString());
    }
}
