// The public API and webhooks: a key is shown once and only its hash is kept; an online shop's order retried
// with the same reference is recorded once; a paid order raises signed webhooks the receiver can verify.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kabuor.Erp.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kabuor.Erp.Tests;

public sealed class FakeReceiver : HttpMessageHandler
{
    public readonly ConcurrentQueue<(string Url, string Body, string Signature, string Event)> Got = new();
    public HttpStatusCode Answer = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Got.Enqueue((req.RequestUri!.ToString(), await req.Content!.ReadAsStringAsync(ct), req.Headers.GetValues("X-Kabuor-Signature").Single(), req.Headers.GetValues("X-Kabuor-Event").Single()));
        return new HttpResponseMessage(Answer);
    }
}

public sealed class IntegrationFactory : WebApplicationFactory<Program>
{
    public readonly FakeReceiver Receiver = new();
    readonly string _db = Path.Combine(Path.GetTempPath(), $"kabuor-erp-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Erp", ErpFactory.Pg is null ? $"Data Source={_db}" : $"{ErpFactory.Pg};Database=kabuor_erp_api_{Guid.NewGuid():N}");
        builder.UseSetting("Jwt:Key", "test-key-test-key-test-key-test-key-0123456789");
        builder.UseSetting("Agents:Disabled", "true");
        builder.UseSetting("Owners:OversightByDefault", "false"); // the owner does the work in these tests
        builder.UseSetting("Billing:PayToStart", "false");
        builder.UseSetting("Webhooks:Disabled", "true"); // the test sends them itself
        builder.ConfigureTestServices(s => s.AddHttpClient("webhooks").ConfigurePrimaryHttpMessageHandler(() => Receiver));
    }

    public WebhookWorker Worker => Services.GetServices<IHostedService>().OfType<WebhookWorker>().Single();
}

public class IntegrationTests(IntegrationFactory factory) : IClassFixture<IntegrationFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task A_shop_order_comes_in_once_and_its_payment_is_announced_by_signed_webhook()
    {
        var owner = factory.CreateClient();
        var s = await Ok(await owner.PostAsJsonAsync("/api/auth/register", new { companyName = "Duka Online", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        await Ok(await owner.PostAsJsonAsync("/api/items", new { name = "Honey 500g", sku = "HNY500", price = 650_00 }));

        var made = await Ok(await owner.PostAsJsonAsync("/api/settings/api-keys", new { name = "Online shop" }));
        var key = made.GetProperty("key").GetString()!;
        Assert.DoesNotContain(key, (await Ok(await owner.GetAsync("/api/settings/api-keys"))).ToString()); // shown once only
        var hook = await Ok(await owner.PostAsJsonAsync("/api/settings/webhooks", new { url = "https://shop.example.test/kabuor", events = "invoice.paid" }));
        var secret = hook.GetProperty("secret").GetString()!;

        var shop = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await shop.GetAsync("/api/v1/items")).StatusCode);
        shop.DefaultRequestHeaders.Add("X-Api-Key", key);
        var items = await Ok(await shop.GetAsync("/api/v1/items"));
        Assert.Equal("HNY500", items[0].GetProperty("sku").GetString());

        var order = new { externalRef = "SHOP-1042", customerName = "Achieng Otieno", customerPhone = "0733 444 555", lines = new[] { new { sku = "HNY500", quantity = 2 } }, mpesaReceipt = "TJK9XYZ001" };
        var first = await Ok(await shop.PostAsJsonAsync("/api/v1/orders", order));
        var again = await Ok(await shop.PostAsJsonAsync("/api/v1/orders", order));
        Assert.Equal(first.GetProperty("invoice").GetString(), again.GetProperty("invoice").GetString());
        Assert.True(again.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Equal("Paid", first.GetProperty("status").GetString());
        Assert.Equal(1_300_00, first.GetProperty("total").GetInt64());
        Assert.Equal("Paid", (await Ok(await shop.GetAsync("/api/v1/orders/SHOP-1042"))).GetProperty("status").GetString());

        var invoices = await Ok(await owner.GetAsync("/api/sales?kind=Invoice"));
        Assert.Equal(1, invoices.GetProperty("counts").GetProperty("all").GetInt32());

        await factory.Worker.SendDue();
        Assert.True(factory.Receiver.Got.TryDequeue(out var got));
        Assert.Equal("invoice.paid", got.Event);
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(got.Body))).ToLowerInvariant();
        Assert.Equal(expected, got.Signature);
        Assert.Contains("SHOP-1042", got.Body);
        Assert.Empty(factory.Receiver.Got); // payment.received wasn't subscribed

        // A revoked key stops working at once.
        await owner.DeleteAsync($"/api/settings/api-keys/{made.GetProperty("id")}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await shop.GetAsync("/api/v1/items")).StatusCode);
    }
}
