// Double-entry bookkeeping. Sales and payments post here; nothing else writes journal entries.
//   Invoice issued:      Dr Accounts receivable (total)   Cr Sales (net)   Cr VAT output (tax)
//   Payment received:    Dr M-Pesa / Cash / Bank          Cr Unapplied receipts
//   Payment matched:     Dr Unapplied receipts            Cr Accounts receivable
//   Invoice voided:      the invoice entry reversed
//   Bill posted:         Dr Stock (stocked items) / expense accounts (net)   Dr VAT paid (tax)   Cr Owed to suppliers
//   Supplier paid:       Dr Owed to suppliers                 Cr M-Pesa / Cash / Bank
//   Stock sold:          Dr Cost of goods sold                Cr Stock
//   Stock counted/fixed: Dr/Cr Stock                          Cr/Dr Stock adjustments
//   Parts used on a job: Dr Repairs and maintenance            Cr Stock
//   Issued to a department: Dr Supplies used by departments   Cr Stock
//   Job cost paid direct: Dr Repairs and maintenance           Cr M-Pesa / Cash / Bank
// Lines can also carry the department, asset and maintenance job the money was for (Dims), so costs can be read by
// cost centre and by machine as well as by account.
// Holding unmatched money in "Unapplied receipts" keeps the books right even before anyone knows who paid.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Accounting;

public static class SystemAccounts
{
    public const string Receivable = "AR";
    public const string Sales = "SALES";
    public const string VatOutput = "VAT_OUT";
    public const string Mpesa = "MPESA";
    public const string Cash = "CASH";
    public const string Bank = "BANK";
    public const string Unapplied = "UNAPPLIED";
    public const string Equity = "EQUITY";
    public const string Payable = "AP";
    public const string VatInput = "VAT_IN";
    public const string Stock = "INVENTORY";
    public const string CostOfSales = "COGS";
    public const string StockAdjust = "STOCK_ADJ";
    public const string OtherExpenses = "GENERAL_EXP";
    public const string Salaries = "SALARIES";
    public const string EmployerStatutory = "EMPLOYER_STAT";
    public const string PayePayable = "PAYE";
    public const string NssfPayable = "NSSF";
    public const string ShifPayable = "SHIF";
    public const string HousingLevyPayable = "AHL";
    public const string NitaPayable = "NITA";
    public const string NetPayPayable = "NET_PAY";
    public const string StaffAdvances = "STAFF_ADV";
    public const string DeductionsPayable = "DEDUCTIONS_PAYABLE";
    public const string VatWithheld = "VAT_WITHHELD";
    public const string TaxWithheld = "TAX_WITHHELD";
    public const string BankCharges = "BANK_CHARGES";
    public const string ProductionAbsorbed = "PROD_ABSORBED";
    public const string Fx = "FX";
    public const string Maintenance = "MAINTENANCE";
    public const string SuppliesUsed = "SUPPLIES_USED";
    /// <summary>Spares, tools, fuel and supplies held to run the business: an asset until issued, never cost of sales.</summary>
    public const string Stores = "STORES";

    /// <summary>The account an item's stock is held in.</summary>
    public static string StockFor(Item item) => item.Use == ItemUse.Stores ? Stores : Stock;

