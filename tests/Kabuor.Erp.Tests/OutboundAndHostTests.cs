// Two ways a business account could reach past its own data. A webhook address is called by this server, so it must
// never lead into the server's private network, whether typed as an address, hidden behind a name, or reached at
// send time. And links with secrets in them (M-Pesa callbacks) are built from the configured public address, never
// from a Host header the caller chose.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Kabuor.Erp.Tests;

public sealed class PublicUrlFactory : ErpFactory
{
    public const string Url = "https://erp.kabuor.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:PublicUrl", Url);
    }
}

public class OutboundAndHostTests(PublicUrlFactory factory) : IClassFixture<PublicUrlFactory>
{
    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Guard Shop {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" });
        var s = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("41.90.64.10", true)]
    [InlineData("2001:4860:4860::8888", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.4.0.9", false)]
    [InlineData("172.17.0.2", false)]   // Docker's default network: the old check only knew 172.16.x
    [InlineData("172.31.255.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]   // carrier-grade NAT, used inside cloud networks
    [InlineData("169.254.169.254", false)]
    [InlineData("169.254.170.2", false)] // container metadata
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Only_public_internet_addresses_count_as_public(string address, bool expected) =>
        Assert.Equal(expected, OutboundGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://shop.example.com/kabuor", true)]
    [InlineData("https://hooks.example.co.ke:443/in", true)]
    [InlineData("http://shop.example.com/kabuor", false)]
    [InlineData("https://localhost/hook", false)]
    [InlineData("https://intranet/hook", false)]
    [InlineData("https://db.internal/hook", false)]
    [InlineData("https://printer.local/hook", false)]
    [InlineData("https://172.20.0.5/hook", false)]
    [InlineData("https://100.64.1.1/hook", false)]
    [InlineData("https://2852039166/hook", false)] // 169.254.169.254 written as one number
    [InlineData("https://[::1]/hook", false)]
    [InlineData("https://[fd12::1]/hook", false)]
    [InlineData("https://shop.example.com:6379/hook", false)]
    public void Typed_webhook_addresses_are_checked(string url, bool allowed) =>
        Assert.Equal(allowed, OutboundGuard.Problem(new Uri(url)) is null);

    [Theory]
    [InlineData("https://127.0.0.1:9/hook")]
    [InlineData("https://localhost:9/hook")] // a name: refused after it resolves, at the moment of sending
    [InlineData("https://[::1]:9/hook")]
    public async Task Sending_never_connects_inside_the_private_network(string url)
    {
        using var client = new HttpClient(OutboundGuard.Handler());
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(url, new StringContent("{}")));
        Assert.Contains("private network", e.Message);
    }

    [Fact]
    public async Task Webhook_registration_refuses_ranges_the_old_check_missed()
    {
        var owner = await Owner();
        foreach (var url in new[] { "https://172.20.0.5/x", "https://100.64.0.1/x", "https://169.254.170.2/v2/credentials", "https://metadata.internal/x" })
        {
            var res = await owner.PostAsJsonAsync("/api/settings/webhooks", new { url, events = "invoice.paid" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/settings/webhooks", new { url = "https://shop.example.com/kabuor", events = "invoice.paid" })).StatusCode);
    }

    [Fact]
    public async Task A_forged_Host_header_never_reaches_the_M_Pesa_callback_address()
    {
        var owner = await Owner();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/settings/company");
        req.Headers.Host = "evil.example";
        var res = await owner.SendAsync(req);
        var company = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var url = company.GetProperty("mpesaConfirmationUrl").GetString()!;
        Assert.StartsWith($"{PublicUrlFactory.Url}/api/mpesa/c2b/", url);
        Assert.DoesNotContain("evil.example", url);
    }

    [Fact]
    public async Task An_invoice_that_never_was_a_quote_has_nothing_to_compare()
    {
        var owner = await Owner();
        var customer = JsonDocument.Parse(await (await owner.PostAsJsonAsync("/api/customers", new { name = "Walk-in Buyer" })).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var doc = JsonDocument.Parse(await (await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { description = "Delivery", quantity = 1, unitPrice = 1_500_00 } } })).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.GetAsync($"/api/sales/{doc}/variance")).StatusCode);
    }

    [Theory]
    [InlineData("https://erp.kabuor.co.ke/", "erp.kabuor.test", "https://kabuor.onrender.com", "https://erp.kabuor.co.ke")]
    [InlineData(null, "erp.kabuor.co.ke", "https://kabuor.onrender.com", "https://erp.kabuor.co.ke")] // Vercel's production domain
    [InlineData(null, null, "https://kabuor.onrender.com", "https://kabuor.onrender.com")]
    [InlineData(null, null, null, null)] // development: the request's own host
    public void The_public_address_comes_from_configuration_in_this_order(string? configured, string? vercel, string? render, string? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:PublicUrl"] = configured, ["VERCEL_PROJECT_PRODUCTION_URL"] = vercel, ["RENDER_EXTERNAL_URL"] = render,
        }).Build();
        Assert.Equal(expected, PublicUrl.Configured(config));
    }
}
