// Paying to start: a new business pays by M-Pesa before anything opens. Until then every other route answers 402;
// Safaricom's answer has to carry the secret, the full amount, and only counts once; a live server never hands out
// a plan for a "test" payment; and M-Pesa prompts are limited per business, not across all of them.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public sealed class PayToStartFactory : ErpFactory
{
    public const string CallbackSecret = "cb-secret-0123456789abcdef";
    protected override bool PayToStart => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Billing:Mpesa:CallbackSecret", CallbackSecret);
    }
}

/// <summary>A live server (Production) without M-Pesa keys.</summary>
public sealed class LiveNoKeysFactory : ErpFactory
{
    protected override bool PayToStart => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

public class PayToStartTests(PayToStartFactory factory) : IClassFixture<PayToStartFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    internal static async Task<(HttpClient Client, JsonElement Session)> SignUp(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> f)
    {
        var c = f.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"New Shop {Guid.NewGuid():N}", ownerName = "Akinyi Owner", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    [Fact]
    public async Task Nothing_opens_until_the_first_payment_then_everything_does()
    {
        var (c, session) = await SignUp(factory);
        Assert.True(session.GetProperty("company").GetProperty("awaitingFirstPayment").GetBoolean());

        foreach (var (method, path) in new[] { ("GET", "/api/today"), ("GET", "/api/customers"), ("POST", "/api/customers"), ("GET", "/api/settings/company"), ("POST", "/api/settings/users"), ("GET", "/api/onboarding/status") })
        {
            var res = await c.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? JsonContent.Create(new { name = "Sneaky" }) : null });
            Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
            Assert.Contains("pay-to-start", await res.Content.ReadAsStringAsync());
        }

        var billing = await Ok(await c.GetAsync("/api/billing"));
        Assert.True(billing.GetProperty("awaitingFirstPayment").GetBoolean());
        Assert.False(billing.GetProperty("onTrial").GetBoolean()); // no free trial when paying to start
        Assert.Equal("254712345678", billing.GetProperty("phone").GetString());

