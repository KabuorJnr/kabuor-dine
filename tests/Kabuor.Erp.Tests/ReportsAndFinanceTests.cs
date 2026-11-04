// Report files and the finance desk. Every report downloads as a real workbook and as CSV, with money as numbers;
// the monthly pack holds the month on one file; the finance overview adds up; manual journals balance, stay out of
// closed periods, reverse once; and only finance changes the books.
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Kabuor.Erp.Tests;

public class ReportsAndFinanceTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    static async Task<JsonElement> Ok(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement;
    }

    static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    /// <summary>A business with a sale, a payment and a supplier bill in the books.</summary>
    async Task<(HttpClient Owner, string Company)> Business()
    {
        var c = factory.CreateClient();
        var company = $"Ledger Shop {Guid.NewGuid():N}"[..20];
        var s = await Ok(await c.PostAsJsonAsync("/api/auth/register", new { companyName = company, ownerName = "Owner Person", email = $"{Guid.NewGuid():N}@example.com", phone = "0712345678", password = "a-long-password", kraPin = "P051234567X" }));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
        var cement = Id(await Ok(await c.PostAsJsonAsync("/api/items", new { name = "Cement 50kg", unit = "bag", price = 850_00, trackStock = true })));
        await Ok(await c.PostAsJsonAsync("/api/stock/opening", new { itemId = cement, quantity = 100, unitCost = 690_00 }));
        var customer = Id(await Ok(await c.PostAsJsonAsync("/api/customers", new { name = "Otieno Builders", phone = "0722410118" })));
        var inv = await Ok(await c.PostAsJsonAsync("/api/sales", new { kind = "Invoice", customerId = customer, lines = new[] { new { itemId = cement, quantity = 10 } } }));
        inv = await Ok(await c.PostAsync($"/api/sales/{Id(inv)}/issue", null));
        await Ok(await c.PostAsJsonAsync("/api/payments", new { method = "Mpesa", amount = 5_000_00, reference = "SJK41XQ", invoiceId = Id(inv) }));
        var supplier = Id(await Ok(await c.PostAsJsonAsync("/api/suppliers", new { name = "Bamburi Cement" })));
        var bill = await Ok(await c.PostAsJsonAsync("/api/purchases", new { kind = "Bill", supplierId = supplier, supplierRef = "BC-1", lines = new[] { new { itemId = cement, quantity = 20, unitCost = 690_00 } } }));
        await Ok(await c.PostAsync($"/api/purchases/{Id(bill)}/post", null));
        return (c, company);
    }

    static ZipArchive Workbook(byte[] bytes) => new(new MemoryStream(bytes), ZipArchiveMode.Read);
    static string Part(ZipArchive zip, string path) => new StreamReader(zip.GetEntry(path)!.Open()).ReadToEnd();

    [Fact]
    public async Task Every_report_downloads_as_a_workbook_and_as_csv()
    {
        var (owner, company) = await Business();
        foreach (var report in new[] { "profit-and-loss", "balance-sheet", "cash-flow", "aging", "vat", "trial-balance", "general-ledger" })
        {
            var res = await owner.GetAsync($"/api/reports/export/{report}");
            Assert.True(res.IsSuccessStatusCode, $"{report}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType!.MediaType);
            Assert.EndsWith(".xlsx", res.Content.Headers.ContentDisposition!.FileNameStar ?? res.Content.Headers.ContentDisposition.FileName);
            using var zip = Workbook(await res.Content.ReadAsByteArrayAsync());
            Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
            var sheet = Part(zip, "xl/worksheets/sheet1.xml");
            Assert.Contains(company, sheet); // the title names the business

            var csv = await owner.GetAsync($"/api/reports/export/{report}?format=csv");
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
            Assert.True((await csv.Content.ReadAsStringAsync()).Split('\n').Length >= 2, report);
        }

        // Money is a number in the workbook (so it adds up in Excel) and plain in CSV.
        using var aging = Workbook(await (await owner.GetAsync("/api/reports/export/aging")).Content.ReadAsByteArrayAsync());
        var agingSheet = Part(aging, "xl/worksheets/sheet1.xml");
        Assert.Contains("Otieno Builders", agingSheet);
        Assert.Contains("<v>3500</v>", agingSheet); // 10 bags at 850 (VAT included) = 8,500, less 5,000 paid
        var agingCsv = await (await owner.GetAsync("/api/reports/export/aging?format=csv")).Content.ReadAsStringAsync();
        Assert.Contains("Otieno Builders", agingCsv);
        Assert.Contains("3500.00", agingCsv);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/reports/export/salaries-of-the-ceo")).StatusCode);
        var tables = await Ok(await owner.GetAsync("/api/reports/export/profit-and-loss?format=json"));
        Assert.Equal("Profit and loss", tables.GetProperty("sheets")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_monthly_pack_holds_the_month_in_one_workbook()
    {
        var (owner, _) = await Business();
        var month = DateTime.UtcNow.AddHours(3).ToString("yyyy-MM");
        var res = await owner.GetAsync($"/api/reports/monthly?month={month}");
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        using var zip = Workbook(await res.Content.ReadAsByteArrayAsync());
        var workbook = Part(zip, "xl/workbook.xml");
        foreach (var name in new[] { "Summary", "Profit and loss", "Balance sheet", "Cash flow", "Sales by product", "Sales by customer", "Customers owe", "VAT", "Payroll", "Last 12 months" })
            Assert.Contains($"name=\"{name}\"", workbook);
        Assert.Equal(10, zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/")));

        var json = await Ok(await owner.GetAsync($"/api/reports/monthly?month={month}&format=json"));
        var summary = json.GetProperty("sheets")[0];
        Assert.Equal("Summary", summary.GetProperty("name").GetString());
        Assert.Contains(summary.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetProperty("v").GetString() == "Sales (before VAT)");
        var products = json.GetProperty("sheets").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Sales by product");
        Assert.Contains(products.GetProperty("rows").EnumerateArray(), r => r.GetProperty("cells")[0].GetProperty("v").GetString() == "Cement 50kg");

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/reports/monthly?month={DateTime.UtcNow.AddMonths(2):yyyy-MM}")).StatusCode);
    }

    [Fact]
    public async Task The_finance_overview_adds_up_and_names_what_is_due()
    {
        var (owner, _) = await Business();
        var o = await Ok(await owner.GetAsync("/api/finance/overview"));
        Assert.Equal(5_000_00, o.GetProperty("cash").GetProperty("total").GetInt64()); // the M-Pesa payment
        Assert.Equal(3_500_00, o.GetProperty("receivables").GetProperty("total").GetInt64());
        Assert.True(o.GetProperty("payables").GetProperty("total").GetInt64() > 0); // the posted bill
        Assert.Contains(o.GetProperty("deadlines").EnumerateArray(), d => d.GetProperty("payTo").GetString() == "KRA (iTax)");
        Assert.Contains(o.GetProperty("todo").EnumerateArray(), t => t.GetProperty("key").GetString() == "close");

        var accounts = (await Ok(await owner.GetAsync("/api/finance/accounts"))).EnumerateArray().ToList();
        var mpesa = accounts.Single(a => a.GetProperty("code").GetString() == "1010");
        Assert.Equal(5_000_00, mpesa.GetProperty("balance").GetInt64());
        Assert.True(mpesa.GetProperty("system").GetBoolean());
    }

    [Fact]
    public async Task Manual_journals_balance_stay_out_of_closed_months_and_reverse_once()
    {
        var (owner, company) = await Business();
        var fuel = await Ok(await owner.PostAsJsonAsync("/api/finance/accounts", new { code = "6150", name = "Fuel and transport", type = "Expense" }));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/finance/accounts", new { code = "6150", name = "Again", type = "Expense" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/finance/accounts", new { code = "6 1", name = "Bad code", type = "Expense" })).StatusCode);
        var cash = (await Ok(await owner.GetAsync("/api/finance/accounts"))).EnumerateArray().Single(a => a.GetProperty("code").GetString() == "1010");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));

        object Entry(long dr, long cr, DateOnly? date = null) => new { date = date ?? today, memo = "Fuel for the Kondele deliveries", lines = new[] { new { accountId = Id(fuel), debit = dr, credit = 0L }, new { accountId = Id(cash), debit = 0L, credit = cr } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/finance/journal", Entry(1_500_00, 1_400_00))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/finance/journal", new { date = today, memo = "One line", lines = new[] { new { accountId = Id(fuel), debit = 100L, credit = 0L } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/finance/journal", Entry(100, 100, today.AddDays(3)))).StatusCode);
        var posted = await Ok(await owner.PostAsJsonAsync("/api/finance/journal", Entry(1_500_00, 1_500_00)));

        var ledger = await Ok(await owner.GetAsync($"/api/finance/journal?accountId={Id(fuel)}"));
        var row = ledger.GetProperty("items")[0];
        Assert.True(row.GetProperty("manual").GetBoolean());
        Assert.Equal(1_500_00, row.GetProperty("amount").GetInt64());
        Assert.True((await Ok(await owner.GetAsync("/api/reports/trial-balance"))).GetProperty("balanced").GetBoolean());

        await Ok(await owner.PostAsync($"/api/finance/journal/{Id(posted)}/reverse", null));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/finance/journal/{Id(posted)}/reverse", null)).StatusCode);
        Assert.Contains((await Ok(await owner.GetAsync($"/api/finance/journal?accountId={Id(fuel)}"))).GetProperty("items").EnumerateArray(), e => e.GetProperty("reversed").GetBoolean());
        Assert.Equal(0, (await Ok(await owner.GetAsync("/api/finance/accounts"))).EnumerateArray().Single(a => a.GetProperty("code").GetString() == "6150").GetProperty("balance").GetInt64());
        // An invoice's entry is undone by voiding the invoice, not here.
        var invoiceEntry = (await Ok(await owner.GetAsync("/api/finance/journal?q=invoice"))).GetProperty("items").EnumerateArray().First(e => !e.GetProperty("manual").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/finance/journal/{Id(invoiceEntry)}/reverse", null)).StatusCode);

        // Close last month: nothing can be dated into it.
        var lastMonthEnd = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        await Ok(await owner.PutAsJsonAsync("/api/settings/company", new { name = company, kraPin = "P051234567X", vatRegistered = true, accountingLockDate = lastMonthEnd }));
        var closed = await owner.PostAsJsonAsync("/api/finance/journal", Entry(100, 100, lastMonthEnd));
        Assert.Equal(HttpStatusCode.BadRequest, closed.StatusCode);
        Assert.Contains("closed", await closed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Viewers_read_the_books_only_finance_changes_them_and_sales_sees_neither()
    {
        var (owner, _) = await Business();
        async Task<HttpClient> As(string role)
        {
            var email = $"{Guid.NewGuid():N}@example.com";
            await Ok(await owner.PostAsJsonAsync("/api/settings/users", new { name = $"{role} Person", email, role, password = "starting-password-1" }));
            var c = factory.CreateClient();
            var s = await TestSignIn.First(c, email, "starting-password-1");
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GetProperty("token").GetString());
            return c;
        }
        var viewer = await As("Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/finance/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/reports/monthly")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/finance/accounts", new { code = "6160", name = "Repairs", type = "Expense" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/finance/journal", new { date = "2026-10-01", memo = "Sneaky", lines = Array.Empty<object>() })).StatusCode);

        var accountant = await As("Accountant");
        Assert.Equal(HttpStatusCode.Created, (await accountant.PostAsJsonAsync("/api/finance/accounts", new { code = "6160", name = "Repairs", type = "Expense" })).StatusCode);

        var sales = await As("Sales");
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/finance/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/reports/export/profit-and-loss")).StatusCode);
    }

    // The ledger page went blank on a server that didn't have its address yet: the web app's page came back where
    // data was expected. An API address that matches nothing now says so, in JSON, signed in or not.
    [Fact]
    public async Task An_api_address_that_matches_nothing_answers_not_found_in_json()
    {
        var (owner, _) = await Business();
        foreach (var c in new[] { owner, factory.CreateClient() })
        {
            var res = await c.GetAsync("/api/finance/no-such-thing");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
            Assert.False(string.IsNullOrWhiteSpace(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()));
        }
    }
}
