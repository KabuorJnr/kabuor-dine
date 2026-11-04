// Invoicing in another currency: the customer sees and owes dollars, the books, VAT and eTIMS stay in shillings at
// the invoice's rate, and the shillings that actually arrive settle it, with the difference an exchange gain or loss.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class ForeignCurrencyTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient C, Guid Customer)> Business()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Lake Basin Exporters", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Kampala Traders Ltd", currency = "usd" }));
        Assert.Equal("USD", customer.GetProperty("currency").GetString());
        return (c, customer.GetProperty("id").GetGuid());
    }

    static async Task<JsonElement> Account(HttpClient c, string code) =>
        (await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("accounts").EnumerateArray().FirstOrDefault(a => a.GetProperty("code").GetString() == code);

    async Task<JsonElement> UsdInvoice(HttpClient c, Guid customer)
    {
        // 2 × USD 500 (VAT inclusive) at KSh 129.50: KSh 64,750 a unit, KSh 129,500 in all.
        var draft = await Ok(await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice", customerId = customer, currency = "USD", rate = 129.5m,
            lines = new[] { new { description = "Avocado, 1 tonne", quantity = 2, unitPrice = 50000, taxType = "B_Standard16" } },
        }));
        Assert.Equal("USD", draft.GetProperty("currency").GetString());
        Assert.Equal(100000, draft.GetProperty("fxTotal").GetInt64());
        Assert.Equal(12950000, draft.GetProperty("total").GetInt64());
        Assert.Equal(6475000, draft.GetProperty("lines")[0].GetProperty("unitPrice").GetInt64());
        Assert.Equal(50000, draft.GetProperty("lines")[0].GetProperty("fxUnitPrice").GetInt64());
        Assert.Equal(1786207, draft.GetProperty("tax").GetInt64()); // VAT in shillings, as KRA reads it
        var issued = await Ok(await c.PostAsync($"/api/sales/{draft.GetProperty("id")}/issue", null));
        Assert.Equal("Signed", issued.GetProperty("etimsStatus").GetString());
        return issued;
    }

    [Fact]
    public async Task A_dollar_invoice_is_booked_in_shillings_and_paying_it_posts_the_exchange_difference()
    {
        var (c, customer) = await Business();
        var inv = await UsdInvoice(c, customer);
        var id = inv.GetProperty("id").GetGuid();
        Assert.Equal(12950000, (await Account(c, "1100")).GetProperty("debit").GetInt64());

        // Half paid when the dollar is at 132: KSh 66,000 lands for USD 500 booked at KSh 64,750, a KSh 1,250 gain.
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Bank", amount = 6600000, reference = "TT-001", customerId = customer, invoiceId = id, fxAmount = 50000 }));
        var half = (await Ok(await c.GetAsync($"/api/sales/{id}"))).GetProperty("document");
        Assert.Equal("PartPaid", half.GetProperty("status").GetString());
        Assert.Equal(50000, half.GetProperty("fxBalance").GetInt64());
        Assert.Equal(6475000, half.GetProperty("balance").GetInt64());
        Assert.Equal(125000, (await Account(c, "4900")).GetProperty("credit").GetInt64());

        // The rest when it's at 127: KSh 63,500 for the last USD 500, a KSh 1,250 loss; the invoice is closed.
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Bank", amount = 6350000, reference = "TT-002", customerId = customer, invoiceId = id, fxAmount = 50000 }));
        var paid = await Ok(await c.GetAsync($"/api/sales/{id}"));
        Assert.Equal("Paid", paid.GetProperty("document").GetProperty("status").GetString());
        Assert.Equal(0, paid.GetProperty("document").GetProperty("balance").GetInt64());
        Assert.Equal(50000, paid.GetProperty("payments")[1].GetProperty("fxAmount").GetInt64());

        var tb = await Ok(await c.GetAsync("/api/reports/trial-balance"));
        Assert.True(tb.GetProperty("balanced").GetBoolean());
        var ar = await Account(c, "1100");
        Assert.Equal(ar.GetProperty("debit").GetInt64(), ar.GetProperty("credit").GetInt64()); // nothing left owed
        var fx = await Account(c, "4900");
        Assert.Equal(fx.GetProperty("debit").GetInt64(), fx.GetProperty("credit").GetInt64()); // the gain and the loss cancel
        Assert.Equal(12950000, (await Account(c, "1020")).GetProperty("debit").GetInt64());

        var rates = await Ok(await c.GetAsync("/api/sales/rates"));
        Assert.Equal(129.5m, rates[0].GetProperty("rate").GetDecimal());
    }

    [Fact]
    public async Task Money_for_a_dollar_invoice_needs_the_dollars_it_covers_and_never_matches_by_itself()
    {
        var (c, customer) = await Business();
        var inv = await UsdInvoice(c, customer);
        var id = inv.GetProperty("id").GetGuid();

        var missing = await c.PostAsJsonAsync("/api/payments", new { method = "Bank", amount = 6600000, reference = "TT-009", customerId = customer, invoiceId = id });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("USD", await missing.Content.ReadAsStringAsync());
        var tooMuch = await c.PostAsJsonAsync("/api/payments", new { method = "Bank", amount = 6600000, reference = "TT-010", customerId = customer, invoiceId = id, fxAmount = 100001 });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        // Refused before anything was recorded.
        Assert.Equal(0, (await Ok(await c.GetAsync("/api/payments"))).GetProperty("counts").GetProperty("all").GetInt32());

        // M-Pesa money quoting the invoice number waits for a person instead of being applied in shillings.
        var mpesa = await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 12950000, reference = "SKA9Z8Y7X6", accountRef = inv.GetProperty("number").GetString() }));
        Assert.Equal("Unmatched", mpesa.GetProperty("status").GetString());
        Assert.Equal("Sent", (await Ok(await c.GetAsync($"/api/sales/{id}"))).GetProperty("document").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("XYZ", 100.0)]
    [InlineData("USD", null)]
    [InlineData("USD", 0.0)]
    [InlineData("USD", 1000000.0)]
    public async Task An_unknown_currency_or_a_missing_or_absurd_rate_is_refused(string currency, double? rate)
    {
        var (c, customer) = await Business();
        var res = await c.PostAsJsonAsync("/api/sales", new
        {
            kind = "Invoice", customerId = customer, currency, rate = (decimal?)rate,
            lines = new[] { new { description = "Avocado", quantity = 1, unitPrice = 1000, taxType = "B_Standard16" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_shilling_invoice_is_unchanged_and_a_dollar_quote_becomes_a_dollar_invoice()
    {
        var (c, customer) = await Business();
        var kes = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { description = "Crates", quantity = 1, unitPrice = 116000, taxType = "B_Standard16" } } }));
        Assert.Equal("", kes.GetProperty("currency").GetString());
        Assert.Equal(116000, kes.GetProperty("total").GetInt64());
        Assert.Equal(0, kes.GetProperty("fxTotal").GetInt64());

        var quote = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Quote", customerId = customer, currency = "USD", rate = 130m, lines = new[] { new { description = "Avocado", quantity = 3, unitPrice = 20000, taxType = "B_Standard16" } } }));
        var inv = await Ok(await c.PostAsync($"/api/sales/{quote.GetProperty("id")}/to-invoice", null));
        Assert.Equal("USD", inv.GetProperty("currency").GetString());
        Assert.Equal(60000, inv.GetProperty("fxTotal").GetInt64());
        Assert.Equal(7800000, inv.GetProperty("total").GetInt64());
    }
}
