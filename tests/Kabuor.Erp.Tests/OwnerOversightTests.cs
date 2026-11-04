// The owner oversees: with owner oversight on, the owner reads every module, approves and signs off, sets targets and
// runs the settings, but the daily work (customers, items, invoices, stock) is refused with a clear answer, and the
// sign-in says so for the screens. Turning it off in Settings gives the owner back the daily work.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class OwnerOversightTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    string email = "";

    async Task<HttpClient> Owner()
    {
        var c = factory.CreateClient();
        email = $"{Guid.NewGuid():N}@example.com";
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Oversight {Guid.NewGuid():N}", ownerName = "Owner Person", email, phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static async Task SetOversight(HttpClient owner, bool on)
    {
        var c = await Ok(await owner.GetAsync("/api/settings/company"));
        await Ok(await owner.PutAsJsonAsync("/api/settings/company", new { name = c.GetProperty("name").GetString(), kraPin = c.GetProperty("kraPin").GetString(), vatRegistered = true, ownerOversight = on }));
    }

    [Fact]
    public async Task An_overseeing_owner_reads_and_decides_but_leaves_the_daily_work_to_the_team()
    {
        var owner = await Owner();
        // Done before oversight, so there's something to read.
        var customer = (await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Read Only Shop", phone = "0711000999" }))).GetProperty("id").GetGuid();
        await SetOversight(owner, true);
        Assert.True((await Ok(await owner.GetAsync("/api/settings/company"))).GetProperty("ownerOversight").GetBoolean());

        // Daily work: refused, with the reason and a code the screens recognise.
        var refused = await owner.PostAsJsonAsync("/api/customers", new { name = "Not By Me", phone = "0711000998" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var why = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("oversight", why.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/items", new { name = "Widget", unit = "pc", price = 100_00 })).StatusCode);

        // Reading: everything.
        await Ok(await owner.GetAsync($"/api/customers/{customer}"));
        await Ok(await owner.GetAsync("/api/os/chain"));
        await Ok(await owner.GetAsync("/api/workspaces/finance"));

        // Deciding and running the business: still theirs.
        await Ok(await owner.PutAsJsonAsync("/api/insights/kpis/targets", new Dictionary<string, decimal?>()));
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = "Sales Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0700 111 222", role = "Sales", password = "role-password-1" }));

        // The sign-in tells the screens.
        var signIn = await TestSignIn.First(factory.CreateClient(), email, "a-long-password");
        Assert.True(signIn.GetProperty("company").GetProperty("ownerOversight").GetBoolean());

        // Turned off: the owner can do the work again.
        await SetOversight(owner, false);
        await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "By Me Again", phone = "0711000997" }));
    }

    [Theory]
    [InlineData("/api/budgets/00000000-0000-0000-0000-000000000001/approve", true)]
    [InlineData("/api/requisitions/requests/00000000-0000-0000-0000-000000000001/reject", true)]
    [InlineData("/api/payroll/runs/00000000-0000-0000-0000-000000000001/approve", true)]
    [InlineData("/api/tenders/00000000-0000-0000-0000-000000000001/award", true)]
    [InlineData("/api/settings/company", true)]
    [InlineData("/api/os/plan", true)]
    [InlineData("/api/employees/00000000-0000-0000-0000-000000000001/access", true)]
    [InlineData("/api/employees/00000000-0000-0000-0000-000000000001", false)]
    [InlineData("/api/sales", false)]
    [InlineData("/api/sales/00000000-0000-0000-0000-000000000001/issue", false)]
    [InlineData("/api/payroll/runs/00000000-0000-0000-0000-000000000001/pay", false)]
    [InlineData("/api/stock/transfer", false)]
    [InlineData("/api/work-orders", false)]
    public void What_an_overseeing_owner_may_still_change(string path, bool allowed) =>
        Assert.Equal(allowed, Kabuor.Erp.Auth.Oversight.Allows(path));
}
