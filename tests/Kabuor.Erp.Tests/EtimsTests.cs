// eTIMS through OSCU, against a stand-in KRA that answers like the OSCU spec v2.0 and records what it was sent:
// connecting the device, registering products, signing sales with VAT-inclusive band totals and sequential
// numbers, credit notes on void, KRA refusing (the invoice waits and is retried), and supplier invoices becoming bills.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public sealed class FakeKra : HttpMessageHandler
{
    public readonly ConcurrentQueue<(string Path, JsonObject Body, Dictionary<string, string> Headers)> Calls = new();
    public string? RefuseNextSaleWith;
    public JsonArray SupplierSales = [];
    int receipt;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath.Split('/').Last();
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
        Calls.Enqueue((path, body, req.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
        JsonObject Ok(JsonObject? data = null) => new() { ["resultCd"] = "000", ["resultMsg"] = "It is succeeded", ["resultDt"] = "20261021100000", ["data"] = data };
        var res = path switch
        {
            "selectInitOsdcInfo" => Ok(new JsonObject { ["info"] = new JsonObject { ["tin"] = body["tin"]!.GetValue<string>(), ["taxprNm"] = "MAMA PILI SAUCES LTD", ["bhfId"] = "00", ["bhfNm"] = "Headquarter", ["dvcId"] = "9999911300000001", ["sdcId"] = "KRACU013000001", ["mrcNo"] = "WIS01000150", ["cmcKey"] = "f0b9831bd2334874b7ec815e40347bc4" } }),
            "saveItem" => Ok(),
            "saveTrnsSalesOsdc" when RefuseNextSaleWith is { } code => Refused(code),
            "saveTrnsSalesOsdc" => Ok(new JsonObject { ["curRcptNo"] = ++receipt, ["totRcptNo"] = receipt, ["intrlData"] = "EAHSAV6ECUUXSY6PCCJYAUP6MI", ["rcptSign"] = $"SIGN{receipt:D12}", ["sdcDateTime"] = "20261021100000" }),
            "selectTrnsPurchaseSalesList" => SupplierSales.Count == 0 ? new JsonObject { ["resultCd"] = "001", ["resultMsg"] = "There is no search result" } : Ok(new JsonObject { ["saleList"] = SupplierSales.DeepClone() }),
            "insertTrnsPurchase" => Ok(),
            _ => new JsonObject { ["resultCd"] = "910", ["resultMsg"] = "Request parameter error" },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(res) };
    }

    JsonObject Refused(string code)
    {
        RefuseNextSaleWith = null;
        return new JsonObject { ["resultCd"] = code, ["resultMsg"] = "Request parameter error" };
    }

    public JsonObject Last(string path) => Calls.Last(c => c.Path == path).Body;
}

public sealed class EtimsFactory : WebApplicationFactory<Program>
{
    public readonly FakeKra Kra = new();
    readonly string _db = Path.Combine(Path.GetTempPath(), $"kabuor-erp-etims-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Erp", ErpFactory.Pg is null ? $"Data Source={_db}" : $"{ErpFactory.Pg};Database=kabuor_erp_etims_{Guid.NewGuid():N}");
        builder.UseSetting("Jwt:Key", "test-key-test-key-test-key-test-key-0123456789");
        builder.UseSetting("Agents:Disabled", "true");
        builder.UseSetting("Owners:OversightByDefault", "false"); // the owner does the work in these tests
        builder.UseSetting("Billing:PayToStart", "false");
        builder.ConfigureTestServices(s => s.AddHttpClient("kra-etims").ConfigurePrimaryHttpMessageHandler(() => Kra));
    }
}

