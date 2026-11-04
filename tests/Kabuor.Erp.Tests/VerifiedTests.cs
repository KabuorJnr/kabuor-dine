// Kabuor Verified, step 2: the owner's proof page. Only money Safaricom's servers confirmed counts; the summary is
// facts (totals, payer counts, KRA signing), never names or phone numbers; the owner and accountant see it, and only
// the owner may share it.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class VerifiedTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : Json(body);
    }

    async Task<(HttpClient Client, JsonElement Session)> Owner(string company)
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Akinyi Owner", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    async Task<(HttpClient Client, JsonElement Session)> Staff(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, role, password = "another-long-password" }));
        var c = factory.CreateClient();
        var s = await TestSignIn.First(c, email, "another-long-password");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    /// <summary>Last month's money in, written straight to the books as each kind of arrival would leave it.</summary>
    async Task SeedLastMonth(string companyName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var company = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Name == companyName);
        var today = Clock.Today;
        var at = new DateTimeOffset(new DateOnly(today.Year, today.Month, 1).AddMonths(-1).AddDays(9).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(3));
        var n = 0;
        Payment Pay(long cents, string phone, PaymentSource source, bool safaricom) => new()
        {
            CompanyId = company.Id, Method = PaymentMethod.Mpesa, Amount = cents, ReceivedAt = at, Reference = $"SEED{n++:D4}",
            PayerPhone = phone, PayerName = "Secret Payer", Source = source, FromSafaricom = safaricom, CallbackIp = safaricom ? "196.201.214.200" : "",
        };
        for (var i = 1; i <= 6; i++) db.Payments.Add(Pay(100000, $"25472200000{i}", PaymentSource.MpesaConfirmation, true)); // six customers
        db.Payments.Add(Pay(50000, "254712345678", PaymentSource.MpesaPrompt, true));          // the owner's own phone
        db.Payments.Add(Pay(300000, "254733000999", PaymentSource.Manual, false));             // typed in
        db.Payments.Add(Pay(200000, "254744000888", PaymentSource.MpesaConfirmation, false));  // a "confirmation" from elsewhere
        await db.SaveChangesAsync();
    }

    async Task<Guid> IssueInvoice(HttpClient c)
    {
        var customer = await Ok(await c.PostAsJsonAsync("/api/customers", new { name = $"Buyer {Guid.NewGuid():N}"[..14], kraPin = "P051111111A" }));
        var item = await Ok(await c.PostAsJsonAsync("/api/items", new { name = $"Chilli {Guid.NewGuid():N}"[..14], price = 116000, taxType = "B_Standard16" }));
        var draft = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer.GetProperty("id").GetGuid(), lines = new[] { new { itemId = item.GetProperty("id").GetGuid(), quantity = 1 } } }));
        await Ok(await c.PostAsync($"/api/sales/{draft.GetProperty("id")}/issue", null));
        return draft.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Only_money_Safaricom_confirmed_counts_and_the_rest_is_explained()
    {
        var (c, _) = await Owner("Proof Ltd");
        await SeedLastMonth("Proof Ltd");
        var live = await IssueInvoice(c);
        await IssueInvoice(c); // signed in KRA's test mode
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            await db.SalesDocs.IgnoreQueryFilters().Where(d => d.Id == live).ExecuteUpdateAsync(s => s.SetProperty(d => d.EtimsControlCode, "KRACU0100012345"));
        }

        var res = await c.GetAsync("/api/verified/summary?months=3");
        var raw = await res.Content.ReadAsStringAsync();
        var s = await Ok(res);

        var lastMonth = s.GetProperty("monthList").EnumerateArray().ToList()[^2];
        Assert.False(lastMonth.GetProperty("partial").GetBoolean());
        Assert.Equal(650000, lastMonth.GetProperty("confirmed").GetInt64());
        Assert.Equal(1150000, lastMonth.GetProperty("allMoneyIn").GetInt64());
        Assert.Equal(7, lastMonth.GetProperty("payers").GetInt32());
        Assert.Equal(Math.Round(500000d / 650000, 4), lastMonth.GetProperty("topFiveShare").GetDouble());
        Assert.Equal(650000 / 3, s.GetProperty("averageMonthlyConfirmed").GetInt64());
        Assert.Equal(4, s.GetProperty("monthList").GetArrayLength()); // three whole months and this one so far
        Assert.Equal(50000, s.GetProperty("ownNumbers").GetInt64());
        Assert.Equal(200000, s.GetProperty("notFromSafaricom").GetInt64());

        var etims = s.GetProperty("etims");
        Assert.Equal(2, etims.GetProperty("issued").GetInt32());
        Assert.Equal(1, etims.GetProperty("signed").GetInt32());
        Assert.Equal(1, etims.GetProperty("testSigned").GetInt32());
        Assert.Equal(0.5, etims.GetProperty("signedShare").GetDouble());

        var fixes = s.GetProperty("fixes").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ToHashSet();
        foreach (var code in new[] { "typed-share", "not-from-safaricom", "concentration", "own-numbers", "etims-test" }) Assert.Contains(code, fixes);
        Assert.DoesNotContain("etims-unsigned", fixes);

        // Facts only: nobody's name or number leaves the summary.
        foreach (var secret in new[] { "Secret Payer", "2547", "0712", "Buyer " }) Assert.DoesNotContain(secret, raw);
    }

    [Fact]
    public async Task A_new_business_gets_an_empty_summary_not_an_error()
    {
        var (c, _) = await Owner("Brand New Ltd");
        var s = await Ok(await c.GetAsync("/api/verified/summary"));
        Assert.Equal(6, s.GetProperty("months").GetInt32());
        Assert.Equal(0, s.GetProperty("averageMonthlyConfirmed").GetInt64());
        Assert.Equal(0, s.GetProperty("payers").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/verified/summary?months=7")).StatusCode);
    }

    [Fact]
    public async Task The_accountant_sees_the_proof_but_only_the_owner_may_share_it()
    {
        var (owner, ownerSession) = await Owner("Roles Ltd");
        var (accountant, accountantSession) = await Staff(owner, "Accountant");
        var (sales, _) = await Staff(owner, "Sales");

        await Ok(await accountant.GetAsync("/api/verified/summary"));
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/verified/summary")).StatusCode);

        static string[] Perms(JsonElement s) => s.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray();
        Assert.Contains("verified.share", Perms(ownerSession));
        Assert.Contains("verified.view", Perms(accountantSession));
        Assert.DoesNotContain("verified.share", Perms(accountantSession));
    }
}
