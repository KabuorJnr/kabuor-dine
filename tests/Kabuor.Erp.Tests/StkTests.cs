// "Pay with M-Pesa" from an invoice: the prompt goes to Safaricom with the invoice number and the balance, the
// Daraja secrets never come back out of the API, and Safaricom's confirmation pays exactly that invoice, once.
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

public sealed class FakeDaraja : HttpMessageHandler
{
    public readonly ConcurrentQueue<JsonObject> Pushes = new();
    public string? RejectNext { get; set; }
    int n;

    public void Reset()
    {
        while (Pushes.TryDequeue(out _)) { }
        RejectNext = null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (req.RequestUri!.AbsolutePath.EndsWith("/oauth/v1/generate"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "token", expires_in = "3599" }) };
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
        if (RejectNext is { } reject)
        {
            RejectNext = null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { ResponseCode = "1", errorMessage = reject, CustomerMessage = reject }),
            };
        }
        Pushes.Enqueue(body);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { MerchantRequestID = "m", CheckoutRequestID = $"ws_CO_{Interlocked.Increment(ref n)}", ResponseCode = "0", CustomerMessage = "Success" }) };
    }
}

public sealed class StkFactory : WebApplicationFactory<Program>
{
    public readonly FakeDaraja Daraja = new();
    readonly string _db = Path.Combine(Path.GetTempPath(), $"kabuor-erp-stk-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Erp", ErpFactory.Pg is null ? $"Data Source={_db}" : $"{ErpFactory.Pg};Database=kabuor_erp_stk_{Guid.NewGuid():N}");
        builder.UseSetting("Jwt:Key", "test-key-test-key-test-key-test-key-0123456789");
        builder.UseSetting("Agents:Disabled", "true");
        builder.UseSetting("Owners:OversightByDefault", "false"); // the owner does the work in these tests
        builder.UseSetting("Billing:PayToStart", "false");
        builder.ConfigureTestServices(s => s.AddHttpClient("daraja").ConfigurePrimaryHttpMessageHandler(() => Daraja));
    }
}

public class StkTests : IClassFixture<StkFactory>, IDisposable
{
    readonly StkFactory factory;

    public StkTests(StkFactory factory)
    {
        this.factory = factory;
        factory.Daraja.Reset();
    }

