// M-Pesa and bank statements: import the CSV the bank or Safaricom gives you, and every line is matched to what's
// already in the books (a customer payment by its M-Pesa code, a supplier paid, salaries or KRA paid). What's left
// is a short list: money that came in and isn't recorded yet (record it as a payment; it then matches invoices by
// itself), money that went out (record the expense), or a line to ignore. The account's book balance is shown next
// to the statement's closing balance, so a difference is visible at once.
using System.Globalization;
using System.Text;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Accounting;

public sealed record ImportInput(string Account, string? FileName, string Csv);
public sealed record RecordPaymentInput(Guid? CustomerId, string? PayerName);
public sealed record RecordExpenseInput(Guid AccountId, string? Memo);
public sealed record IgnoreInput(string? Note);

public sealed class StatementService(ErpDb db, Ledger ledger, PaymentService payments, RequestContext ctx)
{
    static readonly string[] Accounts = [SystemAccounts.Mpesa, SystemAccounts.Bank, SystemAccounts.Cash];

    // ---------- reading the file ----------

    public sealed record Parsed(DateOnly Date, string Reference, string Description, long In, long Out, long? Balance);

    static readonly string[] DateNames = ["completion time", "transaction date", "value date", "posting date", "trans date", "date"];
    static readonly string[] RefNames = ["receipt no.", "receipt no", "receipt", "transaction id", "reference", "ref no", "ref", "cheque no"];
    static readonly string[] DescNames = ["details", "description", "narration", "particulars", "transaction details", "narrative"];
    static readonly string[] InNames = ["paid in", "money in", "credit", "credits", "deposit", "deposits", "cr"];
    static readonly string[] OutNames = ["withdrawn", "money out", "debit", "debits", "withdrawal", "withdrawals", "dr"];
    static readonly string[] BalanceNames = ["balance", "running balance", "book balance"];
    static readonly string[] Formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "dd MMM yyyy", "d MMM yyyy", "dd-MMM-yyyy", "dd-MMM-yy", "yyyy/MM/dd"];

    public static List<string[]> Csv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',' || c == ';' || c == '\t') { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString().Trim()); cell.Clear();
                if (row.Any(x => x.Length > 0)) rows.Add([.. row]);
                row.Clear();
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString().Trim());
        if (row.Any(x => x.Length > 0)) rows.Add([.. row]);
        if (rows.Count > 10_000) throw new AppException("CSV exceeds the maximum limit of 10,000 rows. Please split the file into smaller batches.");
        return rows;
    }

    static long? Money(string s)
    {
        s = s.Replace("KES", "", StringComparison.OrdinalIgnoreCase).Replace("KSh", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Trim();
        if (s.Length == 0 || s == "-") return null;
        var negative = s.StartsWith('(') && s.EndsWith(')') || s.StartsWith('-');
        s = s.Trim('(', ')', '-', ' ');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d * 100) * (negative ? -1 : 1) : null;
    }

    public static List<Parsed> Parse(string csv)
    {
        var rows = Csv(csv);
        int header = rows.FindIndex(r => r.Any(c => DateNames.Contains(c.ToLowerInvariant())) && r.Length >= 3);
        if (header < 0) throw new AppException("Couldn't find the column titles (Date, Details, Paid in / Withdrawn, or Debit / Credit). Export the statement as CSV and try again.");
        var h = rows[header].Select(c => c.ToLowerInvariant()).ToArray();
        int Col(string[] names) { foreach (var n in names) { var i = Array.IndexOf(h, n); if (i >= 0) return i; } return -1; }
        int date = Col(DateNames), reference = Col(RefNames), desc = Col(DescNames), inCol = Col(InNames), outCol = Col(OutNames), balance = Col(BalanceNames), amount = Col(["amount"]);
        int status = Array.IndexOf(h, "transaction status");
        if (inCol < 0 && outCol < 0 && amount < 0) throw new AppException("Couldn't find the money columns (Paid in / Withdrawn, Credit / Debit, or Amount).");
        var lines = new List<Parsed>();
        foreach (var r in rows.Skip(header + 1))
        {
            string At(int i) => i >= 0 && i < r.Length ? r[i] : "";
            if (status >= 0 && At(status).Length > 0 && !At(status).Equals("Completed", StringComparison.OrdinalIgnoreCase)) continue; // failed or reversed M-Pesa
            if (!DateTime.TryParseExact(At(date), Formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var when)) continue; // totals, blank lines
            long moneyIn, moneyOut;
            if (inCol >= 0 || outCol >= 0)
            {
                moneyIn = Math.Abs(Money(At(inCol)) ?? 0);
                moneyOut = Math.Abs(Money(At(outCol)) ?? 0);
            }
            else
            {
                var a = Money(At(amount)) ?? 0;
                (moneyIn, moneyOut) = a >= 0 ? (a, 0L) : (0L, -a);
            }
            if (moneyIn == 0 && moneyOut == 0) continue;
            lines.Add(new Parsed(DateOnly.FromDateTime(when), At(reference).ToUpperInvariant(), At(desc), moneyIn, moneyOut, Money(At(balance))));
        }
        if (lines.Count == 0) throw new AppException("No transactions found in that file.");
        return lines;
    }

    // ---------- importing and matching ----------

    public async Task<object> Import(ImportInput input)
    {
        var account = (input.Account ?? "").ToUpperInvariant();
        if (!Accounts.Contains(account)) throw new AppException("Choose M-Pesa, Bank or Cash.");
        if ((input.Csv ?? "").Length > 5_000_000) throw new AppException("That file is too big; import a month at a time.");
        var parsed = Parse(input.Csv ?? "");
        var from = parsed.Min(p => p.Date); var to = parsed.Max(p => p.Date);
        var existing = await db.StatementLines.Where(l => l.Account == account && l.Date >= from && l.Date <= to)
            .Select(l => new { l.Reference, l.Date, l.MoneyIn, l.MoneyOut, l.Description }).ToListAsync();
        var import = new StatementImport { Account = account, FileName = (input.FileName ?? "").Trim(), From = from, To = to, ImportedBy = ctx.Actor };
        // The closing balance is on the latest line (statements list newest first or last).
        import.ClosingBalance = parsed.Where(p => p.Balance is not null).OrderBy(p => p.Date).Select(p => p.Balance).LastOrDefault();
        db.StatementImports.Add(import);
        foreach (var p in parsed)
        {
            var seen = p.Reference.Length > 0
                ? existing.Any(e => e.Reference == p.Reference)
                : existing.Any(e => e.Date == p.Date && e.MoneyIn == p.In && e.MoneyOut == p.Out && e.Description == p.Description);
            if (seen) { import.Skipped++; continue; }
            db.StatementLines.Add(new StatementLine { ImportId = import.Id, Account = account, Date = p.Date, Reference = p.Reference, Description = p.Description, MoneyIn = p.In, MoneyOut = p.Out, Balance = p.Balance });
            import.Lines++;
        }
        await db.SaveChangesAsync();
        var matched = await Match(account);
        return new { import.Id, import.Lines, import.Skipped, matched, from, to };
    }

    static PaymentMethod MethodFor(string account) => account == SystemAccounts.Mpesa ? PaymentMethod.Mpesa : account == SystemAccounts.Cash ? PaymentMethod.Cash : PaymentMethod.Bank;
    static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime.AddHours(3));

    /// <summary>Matches unmatched lines to the books: by M-Pesa code, else by amount within a few days when only one fits.</summary>
    public async Task<int> Match(string account)
    {
        var open = await db.StatementLines.Where(l => l.Account == account && l.Status == StatementLineStatus.Unmatched).OrderBy(l => l.Date).ToListAsync();
        if (open.Count == 0) return 0;
        var taken = (await db.StatementLines.Where(l => l.MatchId != null).Select(l => l.MatchId!.Value).ToListAsync()).ToHashSet();
        var method = MethodFor(account);
        var from = open.Min(l => l.Date).AddDays(-4); var to = open.Max(l => l.Date).AddDays(4);
        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var until = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.FromHours(3));
        // An M-Pesa code or bank reference is unique: matched whatever the date. By amount only within a few days.
        var refs = open.Where(l => l.Reference.Length > 0).Select(l => l.Reference).Distinct().ToList();
        var received = await db.Payments.Where(p => (p.Method == method && p.ReceivedAt >= since && p.ReceivedAt <= until) || refs.Contains(p.Reference))
            .Select(p => new { p.Id, p.Reference, p.Amount, p.ReceivedAt }).ToListAsync();
        var paid = await db.SupplierPayments.Where(p => p.Method == method && p.PaidAt >= since && p.PaidAt <= until).Select(p => new { p.Id, p.Amount, p.PaidAt }).ToListAsync();
        var cashId = await ledger.Id(account);
        var outgoing = await db.JournalLines.Where(l => l.AccountId == cashId && l.Credit > 0)
            .Join(db.JournalEntries.Where(e => e.Date >= from && e.Date <= to && e.SourceType != "SupplierPayment"), l => l.JournalEntryId, e => e.Id, (l, e) => new { e.Id, e.Date, l.Credit }).ToListAsync();
        var n = 0;
        foreach (var line in open)
        {
            (string Kind, Guid Id)? hit = null;
            if (line.MoneyIn > 0)
            {
                var byRef = line.Reference.Length > 0 ? received.FirstOrDefault(p => p.Reference == line.Reference && !taken.Contains(p.Id)) : null;
                var byAmount = received.Where(p => p.Amount == line.MoneyIn && Math.Abs(Day(p.ReceivedAt).DayNumber - line.Date.DayNumber) <= 2 && !taken.Contains(p.Id)).ToList();
                if (byRef is not null) hit = ("Payment", byRef.Id);
                else if (byAmount.Count == 1) hit = ("Payment", byAmount[0].Id);
            }
            else
            {
                var sp = paid.Where(p => p.Amount == line.MoneyOut && Math.Abs(Day(p.PaidAt).DayNumber - line.Date.DayNumber) <= 3 && !taken.Contains(p.Id)).ToList();
                var je = outgoing.Where(e => e.Credit == line.MoneyOut && Math.Abs(e.Date.DayNumber - line.Date.DayNumber) <= 3 && !taken.Contains(e.Id)).ToList();
                if (sp.Count == 1 && je.Count == 0) hit = ("SupplierPayment", sp[0].Id);
                else if (je.Count == 1 && sp.Count == 0) hit = ("Journal", je[0].Id);
            }
            if (hit is { } h)
            {
                (line.Status, line.MatchKind, line.MatchId) = (StatementLineStatus.Matched, h.Kind, h.Id);
                taken.Add(h.Id);
                n++;
            }
        }
        await db.SaveChangesAsync();
        return n;
    }

    // ---------- what's left ----------

    public async Task<object> Overview(string account)
    {
        account = account.ToUpperInvariant();
        if (!Accounts.Contains(account)) throw new AppException("Choose M-Pesa, Bank or Cash.");
        var last = await db.StatementImports.Where(i => i.Account == account).OrderByDescending(i => i.To).ThenByDescending(i => i.At).FirstOrDefaultAsync();
        var lines = await db.StatementLines.Where(l => l.Account == account).OrderByDescending(l => l.Date).Take(500).ToListAsync();
        var counts = new { unmatched = lines.Count(l => l.Status == StatementLineStatus.Unmatched), matched = lines.Count(l => l.Status == StatementLineStatus.Matched), ignored = lines.Count(l => l.Status == StatementLineStatus.Ignored) };
        long? book = null;
        if (last is not null)
        {
            var id = await ledger.Id(account);
            var sums = await db.JournalLines.Where(l => l.AccountId == id).Join(db.JournalEntries.Where(e => e.Date <= last.To), l => l.JournalEntryId, e => e.Id, (l, e) => l)
                .GroupBy(_ => 1).Select(g => new { d = g.Sum(x => x.Debit), c = g.Sum(x => x.Credit) }).FirstOrDefaultAsync();
            book = (sums?.d ?? 0) - (sums?.c ?? 0);
        }
        return new
        {
            account, counts,
            statement = last is null ? null : new { last.From, last.To, last.ClosingBalance, last.FileName },
            bookBalance = book,
            difference = last?.ClosingBalance is { } closing && book is { } b ? closing - b : (long?)null,
            unmatchedIn = lines.Where(l => l.Status == StatementLineStatus.Unmatched).Sum(l => l.MoneyIn),
            unmatchedOut = lines.Where(l => l.Status == StatementLineStatus.Unmatched).Sum(l => l.MoneyOut),
            lines = lines.Select(l => new { l.Id, l.Date, l.Reference, l.Description, l.MoneyIn, l.MoneyOut, l.Status, l.MatchKind, l.Note, suggestCharges = l.MoneyOut > 0 && l.Description.Contains("charge", StringComparison.OrdinalIgnoreCase) }),
        };
    }

    async Task<StatementLine> Open(Guid id) =>
        await db.StatementLines.SingleOrDefaultAsync(l => l.Id == id) is { Status: StatementLineStatus.Unmatched } l ? l : throw new AppException("This line is already dealt with.");

    /// <summary>Money in that isn't in the books yet: record it as a payment (it then matches its invoice by itself).</summary>
    public async Task<object> RecordPayment(Guid lineId, RecordPaymentInput input)
    {
        var l = await Open(lineId);
        if (l.MoneyIn <= 0) throw new AppException("Only money in can be recorded as a payment.");
        var reference = l.Reference.Length > 0 ? l.Reference : $"STMT-{l.Date:yyyyMMdd}-{l.Id.ToString()[..6].ToUpperInvariant()}";
        var p = await payments.Record(new PaymentInput(MethodFor(l.Account), l.MoneyIn, new DateTimeOffset(l.Date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(3)), reference, null,
            string.IsNullOrWhiteSpace(input.PayerName) ? l.Description : input.PayerName, l.Description, input.CustomerId, null), ctx.Actor, PaymentSource.StatementImport);
        (l.Status, l.MatchKind, l.MatchId) = (StatementLineStatus.Matched, "Payment", p.Id);
        await db.SaveChangesAsync();
        return new { payment = p.Id, p.Status };
    }

    /// <summary>Money out that isn't in the books: an expense (charges, fuel, rent…) paid from this account.</summary>
    public async Task<object> RecordExpense(Guid lineId, RecordExpenseInput input)
    {
        var l = await Open(lineId);
        if (l.MoneyOut <= 0) throw new AppException("Only money out can be recorded as an expense.");
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == input.AccountId) ?? throw new AppException("Choose the expense account.");
        if (account.Type is not (AccountType.Expense or AccountType.Asset or AccountType.Liability or AccountType.Equity)) throw new AppException("Choose an expense (or what the money paid for).");
        var cash = await ledger.Id(l.Account);
        var memo = string.IsNullOrWhiteSpace(input.Memo) ? l.Description : input.Memo.Trim();
        var entry = await ledger.PostIds(l.Date, $"{memo} {l.Reference}".Trim(), "StatementExpense", l.Id, [(account.Id, l.MoneyOut, 0), (cash, 0, l.MoneyOut)]);
        (l.Status, l.MatchKind, l.MatchId) = (StatementLineStatus.Matched, "Journal", entry.Id);
        await db.SaveChangesAsync();
        return new { entry = entry.Id };
    }

    public async Task<object> Ignore(Guid lineId, IgnoreInput input)
    {
        var l = await Open(lineId);
        (l.Status, l.Note) = (StatementLineStatus.Ignored, (input.Note ?? "").Trim());
        await db.SaveChangesAsync();
        return new { l.Id, l.Status };
    }
}
