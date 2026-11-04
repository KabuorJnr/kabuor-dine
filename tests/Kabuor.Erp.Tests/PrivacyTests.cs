// Data subjects' rights (Data Protection Act, 2019): the owner can hand a customer everything held about them, and
// erase them; erasure keeps what tax law requires on issued invoices, removes the rest, leaves no trace in the
// audit log, and is refused to staff below admin.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class PrivacyTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task A_customer_can_be_exported_and_erased_keeping_only_tax_records()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Privacy {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var soap = (await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Soap", price = 100_00, trackStock = false, taxType = "A_Exempt" }))).GetProperty("id").GetGuid();
        var buyer = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Wanjiku Kamau", phone = "0722111222", email = "wanjiku@example.com", address = "Ruiru" }))).GetProperty("id").GetGuid();
        var stranger = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Otieno Never Bought", phone = "0733444555" }))).GetProperty("id").GetGuid();
        await Ok(await c.PostAsJsonAsync("/api/crm/leads", new { name = "Wanjiku Kamau", phone = "0722111222", interest = "Soap" }));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = buyer, lines = new[] { new { itemId = soap, quantity = 2 } } }));
        await Ok(await c.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 200_00, reference = "SPR1VACY01", payerPhone = "254722111222", payerName = "WANJIKU KAMAU", customerId = buyer, invoiceId = inv.GetProperty("id") }));

        var export = await Ok(await c.GetAsync($"/api/customers/{buyer}/data-export"));
        Assert.Equal("254722111222", export.GetProperty("customer").GetProperty("phone").GetString());
        Assert.Equal(1, export.GetProperty("documents").GetArrayLength());
        Assert.Equal(1, export.GetProperty("leads").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/customers/{buyer}/erase", new { reason = "" })).StatusCode);
        var erased = await Ok(await c.PostAsJsonAsync($"/api/customers/{buyer}/erase", new { reason = "Asked by phone" }));
        Assert.Equal(1, erased.GetProperty("keptForTax").GetInt32());
        await Ok(await c.PostAsJsonAsync($"/api/customers/{stranger}/erase", new { reason = "Asked by email" }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var all = await db.Customers.IgnoreQueryFilters().Where(x => x.Id == buyer || x.Id == stranger).ToListAsync();
        var kept = all.Single(x => x.Id == buyer);
        Assert.Equal("Wanjiku Kamau", kept.Name); // on an issued invoice: a tax record
        Assert.Equal("", kept.Phone);
        Assert.Equal("", kept.Email);
        Assert.True(kept.MarketingOptOut);
        Assert.StartsWith("Erased customer", all.Single(x => x.Id == stranger).Name); // never invoiced: nothing kept
        Assert.False(await db.Leads.IgnoreQueryFilters().AnyAsync(l => l.Phone == "254722111222" || l.Phone == "0722111222"));
        Assert.False(await db.Payments.IgnoreQueryFilters().AnyAsync(p => p.PayerPhone == "254722111222"));
        // Nothing the customer gave us survives in the audit log.
        Assert.False(await db.AuditEntries.IgnoreQueryFilters().AnyAsync(a => a.Changes.Contains("722111222") || a.Changes.Contains("wanjiku@example.com") || a.Changes.Contains("Otieno Never Bought")));
        Assert.True(await db.AuditEntries.IgnoreQueryFilters().AnyAsync(a => a.Action == "Erased" && a.EntityId == buyer.ToString()));
    }

    [Fact]
    public async Task Only_admins_can_export_or_erase()
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Privacy2 {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var who = (await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Someone", phone = "0700000001" }))).GetProperty("id").GetGuid();
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await c.PostAsJsonAsync("/api/settings/users", new { name = "Sales Person", email, role = "Sales", password = "Sales-password-2026" }));
        var sales = factory.CreateClient();
        var t = await TestSignIn.First(sales, email, "Sales-password-2026");
        sales.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync($"/api/customers/{who}/data-export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsJsonAsync($"/api/customers/{who}/erase", new { reason = "test" })).StatusCode);
    }
}