    /// <summary>A starter chart of accounts in plain words. Owners can add their own beside these.</summary>
    public static IEnumerable<Account> Starter(Guid companyId) =>
    [
        new() { CompanyId = companyId, Code = "1000", Name = "Cash on hand", Type = AccountType.Asset, SystemKey = Cash },
        new() { CompanyId = companyId, Code = "1010", Name = "M-Pesa", Type = AccountType.Asset, SystemKey = Mpesa },
        new() { CompanyId = companyId, Code = "1020", Name = "Bank", Type = AccountType.Asset, SystemKey = Bank },
        new() { CompanyId = companyId, Code = "1100", Name = "Money customers owe us", Type = AccountType.Asset, SystemKey = Receivable },
        new() { CompanyId = companyId, Code = "1200", Name = "Stock", Type = AccountType.Asset, SystemKey = Stock },
        new() { CompanyId = companyId, Code = "1210", Name = "Stores and spares (for own use)", Type = AccountType.Asset, SystemKey = Stores },
        new() { CompanyId = companyId, Code = "1300", Name = "VAT paid on purchases (claimable)", Type = AccountType.Asset, SystemKey = VatInput },
        new() { CompanyId = companyId, Code = "1400", Name = "Money staff owe us (advances, loans)", Type = AccountType.Asset, SystemKey = StaffAdvances },
        new() { CompanyId = companyId, Code = "1310", Name = "VAT withheld by customers (claimable)", Type = AccountType.Asset, SystemKey = VatWithheld },
        new() { CompanyId = companyId, Code = "1320", Name = "Income tax withheld by customers (claimable)", Type = AccountType.Asset, SystemKey = TaxWithheld },
        new() { CompanyId = companyId, Code = "2000", Name = "Money we owe suppliers", Type = AccountType.Liability, SystemKey = Payable },
        new() { CompanyId = companyId, Code = "2100", Name = "VAT collected (owed to KRA)", Type = AccountType.Liability, SystemKey = VatOutput },
        new() { CompanyId = companyId, Code = "2200", Name = "Payments not yet matched", Type = AccountType.Liability, SystemKey = Unapplied },
        new() { CompanyId = companyId, Code = "2300", Name = "PAYE owed to KRA", Type = AccountType.Liability, SystemKey = PayePayable },
        new() { CompanyId = companyId, Code = "2310", Name = "NSSF owed", Type = AccountType.Liability, SystemKey = NssfPayable },
        new() { CompanyId = companyId, Code = "2320", Name = "SHIF owed", Type = AccountType.Liability, SystemKey = ShifPayable },
        new() { CompanyId = companyId, Code = "2330", Name = "Housing Levy owed", Type = AccountType.Liability, SystemKey = HousingLevyPayable },
        new() { CompanyId = companyId, Code = "2340", Name = "NITA owed", Type = AccountType.Liability, SystemKey = NitaPayable },
        new() { CompanyId = companyId, Code = "2350", Name = "Salaries to pay", Type = AccountType.Liability, SystemKey = NetPayPayable },
        new() { CompanyId = companyId, Code = "2360", Name = "Deductions to pay over (HELB, SACCOs, insurance)", Type = AccountType.Liability, SystemKey = DeductionsPayable },
        new() { CompanyId = companyId, Code = "3000", Name = "Owner's equity", Type = AccountType.Equity, SystemKey = Equity },
        new() { CompanyId = companyId, Code = "4000", Name = "Sales", Type = AccountType.Income, SystemKey = Sales },
        new() { CompanyId = companyId, Code = "4900", Name = "Exchange gains and losses", Type = AccountType.Income, SystemKey = Fx },
        new() { CompanyId = companyId, Code = "5000", Name = "Cost of goods sold", Type = AccountType.Expense, SystemKey = CostOfSales },
        new() { CompanyId = companyId, Code = "5100", Name = "Stock adjustments (losses and finds)", Type = AccountType.Expense, SystemKey = StockAdjust },
        new() { CompanyId = companyId, Code = "5200", Name = "Production costs moved into stock", Type = AccountType.Expense, SystemKey = ProductionAbsorbed },
        new() { CompanyId = companyId, Code = "6000", Name = "Rent", Type = AccountType.Expense },
        new() { CompanyId = companyId, Code = "6100", Name = "Salaries and wages", Type = AccountType.Expense, SystemKey = Salaries },
        new() { CompanyId = companyId, Code = "6110", Name = "Employer NSSF, Housing Levy and NITA", Type = AccountType.Expense, SystemKey = EmployerStatutory },
        new() { CompanyId = companyId, Code = "6200", Name = "Transport and delivery", Type = AccountType.Expense },
        new() { CompanyId = companyId, Code = "6300", Name = "Airtime and internet", Type = AccountType.Expense },
        new() { CompanyId = companyId, Code = "6400", Name = "Packaging and supplies", Type = AccountType.Expense },
        new() { CompanyId = companyId, Code = "6410", Name = "Supplies used by departments", Type = AccountType.Expense, SystemKey = SuppliesUsed },
        new() { CompanyId = companyId, Code = "6500", Name = "Electricity and water", Type = AccountType.Expense },
        new() { CompanyId = companyId, Code = "6600", Name = "Bank and M-Pesa charges", Type = AccountType.Expense, SystemKey = BankCharges },
        new() { CompanyId = companyId, Code = "6700", Name = "Repairs and maintenance", Type = AccountType.Expense, SystemKey = Maintenance },
        new() { CompanyId = companyId, Code = "6900", Name = "Other expenses", Type = AccountType.Expense, SystemKey = OtherExpenses },
    ];

