// The finance department's desk. The accountant (or the owner wearing that hat) is deciding "what needs paying,
// what needs chasing, and can I close the month?", so the overview puts cash, what is owed both ways, this month's
// profit, what KRA and the statutory bodies are due and when, and the jobs that block a clean month-end in one place.
// Behind it: the chart of accounts with balances, the general ledger, and manual journals for the adjustments only an
// accountant makes (accruals, depreciation, corrections), which must balance and can't be dated into a closed period.
using System.Text.Json;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Reports;
using Kabuor.Erp.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Finance;

public sealed record AccountInput(string Code, string Name, AccountType Type);
public sealed record JournalLineInput(Guid AccountId, long Debit, long Credit);
public sealed record JournalInput(DateOnly Date, string Memo, List<JournalLineInput>? Lines);

public sealed class FinanceService(ErpDb db, ReportService reports, Ledger ledger)
{
    static readonly string[] CashKeys = [SystemAccounts.Mpesa, SystemAccounts.Bank, SystemAccounts.Cash];
    static readonly (string Key, string Name, string PayTo)[] Statutory =
    [
        (SystemAccounts.PayePayable, "PAYE", "KRA"), (SystemAccounts.NssfPayable, "NSSF", "NSSF"), (SystemAccounts.ShifPayable, "SHIF", "SHA"),
        (SystemAccounts.HousingLevyPayable, "Housing Levy", "KRA"), (SystemAccounts.NitaPayable, "NITA", "NITA"),
    ];
    const string Manual = "Manual", ManualReversal = "ManualReversal";

