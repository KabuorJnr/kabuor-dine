// Kenyan payroll: statutory maths worked out by hand, then the whole flow through the API.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Common;
using Kabuor.Erp.Payroll;

namespace Kabuor.Erp.Tests;

public class StatutoryTests
{
    static readonly StatutoryRates Oct2026 = Statutory.For(new DateOnly(2026, 10, 1));

    [Fact]
    public void Fifty_thousand_gross_in_2026()
    {
        // NSSF 540 + 2,460; SHIF 2.75%; Housing Levy 1.5%; taxable 44,875; tax 2,400 + 2,083.25 + 3,762.60 − relief 2,400.
        var r = Statutory.Calculate(new PayInput(50_000_00, 0, 0, 0, 0), Oct2026);
        Assert.Equal(3_000_00, r.NssfEmployee);
        Assert.Equal(1_375_00, r.Shif);
        Assert.Equal(750_00, r.HousingLevyEmployee);
        Assert.Equal(44_875_00, r.Taxable);
        Assert.Equal(8_245_85, r.PayeBeforeRelief);
        Assert.Equal(5_845_85, r.Paye);
        Assert.Equal(39_029_15, r.Net);
        Assert.Equal(3_000_00 + 750_00 + 50_00, r.EmployerCost);
    }

    [Fact]
    public void Low_pay_gets_relief_down_to_zero_paye_and_the_shif_minimum_applies()
    {
        var r = Statutory.Calculate(new PayInput(15_000_00, 0, 0, 0, 0), Oct2026);
        Assert.Equal(900_00, r.NssfEmployee);
        Assert.Equal(412_50, r.Shif);
        Assert.Equal(0, r.Paye);
        Assert.Equal(13_462_50, r.Net);
        Assert.Equal(300_00, Statutory.Calculate(new PayInput(8_000_00, 0, 0, 0, 0), Oct2026).Shif);
    }

    [Fact]
    public void High_pay_hits_the_nssf_ceiling()
    {
        var r = Statutory.Calculate(new PayInput(150_000_00, 50_000_00, 0, 0, 0), Oct2026);
        Assert.Equal(6_480_00, r.NssfEmployee); // the 2026 maximum
        Assert.Equal(185_020_00, r.Taxable);
        Assert.Equal(47_889_35, r.Paye);
        Assert.Equal(137_130_65, r.Net);
    }

    [Fact]
    public void Each_month_uses_the_rates_in_force_then()
    {
        Assert.Equal(4_320_00, Statutory.Nssf(100_000_00, Statutory.For(new DateOnly(2025, 3, 1))));
        Assert.Equal(6_000_00, Statutory.Nssf(100_000_00, Statutory.For(new DateOnly(2026, 3, 1))));
        Assert.Throws<Common.AppException>(() => Statutory.For(new DateOnly(2024, 6, 1)));
    }

    [Fact]
    public void Deductions_and_untaxed_pay_change_net_but_not_tax()
    {
        var plain = Statutory.Calculate(new PayInput(50_000_00, 0, 0, 0, 0), Oct2026);
        var r = Statutory.Calculate(new PayInput(50_000_00, 0, 0, 2_000_00, 5_000_00), Oct2026);
        Assert.Equal(plain.Paye, r.Paye);
        Assert.Equal(plain.Net + 2_000_00 - 5_000_00, r.Net);
    }

    [Fact]
    public void Leave_counts_working_days()
    {
        Assert.Equal(5, PeopleService.WorkingDays(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25))); // Mon–Sun
        Assert.Equal(0, PeopleService.WorkingDays(new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 25)));
    }
}

