using Kabuor.Erp.Common;
// The statements an accountant signs off: balance sheet and cash flow, both straight from the ledger, and the
// sales and purchases registers in the column order of KRA's VAT3 return (to paste into the iTax template).
using System.Globalization;
using System.Text;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Reports;

public sealed partial class ReportService
{
    /// <summary>What the business owns and owes at a date. Profit not yet moved into equity shows as "profit so far".</summary>
    public async Task<object> BalanceSheet(DateOnly at)
    {
        var b = await Balances(null, at);
        object Group(AccountType t, bool debitNormal) =>
            b.Where(x => x.Type == t && x.Debit != x.Credit)
             .Select(x => new { x.Code, x.Name, amount = debitNormal ? x.Debit - x.Credit : x.Credit - x.Debit })
             .ToList();
        long Sum(AccountType t, bool debitNormal) => b.Where(x => x.Type == t).Sum(x => debitNormal ? x.Debit - x.Credit : x.Credit - x.Debit);
        var assets = Sum(AccountType.Asset, true);
        var liabilities = Sum(AccountType.Liability, false);
        var equity = Sum(AccountType.Equity, false);
        var profit = Sum(AccountType.Income, false) - Sum(AccountType.Expense, true);
        return new
        {
            at,
            assets = new { lines = Group(AccountType.Asset, true), total = assets },
            liabilities = new { lines = Group(AccountType.Liability, false), total = liabilities },
            equity = new { lines = Group(AccountType.Equity, false), profitSoFar = profit, total = equity + profit },
            balanced = assets == liabilities + equity + profit,
        };
    }

    static readonly Dictionary<string, string> CashCategory = new()
    {
        ["Payment"] = "Customers paid you",
        ["SupplierPayment"] = "Suppliers paid",
        ["PayrollPaid"] = "Salaries paid",
        ["Remittance"] = "PAYE, NSSF, SHIF, Housing Levy and deductions paid over",
        ["StatementExpense"] = "Expenses paid from the account",
    };