    static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, AppCache.Json);
    static long L(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    /// <summary>The next date in the month cycle on <paramref name="day"/> (today if it is that day).</summary>
    static DateOnly Next(DateOnly today, int day) => today.Day <= day ? new DateOnly(today.Year, today.Month, day) : new DateOnly(today.Year, today.Month, day).AddMonths(1);

    public async Task<object> Overview()
    {
        var today = Clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var (lastFrom, lastTo) = (monthStart.AddMonths(-1), monthStart.AddDays(-1));
        var balances = await reports.Balances(null, today);
        var cash = balances.Where(b => b.SystemKey is not null && CashKeys.Contains(b.SystemKey))
            .Select(b => new { b.Id, b.Code, b.Name, balance = b.Debit - b.Credit }).OrderByDescending(b => b.balance).ToList();

        var invoices = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid))
            .Select(d => new { balance = d.Total - d.Paid, d.DueDate }).ToListAsync();
        var bills = await db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Bill && d.Status == PurchaseStatus.Open)
            .Select(d => new { balance = d.Total - d.Paid, d.DueDate }).ToListAsync();
        var week = today.AddDays(7);

        var pnl = J(await reports.ProfitAndLoss(monthStart, today));
        var (now, before) = (pnl.GetProperty("current"), pnl.GetProperty("previous"));
        var vatLast = J(await reports.Vat(lastFrom, lastTo));
        var vatNow = J(await reports.Vat(monthStart, today));

        // Deductions taken from pay and not yet paid over: what the payable accounts still hold.
        var statutory = Statutory.Select(s => new { name = s.Name, payTo = s.PayTo, owed = balances.Where(b => b.SystemKey == s.Key).Sum(b => b.Credit - b.Debit) })
            .Where(s => s.owed > 0).ToList();
        var deadlines = new List<object>();
        if (today.Day <= 20 && L(vatLast, "payable") > 0)
            deadlines.Add(new { what = $"VAT return for {lastFrom:MMMM}", payTo = "KRA (iTax)", due = new DateOnly(today.Year, today.Month, 20), amount = L(vatLast, "payable"), soFar = false, href = "/reports?tab=vat&period=last-month" });
        if (statutory.Count > 0)
            deadlines.Add(new { what = "PAYE, NSSF, SHIF, Housing Levy and NITA from payroll", payTo = string.Join(", ", statutory.Select(s => s.payTo).Distinct()), due = Next(today, 9), amount = statutory.Sum(s => s.owed), soFar = false, href = "/payroll" });
        deadlines.Add(new { what = $"VAT return for {monthStart:MMMM}", payTo = "KRA (iTax)", due = monthStart.AddMonths(1).AddDays(19), amount = L(vatNow, "payable"), soFar = true, href = "/reports?tab=vat" });

        // What stands between the accountant and a clean month-end, biggest first.
        var company = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => new { c.AccountingLockDate }).SingleAsync();
        var unmatched = await db.Payments.Where(p => p.Allocated < p.Amount).Select(p => p.Amount - p.Allocated).ToListAsync();
        var etimsFailed = await db.SalesDocs.CountAsync(d => d.EtimsStatus == EtimsStatus.Failed);
        var noSupplierRef = await db.PurchaseDocs.CountAsync(d => d.Kind == PurchaseKind.Bill && d.Status != PurchaseStatus.Draft && d.Status != PurchaseStatus.Void && d.Tax > 0 && d.SupplierRef == "");
        var draftPayroll = await db.PayrollRuns.CountAsync(r => r.Status == PayrollStatus.Draft);
        var todo = new List<object>();
        if (unmatched.Count > 0) todo.Add(new { key = "unmatched", title = $"{unmatched.Count} payment{(unmatched.Count == 1 ? "" : "s")} not matched to an invoice", detail = "Until it's matched, the customer still looks like they owe.", amount = unmatched.Sum(), href = "/money-in" });
        if (etimsFailed > 0) todo.Add(new { key = "etims", title = $"{etimsFailed} invoice{(etimsFailed == 1 ? "" : "s")} not signed by KRA", detail = "Unsigned invoices can't go on the VAT return.", amount = (long?)null, href = "/invoices" });
        if (noSupplierRef > 0) todo.Add(new { key = "supplier-ref", title = $"{noSupplierRef} bill{(noSupplierRef == 1 ? "" : "s")} missing the supplier's invoice number", detail = "VAT on them can't be claimed without it.", amount = (long?)null, href = "/purchases" });
        if (draftPayroll > 0) todo.Add(new { key = "payroll", title = $"{draftPayroll} payroll run{(draftPayroll == 1 ? "" : "s")} waiting for approval", detail = "Approving posts salaries and statutory deductions to the books.", amount = (long?)null, href = "/payroll" });
        if (company.AccountingLockDate is null || company.AccountingLockDate < lastTo)
            todo.Add(new { key = "close", title = $"Close {lastFrom:MMMM}", detail = "Once the VAT return is filed, close the month so nothing can be dated into it.", amount = (long?)null, href = "/settings" });

        return new
        {
            asAt = today,
            cash = new { accounts = cash, total = cash.Sum(c => c.balance) },
            receivables = new { total = invoices.Sum(i => i.balance), overdue = invoices.Where(i => i.DueDate < today).Sum(i => i.balance), overdueCount = invoices.Count(i => i.DueDate < today), dueThisWeek = invoices.Where(i => i.DueDate >= today && i.DueDate <= week).Sum(i => i.balance) },
            payables = new { total = bills.Sum(b => b.balance), overdue = bills.Where(b => b.DueDate < today).Sum(b => b.balance), overdueCount = bills.Count(b => b.DueDate < today), dueThisWeek = bills.Where(b => b.DueDate >= today && b.DueDate <= week).Sum(b => b.balance) },
            month = new { from = monthStart, to = today, income = L(now, "totalIncome"), expenses = L(now, "totalExpenses"), profit = L(now, "profit"), previousProfit = L(before, "profit") },
            statutory,
            deadlines,
            todo,
            lockDate = company.AccountingLockDate,
        };
    }

    /// <summary>The chart of accounts with what each holds today (in its normal direction: assets and expenses as
    /// debits, the rest as credits).</summary>
    public async Task<object> Accounts()
    {
        if (!await db.Accounts.AnyAsync())
        {
            db.Accounts.AddRange(SystemAccounts.Starter(db.CompanyId));
            await db.SaveChangesAsync();
        }
        var b = await reports.Balances(null, Clock.Today);
        return b.OrderBy(x => x.Code).Select(x => new
        {
            x.Id, x.Code, x.Name, type = x.Type.ToString(), system = x.SystemKey is not null, x.Debit, x.Credit,
            balance = x.Type is AccountType.Asset or AccountType.Expense ? x.Debit - x.Credit : x.Credit - x.Debit,
        });
    }

    public async Task<object> AddAccount(AccountInput input)
    {
        var code = (input.Code ?? "").Trim().ToUpperInvariant();
        var name = (input.Name ?? "").Trim();
        if (code.Length is < 2 or > 12 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) throw new AppException("Give the account a code of 2 to 12 letters or numbers, like 6150.");
        if (name.Length is < 2 or > 80) throw new AppException("Name the account in a few words, like \"Fuel and transport\".");
        if (!Enum.IsDefined(input.Type)) throw new AppException("Choose what kind of account it is.");
        if (await db.Accounts.AnyAsync(a => a.Code.ToUpper() == code)) throw new AppException($"There's already an account {code}.", 409);
        var a = new Account { Code = code, Name = name, Type = input.Type };
        db.Accounts.Add(a);
        await db.SaveChangesAsync();
        return new { a.Id, a.Code, a.Name, type = a.Type.ToString(), system = false, debit = 0, credit = 0, balance = 0 };
    }

    /// <summary>The general ledger: entries newest first, each with its lines, optionally for one account or matching
    /// words in the description.</summary>
    public async Task<object> Journal(DateOnly? from, DateOnly? to, Guid? accountId, string? q, int? skip, int? take)
    {
        var (s, t) = Paging.Of(skip, take);
        var query = db.JournalEntries.AsNoTracking().AsQueryable();
        if (from is { } f) query = query.Where(e => e.Date >= f);
        if (to is { } u) query = query.Where(e => e.Date <= u);
        if (accountId is { } acc) query = query.Where(e => e.Lines.Any(l => l.AccountId == acc));
        if (!string.IsNullOrWhiteSpace(q)) { var w = q.Trim().ToLower(); query = query.Where(e => e.Memo.ToLower().Contains(w)); }
        var total = await query.CountAsync();
        var page = await query.OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt).Skip(s).Take(t).Include(e => e.Lines).ToListAsync();
        var accounts = await db.Accounts.ToDictionaryAsync(a => a.Id, a => new { a.Code, a.Name });
        var ids = page.Select(e => e.Id).ToList();
        var reversed = (await db.JournalEntries.Where(e => e.SourceType == ManualReversal && e.SourceId != null && ids.Contains(e.SourceId!.Value)).Select(e => e.SourceId!.Value).ToListAsync()).ToHashSet();
        return new
        {
            total, skip = s, take = t,
            items = page.Select(e => new
            {
                e.Id, e.Date, e.Memo, source = e.SourceType, e.SourceId, e.CreatedAt,
                manual = e.SourceType == Manual, reversed = reversed.Contains(e.Id),
                amount = e.Lines.Sum(l => l.Debit),
                lines = e.Lines.OrderByDescending(l => l.Debit).Select(l => new { l.AccountId, code = accounts.GetValueOrDefault(l.AccountId)?.Code ?? "?", name = accounts.GetValueOrDefault(l.AccountId)?.Name ?? "Unknown account", l.Debit, l.Credit }),
            }),
        };
    }

    async Task NotClosed(DateOnly date)
    {
        var locked = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => c.AccountingLockDate).SingleAsync();
        if (locked is { } l && date <= l) throw new AppException($"The books are closed up to {l:d MMM yyyy}. Date the entry after that, or reopen the period in Settings.");
    }

    /// <summary>A manual journal: two or more lines, each a debit or a credit, adding up on both sides.</summary>
    public async Task<object> Post(JournalInput input)
    {
        var memo = (input.Memo ?? "").Trim();
        if (memo.Length is < 3 or > 200) throw new AppException("Say what the entry is for, so whoever reads the books later understands it.");
        if (input.Date > Clock.Today) throw new AppException("A journal can't be dated in the future.");
        var lines = (input.Lines ?? []).Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        if (lines.Count < 2) throw new AppException("A journal needs at least two lines: something debited and something credited.");
        if (lines.Any(l => l.Debit < 0 || l.Credit < 0 || (l.Debit > 0 && l.Credit > 0))) throw new AppException("Each line is either a debit or a credit, and never negative.");
        var (dr, cr) = (lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
        if (dr != cr) throw new AppException($"Debits ({Money.Ksh(dr)}) and credits ({Money.Ksh(cr)}) must be equal.");
        var ids = lines.Select(l => l.AccountId).Distinct().ToList();
        if (await db.Accounts.CountAsync(a => ids.Contains(a.Id)) != ids.Count) throw new AppException("One of the accounts doesn't exist.");
        await NotClosed(input.Date);
        var entry = await ledger.PostIds(input.Date, memo, Manual, null, lines.Select(l => (l.AccountId, l.Debit, l.Credit)));
        await db.SaveChangesAsync();
        return new { entry.Id, entry.Date, entry.Memo, amount = dr };
    }

    /// <summary>Undoes a manual journal with its mirror image, dated today; the original stays in the books.</summary>
    public async Task<object> Reverse(Guid id)
    {
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleOrDefaultAsync(e => e.Id == id) ?? throw new AppException("Not found.", 404);
        if (entry.SourceType != Manual) throw new AppException("Only manual journals are reversed here. To undo an invoice, bill or payment, void it where it was made.");
        if (await db.JournalEntries.AnyAsync(e => e.SourceType == ManualReversal && e.SourceId == id)) throw new AppException("This journal has already been reversed.", 409);
        var today = Clock.Today;
        await NotClosed(today);
        var reversal = await ledger.PostIds(today, $"Reversal: {entry.Memo}"[..Math.Min(200, $"Reversal: {entry.Memo}".Length)], ManualReversal, entry.Id, entry.Lines.Select(l => (l.AccountId, l.Credit, l.Debit)));
        await db.SaveChangesAsync();
        return new { reversal.Id, reversal.Date, reversal.Memo };
    }
}

public static class FinanceEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // Reading the numbers is for anyone who sees reports; changing the books is for finance.
        var finance = api.MapGroup("/finance").RequireAuthorization(Policies.Reports);
        finance.MapGet("/overview", (FinanceService s) => s.Overview()).CachedPerCompany();
        finance.MapGet("/accounts", (FinanceService s) => s.Accounts());
        finance.MapPost("/accounts", async (AccountInput input, FinanceService s) => Results.Created("", await s.AddAccount(input))).RequireAuthorization(Policies.Finance);
        finance.MapGet("/journal", (FinanceService s, DateOnly? from, DateOnly? to, Guid? accountId, string? q, int? skip, int? take) => s.Journal(from, to, accountId, q, skip, take));
        finance.MapPost("/journal", async (JournalInput input, FinanceService s) => Results.Created("", await s.Post(input))).RequireAuthorization(Policies.Finance);
        finance.MapPost("/journal/{id:guid}/reverse", (Guid id, FinanceService s) => s.Reverse(id)).RequireAuthorization(Policies.Finance);
    }
}
