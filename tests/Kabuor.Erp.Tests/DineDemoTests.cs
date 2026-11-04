// The Kabuor Dine demo restaurant: made on start only when the host sets DEMO__PASSWORD, signed in to with that
// password, tidy (the starter menu and tables, no orders), and its M-Pesa prompt sent with the platform's Daraja app
// from the environment (the MPESA_* names Kabuor Builds uses), never with any keys of its own. Safaricom is played
// by a fake here: nothing real is ever called.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

/// <summary>Safaricom's Daraja, played back: a token, then an accepted STK push. Keeps what it was sent.</summary>
public sealed class DemoDaraja : HttpMessageHandler
{
    public readonly List<JsonObject> Pushes = [];
    public string? Basic;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath.Contains("oauth"))
        {
            Basic = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "fake-token", expires_in = "3599" }) };
        }
        Pushes.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { MerchantRequestID = "m-1", CheckoutRequestID = $"ws_CO_{Pushes.Count}", ResponseCode = "0", CustomerMessage = "Success" }) };
    }
}

public sealed class DineDemoFactory : ErpFactory
{
    public const string Password = "demo-password-for-the-tests";
    public readonly DemoDaraja Daraja = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Demo:Password", Password);
        // The platform's Daraja app, under the names in Kabuor Builds' .env (test values; Safaricom is the fake above).
        builder.UseSetting("MPESA_ENVIRONMENT", "sandbox");
        builder.UseSetting("MPESA_CONSUMER_KEY", "platform-key");
        builder.UseSetting("MPESA_CONSUMER_SECRET", "platform-secret");
        builder.UseSetting("MPESA_STORE_NUMBER", "5123456");
        builder.UseSetting("MPESA_TILL_NUMBER", "8765432");
        builder.UseSetting("MPESA_PASSKEY", "platform-passkey");
        builder.ConfigureTestServices(s => s.AddHttpClient("daraja").ConfigurePrimaryHttpMessageHandler(() => Daraja));
    }
}

public class DineDemoTests(DineDemoFactory factory) : IClassFixture<DineDemoFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task The_demo_restaurant_is_tidy_and_its_guests_get_a_real_prompt_from_the_platforms_till()
    {
        // Signed in with the host's DEMO__PASSWORD; nothing else opens it.
        var anon = factory.CreateClient();
        Assert.False((await anon.PostAsJsonAsync("/api/auth/login", new { email = "demo@kabuor-dine.test", password = "a-guess-1234567" })).IsSuccessStatusCode);
        var login = await Ok(await anon.PostAsJsonAsync("/api/auth/login", new { email = "demo@kabuor-dine.test", password = DineDemoFactory.Password }));
        var owner = factory.CreateClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());

        // Tidy: ten tables in two areas, the starter menu, and nothing on the floor yet.
        var tables = (await Ok(await owner.GetAsync("/api/dine/tables"))).EnumerateArray().ToList();
        Assert.Equal(10, tables.Count);
        Assert.Equal(new[] { "Main room", "Terrace" }, tables.Select(t => t.GetProperty("area").GetString()).Distinct().OrderBy(a => a).ToArray());
        var floor = await Ok(await owner.GetAsync("/api/dine/floor"));
        Assert.All(floor.GetProperty("tables").EnumerateArray(), t => Assert.Equal(JsonValueKind.Null, t.GetProperty("session").ValueKind));

        // A guest scans a table: the menu offers the M-Pesa prompt, and bills show the platform's till.
        var qr = tables.First(t => t.GetProperty("name").GetString() == "Table 1").GetProperty("token").GetString()!;
        var guest = factory.CreateClient();
        var menu = await Ok(await guest.GetAsync($"/api/dine/m/{qr}"));
        Assert.True(menu.GetProperty("mpesaPrompt").GetBoolean());
        Assert.Equal("8765432", menu.GetProperty("restaurant").GetProperty("paybill").GetString());
        var tea = menu.GetProperty("menu").EnumerateArray().First(i => i.GetProperty("name").GetString() == "Kenyan tea").GetProperty("id").GetGuid();

        // They order, a waiter confirms the table, and they pay from their phone.
        var placed = await Ok(await guest.PostAsJsonAsync($"/api/dine/m/{qr}/orders", new { lines = new[] { new { itemId = tea, quantity = 2 } } }));
        var token = placed.GetProperty("token").GetString()!;
        var session = (await Ok(await owner.GetAsync("/api/dine/floor"))).GetProperty("tables").EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == "Table 1").GetProperty("session").GetProperty("id").GetGuid();
        await Ok(await owner.PostAsync($"/api/dine/sessions/{session}/approve", null));
        var mine = factory.CreateClient();
        mine.DefaultRequestHeaders.Add("X-Dine-Token", token);
        var pay = await Ok(await mine.PostAsJsonAsync($"/api/dine/m/{qr}/pay", new { phone = "0722000111" }));
        Assert.True(pay.GetProperty("prompt").GetBoolean());

        // The prompt went with the platform's app: its keys, its store number, paid to its till, for the real amount.
        var push = Assert.Single(factory.Daraja.Pushes);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("platform-key:platform-secret")), factory.Daraja.Basic);
        Assert.Equal("5123456", push["BusinessShortCode"]!.GetValue<string>());
        Assert.Equal("8765432", push["PartyB"]!.GetValue<string>());
        Assert.Equal("CustomerBuyGoodsOnline", push["TransactionType"]!.GetValue<string>());
        Assert.Equal("254722000111", push["PhoneNumber"]!.GetValue<string>());
        Assert.Equal(pay.GetProperty("due").GetInt64() / 100, push["Amount"]!.GetValue<long>());
    }

    [Fact]
    public async Task Other_restaurants_never_prompt_with_the_platforms_keys()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Another Grill {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", product = "dine" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        await Ok(await c.PostAsync("/api/dine/menu/sample", null));
        var qr = (await Ok(await c.GetAsync("/api/dine/tables"))).EnumerateArray().First().GetProperty("token").GetString()!;
        var menu = await Ok(await factory.CreateClient().GetAsync($"/api/dine/m/{qr}"));
        Assert.False(menu.GetProperty("mpesaPrompt").GetBoolean());
    }
}
