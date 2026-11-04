// The platform console: only the emails in Platform:Admins get in; it reports health and reads across every
// business (counts and totals only), and a business owner who isn't listed is refused.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class PlatformTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    [Fact]
    public async Task Only_listed_admins_see_health_and_analytics_across_businesses()
    {
        var adminEmail = $"{Guid.NewGuid():N}@kabuor.test";
        var app = factory.WithWebHostBuilder(b => b.UseSetting("Platform:Admins", $"someone@else.test, {adminEmail.ToUpperInvariant()}"));

        async Task<HttpClient> Business(string email, string name)
        {
            var c = app.CreateClient();
            var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = name, ownerName = "Owner Person", email, phone = "0712345678", password = "a-long-password" }));
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
            return c;
        }
        var shop = await Business($"{Guid.NewGuid():N}@example.com", $"Duka {Guid.NewGuid():N}");
        var soap = (await Ok(await shop.PostAsJsonAsync("/api/items", new { name = "Soap", price = 100_00, taxType = "A_Exempt" }))).GetProperty("id").GetGuid();
        var buyer = (await Ok(await shop.PostAsJsonAsync("/api/customers", new { name = "Buyer" }))).GetProperty("id").GetGuid();
        var inv = await Ok(await shop.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = buyer, lines = new[] { new { itemId = soap, quantity = 3 } } }));
        await Ok(await shop.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));

        // A business owner who isn't a platform admin.
        Assert.False((await Ok(await shop.GetAsync("/api/platform/me"))).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/platform/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync("/api/platform/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/platform/health")).StatusCode);

        var admin = await Business(adminEmail, "Kabuor Builds");
        Assert.True((await Ok(await admin.GetAsync("/api/platform/me"))).GetProperty("allowed").GetBoolean());

        var health = await Ok(await admin.GetAsync("/api/platform/health"));
        var checks = health.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("key").GetString()!);
        Assert.Equal("good", checks["database"].GetProperty("status").GetString());
        Assert.Equal("good", checks["cache"].GetProperty("status").GetString());
        Assert.Contains("requests", checks.Keys);
        Assert.True(health.GetProperty("traffic").GetProperty("requests").GetInt64() > 0);
        // The simulated demo load shows only when asked for, and never in the checks.
        var withDemo = await Ok(await admin.GetAsync("/api/platform/health?demo=true"));
        Assert.True(withDemo.GetProperty("traffic").GetProperty("requests").GetInt64() > health.GetProperty("traffic").GetProperty("requests").GetInt64() + 1000);
        Assert.DoesNotContain(health.GetProperty("traffic").GetProperty("slowest").EnumerateArray(), r => r.GetProperty("route").GetString() == "GET /api/pos/terminal");
        var requestsCheck = withDemo.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "requests");
        Assert.True(int.Parse(requestsCheck.GetProperty("summary").GetString()!.Split(' ')[0]) < 1000);

        var a = await Ok(await admin.GetAsync("/api/platform/analytics?days=30"));
        Assert.True(a.GetProperty("businesses").GetProperty("total").GetInt32() >= 2);
        Assert.True(a.GetProperty("businesses").GetProperty("trial").GetInt32() >= 2);
        Assert.True(a.GetProperty("users").GetProperty("signedIn7d").GetInt32() >= 0);
        Assert.True(a.GetProperty("volume").GetProperty("invoiceValue").GetInt64() >= 300_00); // the shop's invoice counts, across businesses
        Assert.Equal(30, a.GetProperty("series").GetArrayLength());
        Assert.Contains(a.GetProperty("top").EnumerateArray(), c => c.GetProperty("invoiceValue").GetInt64() >= 300_00);

        // Without the demo network, only real businesses count.
        var real = await Ok(await admin.GetAsync("/api/platform/analytics?days=30&demo=false"));
        Assert.DoesNotContain(real.GetProperty("top").EnumerateArray(), c => c.GetProperty("id").GetString()!.StartsWith("b0000000-0000-0000-0000-"));
        Assert.True(real.GetProperty("businesses").GetProperty("total").GetInt32() >= 2);
    }

    [Fact]
    public async Task An_admin_answers_a_business_inquiry_changes_its_plan_and_erases_it_keeping_a_record()
    {
        var adminEmail = $"{Guid.NewGuid():N}@kabuor.test";
        var app = factory.WithWebHostBuilder(b => b.UseSetting("Platform:Admins", adminEmail));
        async Task<(HttpClient Client, JsonElement Session)> Business(string email, string name, string owner)
        {
            var c = app.CreateClient();
            var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = name, ownerName = owner, email, phone = "0712345678", password = "a-long-password" }));
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
            return (c, s);
        }
        var shopName = $"Duka {Guid.NewGuid():N}";
        var (shop, shopSession) = await Business($"{Guid.NewGuid():N}@example.com", shopName, "Owner Person");
        var shopId = shopSession.GetProperty("company").GetProperty("id").GetGuid();
        await Ok(await shop.PostAsJsonAsync("/api/customers", new { name = "Wanjiru Kamau", phone = "0722000111" }));
        var asked = await Ok(await shop.PostAsJsonAsync("/api/inquiries", new { name = "Wanjiru Kamau", phone = "0722000111", message = "Is the shop open on Sunday?" }));
        var (admin, _) = await Business(adminEmail, "Kabuor Builds", "Platform Person");

        // Replying lands in the business's own thread, signed by the admin, and marks it answered.
        var issue = (await Ok(await admin.GetAsync("/api/platform/issues"))).EnumerateArray().Single(i => i.GetProperty("companyId").GetGuid() == shopId);
        Assert.Equal("Is the shop open on Sunday?", issue.GetProperty("messages")[0].GetProperty("text").GetString());
        var replied = await Ok(await admin.PostAsJsonAsync($"/api/platform/issues/{issue.GetProperty("id")}/reply", new { message = "Yes, 9am to 1pm." }));
        Assert.Equal("Answered", replied.GetProperty("status").GetString());
        var thread = await Ok(await shop.GetAsync($"/api/inquiries/{asked.GetProperty("id")}"));
        var last = thread.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("staff", last.GetProperty("from").GetString());
        Assert.Contains("Platform Person", last.GetProperty("author").GetString());

        // Plans are the real ones; extending gives paid access, locking takes it away, extending again restores it.
        var plans = (await Ok(await admin.GetAsync("/api/platform/plans"))).EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Equal(["starter", "business", "growth", "dine"], plans); // Kabuor Dine is a plan the platform can give too
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/platform/businesses/{shopId}/plan", new { plan = "scale" })).StatusCode);
        Assert.Equal("paying", (await Ok(await admin.PostAsJsonAsync($"/api/platform/businesses/{shopId}/plan", new { plan = "business", extendDays = 30 }))).GetProperty("state").GetString());
        Assert.Equal("locked", (await Ok(await admin.PostAsJsonAsync($"/api/platform/businesses/{shopId}/plan", new { @lock = true }))).GetProperty("state").GetString());
        Assert.Equal("paying", (await Ok(await admin.PostAsJsonAsync($"/api/platform/businesses/{shopId}/plan", new { extendDays = 14 }))).GetProperty("state").GetString());
        var listed = (await Ok(await admin.GetAsync("/api/platform/businesses"))).EnumerateArray().Single(b => b.GetProperty("id").GetGuid() == shopId);
        Assert.Equal("business", listed.GetProperty("plan").GetString());
        Assert.False(listed.GetProperty("isDemo").GetBoolean());

        // Erasing needs the name typed; afterwards nothing of the business is left, not even in its audit log,
        // and the erasure itself is recorded on the platform's side.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/platform/businesses/{shopId}/wipe", new { reason = "Owner asked to close the account", confirm = "Some other shop" })).StatusCode);
        var wiped = await Ok(await admin.PostAsJsonAsync($"/api/platform/businesses/{shopId}/wipe", new { reason = "Owner asked to close the account", confirm = shopName }));
        Assert.True(wiped.GetProperty("removed").GetInt32() > 0);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/platform/businesses/{shopId}")).StatusCode);
        var erasure = (await Ok(await admin.GetAsync("/api/platform/erasures"))).EnumerateArray().Single(e => e.GetProperty("companyId").GetGuid() == shopId);
        Assert.Equal(shopName, erasure.GetProperty("name").GetString());
        Assert.Contains("Owner asked to close the account", erasure.GetProperty("reason").GetString());
        Assert.Equal("Platform Person", erasure.GetProperty("actor").GetString());

        using var scope = app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Kabuor.Erp.Data.RequestContext>();
        ctx.CompanyId = shopId;
        var db = scope.ServiceProvider.GetRequiredService<Kabuor.Erp.Data.ErpDb>();
        Assert.Equal(0, await db.Customers.CountAsync());
        Assert.Equal(0, await db.Inquiries.CountAsync());
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Signing_up_as_admin_at_some_domain_opens_nothing_and_shared_passwords_dont_sign_anyone_in()
    {
        var c = factory.CreateClient();
        var email = $"admin@{Guid.NewGuid():N}.test";
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Duka {Guid.NewGuid():N}", ownerName = "Owner Person", email, phone = "0712345678", password = "a-long-password" }));
        Assert.False(s.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean());
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        Assert.False((await Ok(await c.GetAsync("/api/platform/me"))).GetProperty("allowed").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/platform/businesses")).StatusCode);

        foreach (var shared in new[] { "admin", "password", "admin123" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = shared })).StatusCode);
        await Ok(await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password = "a-long-password" }));
    }
}
