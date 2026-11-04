// Employee self-service: HR gives access, the employee signs in with their phone, sets their own password,
// applies for leave, sees only their own payslips once payroll is approved, and nothing else in the business.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class SelfServiceTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : Json(body);
    }

    async Task<HttpClient> SignUp(string company)
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static string Phone() => $"07{Random.Shared.Next(10_000_000, 99_999_999)}";

    async Task<(HttpClient Client, JsonElement Session)> SignIn(string id, string password)
    {
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/login", new { email = id, password }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return (c, s);
    }

    [Fact]
    public async Task An_employee_signs_in_with_their_phone_applies_for_leave_and_sees_only_their_own_pay()
    {
        var hr = await SignUp("Self Service Ltd");
        var phone = Phone();
        var wanjiru = await Ok(await hr.PostAsJsonAsync("/api/employees", new { name = "Wanjiru Kamau", phone, startDate = "2025-01-06", basicSalary = 50_000_00 }));
        var otieno = await Ok(await hr.PostAsJsonAsync("/api/employees", new { name = "Otieno Ouma", phone = Phone(), startDate = "2025-01-06", basicSalary = 20_000_00 }));
        var grant = await Ok(await hr.PostAsync($"/api/employees/{wanjiru.GetProperty("id")}/access", null));
        Assert.False(grant.GetProperty("reset").GetBoolean());

        // Signs in with the phone (any format), must set a password before anything else.
        var (me, session) = await SignIn("+254" + phone[1..], grant.GetProperty("password").GetString()!);
        Assert.Equal("Employee", session.GetProperty("user").GetProperty("role").GetString());
        Assert.True(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/auth/password", new { current = "wrong-one", @new = "my-own-password" })).StatusCode);
        var changed = await Ok(await me.PostAsJsonAsync("/api/auth/password", new { current = grant.GetProperty("password").GetString(), @new = "my-own-password" }));
        me.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.GetProperty("token").GetString());

        var mine = await Ok(await me.GetAsync("/api/me"));
        Assert.Equal("Wanjiru Kamau", mine.GetProperty("employee").GetProperty("name").GetString());
        Assert.Equal(21, mine.GetProperty("leaveBalance").GetProperty("entitlement").GetDecimal());

        // Applies for leave; HR approves it; she sees the decision.
        var leave = await Ok(await me.PostAsJsonAsync("/api/me/leave", new { type = "Annual", from = "2026-11-02", to = "2026-11-06", note = "Family visit" }));
        Assert.Equal("Pending", leave.GetProperty("status").GetString());
        var pending = await Ok(await hr.GetAsync("/api/leave?status=pending"));
        Assert.Equal("Wanjiru Kamau", Assert.Single(pending.GetProperty("items").EnumerateArray()).GetProperty("employee").GetString());
        await Ok(await hr.PostAsync($"/api/leave/{leave.GetProperty("id")}/approve", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsync($"/api/me/leave/{leave.GetProperty("id")}/cancel", null)).StatusCode);
        Assert.Equal("Approved", (await Ok(await me.GetAsync("/api/me"))).GetProperty("leave")[0].GetProperty("status").GetString());

        // A second request she withdraws herself.
        var second = await Ok(await me.PostAsJsonAsync("/api/me/leave", new { type = "Sick", from = "2026-11-16", to = "2026-11-16" }));
        Assert.Equal("Cancelled", (await Ok(await me.PostAsync($"/api/me/leave/{second.GetProperty("id")}/cancel", null))).GetProperty("status").GetString());

        // Payslips: nothing while the payroll is a draft, her own once approved, never a colleague's.
        var run = await Ok(await hr.PostAsJsonAsync("/api/payroll/runs", new { month = "2026-10" }));
        Assert.Empty((await Ok(await me.GetAsync("/api/me"))).GetProperty("payslips").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync($"/api/me/payslips/{run.GetProperty("id")}")).StatusCode);
        await Ok(await hr.PostAsync($"/api/payroll/runs/{run.GetProperty("id")}/approve", null));
        var slip = await Ok(await me.GetAsync($"/api/me/payslips/{run.GetProperty("id")}"));
        Assert.Equal(39_029_15, slip.GetProperty("slip").GetProperty("net").GetInt64());
        Assert.Equal(wanjiru.GetProperty("id").GetGuid(), slip.GetProperty("slip").GetProperty("employeeId").GetGuid());
        Assert.Equal(5_845_85, (await Ok(await me.GetAsync("/api/me/p9?year=2026"))).GetProperty("totals").GetProperty("paye").GetInt64());

        // Nothing else in the business is open to her.
        foreach (var path in new[] { "/api/today", "/api/customers", "/api/employees", $"/api/employees/{otieno.GetProperty("id")}", "/api/payroll/runs", "/api/leave", "/api/reports/accounts", "/api/assistant/proposals" })
            Assert.Equal(HttpStatusCode.Forbidden, (await me.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Access_can_be_reset_and_taken_away_and_leavers_lose_it()
    {
        var hr = await SignUp("Access Ltd");
        var phone = Phone();
        var e = await Ok(await hr.PostAsJsonAsync("/api/employees", new { name = "Kevin Mutua", phone, startDate = "2025-01-06", basicSalary = 16_000_00 }));
        var first = await Ok(await hr.PostAsync($"/api/employees/{e.GetProperty("id")}/access", null));
        var again = await Ok(await hr.PostAsync($"/api/employees/{e.GetProperty("id")}/access", null));
        Assert.True(again.GetProperty("reset").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = phone, password = first.GetProperty("password").GetString() })).StatusCode);
        await SignIn(phone, again.GetProperty("password").GetString()!);

        await Ok(await hr.DeleteAsync($"/api/employees/{e.GetProperty("id")}/access"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = phone, password = again.GetProperty("password").GetString() })).StatusCode);

        var third = await Ok(await hr.PostAsync($"/api/employees/{e.GetProperty("id")}/access", null));
        await Ok(await hr.PutAsJsonAsync($"/api/employees/{e.GetProperty("id")}", new { name = "Kevin Mutua", phone, startDate = "2025-01-06", endDate = DateTime.UtcNow.AddHours(3).AddDays(-1).ToString("yyyy-MM-dd"), basicSalary = 16_000_00 }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = phone, password = third.GetProperty("password").GetString() })).StatusCode);

        // The owner isn't an employee record: self-service says so instead of showing anything.
        Assert.Equal(HttpStatusCode.NotFound, (await hr.GetAsync("/api/me")).StatusCode);
    }
}
