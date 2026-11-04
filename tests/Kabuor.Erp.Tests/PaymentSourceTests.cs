// Kabuor Verified, step 1: every payment says how it reached the books, and only an M-Pesa callback from Safaricom's
// own addresses counts as confirmed. A business can see (and post to) its confirmation address, so the key in the URL
// proves nothing; a request body can't claim a source either.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class PaymentSourceTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : Json(body);
    }

    async Task<HttpClient> SignUp(string company)
    {
        var c = factory.CreateClient();
        var session = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Akinyi Owner", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());
        return c;
    }

    /// <summary>The test server has no network caller; this lets a test say which address a request came from.</summary>
    sealed class CallerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext http, RequestDelegate n) =>
            {
                if (IPAddress.TryParse(http.Request.Headers["X-Test-Caller"], out var ip)) http.Connection.RemoteIpAddress = ip;
                return n(http);
            });
            next(app);
        };
    }

    async Task<string> ConfirmationUrl(HttpClient c)
    {
        var settings = await Ok(await c.GetAsync("/api/settings/company"));
        return new Uri(settings.GetProperty("mpesaConfirmationUrl").GetString()!).PathAndQuery;
    }

    static object C2B(string id, string amount = "500.00") => new { TransID = id, TransAmount = amount, TransTime = "20261020103000", BusinessShortCode = "", BillRefNumber = "", MSISDN = "254722000111", FirstName = "WANJIKU" };

    static async Task<JsonElement> Payments(HttpClient c, string status = "all") =>
        await Ok(await c.GetAsync($"/api/payments?status={status}"));

    static JsonElement ByRef(JsonElement list, string reference) =>
        list.GetProperty("page").GetProperty("items").EnumerateArray().Single(p => p.GetProperty("reference").GetString() == reference);

    [Fact]
    public async Task A_payment_typed_in_is_manual_whatever_the_body_claims()
    {
        var c = await SignUp("Typed Ltd");
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 50000, reference = "QWE123TYPED", source = "MpesaConfirmation", fromSafaricom = true, callbackIp = "196.201.214.200" }));
        var p = ByRef(await Payments(c), "QWE123TYPED");
        Assert.Equal("Manual", p.GetProperty("source").GetString());
        Assert.False(p.GetProperty("fromSafaricom").GetBoolean());
        Assert.Equal("", p.GetProperty("callbackIp").GetString());
    }

    [Fact]
    public async Task Only_a_confirmation_from_Safaricoms_addresses_counts_as_confirmed()
    {
        var app = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, CallerFromHeader>()));
        var c = app.CreateClient();
        var session = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Paybill Ltd", ownerName = "Akinyi Owner", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());
        var url = await ConfirmationUrl(c);

        async Task Post(string id, string? from)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(C2B(id)) };
            if (from is not null) req.Headers.Add("X-Test-Caller", from);
            await Ok(await app.CreateClient().SendAsync(req));
        }
        await Post("SAFCOM0001", "196.201.214.200");
        await Post("SAFCOM0002", "::ffff:196.201.213.44"); // IPv4 seen through an IPv6 socket
        await Post("FORGED0001", "41.90.10.20");           // the owner's own laptop
        await Post("NOADDR0001", null);

        var all = await Payments(c);
        foreach (var id in new[] { "SAFCOM0001", "SAFCOM0002" })
        {
            var p = ByRef(all, id);
            Assert.Equal("MpesaConfirmation", p.GetProperty("source").GetString());
            Assert.True(p.GetProperty("fromSafaricom").GetBoolean(), id);
        }
        var forged = ByRef(all, "FORGED0001");
        Assert.Equal("MpesaConfirmation", forged.GetProperty("source").GetString()); // still recorded: money mustn't go missing
        Assert.False(forged.GetProperty("fromSafaricom").GetBoolean());
        Assert.Equal("41.90.10.20", forged.GetProperty("callbackIp").GetString());
        Assert.False(ByRef(all, "NOADDR0001").GetProperty("fromSafaricom").GetBoolean());

        Assert.Equal(2, all.GetProperty("counts").GetProperty("confirmed").GetInt32());
        var confirmed = await Payments(c, "confirmed");
        Assert.Equal(new[] { "SAFCOM0001", "SAFCOM0002" }, confirmed.GetProperty("page").GetProperty("items").EnumerateArray().Select(p => p.GetProperty("reference").GetString()).Order());
    }

    [Theory]
    [InlineData("196.201.214.200", true)]
    [InlineData("::ffff:196.201.214.206", true)]
    [InlineData("196.201.214.201", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("::1", false)]
    public void Safaricom_addresses_are_matched_exactly(string ip, bool expected) =>
        Assert.Equal(expected, SafaricomCallers.IsIn(IPAddress.Parse(ip), SafaricomCallers.DefaultList));

    [Fact]
    public void Ranges_can_be_configured_and_no_address_is_never_Safaricom()
    {
        Assert.True(SafaricomCallers.IsIn(IPAddress.Parse("196.201.215.9"), "196.201.215.0/24"));
        Assert.False(SafaricomCallers.IsIn(IPAddress.Parse("196.201.216.9"), "196.201.215.0/24, nonsense"));
        Assert.False(SafaricomCallers.IsIn(null, SafaricomCallers.DefaultList));
    }

    [Fact]
    public async Task Earlier_payments_get_a_source_from_what_the_books_already_show()
    {
        var c = await SignUp("Older Books Ltd");
        await Ok(await c.PostAsJsonAsync(await ConfirmationUrl(c), C2B("OLDC2B0001")));
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Cash", amount = 20000, reference = "SALE7-POS" }));
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Cash", amount = 30000, reference = "COUNTER-1" }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        // As they were before payments carried a source.
        await db.Payments.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(p => p.Source, PaymentSource.Unknown).SetProperty(p => p.FromSafaricom, false).SetProperty(p => p.CallbackIp, ""));

        Assert.True(await PaymentSourceBackfill.Run(db) >= 3);
        var all = await Payments(c);
        Assert.Equal("MpesaConfirmation", ByRef(all, "OLDC2B0001").GetProperty("source").GetString());
        Assert.False(ByRef(all, "OLDC2B0001").GetProperty("fromSafaricom").GetBoolean()); // no address on record: not proof
        Assert.Equal("Till", ByRef(all, "SALE7-POS").GetProperty("source").GetString());
        Assert.Equal("Manual", ByRef(all, "COUNTER-1").GetProperty("source").GetString());

        Assert.Equal(0, await PaymentSourceBackfill.Run(db)); // runs again harmlessly
    }
}