        // Development without keys: a clearly marked test payment opens it.
        var paid = await Ok(await c.PostAsJsonAsync("/api/billing/pay", new { plan = "business", months = 1, phone = "0712345678" }));
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.True(paid.GetProperty("test").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/today")).StatusCode);
        await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "First Customer" }));
        var after = await Ok(await c.GetAsync("/api/billing"));
        Assert.False(after.GetProperty("awaitingFirstPayment").GetBoolean());
        Assert.Equal("business", after.GetProperty("plan").GetString());
        Assert.InRange(after.GetProperty("daysLeft").GetInt32(), 28, 31);

        // The setup checklist is what comes next.
        var steps = (await Ok(await c.GetAsync("/api/onboarding/status"))).GetProperty("steps");
        Assert.Contains(steps.EnumerateArray(), s => s.GetProperty("key").GetString() == "customers" && s.GetProperty("done").GetBoolean());
    }

    async Task<(Guid Company, Guid Payment)> Pending(JsonElement session, string checkout, long amount)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var company = session.GetProperty("company").GetProperty("id").GetGuid();
        var p = new BillingPayment { CompanyId = company, Plan = "business", Months = 1, Amount = amount, Phone = "254712345678", CheckoutId = checkout };
        db.BillingPayments.Add(p);
        await db.SaveChangesAsync();
        return (company, p.Id);
    }

    static object Callback(string checkout, int result, decimal amount, string receipt) => new
    {
        Body = new
        {
            stkCallback = new
            {
                MerchantRequestID = "m-1", CheckoutRequestID = checkout, ResultCode = result, ResultDesc = result == 0 ? "The service request is processed successfully." : "Request cancelled by user",
                CallbackMetadata = new { Item = new object[] { new { Name = "Amount", Value = amount }, new { Name = "MpesaReceiptNumber", Value = receipt }, new { Name = "PhoneNumber", Value = 254712345678 } } },
            },
        },
    };

    async Task<Company> CompanyOf(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ErpDb>().Companies.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    async Task<BillingPayment> PaymentOf(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ErpDb>().BillingPayments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task Safaricoms_answer_needs_the_secret_and_the_full_amount_and_counts_once()
    {
        var (c, session) = await SignUp(factory);
        var safaricom = factory.CreateClient();
        var good = $"/api/billing/mpesa/{PayToStartFactory.CallbackSecret}";

        // Forged: the wrong secret (or none) gets nothing.
        var (company, short_) = await Pending(session, "ws_CO_short", 4_500_00);
        Assert.Equal(HttpStatusCode.NotFound, (await safaricom.PostAsJsonAsync("/api/billing/mpesa/guess", Callback("ws_CO_short", 0, 4500, "QAB1"))).StatusCode);
        Assert.True((await CompanyOf(company)).AwaitingFirstPayment);

        // Paid less than asked: not activated, and the shortfall is recorded for Kabuor to sort out.
        await Ok(await safaricom.PostAsJsonAsync(good, Callback("ws_CO_short", 0, 10, "QAB2SHORT")));
        Assert.True((await CompanyOf(company)).AwaitingFirstPayment);
        var shortPaid = await PaymentOf(short_);
        Assert.Equal(BillingStatus.Failed, shortPaid.Status);
        Assert.Contains("QAB2SHORT", shortPaid.Note);

        // Cancelled on the phone: nothing changes.
        var (_, cancelled) = await Pending(session, "ws_CO_cancel", 4_500_00);
        await Ok(await safaricom.PostAsJsonAsync(good, Callback("ws_CO_cancel", 1032, 0, "")));
        Assert.Equal(BillingStatus.Failed, (await PaymentOf(cancelled)).Status);
        Assert.True((await CompanyOf(company)).AwaitingFirstPayment);

        // Paid in full: open, for a month.
        var (_, full) = await Pending(session, "ws_CO_full", 4_500_00);
        await Ok(await safaricom.PostAsJsonAsync(good, Callback("ws_CO_full", 0, 4500, "QAB3FULL")));
        var opened = await CompanyOf(company);
        Assert.False(opened.AwaitingFirstPayment);
        Assert.Equal("business", opened.Plan);
        Assert.Equal("QAB3FULL", (await PaymentOf(full)).Receipt);

        // Safaricom retries (or someone replays it): still one month, not two.
        await Ok(await safaricom.PostAsJsonAsync(good, Callback("ws_CO_full", 0, 4500, "QAB3FULL")));
        Assert.Equal(opened.PaidUntil, (await CompanyOf(company)).PaidUntil);

        // A callback for a prompt nobody sent is ignored.
        await Ok(await safaricom.PostAsJsonAsync(good, Callback("ws_CO_unknown", 0, 9500, "QAB4")));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/today")).StatusCode);
    }

    [Fact]
    public async Task One_prompt_at_a_time_and_prompts_are_limited_per_business_not_for_everyone()
    {
        var (a, aSession) = await SignUp(factory);
        await Pending(aSession, $"ws_CO_wait_{Guid.NewGuid():N}", 4_500_00);
        var again = await a.PostAsJsonAsync("/api/billing/pay", new { plan = "business", months = 1, phone = "0712345678" });
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Contains("prompt was just sent", await again.Content.ReadAsStringAsync());

        // Ten an hour for one business...
        var (b, _) = await SignUp(factory);
        for (var i = 0; i < 10; i++) await Ok(await b.PostAsJsonAsync("/api/billing/pay", new { plan = "starter", months = 1, phone = "0712345678" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await b.PostAsJsonAsync("/api/billing/pay", new { plan = "starter", months = 1, phone = "0712345678" })).StatusCode);
        // ...and the next business isn't held up by it.
        var (c, _) = await SignUp(factory);
        await Ok(await c.PostAsJsonAsync("/api/billing/pay", new { plan = "starter", months = 1, phone = "0712345678" }));
    }
}

public class LivePaymentTests(LiveNoKeysFactory factory) : IClassFixture<LiveNoKeysFactory>
{
    [Fact]
    public async Task A_live_server_without_MPesa_keys_never_hands_out_a_plan_for_free()
    {
        var (c, _) = await PayToStartTests.SignUp(factory);
        var billing = JsonDocument.Parse(await (await c.GetAsync("/api/billing")).Content.ReadAsStringAsync()).RootElement;
        Assert.False(billing.GetProperty("canPay").GetBoolean());
        var pay = await c.PostAsJsonAsync("/api/billing/pay", new { plan = "growth", months = 12, phone = "0712345678" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pay.StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.GetAsync("/api/today")).StatusCode);
    }
}