    public void Dispose() => factory.Daraja.Reset();

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task An_invoice_prompt_is_paid_by_Safaricoms_confirmation_exactly_once()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Prompt Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());

        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Wanjiru Hardware", phone = "0722000111" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { description = "Cement", quantity = 2, unitPrice = 850_00 } } }));
        var id = inv.GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));

        // Not set up yet: a clear message, nothing sent.
        var early = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000111" });
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Contains("Daraja", await early.Content.ReadAsStringAsync());

        var setup = await Ok(await c.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "paybill", shortcode = "174379", consumerKey = "ck", consumerSecret = "very-secret", passkey = "very-secret-passkey" }));
        Assert.True(setup.GetProperty("ready").GetBoolean());
        Assert.DoesNotContain("very-secret", setup.ToString());
        Assert.DoesNotContain("very-secret", await (await c.GetAsync("/api/settings/mpesa-prompt")).Content.ReadAsStringAsync());

        var r = await Ok(await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722 000 111" }));
        Assert.Equal("Pending", r.GetProperty("status").GetString());
        Assert.True(factory.Daraja.Pushes.TryDequeue(out var push));
        Assert.Equal(1700, push!["Amount"]!.GetValue<long>());
        Assert.Equal("254722000111", push["PartyA"]!.GetValue<string>());
        Assert.Equal(inv.GetProperty("number").GetString(), push["AccountReference"]!.GetValue<string>());
        var callback = new Uri(push["CallBackURL"]!.GetValue<string>()).AbsolutePath;

        // Another prompt straight away is refused (the customer is still entering their PIN).
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000111" })).StatusCode);

        var paid = new
        {
            Body = new
            {
                stkCallback = new
                {
                    MerchantRequestID = "m", CheckoutRequestID = r.GetProperty("checkoutId").GetString(), ResultCode = 0, ResultDesc = "The service request is processed successfully.",
                    CallbackMetadata = new { Item = new object[] { new { Name = "Amount", Value = 1700 }, new { Name = "MpesaReceiptNumber", Value = "TJK7ABC123" }, new { Name = "PhoneNumber", Value = 254722000111 } } },
                },
            },
        };
        var anon = factory.CreateClient();
        await Ok(await anon.PostAsJsonAsync(callback, paid));
        await Ok(await anon.PostAsJsonAsync(callback, paid)); // Safaricom retries

        var doc = (await Ok(await c.GetAsync($"/api/sales/{id}"))).GetProperty("document");
        Assert.Equal("Paid", doc.GetProperty("status").GetString());
        Assert.Equal(1700_00, doc.GetProperty("paid").GetInt64());
        var requests = await Ok(await c.GetAsync($"/api/sales/{id}/mpesa-requests"));
        Assert.Equal("TJK7ABC123", requests[0].GetProperty("receipt").GetString());
        var payments = (await c.GetAsync("/api/payments")).Content.ReadAsStringAsync();
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(await payments, "TJK7ABC123"));
    }

    [Fact]
    public async Task Customer_cancelled_prompt_marks_request_cancelled_and_leaves_invoice_unpaid()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Cancel Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());

        await Ok(await c.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "paybill", shortcode = "174379", consumerKey = "ck", consumerSecret = "secret", passkey = "passkey" }));

        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Kamau Hardware", phone = "0722000222" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { description = "Paint", quantity = 1, unitPrice = 1500_00 } } }));
        var id = inv.GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));

        var r = await Ok(await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000222" }));
        Assert.Equal("Pending", r.GetProperty("status").GetString());
        Assert.True(factory.Daraja.Pushes.TryDequeue(out var push));
        var callback = new Uri(push!["CallBackURL"]!.GetValue<string>()).AbsolutePath;

        // Customer cancels on their phone (ResultCode 1032)
        var cancelled = new
        {
            Body = new
            {
                stkCallback = new
                {
                    MerchantRequestID = "m",
                    CheckoutRequestID = r.GetProperty("checkoutId").GetString(),
                    ResultCode = 1032,
                    ResultDesc = "[STK_CB - ]Request cancelled by user.",
                },
            },
        };
        var anon = factory.CreateClient();
        await Ok(await anon.PostAsJsonAsync(callback, cancelled));

        // Invoice remains Sent / unpaid
        var doc = (await Ok(await c.GetAsync($"/api/sales/{id}"))).GetProperty("document");
        Assert.Equal("Sent", doc.GetProperty("status").GetString());
        Assert.Equal(0, doc.GetProperty("paid").GetInt64());

        var requests = await Ok(await c.GetAsync($"/api/sales/{id}/mpesa-requests"));
        Assert.Equal("Cancelled", requests[0].GetProperty("status").GetString());
        Assert.Contains("cancelled", requests[0].GetProperty("note").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Can request again immediately after cancellation
        var retry = await Ok(await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000222" }));
        Assert.Equal("Pending", retry.GetProperty("status").GetString());
        Assert.True(factory.Daraja.Pushes.TryDequeue(out _));
    }

    [Fact]
    public async Task Partial_prompt_payments_update_invoice_balance_and_status()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Partial Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());

        await Ok(await c.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "paybill", shortcode = "174379", consumerKey = "ck", consumerSecret = "secret", passkey = "passkey" }));

        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Mwangi Stores", phone = "0722000333" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { description = "Timber", quantity = 5, unitPrice = 600_00 } } }));
        var id = inv.GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));

        // Prompt for partial amount: KSh 1,000 (100,000 cents) out of KSh 3,000
        var r1 = await Ok(await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000333", amount = 1000_00 }));
        Assert.True(factory.Daraja.Pushes.TryDequeue(out var push1));
        Assert.Equal(1000, push1!["Amount"]!.GetValue<long>());
        var callback1 = new Uri(push1["CallBackURL"]!.GetValue<string>()).AbsolutePath;

        var anon = factory.CreateClient();
        var paid1 = new
        {
            Body = new
            {
                stkCallback = new
                {
                    MerchantRequestID = "m",
                    CheckoutRequestID = r1.GetProperty("checkoutId").GetString(),
                    ResultCode = 0,
                    ResultDesc = "Success",
                    CallbackMetadata = new { Item = new object[] { new { Name = "Amount", Value = 1000 }, new { Name = "MpesaReceiptNumber", Value = "RCPT001" }, new { Name = "PhoneNumber", Value = 254722000333 } } },
                },
            },
        };
        await Ok(await anon.PostAsJsonAsync(callback1, paid1));

        // Invoice is now PartPaid with balance 2,000 KES
        var doc1 = (await Ok(await c.GetAsync($"/api/sales/{id}"))).GetProperty("document");
        Assert.Equal("PartPaid", doc1.GetProperty("status").GetString());
        Assert.Equal(1000_00, doc1.GetProperty("paid").GetInt64());

        // Second prompt for remaining KSh 2,000 (default amount when omitted is remaining balance)
        var r2 = await Ok(await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000333" }));
        Assert.True(factory.Daraja.Pushes.TryDequeue(out var push2));
        Assert.Equal(2000, push2!["Amount"]!.GetValue<long>());
        var callback2 = new Uri(push2["CallBackURL"]!.GetValue<string>()).AbsolutePath;

        var paid2 = new
        {
            Body = new
            {
                stkCallback = new
                {
                    MerchantRequestID = "m",
                    CheckoutRequestID = r2.GetProperty("checkoutId").GetString(),
                    ResultCode = 0,
                    ResultDesc = "Success",
                    CallbackMetadata = new { Item = new object[] { new { Name = "Amount", Value = 2000 }, new { Name = "MpesaReceiptNumber", Value = "RCPT002" }, new { Name = "PhoneNumber", Value = 254722000333 } } },
                },
            },
        };
        await Ok(await anon.PostAsJsonAsync(callback2, paid2));

        // Invoice is now fully Paid
        var doc2 = (await Ok(await c.GetAsync($"/api/sales/{id}"))).GetProperty("document");
        Assert.Equal("Paid", doc2.GetProperty("status").GetString());
        Assert.Equal(3000_00, doc2.GetProperty("paid").GetInt64());
    }

    [Fact]
    public async Task Till_prompt_uses_BuyGoods_and_till_as_PartyB()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Till Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());

        // Setup Till: store shortcode = 123456, till = 654321
        await Ok(await c.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "till", shortcode = "123456", till = "654321", consumerKey = "ck", consumerSecret = "secret", passkey = "passkey" }));

        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Akinyi Shopper", phone = "0711223344" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { description = "Handbag", quantity = 1, unitPrice = 2500_00 } } }));
        var id = inv.GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));

        await Ok(await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0711 223 344" }));
        Assert.True(factory.Daraja.Pushes.TryDequeue(out var push));
        Assert.Equal("CustomerBuyGoodsOnline", push!["TransactionType"]!.GetValue<string>());
        Assert.Equal("123456", push["BusinessShortCode"]!.GetValue<string>());
        Assert.Equal("654321", push["PartyB"]!.GetValue<string>());
        Assert.Equal("254711223344", push["PartyA"]!.GetValue<string>());
        Assert.Equal(2500, push["Amount"]!.GetValue<long>());
    }

    [Fact]
    public async Task Invalid_prompt_parameters_and_states_are_rejected()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Invalid Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());

        await Ok(await c.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "paybill", shortcode = "174379", consumerKey = "ck", consumerSecret = "secret", passkey = "passkey" }));

        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "John Doe", phone = "0722123456" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { description = "Widget", quantity = 1, unitPrice = 1000_00 } } }));
        var id = inv.GetProperty("id").GetGuid();

        // 1. Unissued (Draft) invoice rejected
        var draftRes = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722123456" });
        Assert.Equal(HttpStatusCode.BadRequest, draftRes.StatusCode);
        Assert.Contains("unpaid invoice", await draftRes.Content.ReadAsStringAsync());

        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));

        // 2. Invalid phone format rejected
        var badPhone = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "not-a-phone" });
        Assert.Equal(HttpStatusCode.BadRequest, badPhone.StatusCode);

        // 3. Amount exceeding balance rejected (balance is 1,000, asking for 1,500)
        var overAmount = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722123456", amount = 1500_00 });
        Assert.Equal(HttpStatusCode.BadRequest, overAmount.StatusCode);

        // 4. Amount less than 1 KSh rejected
        var underAmount = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722123456", amount = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, underAmount.StatusCode);

        // 5. Non-whole shilling rejected
        var nonWhole = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722123456", amount = 100_50 });
        Assert.Equal(HttpStatusCode.BadRequest, nonWhole.StatusCode);
        Assert.Contains("whole shillings", await nonWhole.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Daraja_rejection_returns_actionable_error()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Refuse Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());

        await Ok(await c.PutAsJsonAsync("/api/settings/mpesa-prompt", new { environment = "sandbox", kind = "paybill", shortcode = "174379", consumerKey = "ck", consumerSecret = "secret", passkey = "passkey" }));

        var cust = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Customer", phone = "0722000999" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = cust.GetProperty("id").GetGuid(), lines = new[] { new { description = "Tools", quantity = 1, unitPrice = 500_00 } } }));
        var id = inv.GetProperty("id").GetGuid();
        await Ok(await c.PostAsync($"/api/sales/{id}/issue", null));

        // Configure FakeDaraja to simulate Daraja rejection (responseCode != 0)
        factory.Daraja.RejectNext = "Rule 29: Subscriber is unreachable";
        var err = await c.PostAsJsonAsync($"/api/sales/{id}/mpesa-request", new { phone = "0722000999" });
        Assert.Equal(HttpStatusCode.BadGateway, err.StatusCode);
        var body = await err.Content.ReadAsStringAsync();
        Assert.Contains("M-Pesa didn't send the prompt", body);
        Assert.Contains("Subscriber is unreachable", body);
    }
}
