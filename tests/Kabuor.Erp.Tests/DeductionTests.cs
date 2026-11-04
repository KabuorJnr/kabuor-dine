// Agreed deductions: HR sets them up, nothing is taken until the employee agrees (or HR records a signed form /
// HELB notice), payroll takes them in order without leaving less than a third of pay, loans stop when repaid,
// the books keep money owed to HELB/SACCOs apart from loans repaid to the business, and pension cuts the tax.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Common;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payroll;

namespace Kabuor.Erp.Tests;

public class DeductionTests(ErpFactory factory) : IClassFixture<ErpFactory>
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
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = "Deductions Ltd", ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        return c;
    }

    static string Phone() => $"07{Random.Shared.Next(10_000_000, 99_999_999)}";

    async Task<HttpClient> EmployeeApp(HttpClient hr, Guid employeeId)
    {
        var grant = await Ok(await hr.PostAsync($"/api/employees/{employeeId}/access", null));
        var c = factory.CreateClient();
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/login", new { email = grant.GetProperty("phone").GetString(), password = grant.GetProperty("password").GetString() }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var changed = await Ok(await c.PostAsJsonAsync("/api/auth/password", new { current = grant.GetProperty("password").GetString(), @new = "employee-own-password" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", changed.GetProperty("token").GetString());
        return c;
    }

    static List<JsonElement> Lines(JsonElement slip) => JsonDocument.Parse(slip.GetProperty("deductionLines").GetString()!).RootElement.EnumerateArray().ToList();

    [Fact]
    public async Task Nothing_is_taken_until_the_employee_agrees_and_the_books_keep_HELB_apart_from_loans()
    {
        var hr = await Owner();
        var wanjiru = await Ok(await hr.PostAsJsonAsync("/api/employees", new { name = "Wanjiru Kamau", phone = Phone(), startDate = "2025-01-06", basicSalary = 60_000_00 }));
        var id = wanjiru.GetProperty("id").GetGuid();

        // The app needs access to ask her; without it HR must record a signed form.
        Assert.Equal(HttpStatusCode.BadRequest, (await hr.PostAsJsonAsync($"/api/employees/{id}/deductions", new { kind = "Sacco", payTo = "Stima Sacco", monthlyAmount = 5_000_00, consent = "app" })).StatusCode);
        var me = await EmployeeApp(hr, id);

        var helb = await Ok(await hr.PostAsJsonAsync($"/api/employees/{id}/deductions", new { kind = "Helb", reference = "HELB-22-1234", monthlyAmount = 3_500_00, consent = "helb" }));
        Assert.Equal("Active", helb.GetProperty("status").GetString());
        Assert.Equal("HELB", helb.GetProperty("payTo").GetString());
        var sacco = await Ok(await hr.PostAsJsonAsync($"/api/employees/{id}/deductions", new { kind = "Sacco", payTo = "Stima Sacco", reference = "M-889", monthlyAmount = 5_000_00, consent = "app" }));
        Assert.Equal("AwaitingEmployee", sacco.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await hr.PostAsJsonAsync($"/api/employees/{id}/deductions", new { kind = "Loan", monthlyAmount = 5_000_00, consent = "form" })).StatusCode); // a loan needs its total
        var loan = await Ok(await hr.PostAsJsonAsync($"/api/employees/{id}/deductions", new { kind = "Loan", monthlyAmount = 6_000_00, total = 10_000_00, consent = "app", note = "Emergency loan, 3 Oct" }));

        // She sees both waiting, agrees to the loan, says no to the SACCO.
        var mine = await Ok(await me.GetAsync("/api/me"));
        Assert.Equal(3, mine.GetProperty("deductions").GetArrayLength());
        await Ok(await me.PostAsync($"/api/me/deductions/{loan.GetProperty("id")}/agree", null));
        await Ok(await me.PostAsJsonAsync($"/api/me/deductions/{sacco.GetProperty("id")}/decline", new { reason = "Not joining this year" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsync($"/api/me/deductions/{sacco.GetProperty("id")}/agree", null)).StatusCode);

        // This month's payroll (deductions start the month they are added): HELB and the loan, not the SACCO.
        var run = await Ok(await hr.PostAsJsonAsync("/api/payroll/runs", new { month = Clock.Today.ToString("yyyy-MM") }));
        var detail = await Ok(await hr.GetAsync($"/api/payroll/runs/{run.GetProperty("id")}"));
        var slip = detail.GetProperty("payslips")[0];
        var lines = Lines(slip);
        Assert.Equal(["HELB loan repayment", "Loan from the business"], lines.Select(l => l.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(9_500_00, slip.GetProperty("otherDeductions").GetInt64());
        Assert.Contains(detail.GetProperty("statutory").EnumerateArray(), k => k.GetProperty("kind").GetString() == "TO:HELB" && k.GetProperty("amount").GetInt64() == 3_500_00);

        await Ok(await hr.PostAsync($"/api/payroll/runs/{run.GetProperty("id")}/approve", null));
        var accounts = (await Ok(await hr.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.Equal(-3_500_00, accounts["DEDUCTIONS_PAYABLE"]); // owed to HELB
        Assert.Equal(-6_000_00, accounts["STAFF_ADV"]);          // she owes the business less
        Assert.True((await Ok(await hr.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());
        await Ok(await hr.PostAsJsonAsync($"/api/payroll/runs/{run.GetProperty("id")}/remit", new { kind = "TO:HELB", method = "Bank", reference = "HELB-OCT" }));
        accounts = (await Ok(await hr.GetAsync("/api/reports/accounts"))).EnumerateArray()
            .Where(a => a.TryGetProperty("systemKey", out var k) && k.ValueKind == JsonValueKind.String)
            .ToDictionary(a => a.GetProperty("systemKey").GetString()!, a => a.GetProperty("balance").GetInt64());
        Assert.Equal(0, accounts["DEDUCTIONS_PAYABLE"]);

        // Next month: only the KSh 4,000 left on the loan, then it's done.
        var next = await Ok(await hr.PostAsJsonAsync("/api/payroll/runs", new { month = Clock.Today.AddMonths(1).ToString("yyyy-MM") }));
        var nextSlip = (await Ok(await hr.GetAsync($"/api/payroll/runs/{next.GetProperty("id")}"))).GetProperty("payslips")[0];
        Assert.Equal(4_000_00, Lines(nextSlip).Single(l => l.GetProperty("kind").GetString() == "Loan").GetProperty("amount").GetInt64());
        await Ok(await hr.PostAsync($"/api/payroll/runs/{next.GetProperty("id")}/approve", null));
        var list = (await Ok(await hr.GetAsync($"/api/employees/{id}/deductions"))).EnumerateArray().ToList();
        Assert.Equal("Completed", list.Single(d => d.GetProperty("kind").GetString() == "Loan").GetProperty("status").GetString());

        // HR raises the HELB amount: it now needs a fresh notice or her agreement.
        var raised = await Ok(await hr.PutAsJsonAsync($"/api/employees/{id}/deductions/{helb.GetProperty("id")}", new { kind = "Helb", reference = "HELB-22-1234", monthlyAmount = 4_000_00, consent = "app" }));
        Assert.Equal("AwaitingEmployee", raised.GetProperty("status").GetString());
    }

    [Fact]
    public void Deductions_never_leave_less_than_a_third_of_pay()
    {
        var month = new DateOnly(2026, 10, 1);
        var agreed = new[]
        {
            new EmployeeDeduction { Kind = DeductionKind.Sacco, Name = "Sacco", PayTo = "Sacco", MonthlyAmount = 9_000_00, Status = DeductionStatus.Active, StartMonth = month, CreatedAt = DateTimeOffset.UtcNow },
            new EmployeeDeduction { Kind = DeductionKind.Helb, Name = "HELB", PayTo = "HELB", MonthlyAmount = 6_000_00, Status = DeductionStatus.Active, StartMonth = month, CreatedAt = DateTimeOffset.UtcNow.AddDays(1) },
        };
        // Gross 30,000; take-home before these 24,000: a third (10,000) must stay, so only 14,000 can go.
        var lines = DeductionService.Plan(agreed, month, 24_000_00, 30_000_00);
        Assert.Equal("HELB", lines[0].Name); // HELB comes before the SACCO
        Assert.Equal(6_000_00, lines[0].Amount);
        Assert.Equal(8_000_00, lines[1].Amount);
        Assert.Equal(9_000_00, lines[1].Wanted); // shown as cut short
    }

    [Fact]
    public void A_voluntary_pension_lowers_the_tax_within_the_limit()
    {
        var rates = Statutory.For(new DateOnly(2026, 10, 1));
        var without = Statutory.Calculate(new PayInput(150_000_00, 0, 0, 0, 0), rates);
        var with = Statutory.Calculate(new PayInput(150_000_00, 0, 0, 0, 0, Pension: 10_000_00), rates);
        Assert.True(with.Paye < without.Paye);
        Assert.Equal(without.Taxable - 10_000_00, with.Taxable);
        Assert.Equal(without.Net - 10_000_00 + (without.Paye - with.Paye), with.Net);
        // Above KSh 30,000 together with NSSF, the rest gets no tax relief.
        var big = Statutory.Calculate(new PayInput(150_000_00, 0, 0, 0, 0, Pension: 50_000_00), rates);
        Assert.Equal(without.Taxable - (30_000_00 - without.NssfEmployee), big.Taxable);
    }
}
