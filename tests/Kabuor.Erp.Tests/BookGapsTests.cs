// What the ERP books say most systems leave out, end to end: maintenance by use (meters), cash advances through budget
// and sign-off, budget transfers, checking a register fingerprint kept outside the system, material requirements
// planning, capacity, the monthly planning meeting, and the ERP health score.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kabuor.Erp.Tests;

public class BookGapsTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = $"Gaps {Guid.NewGuid():N}", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    async Task<HttpClient> As(HttpClient owner, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, phone = "0700 111 222", role, password = "role-password-1" }));
        var c = factory.CreateClient();
        var login = await TestSignIn.First(c, email, "role-password-1");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("token").GetString());
        return c;
    }

    static async Task<Dictionary<string, long>> Balances(HttpClient c) =>
        (await Ok(await c.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());

    static int Year => DateTime.UtcNow.AddHours(3).Year;

    static async Task<Guid> BudgetedDepartment(HttpClient owner, string name, long monthly)
    {
        var id = (await Ok(await owner.PostAsJsonAsync("/api/departments", new { name }))).GetProperty("id").GetGuid();
        var b = await Ok(await owner.PostAsJsonAsync("/api/budgets", new { departmentId = id, year = Year, months = Enumerable.Repeat(monthly, 12), justification = $"{name}: the year's running costs", submit = true }));
        await Ok(await owner.PostAsJsonAsync($"/api/budgets/{b.GetProperty("budget").GetProperty("id")}/approve", new { }));
        return id;
    }

    [Fact]
    public async Task A_service_falls_due_by_use_before_its_date_and_the_next_one_counts_from_the_reading()
    {
        var owner = await Owner();
        var engine = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Engine PW127 #1", category = "EQP", criticality = "High", meterUnit = "hours" }))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var gen = (await Ok(await owner.PostAsJsonAsync("/api/assets", new { name = "Office kettle", category = "EQP" }))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var far = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd");
        // A plan by use needs an asset with a meter.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/maintenance/plans", new { name = "Kettle: descale", assetId = gen, every = 12, unit = "Months", nextDue = far, meterEvery = 100 })).StatusCode);
        await Ok(await owner.PostAsJsonAsync("/api/maintenance/meters/" + engine, new { reading = 1000 }));
        var plan = await Ok(await owner.PostAsJsonAsync("/api/maintenance/plans", new { name = "Engine: 500-hour inspection", assetId = engine, every = 12, unit = "Months", nextDue = far, meterEvery = 500, leadMeter = 50, estimatedHours = 6 }));
        Assert.Equal(1500, plan.GetProperty("nextDueMeter").GetDecimal());

        // 1,400 hours: not yet. 1,460: inside the lead, so the work order is made (a year before its date).
        Assert.Equal(0, (await Ok(await owner.PostAsJsonAsync("/api/maintenance/meters/" + engine, new { reading = 1400 }))).GetProperty("made").GetProperty("made").GetInt32());
        Assert.Equal(1, (await Ok(await owner.PostAsJsonAsync("/api/maintenance/meters/" + engine, new { reading = 1460, note = "After the Kisumu rotation" }))).GetProperty("made").GetProperty("made").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/maintenance/meters/" + engine, new { reading = 900 })).StatusCode); // meters only go up

        var job = (await Ok(await owner.GetAsync("/api/maintenance/work-orders"))).GetProperty("items")[0];
        Assert.Equal("Preventive", job.GetProperty("type").GetString());
        await Ok(await owner.PostAsJsonAsync("/api/maintenance/meters/" + engine, new { reading = 1480 }));
        await Ok(await owner.PostAsJsonAsync($"/api/maintenance/work-orders/{job.GetProperty("id")}/actions", new { kind = "complete" }));
        var after = (await Ok(await owner.GetAsync("/api/maintenance/plans"))).EnumerateArray().Single();
        Assert.Equal(1980, after.GetProperty("nextDueMeter").GetDecimal()); // 500 hours after it was done, at 1,480
        Assert.Equal("hours", after.GetProperty("meter").GetProperty("unit").GetString());
    }

    [Fact]
    public async Task A_cash_advance_goes_through_budget_and_sign_off_and_is_accounted_for_with_receipts()
    {
        var owner = await Owner();
        var employee = (await Ok(await owner.PostAsJsonAsync("/api/employees", new { name = "Brian Kiptoo", phone = "0711223344", startDate = "2025-01-06", basicSalary = 80_000_00, department = "Field sales" }))).GetProperty("id").GetGuid();
        var dept = (await Ok(await owner.PostAsJsonAsync("/api/departments", new { name = "Field sales" }))).GetProperty("id").GetGuid();
        var accountBy = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd");
        var adv = await Ok(await owner.PostAsJsonAsync("/api/advances", new { employeeId = employee, purpose = "Fuel and lodging, Eldoret client visits, 3 days", amount = 15_000_00, accountBy }));
        var id = adv.GetProperty("advance").GetProperty("id").GetGuid();
        Assert.Equal("WithFinance", adv.GetProperty("advance").GetProperty("status").GetString());

        // No approved budget: finance can't sign it off.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/advances/{id}/sign-off", new { })).StatusCode);
        var b = await Ok(await owner.PostAsJsonAsync("/api/budgets", new { departmentId = dept, year = Year, months = Enumerable.Repeat(50_000_00L, 12), justification = "Travel and client visits", submit = true }));
        await Ok(await owner.PostAsJsonAsync($"/api/budgets/{b.GetProperty("budget").GetProperty("id")}/approve", new { }));
        var procurement = await As(owner, "Procurement");
        Assert.Equal(HttpStatusCode.Forbidden, (await procurement.PostAsJsonAsync($"/api/advances/{id}/sign-off", new { })).StatusCode);
        await Ok(await owner.PostAsJsonAsync($"/api/advances/{id}/sign-off", new { }));
        Assert.Equal(15_000_00, (await Ok(await owner.GetAsync("/api/departments"))).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == dept).GetProperty("committed").GetInt64());

        // Paid out (recorded, not sent): Brian owes it.
        await Ok(await owner.PostAsJsonAsync($"/api/advances/{id}/pay", new { method = "Cash", reference = "PV-0012" }));
        Assert.Equal(15_000_00, (await Balances(owner))["STAFF_ADV"]);

        // He spent 12,500 and hands back 2,500: the receipts are the department's spending, and he owes nothing.
        var done = await Ok(await owner.PostAsJsonAsync($"/api/advances/{id}/account", new
        {
            expenses = new object[] { new { description = "Fuel", amount = 6_000_00, receipt = "ETR-88121" }, new { description = "Lodging, 2 nights", amount = 6_500_00, receipt = "ETR-2231" } },
            leftover = "returned", method = "Cash",
        }));
        Assert.Equal("Accounted", done.GetProperty("advance").GetProperty("status").GetString());
        var books = await Balances(owner);
        Assert.Equal(0, books["STAFF_ADV"]);
        Assert.Equal(12_500_00, (await Ok(await owner.GetAsync("/api/departments"))).EnumerateArray().Single(d => d.GetProperty("id").GetGuid() == dept).GetProperty("spent").GetInt64());
        var trail = done.GetProperty("trail").EnumerateArray().Select(t => t.GetProperty("action").GetString()).ToList();
        Assert.Equal(["Asked for", "Signed off by finance", "Paid out", "Accounted for"], trail);

        // The imprest rule: an overdue advance blocks the next one.
        var second = await Ok(await owner.PostAsJsonAsync("/api/advances", new { employeeId = employee, purpose = "Nakuru trade fair stand costs", amount = 5_000_00, accountBy }));
        var sid = second.GetProperty("advance").GetProperty("id").GetGuid();
        await Ok(await owner.PostAsJsonAsync($"/api/advances/{sid}/sign-off", new { }));
        await Ok(await owner.PostAsJsonAsync($"/api/advances/{sid}/pay", new { method = "Cash" }));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
            var a = await db.CashAdvances.IgnoreQueryFilters().SingleAsync(x => x.Id == sid);
            a.AccountBy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
            await db.SaveChangesAsync();
        }
        var refused = await owner.PostAsJsonAsync("/api/advances", new { employeeId = employee, purpose = "Another trip to Kitale", amount = 1_000_00, accountBy });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        // Nothing spent: all of it comes back from his next pay, as a recovery payroll already knows.
        await Ok(await owner.PostAsJsonAsync($"/api/advances/{sid}/account", new { expenses = Array.Empty<object>(), leftover = "payroll" }));
        var deductions = await Ok(await owner.GetAsync($"/api/employees/{employee}/deductions"));
        Assert.Contains("ADV-", deductions.GetRawText());
        Assert.True((await Ok(await owner.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
    }

    [Fact]
    public async Task Budget_moves_between_departments_need_finance_and_a_kept_fingerprint_still_checks()
    {
        var owner = await Owner();
        var a = await BudgetedDepartment(owner, "Engineering", 100_000_00);
        var b = await BudgetedDepartment(owner, "Ground handling", 20_000_00);
        var next = DateTime.UtcNow.AddHours(3).AddMonths(1);
        var accountant = await As(owner, "Accountant");
        // The statement taken before the transfer: its fingerprint must still be found afterwards.
        var statement = await (await owner.GetAsync("/api/approvals/statement")).Content.ReadAsStringAsync();
        var kept = statement.Split('\n').Single(l => l.Contains("Fingerprint")).Split(':')[1].Trim();

        var t = await Ok(await accountant.PostAsJsonAsync("/api/budgets/transfers", new { fromDepartmentId = a, toDepartmentId = b, year = next.Year, month = next.Month, amount = 30_000_00, reason = "Tug overhaul brought forward into next month" }));
        var tid = t[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/budgets/transfers/{tid}/approve", new { })).StatusCode); // asked for it: can't approve it
        await Ok(await owner.PostAsJsonAsync($"/api/budgets/transfers/{tid}/approve", new { }));
        if (next.Year == Year)
        {
            var rows = (await Ok(await owner.GetAsync($"/api/budgets?year={Year}"))).GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(1_200_000_00 - 30_000_00, rows.Single(r => r.GetProperty("departmentId").GetGuid() == a).GetProperty("annual").GetInt64());
            Assert.Equal(240_000_00 + 30_000_00, rows.Single(r => r.GetProperty("departmentId").GetGuid() == b).GetProperty("annual").GetInt64());
        }
        // More than the giver has that month: refused.
        var big = await Ok(await accountant.PostAsJsonAsync("/api/budgets/transfers", new { fromDepartmentId = b, toDepartmentId = a, year = next.Year, month = next.Month, amount = 999_000_00, reason = "Testing an impossible transfer here" }));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/budgets/transfers/{big[0].GetProperty("id")}/approve", new { })).StatusCode);

        var found = await Ok(await owner.PostAsJsonAsync("/api/approvals/check", new { fingerprint = kept }));
        Assert.True(found.GetProperty("found").GetBoolean());
        var missing = await Ok(await owner.PostAsJsonAsync("/api/approvals/check", new { fingerprint = new string('a', 64) }));
        Assert.False(missing.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Material_planning_works_out_what_to_make_and_buy_and_raises_the_request()
    {
        var owner = await Owner();
        var dept = await BudgetedDepartment(owner, "Bakery", 500_000_00);
        async Task<Guid> Item(string name, decimal qty, long cost, long price = 0, decimal reorder = 0)
        {
            var id = (await Ok(await owner.PostAsJsonAsync("/api/items", new { name, unit = "pc", price, trackStock = true, reorderLevel = reorder }))).GetProperty("id").GetGuid();
            if (qty > 0) await Ok(await owner.PostAsJsonAsync("/api/stock/opening", new { itemId = id, quantity = qty, unitCost = cost }));
            return id;
        }
        var bread = await Item("Bread 400g", 2000, 30_00, 65_00, 50);
        var flour = await Item("Wheat flour 1kg", 20, 120_00);
        var yeast = await Item("Yeast 10g", 500, 15_00);
        await Ok(await owner.PostAsJsonAsync("/api/production/recipes", new { name = "Bread batch", outputItemId = bread, outputQuantity = 100, lines = new object[] { new { itemId = flour, quantity = 40 }, new { itemId = yeast, quantity = 10 } } }));
        // Five months of selling about 300 loaves a month.
        var customer = (await Ok(await owner.PostAsJsonAsync("/api/customers", new { name = "Mama Mboga Kiosk" }))).GetProperty("id").GetGuid();
        var now = DateTime.UtcNow.AddHours(3);
        for (var m = 5; m >= 1; m--)
        {
            var date = new DateTime(now.Year, now.Month, 10).AddMonths(-m).ToString("yyyy-MM-dd");
            var inv = await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, issueDate = date, lines = new[] { new { itemId = bread, quantity = 290 + m * 4, unitPrice = 65_00 } } }));
            await Ok(await owner.PostAsync($"/api/sales/{inv.GetProperty("id")}/issue", null));
        }
        // Little bread left: sell most of it.
        var big = await Ok(await owner.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { itemId = bread, quantity = 400, unitPrice = 65_00 } } }));
        await Ok(await owner.PostAsync($"/api/sales/{big.GetProperty("id")}/issue", null));

        var plan = await Ok(await owner.GetAsync("/api/os/mrp?months=3"));
        var product = plan.GetProperty("products").EnumerateArray().Single();
        Assert.True(product.GetProperty("toMake").GetDecimal() > 0);
        var flourLine = plan.GetProperty("materials").EnumerateArray().Single(m => m.GetProperty("itemId").GetGuid() == flour);
        Assert.True(flourLine.GetProperty("toBuy").GetDecimal() > 0); // 20 kg on hand is far from enough
        var req = await Ok(await owner.PostAsJsonAsync("/api/os/mrp/request", new { departmentId = dept, lines = new[] { new { itemId = flour, quantity = flourLine.GetProperty("toBuy").GetDecimal() } } }));
        var r = await Ok(await owner.GetAsync($"/api/requisitions/{req.GetProperty("id")}"));
        Assert.Equal("WithBuying", r.GetProperty("request").GetProperty("status").GetString()); // through procurement and finance like any request
    }

    [Fact]
    public async Task The_planning_meeting_is_sealed_once_agreed_and_health_scores_the_business()
    {
        var owner = await Owner();
        await BudgetedDepartment(owner, "Operations", 100_000_00);
        var page = await Ok(await owner.GetAsync("/api/os/plan"));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("meeting").ValueKind);
        Assert.Equal(4, page.GetProperty("capacity").GetProperty("weeks").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/os/plan", new { agree = true })).StatusCode);
        var sales = await As(owner, "Sales");
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/os/plan")).StatusCode);
        var agreed = await Ok(await owner.PostAsJsonAsync("/api/os/plan", new { attendees = "Achieng (owner), Peter (engineering), Grace (sales)", decisions = "Bring the tug overhaul forward.\nHold hiring until December.\nOrder brake spares now.", agree = true }));
        Assert.NotEqual(JsonValueKind.Null, agreed.GetProperty("meeting").GetProperty("agreedAt").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/os/plan", new { decisions = "Changed my mind" })).StatusCode);
        var register = await Ok(await owner.GetAsync("/api/approvals?subject=Plan"));
        Assert.Single(register.EnumerateArray());

        var health = await Ok(await owner.GetAsync("/api/os/health"));
        Assert.Contains(health.GetProperty("grade").GetString(), new[] { "A", "B", "C", "D" });
        var checks = health.GetProperty("checks").EnumerateArray().ToList();
        Assert.True(checks.Single(c => c.GetProperty("key").GetString() == "budgets").GetProperty("met").GetBoolean());
        Assert.True(checks.Single(c => c.GetProperty("key").GetString() == "meeting").GetProperty("met").GetBoolean());
        Assert.True(checks.Single(c => c.GetProperty("key").GetString() == "register").GetProperty("met").GetBoolean());
    }
}