    /// <summary>Where money came from and went, through M-Pesa, bank and cash, by kind of transaction.</summary>
    public async Task<object> CashFlow(DateOnly from, DateOnly to)
    {
        if (to < from) throw new Common.AppException("The end date is before the start date.");
        var keys = new[] { SystemAccounts.Mpesa, SystemAccounts.Bank, SystemAccounts.Cash };
        var cash = await db.Accounts.Where(a => keys.Contains(a.SystemKey!)).Select(a => a.Id).ToListAsync();
        var moves = await db.JournalLines.Where(l => cash.Contains(l.AccountId))
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { e.Date, e.SourceType, l.Debit, l.Credit }).ToListAsync();
        long Opening(DateOnly d) => moves.Where(m => m.Date < d).Sum(m => m.Debit - m.Credit);
        var period = moves.Where(m => m.Date >= from && m.Date <= to).ToList();
        var rows = period.GroupBy(m => CashCategory.GetValueOrDefault(m.SourceType, "Other money in and out"))
            .Select(g => new { category = g.Key, moneyIn = g.Sum(m => m.Debit), moneyOut = g.Sum(m => m.Credit), net = g.Sum(m => m.Debit - m.Credit) })
            .OrderByDescending(r => r.moneyIn).ThenBy(r => r.moneyOut).ToList();
        var opening = Opening(from);
        var net = period.Sum(m => m.Debit - m.Credit);
        return new { from, to, opening, rows, moneyIn = period.Sum(m => m.Debit), moneyOut = period.Sum(m => m.Credit), net, closing = opening + net };
    }

    static string Csv(params object?[] cells) => string.Join(',', cells.Select(c => CsvCell.Of(Convert.ToString(c, CultureInfo.InvariantCulture))));
    static string Ksh(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    static string D(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>VAT3 sales register: PIN of purchaser, name, ETR / eTIMS serial, invoice date and number, description,
    /// taxable value, VAT. Rate 16% and 8% lines; the totals per invoice.</summary>
    public async Task<string> ItaxSales(DateOnly from, DateOnly to, TaxType type)
    {
        var docs = await db.SalesDocs.Include(d => d.Lines).Include(d => d.Customer)
            .Where(d => d.Kind == SalesDocKind.Invoice && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void && d.IssueDate >= from && d.IssueDate <= to)
            .OrderBy(d => d.IssueDate).ThenBy(d => d.Number).ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine(Csv("PIN of Purchaser", "Name of Purchaser", "ETR Serial Number", "Invoice Date", "Invoice Number", "Description of Goods / Services", "Taxable Value (Ksh)", "Amount of VAT (Ksh)", "Relevant Invoice Number", "Relevant Invoice Date"));
        foreach (var d in docs)
        {
            var lines = d.Lines.Where(l => l.TaxType == type).ToList();
            if (lines.Count == 0) continue;
            var what = string.Join("; ", lines.Select(l => l.Description).Distinct()).Trim();
            sb.AppendLine(Csv(d.Customer?.KraPin ?? "", d.Customer?.Name ?? "", d.EtimsInvoiceNo, D(d.IssueDate), d.Number, what.Length > 200 ? what[..200] : what, Ksh(lines.Sum(l => l.Net)), Ksh(lines.Sum(l => l.Tax)), "", ""));
        }
        return sb.ToString();
    }

    /// <summary>VAT3 purchases register (local purchases): PIN of supplier, name, ETR / eTIMS number, invoice date and
    /// number, description, taxable value, VAT. Bills without the supplier's invoice number are left out (not claimable).</summary>
    public async Task<string> ItaxPurchases(DateOnly from, DateOnly to)
    {
        var docs = await db.PurchaseDocs.Include(d => d.Lines).Include(d => d.Supplier)
            .Where(d => d.Kind == PurchaseKind.Bill && d.Status != PurchaseStatus.Draft && d.Status != PurchaseStatus.Void && d.IssueDate >= from && d.IssueDate <= to && d.SupplierRef != "")
            .OrderBy(d => d.IssueDate).ThenBy(d => d.Number).ToListAsync();
        var sb = new StringBuilder();
        sb.AppendLine(Csv("Type of Purchases", "PIN of Supplier", "Name of Supplier", "ETR Serial Number", "Invoice Date", "Invoice Number", "Description of Goods / Services", "Custom Entry Number", "Taxable Value (Ksh)", "Amount of VAT (Ksh)", "Relevant Invoice Number", "Relevant Invoice Date"));
        foreach (var d in docs)
        {
            var lines = d.Lines.Where(l => l.TaxType is TaxType.B_Standard16 or TaxType.E_Reduced8).ToList();
            if (lines.Count == 0) continue;
            var what = string.Join("; ", lines.Select(l => l.Description).Distinct()).Trim();
            sb.AppendLine(Csv("Local", d.Supplier?.KraPin ?? "", d.Supplier?.Name ?? "", "", D(d.IssueDate), d.SupplierRef, what.Length > 200 ? what[..200] : what, "", Ksh(lines.Sum(l => l.Net)), Ksh(lines.Sum(l => l.Tax)), "", ""));
        }
        return sb.ToString();
    }

    /// <summary>Tax customers withheld in a period (certificates), for the VAT return and the income tax return.</summary>
    public async Task<object> Withheld(DateOnly from, DateOnly to)
    {
        var rows = await db.WithholdingCertificates.Where(w => w.Date >= from && w.Date <= to)
            .Join(db.SalesDocs, w => w.SalesDocId, d => d.Id, (w, d) => new { w.Id, w.Kind, w.Amount, w.Certificate, w.Date, invoice = d.Number, customer = d.Customer!.Name })
            .OrderBy(x => x.Date).ToListAsync();
        return new { from, to, vat = rows.Where(r => r.Kind == WithholdingKind.Vat).Sum(r => r.Amount), incomeTax = rows.Where(r => r.Kind == WithholdingKind.IncomeTax).Sum(r => r.Amount), certificates = rows };
    }
}
