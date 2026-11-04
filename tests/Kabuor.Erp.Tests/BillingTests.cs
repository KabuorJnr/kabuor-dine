// Plans and the free trial: a new company starts on 30 days free, a test payment (no M-Pesa keys) puts it on a plan,
// a plan caps its users, and once the trial and grace are over an unpaid company can still read but not change.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Billing;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class BillingTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient c, string name)> Owner()
    {
        var c = factory.CreateClient();
        var name = $"Billing {Guid.NewGuid():N}";
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = name, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, name);
    }

    async Task Backdate(string company, int days)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var co = await db.Companies.IgnoreQueryFilters().SingleAsync(x => x.Name == company);
        co.CreatedAt = DateTimeOffset.UtcNow.AddDays(-days);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task New_company_is_on_a_free_trial_and_a_test_payment_puts_it_on_a_plan()
    {
        var (c, _) = await Owner();
        var s = await Ok(await c.GetAsync("/api/billing"));
        Assert.True(s.GetProperty("onTrial").GetBoolean());
        Assert.False(s.GetProperty("locked").GetBoolean());
        Assert.InRange(s.GetProperty("daysLeft").GetInt32(), 29, 30);
        Assert.True(s.GetProperty("testMode").GetBoolean());
        var starter = s.GetProperty("plans").EnumerateArray().First(p => p.GetProperty("id").GetString() == "starter");
        Assert.Equal(1_500_00, starter.GetProperty("monthly").GetInt64());
        Assert.Equal(15_000_00, starter.GetProperty("yearly").GetInt64()); // a year costs ten months

        var paid = await Ok(await c.PostAsJsonAsync("/api/billing/pay", new { plan = "starter", months = 12, phone = "0712345678" }));
        Assert.Equal("Paid", paid.GetProperty("status").GetString());
        Assert.True(paid.GetProperty("test").GetBoolean());
        s = await Ok(await c.GetAsync("/api/billing"));
        Assert.False(s.GetProperty("onTrial").GetBoolean());
        Assert.Equal("starter", s.GetProperty("plan").GetString());
        Assert.InRange(s.GetProperty("daysLeft").GetInt32(), 360, 367);

        // Starter has room for 3 users: the owner plus two.
        for (var i = 0; i < 2; i++)
            await Ok(await c.PostAsJsonAsync("/api/settings/users", new { name = $"Clerk {i}", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345679", role = "Sales", password = "a-long-password" }));
        var full = await c.PostAsJsonAsync("/api/settings/users", new { name = "One too many", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345679", role = "Sales", password = "a-long-password" });
        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        Assert.Contains("Starter", await full.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task After_the_trial_and_grace_an_unpaid_company_can_read_but_not_change_until_it_pays()
    {
        var (c, name) = await Owner();
        await Backdate(name, Plans.TrialDays + 3); // in the grace week: still working
        Assert.True((await c.PostAsJsonAsync("/api/customers", new { name = "Grace Customer" })).IsSuccessStatusCode);

        await Backdate(name, Plans.TrialDays + Plans.GraceDays + 1);
        var blocked = await c.PostAsJsonAsync("/api/customers", new { name = "Locked Customer" });
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/customers")).StatusCode); // their records are still theirs
        Assert.True((await Ok(await c.GetAsync("/api/billing"))).GetProperty("locked").GetBoolean());

        await Ok(await c.PostAsJsonAsync("/api/billing/pay", new { plan = "business", months = 1, phone = "0712345678" }));
        Assert.True((await c.PostAsJsonAsync("/api/customers", new { name = "Paid Customer" })).IsSuccessStatusCode);
    }

    [Fact]
    public void A_year_is_ten_months_and_paid_time_stops_the_lock()
    {
        var p = new Plan("x", "X", 1_000_00, 3, 5, []);
        Assert.Equal(10_000_00, Plans.Amount(p, 12));
        Assert.Equal(3_000_00, Plans.Amount(p, 3));
        var now = DateTimeOffset.UtcNow;
        Assert.True(Plans.Locked(new Company { Name = "", CreatedAt = now.AddDays(-60) }, now));
        Assert.False(Plans.Locked(new Company { Name = "", CreatedAt = now.AddDays(-60), PaidUntil = now.AddDays(1) }, now));
        Assert.False(Plans.Locked(new Company { Name = "", CreatedAt = now.AddDays(-60), TrialEndsAt = now.AddDays(2) }, now));
    }
}