public class EtimsTests(EtimsFactory factory) : IClassFixture<EtimsFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Mama Pili Sauces Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X", vatRegistered = true }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    [Fact]
    public async Task Invoices_are_signed_by_KRA_with_the_right_bands_and_numbers_and_voids_send_a_credit_note()
    {
        var c = await Owner();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/etims", new { mode = "Sandbox" })).StatusCode); // serial needed
        var status = await Ok(await c.PutAsJsonAsync("/api/etims", new { mode = "Sandbox", deviceSerial = "dvcv1130", defaultClassCode = "5022110801" }));
        Assert.True(status.GetProperty("connected").GetBoolean());
        Assert.Equal("MAMA PILI SAUCES LTD", status.GetProperty("taxpayerName").GetString());

        var sauce = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Chilli Sauce", price = 116000, taxType = "B_Standard16" }));
        var bread = await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Bread", price = 10800, taxType = "E_Reduced8" }));
        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Naivas", kraPin = "P000111222A" }));
        var doc = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new object[] {
            new { itemId = sauce.GetProperty("id").GetGuid(), quantity = 2 }, new { itemId = bread.GetProperty("id").GetGuid(), quantity = 1 }, new { description = "Delivery", quantity = 1, unitPrice = 58000 } } }));
        var issued = await Ok(await c.PostAsync($"/api/sales/{doc.GetProperty("id")}/issue", null));
        Assert.Equal("Signed", issued.GetProperty("etimsStatus").GetString());
        Assert.Equal("SIGN000000000001", issued.GetProperty("etimsControlCode").GetString());
        Assert.Equal("KRACU013000001/1", issued.GetProperty("etimsInvoiceNo").GetString());

        // Every call carries the PIN, branch and key; products were registered first.
        var sale = factory.Kra.Last("saveTrnsSalesOsdc");
        var call = factory.Kra.Calls.Last(x => x.Path == "saveTrnsSalesOsdc");
        Assert.Equal("P051234567X", call.Headers["tin"]);
        Assert.Equal("f0b9831bd2334874b7ec815e40347bc4", call.Headers["cmcKey"]);
        Assert.Equal(3, factory.Kra.Calls.Count(x => x.Path == "saveItem")); // sauce, bread, and the general item for "Delivery"
        Assert.Equal(1, sale["invcNo"]!.GetValue<long>());
        Assert.Equal("S", sale["rcptTyCd"]!.GetValue<string>());
        Assert.Equal("P000111222A", sale["custTin"]!.GetValue<string>());
        // Band B: 2 × 1,160 + 580 delivery = 2,900 incl. VAT (400 VAT); band E: 108 (8 VAT). Rates 16 and 8.
        Assert.Equal(2900m, sale["taxblAmtB"]!.GetValue<decimal>());
        Assert.Equal(400m, sale["taxAmtB"]!.GetValue<decimal>());
        Assert.Equal(108m, sale["taxblAmtE"]!.GetValue<decimal>());
        Assert.Equal(8m, sale["taxAmtE"]!.GetValue<decimal>());
        Assert.Equal(16m, sale["taxRtB"]!.GetValue<decimal>());
        Assert.Equal(8m, sale["taxRtE"]!.GetValue<decimal>());
        Assert.Equal(3008m, sale["totAmt"]!.GetValue<decimal>());
        Assert.Equal(3, sale["itemList"]!.AsArray().Count);
        Assert.StartsWith("KE", sale["itemList"]![0]!["itemCd"]!.GetValue<string>());

        // KRA refuses the next one: the sale stands, the invoice waits, and a retry signs it with the same number.
        factory.Kra.RefuseNextSaleWith = "910";
        var doc2 = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { itemId = sauce.GetProperty("id").GetGuid(), quantity = 1 } } }));
        var failed = await Ok(await c.PostAsync($"/api/sales/{doc2.GetProperty("id")}/issue", null));
        Assert.Equal("Failed", failed.GetProperty("etimsStatus").GetString());
        var retried = await Ok(await c.PostAsync($"/api/sales/{doc2.GetProperty("id")}/etims", null));
        Assert.Equal("Signed", retried.GetProperty("etimsStatus").GetString());
        Assert.Equal(2, factory.Kra.Last("saveTrnsSalesOsdc")["invcNo"]!.GetValue<long>());

        // Voiding the first sends a credit note pointing at its number.
        await Ok(await c.PostAsync($"/api/sales/{doc.GetProperty("id")}/void", null));
        var credit = factory.Kra.Last("saveTrnsSalesOsdc");
        Assert.Equal("R", credit["rcptTyCd"]!.GetValue<string>());
        Assert.Equal(1, credit["orgInvcNo"]!.GetValue<long>());
        Assert.Equal(3, credit["invcNo"]!.GetValue<long>());
    }

    [Fact]
    public async Task Supplier_invoices_from_eTIMS_become_bills_and_are_confirmed_to_KRA()
    {
        var c = await Owner();
        await Ok(await c.PutAsJsonAsync("/api/etims", new { mode = "Sandbox", deviceSerial = "dvcv2001", defaultClassCode = "5022110801" }));
        factory.Kra.SupplierSales = [new JsonObject
        {
            ["spplrTin"] = "P099888777Z", ["spplrNm"] = "Mwangi Farm Supplies", ["spplrBhfId"] = "00", ["spplrInvcNo"] = 77, ["rcptTyCd"] = "S", ["pmtTyCd"] = "02",
            ["salesDt"] = "20261015", ["totItemCnt"] = 1, ["totTaxblAmt"] = 11600, ["totTaxAmt"] = 1600, ["totAmt"] = 11600,
            ["itemList"] = new JsonArray(new JsonObject { ["itemSeq"] = 1, ["itemNm"] = "Glass jars 250ml (carton)", ["qty"] = 10, ["prc"] = 1160, ["taxTyCd"] = "B", ["taxblAmt"] = 11600, ["taxAmt"] = 1600, ["totAmt"] = 11600 }),
        }];
        Assert.Equal(1, (await Ok(await c.PostAsync("/api/etims/purchases/sync", null))).GetProperty("received").GetInt32());
        Assert.Equal(0, (await Ok(await c.PostAsync("/api/etims/purchases/sync", null))).GetProperty("received").GetInt32()); // not twice
        var waiting = (await Ok(await c.GetAsync("/api/etims/purchases"))).GetProperty("items")[0];
        Assert.Equal(1_160_000, waiting.GetProperty("total").GetInt64());

        var accepted = await Ok(await c.PostAsJsonAsync($"/api/etims/purchases/{waiting.GetProperty("id")}/accept", new { }));
        var bill = await Ok(await c.GetAsync($"/api/purchases/{accepted.GetProperty("bill")}"));
        Assert.Equal("Open", bill.GetProperty("document").GetProperty("status").GetString());
        Assert.Equal(1_160_000, bill.GetProperty("document").GetProperty("total").GetInt64());
        Assert.Equal(160_000, bill.GetProperty("document").GetProperty("tax").GetInt64());
        var confirm = factory.Kra.Last("insertTrnsPurchase");
        Assert.Equal("P099888777Z", confirm["spplrTin"]!.GetValue<string>());
        Assert.Equal(77, confirm["spplrInvcNo"]!.GetValue<long>());
        var vat = await Ok(await c.GetAsync("/api/reports/vat?from=2026-10-01&to=2026-10-31"));
        Assert.Equal(160_000, vat.GetProperty("inputTax").GetInt64());
    }
}