    public static string ForMethod(PaymentMethod m) => m switch
    {
        PaymentMethod.Mpesa => Mpesa,
        PaymentMethod.Cash => Cash,
        _ => Bank,
    };
}

/// <summary>What a posting was for, beyond its accounts: the department charged, the asset, the maintenance job.</summary>
public readonly record struct Dims(Guid? Department = null, Guid? Asset = null, Guid? WorkOrder = null)
{
    public void Stamp(JournalEntry e)
    {
        foreach (var l in e.Lines) (l.DepartmentId, l.AssetId, l.WorkOrderId) = (Department, Asset, WorkOrder);
    }
}

public sealed class UnbalancedEntryException(long debit, long credit)
    : InvalidOperationException($"Journal entry does not balance: debits {debit} ≠ credits {credit}.");

public sealed class Ledger(ErpDb db)
{
    readonly Dictionary<string, Guid> _ids = [];

    /// <summary>The id of a system account. Companies created before an account existed get it added on first use.</summary>
    public async Task<Guid> Id(string key)
    {
        if (_ids.TryGetValue(key, out var id)) return id;
        var found = await db.Accounts.Where(a => a.SystemKey == key).Select(a => (Guid?)a.Id).SingleOrDefaultAsync()
            ?? db.Accounts.Local.FirstOrDefault(a => a.SystemKey == key)?.Id;
        if (found is null)
        {
            var template = SystemAccounts.Starter(db.CompanyId).Single(a => a.SystemKey == key);
            // An older company may already have this account under the same code without a key: adopt it.
            var same = await db.Accounts.SingleOrDefaultAsync(a => a.Code == template.Code);
            if (same is not null && same.SystemKey is null && same.Type == template.Type) same.SystemKey = key;
            else
            {
                if (same is not null) template.Code = $"{template.Code}-{key}";
                db.Accounts.Add(template);
                same = template;
            }
            found = same.Id;
        }
        return _ids[key] = found.Value;
    }

    /// <summary>Adds a balanced entry to the unit of work. Throws if debits ≠ credits or any line is negative.</summary>
    public async Task<JournalEntry> Post(DateOnly date, string memo, string sourceType, Guid? sourceId, params (string Key, long Debit, long Credit)[] lines)
    {
        var real = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToArray();
        if (real.Any(l => l.Debit < 0 || l.Credit < 0)) throw new InvalidOperationException("Journal lines can't be negative.");
        long dr = real.Sum(l => l.Debit), cr = real.Sum(l => l.Credit);
        if (dr != cr || dr == 0) throw new UnbalancedEntryException(dr, cr);
        var entry = new JournalEntry { Date = date, Memo = memo, SourceType = sourceType, SourceId = sourceId };
        foreach (var l in real) entry.Lines.Add(new JournalLine { AccountId = await Id(l.Key), Debit = l.Debit, Credit = l.Credit });
        db.JournalEntries.Add(entry);
        return entry;
    }

