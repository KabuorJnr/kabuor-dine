// Reports as files. Every report downloads as a spreadsheet or CSV, and the monthly pack puts a month's numbers in
// one workbook to send to a partner, the bank or the accountant: a summary against targets, profit and loss, the
// balance sheet, cash flow, what sold and to whom, who owes, VAT, payroll and the year's trend. The tables come from
// the same services as the screens, so a file never disagrees with what the app shows. The printable monthly report
// renders these same tables.
using System.Text.Json;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Intelligence;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Reports;

public sealed class ReportExports(ErpDb db, ReportService reports, KpiService kpis)
{
    public static readonly string[] Names = ["profit-and-loss", "balance-sheet", "cash-flow", "aging", "vat", "trial-balance", "general-ledger"];

    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, AppCache.Json);
    static Ksh K(JsonElement e, string p) => new(e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDecimal()) : 0);
    static string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static string Range(DateOnly from, DateOnly to) => $"{from:d MMM yyyy} to {to:d MMM yyyy}";

    public async Task<string> CompanyName() => await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => c.Name).SingleAsync();

    /// <summary>One report as a table, for the dates asked (a balance sheet and trial balance are as at the end date).</summary>
    public async Task<Sheet> One(string report, DateOnly from, DateOnly to)
    {
        if (to < from) throw new AppException("The end date is before the start date.");
        var company = await CompanyName();
        return report switch
        {
            "profit-and-loss" => await ProfitAndLoss(company, from, to),
            "balance-sheet" => await BalanceSheet(company, to),
            "cash-flow" => await CashFlow(company, from, to),
            "aging" => await Aging(company),
            "vat" => await Vat(company, from, to),
            "trial-balance" => await TrialBalance(company, to),
            "general-ledger" => await GeneralLedger(company, from, to),
            _ => throw new AppException($"No report called \"{report}\". Choose from: {string.Join(", ", Names)}.", 404),
        };
    }

    async Task<Sheet> ProfitAndLoss(string company, DateOnly from, DateOnly to)
    {
        var r = J(await reports.ProfitAndLoss(from, to));
        var (now, before) = (r.GetProperty("current"), r.GetProperty("previous"));
        var s = new Sheet("Profit and loss", $"{company}: profit and loss", $"{Range(from, to)}, against {Range(DateOnly.Parse(S(before, "from")), DateOnly.Parse(S(before, "to")))}", "Account", "This period (KSh)", "Period before (KSh)", "Change (KSh)");
        void Part(string section, string list, string total, string totalLabel)
        {
            s.Section(section);
            var prev = before.GetProperty(list).EnumerateArray().ToDictionary(x => S(x, "code"), x => K(x, "amount").Cents);
            var codes = new HashSet<string>();
            foreach (var x in now.GetProperty(list).EnumerateArray())
            {
                codes.Add(S(x, "code"));
                var p = prev.GetValueOrDefault(S(x, "code"));
                s.Add($"{S(x, "code")} {S(x, "name")}", K(x, "amount"), new Ksh(p), new Ksh(K(x, "amount").Cents - p));
            }
            foreach (var x in before.GetProperty(list).EnumerateArray().Where(x => !codes.Contains(S(x, "code"))))
                s.Add($"{S(x, "code")} {S(x, "name")}", new Ksh(0), K(x, "amount"), new Ksh(-K(x, "amount").Cents));
            s.Total(totalLabel, K(now, total), K(before, total), new Ksh(K(now, total).Cents - K(before, total).Cents));
        }
        Part("Money earned", "income", "totalIncome", "Total earned");
        Part("Money spent", "expenses", "totalExpenses", "Total spent");
        s.Total("Profit", K(now, "profit"), K(before, "profit"), new Ksh(K(now, "profit").Cents - K(before, "profit").Cents));
        s.Note = "Sales are counted without VAT (VAT belongs to KRA).";
        return s;
    }

    async Task<Sheet> BalanceSheet(string company, DateOnly at)
    {
        var r = J(await reports.BalanceSheet(at));
        var s = new Sheet("Balance sheet", $"{company}: balance sheet", $"At {at:d MMM yyyy}", "Account", "Amount (KSh)");
        void Part(string section, string key, string totalLabel)
        {
            s.Section(section);
            foreach (var x in r.GetProperty(key).GetProperty("lines").EnumerateArray()) s.Add($"{S(x, "code")} {S(x, "name")}", K(x, "amount"));
            if (key == "equity") s.Add("Profit so far (not yet moved to equity)", K(r.GetProperty(key), "profitSoFar"));
            s.Total(totalLabel, K(r.GetProperty(key), "total"));
        }
        Part("What the business owns", "assets", "Total assets");
        Part("What it owes", "liabilities", "Total liabilities");
        Part("Owner's equity", "equity", "Total equity");
        s.Total("Liabilities and equity", new Ksh(K(r.GetProperty("liabilities"), "total").Cents + K(r.GetProperty("equity"), "total").Cents));
        s.Note = r.GetProperty("balanced").GetBoolean() ? "Balanced: assets equal liabilities plus equity." : "Not balanced: tell support, something is wrong in the books.";
        return s;
    }

    async Task<Sheet> CashFlow(string company, DateOnly from, DateOnly to)
    {
        var r = J(await reports.CashFlow(from, to));
        var s = new Sheet("Cash flow", $"{company}: cash flow", $"{Range(from, to)}, through M-Pesa, bank and cash", "", "Money in (KSh)", "Money out (KSh)", "Net (KSh)");
        s.Add("Opening balance", null, null, K(r, "opening"));
        foreach (var x in r.GetProperty("rows").EnumerateArray()) s.Add(S(x, "category"), K(x, "moneyIn"), K(x, "moneyOut"), K(x, "net"));
        s.Total("For the period", K(r, "moneyIn"), K(r, "moneyOut"), K(r, "net"));
        s.Total("Closing balance", null, null, K(r, "closing"));
        return s;
    }

    async Task<Sheet> Aging(string company)
    {
        var r = J(await reports.Aging());
        var labels = r.GetProperty("labels").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var s = new Sheet("Customers owe", $"{company}: what customers owe, by how late", $"As at {Clock.Today:d MMM yyyy}", ["Customer", "Phone", .. labels.Select(l => $"{l} (KSh)"), "Total (KSh)", "Oldest due"]);
        foreach (var x in r.GetProperty("rows").EnumerateArray())
            s.Add([S(x, "customer"), S(x, "phone"), .. x.GetProperty("buckets").EnumerateArray().Select(b => (object?)new Ksh(b.GetInt64())), K(x, "total"), DateOnly.Parse(S(x, "oldestDue"))]);
        s.Total(["Total", null, .. r.GetProperty("totals").EnumerateArray().Select(b => (object?)new Ksh(b.GetInt64())), K(r, "total"), null]);
        return s;
    }

    async Task<Sheet> Vat(string company, DateOnly from, DateOnly to)
    {
        var r = J(await reports.Vat(from, to));
        var s = new Sheet("VAT", $"{company}: VAT", Range(from, to), "", "Value before VAT (KSh)", "VAT (KSh)", "Total (KSh)");
        s.Section("Sales: VAT you charged");
        foreach (var x in r.GetProperty("byType").EnumerateArray()) s.Add(S(x, "label"), K(x, "net"), K(x, "tax"), K(x, "total"));
        s.Section("Purchases: VAT you paid");
        foreach (var x in r.GetProperty("purchases").EnumerateArray()) s.Add(S(x, "label"), K(x, "net"), K(x, "tax"), K(x, "total"));
        s.Total("VAT charged on sales", null, K(r, "totalTax"), null);
        s.Total("Less VAT paid on purchases", null, K(r, "inputTax"), null);
        s.Total("Less VAT withheld by customers", null, K(r, "vatWithheld"), null);
        s.Total("VAT to pay KRA", null, K(r, "payable"), null);
        var missing = r.GetProperty("billsMissingSupplierInvoiceNo").EnumerateArray().Select(x => x.GetString()).ToList();
        s.Note = missing.Count == 0 ? "VAT is due to KRA by the 20th of the following month." : $"Bills without the supplier's invoice number can't be claimed: {string.Join(", ", missing)}.";
        return s;
    }

    async Task<Sheet> TrialBalance(string company, DateOnly to)
    {
        var balances = await reports.Balances(null, to);
        var s = new Sheet("Trial balance", $"{company}: trial balance", $"At {to:d MMM yyyy}", "Code", "Account", "Type", "Debit (KSh)", "Credit (KSh)");
        foreach (var b in balances.Where(b => b.Debit != b.Credit).OrderBy(b => b.Code))
            s.Add(b.Code, b.Name, b.Type.ToString(), new Ksh(Math.Max(0, b.Debit - b.Credit)), new Ksh(Math.Max(0, b.Credit - b.Debit)));
        var (dr, cr) = (balances.Sum(b => Math.Max(0, b.Debit - b.Credit)), balances.Sum(b => Math.Max(0, b.Credit - b.Debit)));
        s.Total("Total", null, null, new Ksh(dr), new Ksh(cr));
        s.Note = dr == cr ? "Debits equal credits." : "Debits and credits differ: tell support, something is wrong in the books.";
        return s;
    }

    async Task<Sheet> GeneralLedger(string company, DateOnly from, DateOnly to)
    {
        var accounts = await db.Accounts.ToDictionaryAsync(a => a.Id, a => (a.Code, a.Name));
        var lines = await db.JournalLines.Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { e.Date, e.Memo, e.SourceType, e.CreatedAt, l.AccountId, l.Debit, l.Credit })
            .Where(x => x.Date >= from && x.Date <= to).ToListAsync();
        var s = new Sheet("General ledger", $"{company}: general ledger", Range(from, to), "Date", "What", "Source", "Code", "Account", "Debit (KSh)", "Credit (KSh)");
        foreach (var x in lines.OrderBy(x => x.Date).ThenBy(x => x.CreatedAt))
        {
            var (code, name) = accounts.GetValueOrDefault(x.AccountId, ("?", "Unknown account"));
            s.Add(x.Date, x.Memo, x.SourceType, code, name, x.Debit == 0 ? null : new Ksh(x.Debit), x.Credit == 0 ? null : new Ksh(x.Credit));
        }
        s.Total("Total", null, null, null, null, new Ksh(lines.Sum(x => x.Debit)), new Ksh(lines.Sum(x => x.Credit)));
        return s;
    }

    /// <summary>The month in one workbook. <paramref name="month"/> is any day in the month.</summary>
    public async Task<List<Sheet>> Monthly(DateOnly month)
    {
        var today = Clock.Today;
        var from = new DateOnly(month.Year, month.Month, 1);
        if (from > today) throw new AppException("That month hasn't started yet.");
        var to = from.AddMonths(1).AddDays(-1);
        var end = to > today ? today : to;
        var company = await CompanyName();
        var title = from.ToString("MMMM yyyy");
        var offset = (from.Year - today.Year) * 12 + from.Month - today.Month;

        // The summary: the scorecard's measures for the month against its targets and the month before.
        var card = J(await kpis.Scorecard("month", Math.Max(offset, -36)));
        var pnl = J(await reports.ProfitAndLoss(from, end)).GetProperty("current");
        var summary = new Sheet("Summary", $"{company}: {title} at a glance", end < to ? $"{Range(from, end)} (the month so far)" : Range(from, to), "Measure", "This month", "Month before", "Change", "Target", "Status");
        summary.Section("Results");
        summary.Total("Profit (before tax)", K(pnl, "profit"), null, null, null, null);
        summary.Add("Money earned", K(pnl, "totalIncome"), null, null, null, null);
        summary.Add("Money spent", K(pnl, "totalExpenses"), null, null, null, null);
        foreach (var group in card.GetProperty("kpis").EnumerateArray().GroupBy(k => S(k, "group")))
        {
            summary.Section(group.Key);
            foreach (var k in group)
            {
                object? Value(string p) => k.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number
                    ? S(k, "unit") switch { "ksh" => new Ksh((long)Math.Round(v.GetDecimal())), "%" => new Pct(v.GetDecimal()), _ => (object)v.GetDecimal() }
                    : null;
                var status = S(k, "status") switch { "met" => "On target", "close" => "Close", "behind" => "Behind", _ => "No target" };
                summary.Add(S(k, "name"), Value("value"), Value("previous"), k.TryGetProperty("change", out var c) && c.ValueKind == JsonValueKind.Number ? new Pct(c.GetDecimal()) : null, Value("target"), status);
            }
        }
        summary.Note = "Change is against the same days of the month before. Targets are set on Today → Targets.";

        var sheets = new List<Sheet>
        {
            summary,
            await ProfitAndLoss(company, from, end),
            await BalanceSheet(company, end),
            await CashFlow(company, from, end),
            await SalesByProduct(company, from, end),
            await SalesByCustomer(company, from, end),
            await Aging(company),
            await Vat(company, from, end),
            await Payroll(company, from),
            await Trend(company),
        };
        return sheets;
    }

    static readonly SalesDocStatus[] Issued = [SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid];

    async Task<Sheet> SalesByProduct(string company, DateOnly from, DateOnly to)
    {
        var rows = await db.SalesLines.Join(db.SalesDocs, l => l.SalesDocId, d => d.Id, (l, d) => new { l, d })
            .Where(x => x.d.Kind == SalesDocKind.Invoice && !x.d.Opening && Issued.Contains(x.d.Status) && x.d.IssueDate >= from && x.d.IssueDate <= to)
            .Select(x => new { x.l.ItemId, x.l.Description, x.l.Quantity, x.l.Net, x.l.Cost }).ToListAsync();
        var s = new Sheet("Sales by product", $"{company}: what sold", Range(from, to), "Product or service", "Quantity", "Sales before VAT (KSh)", "Gross profit (KSh)", "Margin");
        foreach (var g in rows.GroupBy(x => x.ItemId?.ToString() ?? x.Description).Select(g => new { name = g.First().Description, qty = g.Sum(x => x.Quantity), net = g.Sum(x => x.Net), gp = g.Sum(x => x.Net - x.Cost) }).OrderByDescending(x => x.net))
            s.Add(g.name, g.qty, new Ksh(g.net), new Ksh(g.gp), g.net == 0 ? null : new Pct(Math.Round(100m * g.gp / g.net, 1)));
        s.Total("Total", rows.Sum(x => x.Quantity), new Ksh(rows.Sum(x => x.Net)), new Ksh(rows.Sum(x => x.Net - x.Cost)), rows.Sum(x => x.Net) == 0 ? null : new Pct(Math.Round(100m * rows.Sum(x => x.Net - x.Cost) / rows.Sum(x => x.Net), 1)));
        return s;
    }

    async Task<Sheet> SalesByCustomer(string company, DateOnly from, DateOnly to)
    {
        var docs = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= from && d.IssueDate <= to)
            .Select(d => new { d.CustomerId, Customer = d.Customer!.Name, d.Net, d.Total, d.Paid }).ToListAsync();
        var s = new Sheet("Sales by customer", $"{company}: who bought", Range(from, to), "Customer", "Invoices", "Sales before VAT (KSh)", "Invoiced with VAT (KSh)", "Still to pay (KSh)");
        foreach (var g in docs.GroupBy(d => new { d.CustomerId, d.Customer }).OrderByDescending(g => g.Sum(d => d.Net)))
            s.Add(g.Key.Customer, g.Count(), new Ksh(g.Sum(d => d.Net)), new Ksh(g.Sum(d => d.Total)), new Ksh(g.Sum(d => d.Total - d.Paid)));
        s.Total("Total", docs.Count, new Ksh(docs.Sum(d => d.Net)), new Ksh(docs.Sum(d => d.Total)), new Ksh(docs.Sum(d => d.Total - d.Paid)));
        return s;
    }

    async Task<Sheet> Payroll(string company, DateOnly month)
    {
        var run = await db.PayrollRuns.Include(r => r.Payslips).Where(r => r.Period == month && r.Status != PayrollStatus.Draft).FirstOrDefaultAsync();
        var s = new Sheet("Payroll", $"{company}: payroll", month.ToString("MMMM yyyy"), "Employee", "Gross (KSh)", "PAYE (KSh)", "NSSF (KSh)", "SHIF (KSh)", "Housing Levy (KSh)", "Other deductions (KSh)", "Take-home (KSh)");
        if (run is null) { s.Note = "No approved payroll for this month yet."; return s; }
        foreach (var p in run.Payslips.OrderBy(p => p.EmployeeName))
            s.Add(p.EmployeeName, new Ksh(p.Gross), new Ksh(p.Paye), new Ksh(p.NssfEmployee), new Ksh(p.Shif), new Ksh(p.HousingLevyEmployee), new Ksh(p.OtherDeductions + p.Pension), new Ksh(p.Net));
        s.Total("Total", new Ksh(run.Payslips.Sum(p => p.Gross)), new Ksh(run.Payslips.Sum(p => p.Paye)), new Ksh(run.Payslips.Sum(p => p.NssfEmployee)), new Ksh(run.Payslips.Sum(p => p.Shif)), new Ksh(run.Payslips.Sum(p => p.HousingLevyEmployee)), new Ksh(run.Payslips.Sum(p => p.OtherDeductions + p.Pension)), new Ksh(run.Payslips.Sum(p => p.Net)));
        s.Note = $"Employer costs on top: NSSF {Xlsx.Display(new Ksh(run.Payslips.Sum(p => p.NssfEmployer)))}, Housing Levy {Xlsx.Display(new Ksh(run.Payslips.Sum(p => p.HousingLevyEmployer)))}, NITA {Xlsx.Display(new Ksh(run.Payslips.Sum(p => p.Nita)))}. PAYE and statutory deductions are due by the 9th of the following month.";
        return s;
    }

    async Task<Sheet> Trend(string company)
    {
        var r = J(await kpis.Charts(12));
        var s = new Sheet("Last 12 months", $"{company}: the last 12 months", $"To {Clock.Today:d MMM yyyy}", "Month", "Sales before VAT (KSh)", "Target (KSh)", "Gross profit (KSh)", "Invoices", "Money in (KSh)", "Money out (KSh)", "Net cash (KSh)");
        foreach (var m in r.GetProperty("monthly").EnumerateArray())
        {
            var month = DateOnly.Parse(S(m, "month"));
            s.Add(month.ToString("MMM yyyy"), K(m, "sales"), m.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Number ? new Ksh((long)t.GetDecimal()) : null, K(m, "grossProfit"), m.GetProperty("invoices").GetInt32(), K(m, "moneyIn"), K(m, "moneyOut"), new Ksh(K(m, "moneyIn").Cents - K(m, "moneyOut").Cents));
        }
        return s;
    }

    /// <summary>A table for the printable report in the app: each cell says what it is, so money and dates are shown
    /// the way the app shows them everywhere else.</summary>
    public static object ToJson(Sheet s) => new
    {
        s.Name, s.Title, s.Subtitle, s.Columns, s.Note,
        rows = s.Rows.Select(r => new
        {
            style = r.Style.ToString().ToLowerInvariant(),
            cells = r.Cells.Select(c => c switch
            {
                null => null,
                Ksh k => new { k = "ksh", v = (object)k.Cents },
                Pct p => new { k = "pct", v = (object)p.Value },
                DateOnly d => new { k = "date", v = (object)d.ToString("yyyy-MM-dd") },
                int or long or decimal or double => new { k = "num", v = c },
                _ => new { k = "text", v = (object)(Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture) ?? "") },
            }),
        }),
    };

    /// <summary>File names people can keep: business, report and period, nothing a file system dislikes.</summary>
    public static string FileName(string company, string what, string period, string extension)
    {
        var raw = $"{company} - {what} - {period}";
        var safe = new string(raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' or '\'' ? ' ' : c).ToArray());
        return $"{string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries))}.{extension}";
    }
}