public class PayrollFlowTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
            .ToDictionary(a => a.GetProperty("systemKey").GetString() ?? a.GetProperty("code").GetString()!, a => a.GetProperty("balance").GetInt64());

    [Fact]
    public async Task A_month_of_payroll_from_draft_to_paid_keeps_the_books_right()
    {
        var c = await SignUp("Payroll Ltd");
        var wanjiru = await Ok(await c.PostAsJsonAsync("/api/employees", new { name = "Wanjiru Kamau", phone = "0722 333 444", kraPin = "A012345678Z", jobTitle = "Sales lead", startDate = "2025-01-06", basicSalary = 45_000_00, taxableAllowances = 5_000_00 }));
        await Ok(await c.PostAsJsonAsync("/api/employees", new { name = "Otieno Ouma", phone = "0733 555 666", startDate = "2026-03-01", basicSalary = 15_000_00, payMethod = "Mpesa" }));

        var run = await Ok(await c.PostAsJsonAsync("/api/payroll/runs", new { month = "2026-10" }));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/payroll/runs", new { month = "2026-10" })).StatusCode);
        var detail = await Ok(await c.GetAsync($"/api/payroll/runs/{run.GetProperty("id")}"));
        var slips = detail.GetProperty("payslips").EnumerateArray().ToList();
        var w = slips.Single(s => s.GetProperty("employeeName").GetString() == "Wanjiru Kamau");
        Assert.Equal(39_029_15, w.GetProperty("net").GetInt64());

        // A KSh 3,000 salary advance comes off Wanjiru's pay this month.
        await Ok(await c.PutAsJsonAsync($"/api/payroll/runs/{run.GetProperty("id")}/payslips/{w.GetProperty("id")}", new { otherDeductions = 3_000_00, note = "Advance, 2 Oct" }));
        detail = await Ok(await c.GetAsync($"/api/payroll/runs/{run.GetProperty("id")}"));
        Assert.Equal(36_029_15, detail.GetProperty("payslips").EnumerateArray().Single(s => s.GetProperty("employeeName").GetString() == "Wanjiru Kamau").GetProperty("net").GetInt64());

        await Ok(await c.PostAsync($"/api/payroll/runs/{run.GetProperty("id")}/approve", null));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/payroll/runs/{run.GetProperty("id")}/payslips/{w.GetProperty("id")}", new { otherDeductions = 0 })).StatusCode);
        var b = await Balances(c);
        Assert.Equal(65_000_00, b["SALARIES"]);
        Assert.Equal(-5_845_85, b["PAYE"]);
        Assert.Equal(-(3_000_00 + 3_000_00 + 900_00 + 900_00), b["NSSF"]);
        Assert.Equal(-(36_029_15 + 13_462_50), b["NET_PAY"]);
        Assert.Equal(-3_000_00, b["STAFF_ADV"]);

        await Ok(await c.PostAsJsonAsync($"/api/payroll/runs/{run.GetProperty("id")}/pay", new { method = "Mpesa", reference = "B2C-OCT" }));
        await Ok(await c.PostAsJsonAsync($"/api/payroll/runs/{run.GetProperty("id")}/remit", new { kind = "PAYE", method = "Bank", reference = "PRN-123" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/payroll/runs/{run.GetProperty("id")}/remit", new { kind = "PAYE", method = "Bank" })).StatusCode);
        b = await Balances(c);
        Assert.Equal(0, b["NET_PAY"]);
        Assert.Equal(0, b["PAYE"]);
        Assert.True((await Ok(await c.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());

        var today = await Ok(await c.GetAsync("/api/today"));
        Assert.Equal(4, today.GetProperty("people").GetProperty("statutoryDue").GetProperty("items").GetArrayLength()); // NSSF, SHIF, AHL, NITA left

        var p9 = await Ok(await c.GetAsync($"/api/employees/{wanjiru.GetProperty("id")}/p9?year=2026"));
        Assert.Equal(5_845_85, p9.GetProperty("totals").GetProperty("paye").GetInt64());
        var csv = await (await c.GetAsync($"/api/payroll/runs/{run.GetProperty("id")}/p10.csv")).Content.ReadAsStringAsync();
        Assert.Contains("A012345678Z,Wanjiru Kamau,45000.00,5000.00,50000.00", csv);
    }

    [Fact]
    public async Task Leave_is_counted_in_working_days_and_overlaps_are_refused()
    {
        var c = await SignUp("Leave Ltd");
        var e = await Ok(await c.PostAsJsonAsync("/api/employees", new { name = "Akinyi Leave", phone = "0712 000 111", startDate = "2025-01-01", basicSalary = 30_000_00 }));
        var l = await Ok(await c.PostAsJsonAsync("/api/leave", new { employeeId = e.GetProperty("id").GetGuid(), type = "Annual", from = "2026-11-02", to = "2026-11-08" }));
        Assert.Equal(5, l.GetProperty("days").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/leave", new { employeeId = e.GetProperty("id").GetGuid(), type = "Sick", from = "2026-11-05", to = "2026-11-06" })).StatusCode);
        await Ok(await c.PostAsync($"/api/leave/{l.GetProperty("id")}/approve", null));
        var detail = await Ok(await c.GetAsync($"/api/employees/{e.GetProperty("id")}"));
        Assert.Equal(5, detail.GetProperty("leaveBalance").GetProperty("taken").GetDecimal());
        Assert.Equal(21, detail.GetProperty("leaveBalance").GetProperty("entitlement").GetDecimal());
    }

    [Fact]
    public async Task Salaries_are_private_to_owner_accountant_and_hr()
    {
        var owner = await SignUp("Private Pay Ltd");
        await Ok(await owner.PostAsJsonAsync("/api/employees", new { name = "Secret Salary", phone = "0712 000 222", basicSalary = 99_000_00 }));
        var hr = await As(owner, "HR");
        var sales = await As(owner, "Sales");
        var viewer = await As(owner, "Viewer");
        Assert.Equal(1, (await Ok(await hr.GetAsync("/api/employees"))).GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/payroll/runs")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Ok(await sales.GetAsync("/api/today"))).GetProperty("people").ValueKind);
        // HR prepares payroll; only the owner or an accountant approves it (it moves money in the books).
        var run = await Ok(await hr.PostAsJsonAsync("/api/payroll/runs", new { month = Clock.Today.ToString("yyyy-MM") }));
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsync($"/api/payroll/runs/{run.GetProperty("id")}/approve", null)).StatusCode);
        var other = await SignUp("Other Co");
        Assert.Equal(0, (await Ok(await other.GetAsync("/api/employees"))).GetProperty("counts").GetProperty("all").GetInt32());
    }
}