    /// <summary>Like Post, with the department, asset and job the money was for on every line.</summary>
    public async Task<JournalEntry> PostFor(Dims dims, DateOnly date, string memo, string sourceType, Guid? sourceId, params (string Key, long Debit, long Credit)[] lines)
    {
        var entry = await Post(date, memo, sourceType, sourceId, lines);
        dims.Stamp(entry);
        return entry;
    }

    /// <summary>Like Post, for lines that name an account directly (bills, where each line has its own expense account).</summary>
    public Task<JournalEntry> PostIds(DateOnly date, string memo, string sourceType, Guid? sourceId, IEnumerable<(Guid Account, long Debit, long Credit)> lines)
    {
        var real = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToArray();
        if (real.Any(l => l.Debit < 0 || l.Credit < 0)) throw new InvalidOperationException("Journal lines can't be negative.");
        long dr = real.Sum(l => l.Debit), cr = real.Sum(l => l.Credit);
        if (dr != cr || dr == 0) throw new UnbalancedEntryException(dr, cr);
        var entry = new JournalEntry { Date = date, Memo = memo, SourceType = sourceType, SourceId = sourceId };
        foreach (var l in real) entry.Lines.Add(new JournalLine { AccountId = l.Account, Debit = l.Debit, Credit = l.Credit });
        db.JournalEntries.Add(entry);
        return Task.FromResult(entry);
    }

    /// <summary>Posts a bill: each line to Stock or its expense account, VAT paid to VAT input, the total to suppliers.</summary>
    public async Task PostBill(PurchaseDoc bill, bool reverse = false)
    {
        // A contractor's or parts bill for a maintenance job goes to repairs and maintenance unless a line says otherwise,
        // and carries the job's asset and department.
        var job = bill.WorkOrderId is { } w ? await db.WorkOrders.Where(x => x.Id == w).Select(x => new { x.AssetId, x.DepartmentId }).SingleOrDefaultAsync() : null;
        var other = await Id(job is null ? SystemAccounts.OtherExpenses : SystemAccounts.Maintenance);
        var lines = new List<(Guid, long, long)>();
        foreach (var g in bill.Lines.GroupBy(l => l.AccountId ?? other))
            lines.Add((g.Key, g.Sum(l => l.Net), 0));
        lines.Add((await Id(SystemAccounts.VatInput), bill.Tax, 0));
        lines.Add((await Id(SystemAccounts.Payable), 0, bill.Total));
        if (reverse) lines = lines.Select(l => (l.Item1, l.Item3, l.Item2)).ToList();
        var dims = new Dims(bill.DepartmentId ?? job?.DepartmentId, job?.AssetId, bill.WorkOrderId);
        var entry = await PostIds(reverse ? Clock.Today : bill.IssueDate, $"{(reverse ? "Void bill" : "Bill")} {bill.Number}{(bill.SupplierRef.Length > 0 ? $" ({bill.SupplierRef})" : "")}", reverse ? "BillVoid" : "Bill", bill.Id, lines);
        dims.Stamp(entry);
    }

    public Task PostSupplierPayment(SupplierPayment p, PurchaseDoc bill) =>
        Post(DateOnly.FromDateTime(p.PaidAt.UtcDateTime.AddHours(3)), $"Paid {bill.Number} {p.Reference}".Trim(), "SupplierPayment", p.Id,
            (SystemAccounts.Payable, p.Amount, 0),
            (SystemAccounts.ForMethod(p.Method), 0, p.Amount));

    /// <summary>Cost of what was sold (or, reversed, of what came back).</summary>
    public Task PostCostOfSales(DateOnly date, string reference, Guid sourceId, long cost, bool reverse = false) =>
        cost <= 0 ? Task.CompletedTask : Post(date, $"{(reverse ? "Stock back from" : "Cost of sales")} {reference}", reverse ? "CostOfSalesVoid" : "CostOfSales", sourceId,
            reverse ? (SystemAccounts.Stock, cost, 0) : (SystemAccounts.CostOfSales, cost, 0),
            reverse ? (SystemAccounts.CostOfSales, 0, cost) : (SystemAccounts.Stock, 0, cost));

