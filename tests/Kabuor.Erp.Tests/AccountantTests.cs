// What an accountant needs: a balance sheet that balances, cash flow that ties to M-Pesa/bank/cash, withholding
// certificates that close invoices and cut the VAT payable, iTax VAT3 registers, and statement imports that match
// the books and leave a short, fixable list.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Accounting;

namespace Kabuor.Erp.Tests;

public class AccountantTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Books Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X", vatRegistered = true }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static async Task<JsonElement> Invoice(HttpClient c, Guid customer, long unitPrice)
    {
        var d = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { description = "Consulting", quantity = 1, unitPrice } } }));
        return await Ok(await c.PostAsync($"/api/sales/{d.GetProperty("id")}/issue", null));
    }

    [Fact]
    public async Task Withholding_VAT_closes_the_invoice_and_cuts_the_VAT_payable_and_the_balance_sheet_balances()
    {
        var c = await Owner();
        var agent = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Big Agency Ltd", kraPin = "P000111222A" }))).GetProperty("id").GetGuid();
        var inv = await Invoice(c, agent, 1_160_000_00); // 1,000,000 + 160,000 VAT
        // They pay 98% of the taxable value plus VAT less the 2% withheld: 1,140,000 and a certificate for 20,000.
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Bank", amount = 1_140_000_00, reference = "EFT-77", invoiceId = inv.GetProperty("id").GetGuid() }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/sales/{inv.GetProperty("id")}/withheld", new { kind = "Vat", amount = 20_000_00 })).StatusCode); // certificate needed
        await Ok(await c.PostAsJsonAsync($"/api/sales/{inv.GetProperty("id")}/withheld", new { kind = "Vat", amount = 20_000_00, certificate = "KRAWHT0001234" }));
        var doc = await Ok(await c.GetAsync($"/api/sales/{inv.GetProperty("id")}"));
        Assert.Equal("Paid", doc.GetProperty("document").GetProperty("status").GetString());

        var vat = await Ok(await c.GetAsync("/api/reports/vat"));
        Assert.Equal(20_000_00, vat.GetProperty("vatWithheld").GetInt64());
        Assert.Equal(140_000_00, vat.GetProperty("payable").GetInt64());

        var bs = await Ok(await c.GetAsync("/api/reports/balance-sheet"));
        Assert.True(bs.GetProperty("balanced").GetBoolean());
        Assert.Equal(1_000_000_00, bs.GetProperty("equity").GetProperty("profitSoFar").GetInt64());
        Assert.Contains(bs.GetProperty("assets").GetProperty("lines").EnumerateArray(), l => l.GetProperty("code").GetString() == "1310" && l.GetProperty("amount").GetInt64() == 20_000_00);

        var flow = await Ok(await c.GetAsync("/api/reports/cash-flow"));
        Assert.Equal(1_140_000_00, flow.GetProperty("moneyIn").GetInt64());
        Assert.Equal(flow.GetProperty("opening").GetInt64() + flow.GetProperty("net").GetInt64(), flow.GetProperty("closing").GetInt64());

        var csv = await (await c.GetAsync("/api/reports/itax/sales.csv")).Content.ReadAsStringAsync();
        var lines = csv.Trim().Split('\n');
        Assert.StartsWith("PIN of Purchaser,Name of Purchaser,ETR Serial Number", lines[0]);
        Assert.Contains("P000111222A,Big Agency Ltd", lines[1]);
        Assert.Contains("1000000.00,160000.00", lines[1]);
    }

    const string MpesaStatement = """
        Receipt No.,Completion Time,Initiation Time,Details,Transaction Status,Paid In,Withdrawn,Balance
        TKX1111AAA,2026-10-14 09:10:00,2026-10-14 09:10:00,Funds received from 254722000111 - WANJIKU,Completed,"2,320.00",,"52,320.00"
        TKX2222BBB,2026-10-14 10:00:00,2026-10-14 10:00:00,Funds received from 254733000222 - PETER,Completed,"1,000.00",,"53,320.00"
        TKX3333CCC,2026-10-14 10:05:00,2026-10-14 10:05:00,Pay Bill Charge,Completed,,-23.00,"53,297.00"
        TKX4444DDD,2026-10-14 11:00:00,2026-10-14 11:00:00,Failed transfer,Failed,"9,999.00",,"53,297.00"
        """;

    [Fact]
    public async Task An_mpesa_statement_matches_what_is_in_the_books_and_the_rest_is_one_tap_each()
    {
        var c = await Owner();
        var cust = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Wanjiku Stores", phone = "0722000111" }))).GetProperty("id").GetGuid();
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 2_320_00, reference = "TKX1111AAA", payerPhone = "0722000111", receivedAt = "2026-10-14T09:10:00+03:00" }));

        var imp = await Ok(await c.PostAsJsonAsync("/api/statements/import", new { account = "MPESA", fileName = "oct.csv", csv = MpesaStatement }));
        Assert.Equal(3, imp.GetProperty("lines").GetInt32()); // the failed one is left out
        Assert.Equal(1, imp.GetProperty("matched").GetInt32());
        // Importing the same file again adds nothing.
        Assert.Equal(3, (await Ok(await c.PostAsJsonAsync("/api/statements/import", new { account = "MPESA", csv = MpesaStatement }))).GetProperty("skipped").GetInt32());

        var view = await Ok(await c.GetAsync("/api/statements/MPESA"));
        Assert.Equal(2, view.GetProperty("counts").GetProperty("unmatched").GetInt32());
        var lines = view.GetProperty("lines").EnumerateArray().ToList();
        var peter = lines.Single(l => l.GetProperty("reference").GetString() == "TKX2222BBB");
        var charge = lines.Single(l => l.GetProperty("reference").GetString() == "TKX3333CCC");
        Assert.True(charge.GetProperty("suggestCharges").GetBoolean());

        await Ok(await c.PostAsJsonAsync($"/api/statements/lines/{peter.GetProperty("id")}/payment", new { customerId = cust }));
        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray().ToList();
        var charges = accounts.Single(a => a.GetProperty("code").GetString() == "6600").GetProperty("id").GetGuid();
        await Ok(await c.PostAsJsonAsync($"/api/statements/lines/{charge.GetProperty("id")}/expense", new { accountId = charges }));

        view = await Ok(await c.GetAsync("/api/statements/MPESA"));
        Assert.Equal(0, view.GetProperty("counts").GetProperty("unmatched").GetInt32());
        // Books and statement now agree on the M-Pesa balance movements recorded here (2,320 + 1,000 - 23).
        Assert.Equal(3_297_00, view.GetProperty("bookBalance").GetInt64());
        Assert.True((await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }

    [Fact]
    public void Bank_statements_with_debit_and_credit_columns_and_day_first_dates_are_read()
    {
        var csv = "Bank statement for account 0123\nTransaction Date,Narration,Debit,Credit,Balance\n14/10/2026,EFT FROM BIG AGENCY INV-0007,,\"1,140,000.00\",\"1,200,000.00\"\n15/10/2026,LEDGER FEE,350.00,,\"1,199,650.00\"\nTotal,,350.00,\"1,140,000.00\",\n";
        var lines = StatementService.Parse(csv);
        Assert.Equal(2, lines.Count);
        Assert.Equal(new DateOnly(2026, 10, 14), lines[0].Date);
        Assert.Equal(1_140_000_00, lines[0].In);
        Assert.Equal(350_00, lines[1].Out);
        Assert.Equal(1_199_650_00, lines[1].Balance);
    }
}
