// The books must agree with the documents whatever order things happen in. A seeded random mix of invoices
// (16%, 8% and exempt lines, fractional quantities), part payments, overpayments, unmatched M-Pesa, later
// matching, voids and till sales; after every step the ledger is checked against the invoices and payments.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class LedgerInvariantTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Invariant Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X", vatRegistered = true }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    /// <summary>What the documents say, against what the ledger says.</summary>
    static async Task Check(HttpClient c, string after)
    {
        var tb = await Ok(await c.GetAsync("/api/reports/trial-balance"));
        Assert.True(tb.GetProperty("balanced").GetBoolean(), $"trial balance off after {after}");

        var accounts = (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());
        long Bal(string k) => accounts.GetValueOrDefault(k);

        var invoices = (await Ok(await c.GetAsync("/api/sales?kind=Invoice&status=all&take=500"))).GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        var live = invoices.Where(i => i.GetProperty("status").GetString() is not ("Draft" or "Void")).ToList();
        var owed = live.Sum(i => i.GetProperty("total").GetInt64() - i.GetProperty("paid").GetInt64());
        Assert.True(owed == Bal("AR"), $"receivable {Bal("AR")} vs open invoices {owed} after {after}");

        var payments = (await Ok(await c.GetAsync("/api/payments?take=500"))).GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        var received = payments.Sum(p => p.GetProperty("amount").GetInt64());
        var unmatched = payments.Sum(p => p.GetProperty("amount").GetInt64() - p.GetProperty("allocated").GetInt64());
        Assert.True(received == Bal("MPESA") + Bal("CASH") + Bal("BANK"), $"money held vs payments after {after}");
        Assert.True(unmatched == -Bal("UNAPPLIED"), $"unapplied {Bal("UNAPPLIED")} vs unmatched {unmatched} after {after}");

        var vat = live.Sum(i => i.GetProperty("tax").GetInt64());
        Assert.True(vat == -Bal("VAT_OUT"), $"VAT owed {-Bal("VAT_OUT")} vs invoices {vat} after {after}");
        foreach (var i in live)
        {
            var paid = i.GetProperty("paid").GetInt64();
            var total = i.GetProperty("total").GetInt64();
            Assert.InRange(paid, 0, total);
            Assert.Equal(paid == total ? "Paid" : paid > 0 ? "PartPaid" : "Sent", i.GetProperty("status").GetString());
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(2026)]
    public async Task The_books_always_agree_with_invoices_and_payments(int seed)
    {
        var rnd = new Random(seed);
        var c = await Owner();
        var customers = new List<Guid>();
        for (var i = 0; i < 3; i++)
            customers.Add((await Ok(await c.PostAsJsonAsync("/api/customers", new { name = $"Customer {i}", phone = $"07220000{i:00}" }))).GetProperty("id").GetGuid());
        var items = new List<Guid>();
        foreach (var (name, price, tax) in new[] { ("Sauce", 35000L, "B_Standard16"), ("Bread", 6999L, "E_Reduced8"), ("Maize flour", 21050L, "A_Exempt") })
            items.Add((await Ok(await c.PostAsJsonAsync("/api/items", new { name, price, taxType = tax }))).GetProperty("id").GetGuid());
        await Ok(await c.PostAsJsonAsync("/api/pos/session/open", new { openingFloat = 0 }));

        var issued = new List<(Guid Id, long Total)>();
        var refNo = 0;
        for (var step = 0; step < 60; step++)
        {
            var op = rnd.Next(6);
            string what;
            if (op <= 1 || issued.Count == 0)
            {
                var lines = Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => new { itemId = items[rnd.Next(items.Count)], quantity = new[] { 1m, 2m, 0.5m, 1.25m, 3m }[rnd.Next(5)] }).ToArray();
                var doc = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customers[rnd.Next(3)], lines }));
                var inv = await Ok(await c.PostAsync($"/api/sales/{doc.GetProperty("id")}/issue", null));
                issued.Add((inv.GetProperty("id").GetGuid(), inv.GetProperty("total").GetInt64()));
                what = $"invoice {inv.GetProperty("number")}";
            }
            else if (op == 2)
            {
                // A payment against an invoice: part, exact, or more than is owed (the rest stays unapplied).
                var (id, total) = issued[rnd.Next(issued.Count)];
                var amount = Math.Max(100, (long)(total * new[] { 0.3, 1.0, 1.4 }[rnd.Next(3)]));
                var res = await c.PostAsJsonAsync("/api/payments", new { method = rnd.Next(2) == 0 ? "Mpesa" : "Cash", amount, reference = $"T{seed}X{refNo++:D6}", invoiceId = id });
                what = $"payment {amount} for {id} ({(int)res.StatusCode})";
            }
            else if (op == 3)
            {
                // M-Pesa nobody can place yet.
                await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = rnd.Next(1, 50) * 1000, reference = $"U{seed}X{refNo++:D6}", payerPhone = "0799111222" }));
                what = "unmatched payment";
            }
            else if (op == 4)
            {
                // Someone matches an unapplied payment to an open invoice, as far as both go.
                var pays = (await Ok(await c.GetAsync("/api/payments?status=to-match&take=50"))).GetProperty("page").GetProperty("items").EnumerateArray().ToList();
                var open = (await Ok(await c.GetAsync("/api/sales?kind=Invoice&status=unpaid&take=50"))).GetProperty("page").GetProperty("items").EnumerateArray().ToList();
                what = "nothing to match";
                if (pays.Count > 0 && open.Count > 0)
                {
                    var p = pays[rnd.Next(pays.Count)];
                    var i = open[rnd.Next(open.Count)];
                    var amount = Math.Min(p.GetProperty("amount").GetInt64() - p.GetProperty("allocated").GetInt64(), i.GetProperty("total").GetInt64() - i.GetProperty("paid").GetInt64());
                    if (amount > 0) await Ok(await c.PostAsJsonAsync($"/api/payments/{p.GetProperty("id")}/allocate", new { allocations = new[] { new { invoiceId = i.GetProperty("id").GetGuid(), amount } } }));
                    what = $"matched {amount}";
                }
            }
            else
            {
                // A void (refused once money is on it) or a till sale.
                if (rnd.Next(2) == 0)
                {
                    var (id, _) = issued[rnd.Next(issued.Count)];
                    var res = await c.PostAsync($"/api/sales/{id}/void", null);
                    what = $"void {id} ({(int)res.StatusCode})";
                }
                else
                {
                    var r = await Ok(await c.PostAsJsonAsync("/api/pos/checkout", new { paymentMethod = "Cash", amountPaid = 0, changeGiven = 0, lines = new[] { new { itemId = items[rnd.Next(items.Count)], quantity = 1 } } }));
                    what = $"till sale {r.GetProperty("total")}";
                }
            }
            await Check(c, $"step {step}: {what}");
        }
    }
}
