// Who sees what. Each role reaches only its own part of the business, and the sign-in tells the app which parts
// those are, so the menu and the API always agree.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class AccessTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    async Task<(HttpClient Owner, Func<string, Task<(HttpClient, JsonElement)>> As)> Company()
    {
        var owner = factory.CreateClient();
        var s = await Ok(await owner.PostAsJsonAsync("/api/auth/register", new { companyName = "Access Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        async Task<(HttpClient, JsonElement)> As(string role)
        {
            var email = $"{Guid.NewGuid():N}@example.com";
            await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, role, password = "another-long-password" }));
            var c = factory.CreateClient();
            var session = await TestSignIn.First(c, email, "another-long-password");
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("token").GetString());
            return (c, session);
        }
        return (owner, As);
    }

    static string[] Permissions(JsonElement session) =>
        session.GetProperty("user").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray();

    [Theory]
    // role, can open, can't open
    [InlineData("Accountant", "/api/reports/vat,/api/sales?kind=Invoice,/api/purchases,/api/payroll/runs", "/api/pos/session,/api/crm/leads,/api/production/runs")]
    [InlineData("Sales", "/api/pos/session,/api/sales?kind=Invoice,/api/customers,/api/crm/leads", "/api/reports/vat,/api/purchases,/api/employees,/api/stock")]
    [InlineData("Storekeeper", "/api/stock,/api/production/runs,/api/purchases", "/api/customers,/api/sales?kind=Invoice,/api/pos/session,/api/employees,/api/reports/vat")]
    [InlineData("HR", "/api/employees,/api/leave", "/api/customers,/api/sales?kind=Invoice,/api/stock,/api/purchases,/api/pos/session,/api/reports/vat")]
    [InlineData("Procurement", "/api/suppliers,/api/purchases,/api/stock", "/api/customers,/api/pos/session,/api/employees,/api/production/runs")]
    [InlineData("Viewer", "/api/reports/vat,/api/sales?kind=Invoice,/api/stock", "/api/pos/session,/api/employees,/api/crm/leads")]
    public async Task Each_role_reaches_only_its_own_screens(string role, string allowed, string blocked)
    {
        var (_, As) = await Company();
        var (c, _) = await As(role);
        foreach (var url in allowed.Split(','))
            Assert.True((await c.GetAsync(url)).IsSuccessStatusCode, $"{role} should open {url}");
        foreach (var url in blocked.Split(','))
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task The_sign_in_lists_the_role_permissions_and_today_leaves_out_the_rest()
    {
        var (owner, As) = await Company();
        var (accountant, aSession) = await As("Accountant");
        Assert.DoesNotContain("pos", Permissions(aSession));
        Assert.Contains("finance", Permissions(aSession));

        var (hr, hSession) = await As("HR");
        Assert.Equal(["read", "people"], Permissions(hSession));
        var today = await Ok(await hr.GetAsync("/api/today"));
        Assert.Equal(JsonValueKind.Null, today.GetProperty("cash").ValueKind);
        Assert.Equal(JsonValueKind.Null, today.GetProperty("owed").ValueKind);
        Assert.Equal(JsonValueKind.Null, today.GetProperty("headline").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, today.GetProperty("people").ValueKind);

        // Tell me finds customers for the owner but not for HR.
        await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Searchable Shop", phone = "0711000111" }));
        Assert.NotEmpty((await Ok(await owner.GetAsync("/api/search?q=Searchable"))).EnumerateArray());
        Assert.Empty((await Ok(await hr.GetAsync("/api/search?q=Searchable"))).EnumerateArray());
    }
}