    /// <summary>Stock the business already had when it started using Kabuor ERP: it's the owner's, so it's equity.</summary>
    public Task PostOpeningStock(string reference, Guid sourceId, long value, bool stores = false) =>
        value <= 0 ? Task.CompletedTask : Post(Clock.Today, $"Opening stock: {reference}", "OpeningStock", sourceId, (stores ? SystemAccounts.Stores : SystemAccounts.Stock, value, 0), (SystemAccounts.Equity, 0, value));

    /// <summary>Stock found (positive value) or lost (negative) on a count or correction.</summary>
    public Task PostStockAdjustment(string reference, Guid sourceId, long value, bool stores = false)
    {
        var account = stores ? SystemAccounts.Stores : SystemAccounts.Stock;
        return value == 0 ? Task.CompletedTask : value > 0
            ? Post(Clock.Today, $"{(stores ? "Stores" : "Stock")} found: {reference}", "StockAdjustment", sourceId, (account, value, 0), (SystemAccounts.StockAdjust, 0, value))
            : Post(Clock.Today, $"{(stores ? "Stores" : "Stock")} lost: {reference}", "StockAdjustment", sourceId, (SystemAccounts.StockAdjust, -value, 0), (account, 0, -value));
    }

    /// <summary>An item changed between stock for sale and stores: what's on hand moves between the two accounts.</summary>
    public Task PostStockReclass(Item item, long value, bool toStores) =>
        value <= 0 ? Task.CompletedTask : Post(Clock.Today, $"{item.Name}: moved to {(toStores ? "stores for own use" : "stock for sale")}", "StockReclass", item.Id,
            (toStores ? SystemAccounts.Stores : SystemAccounts.Stock, value, 0), (toStores ? SystemAccounts.Stock : SystemAccounts.Stores, 0, value));

    public Task PostInvoice(SalesDoc inv) =>
        Post(inv.IssueDate, $"Invoice {inv.Number}", "Invoice", inv.Id,
            (SystemAccounts.Receivable, inv.Total, 0),
            (SystemAccounts.Sales, 0, inv.Net),
            (SystemAccounts.VatOutput, 0, inv.Tax));

    public Task ReverseInvoice(SalesDoc inv, DateOnly date) =>
        Post(date, $"Void invoice {inv.Number}", "InvoiceVoid", inv.Id,
            (SystemAccounts.Sales, inv.Net, 0),
            (SystemAccounts.VatOutput, inv.Tax, 0),
            (SystemAccounts.Receivable, 0, inv.Total));

    public Task PostReceipt(Payment p) =>
        Post(DateOnly.FromDateTime(p.ReceivedAt.UtcDateTime.AddHours(3)), $"{p.Method} payment {p.Reference}".Trim(), "Payment", p.Id,
            (SystemAccounts.ForMethod(p.Method), p.Amount, 0),
            (SystemAccounts.Unapplied, 0, p.Amount));

    public Task PostAllocation(Payment p, SalesDoc inv, long amount) =>
        Post(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), $"{p.Reference} paid to {inv.Number}".Trim(), "Allocation", p.Id,
            (SystemAccounts.Unapplied, amount, 0),
            (SystemAccounts.Receivable, 0, amount));

    /// <summary>Money applied to a foreign-currency invoice: the shillings that came in clear the shillings that part
    /// of the invoice was booked at; more is an exchange gain, less a loss.</summary>
    public Task PostFxAllocation(Payment p, SalesDoc inv, long amount, long booked, long fx)
    {
        var diff = amount - booked;
        return Post(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)), $"{p.Reference} paid {inv.Currency} {fx / 100m:N2} to {inv.Number}".Trim(), "Allocation", p.Id,
            (SystemAccounts.Unapplied, amount, 0),
            (SystemAccounts.Fx, diff < 0 ? -diff : 0, diff > 0 ? diff : 0),
            (SystemAccounts.Receivable, 0, booked));
    }
}
