// The money side of operations, for the owner and the accountant: what each department spent against its budget and
// what it has committed, what keeping the assets running cost (by asset, by type and by month), what departments used
// from the store, where buying stands (requests, orders, unpaid bills, tenders), how jobs came in against their
// estimates, and what stock takes found. Everything that's money comes from the books (expense lines and the
// department, asset and job they were charged to), so it always agrees with the profit and loss.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed class OperationsFinance(ErpDb db, DepartmentService departments, RequestContext ctx)
{
    public async Task<object> Get(DateOnly from, DateOnly to)
    {
        if (to < from) throw new AppException("The end date is before the start date.");
        var lines = await db.JournalLines
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l, e.Date })
            .Join(db.Accounts, x => x.l.AccountId, a => a.Id, (x, a) => new { x.l.DepartmentId, x.l.AssetId, x.l.WorkOrderId, x.l.Debit, x.l.Credit, x.Date, a.Type, a.SystemKey, a.Name, a.Code })
            .Where(x => x.Type == AccountType.Expense && x.Date >= from && x.Date <= to).ToListAsync();
        long Sum<T>(IEnumerable<T> xs, Func<T, long> f) => xs.Sum(f);
        var total = Sum(lines, l => l.Debit - l.Credit);
        var tagged = Sum(lines.Where(l => l.DepartmentId != null), l => l.Debit - l.Credit);

        // Departments: spent in the period against the budget for its months, and what's committed now.
        var months = (to.Year * 12 + to.Month) - (from.Year * 12 + from.Month) + 1;
        var depts = await db.Departments.OrderBy(d => d.Name).ToListAsync();
        var committed = await departments.Committed();
        // Each month's approved amount, from the budgets finance approved for those years.
        var approvedBudgets = await db.Budgets.Where(b => b.Status == BudgetStatus.Approved && b.Year >= from.Year && b.Year <= to.Year).Select(b => new { b.DepartmentId, b.Year, b.Months }).ToListAsync();
        long BudgetFor(Guid dept) => Enumerable.Range(0, months).Select(i => new DateOnly(from.Year, from.Month, 1).AddMonths(i))
            .Sum(m => approvedBudgets.FirstOrDefault(b => b.DepartmentId == dept && b.Year == m.Year)?.Months.ElementAtOrDefault(m.Month - 1) ?? 0);
        var byDepartment = depts.Select(d =>
        {
            var spent = Sum(lines.Where(l => l.DepartmentId == d.Id), l => l.Debit - l.Credit);
            var budget = BudgetFor(d.Id);
            return new
            {
                d.Id, d.Name, d.Code, spent, budget, committed = committed.GetValueOrDefault(d.Id),
                remaining = budget > 0 ? budget - spent - committed.GetValueOrDefault(d.Id) : (long?)null,
                used = budget > 0 ? Math.Round(100.0 * (spent + committed.GetValueOrDefault(d.Id)) / budget) : (double?)null,
                supplies = Sum(lines.Where(l => l.DepartmentId == d.Id && l.SystemKey == SystemAccounts.SuppliesUsed), l => l.Debit - l.Credit),
                maintenance = Sum(lines.Where(l => l.DepartmentId == d.Id && l.SystemKey == SystemAccounts.Maintenance), l => l.Debit - l.Credit),
            };
        }).ToList();

        // Keeping assets running: everything booked to repairs and maintenance.
        var maint = lines.Where(l => l.SystemKey == SystemAccounts.Maintenance).ToList();
        var assets = await db.Assets.Select(a => new { a.Id, a.Tag, a.Name, a.Category, a.Cost }).ToListAsync();
        var custom = await db.AssetTypes.ToDictionaryAsync(t => t.Code, t => t.Name);
        string TypeName(string code) => custom.GetValueOrDefault(code) ?? AssetCategories.Name(code);
        var assetCost = maint.Where(l => l.AssetId != null).GroupBy(l => l.AssetId!.Value).ToDictionary(g => g.Key, g => Sum(g, l => l.Debit - l.Credit));
        var byAsset = assets.Where(a => assetCost.ContainsKey(a.Id)).Select(a => new
        {
            a.Id, a.Tag, a.Name, type = TypeName(a.Category), cost = assetCost[a.Id], value = a.Cost,
            // Maintenance as a share of what the asset cost: high and rising is the sign to replace rather than repair.
            share = a.Cost > 0 ? Math.Round(100.0 * assetCost[a.Id] / a.Cost) : (double?)null,
        }).OrderByDescending(a => a.cost).Take(10).ToList();
        var byType = assets.Where(a => assetCost.ContainsKey(a.Id)).GroupBy(a => a.Category).Select(g => new { type = TypeName(g.Key), cost = g.Sum(a => assetCost[a.Id]) }).OrderByDescending(x => x.cost).ToList();
        var byMonth = Enumerable.Range(0, months).Select(i => new DateOnly(from.Year, from.Month, 1).AddMonths(i)).Select(m => new
        {
            month = m,
            maintenance = Sum(maint.Where(l => l.Date.Year == m.Year && l.Date.Month == m.Month), l => l.Debit - l.Credit),
            supplies = Sum(lines.Where(l => l.SystemKey == SystemAccounts.SuppliesUsed && l.Date.Year == m.Year && l.Date.Month == m.Month), l => l.Debit - l.Credit),
        }).ToList();

        // Buying: where every request, order and bill stands now.
        var reqs = await db.Requisitions.Include(r => r.Lines).Where(r => r.Status == RequisitionStatus.WithHead || r.Status == RequisitionStatus.WithBuying || r.Status == RequisitionStatus.Approved).ToListAsync();
        var orders = await db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent)).Select(d => new { d.Net, d.Total }).ToListAsync();
        var bills = await db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Bill && (d.Status == PurchaseStatus.Open || d.Status == PurchaseStatus.PartPaid)).Select(d => new { d.Total, d.Paid, d.DueDate }).ToListAsync();
        var tenders = await db.Tenders.Where(t => t.Status == TenderStatus.Published || t.Status == TenderStatus.Opened).CountAsync();
        var pipeline = new
        {
            awaitingApproval = new { count = reqs.Count(r => r.Status != RequisitionStatus.Approved), value = reqs.Where(r => r.Status != RequisitionStatus.Approved).Sum(r => r.EstimatedTotal) },
            approvedNotBought = new { count = reqs.Count(r => r.Status == RequisitionStatus.Approved), value = reqs.Where(r => r.Status == RequisitionStatus.Approved).Sum(r => r.Lines.Sum(l => (long)Math.Round(l.Outstanding * l.EstimatedUnitCost))) },
            openOrders = new { count = orders.Count, value = orders.Sum(o => o.Net) },
            unpaidBills = new { count = bills.Count, value = bills.Sum(b => b.Total - b.Paid), overdue = bills.Where(b => b.DueDate < Clock.Today).Sum(b => b.Total - b.Paid) },
            tenders,
        };

        // Jobs closed in the period: estimate against what they cost (labour only for those who see pay).
        var pay = ctx.Can(Policies.People) || ctx.Can(Policies.Finance);
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var closed = (await db.WorkOrders.Where(w => w.Status == WorkOrderStatus.Closed).ToListAsync()).Where(w => w.ClosedAt >= start && w.ClosedAt < end).ToList();
        long Actual(WorkOrder w) => w.PartsCost + w.ServicesCost + (pay ? w.LabourCost : 0);
        var jobs = new
        {
            closed = closed.Count, estimated = closed.Sum(w => w.EstimatedCost), actual = closed.Sum(Actual),
            overEstimate = closed.Where(w => w.EstimatedCost > 0 && Actual(w) > w.EstimatedCost).OrderByDescending(w => Actual(w) - w.EstimatedCost).Take(5)
                .Select(w => new { w.Id, w.Number, w.Title, estimate = w.EstimatedCost, actual = Actual(w) }),
            labourIncluded = pay,
        };

        // Stock: its value now, and what stock takes found in the period.
        var stockValue = (await db.StockLevels.Join(db.Items, l => l.ItemId, i => i.Id, (l, i) => new { l.Quantity, i.AverageCost }).ToListAsync()).Sum(x => (long)Math.Round(x.Quantity * x.AverageCost));
        var takes = (await db.StockTakes.Where(s => s.Status == StockTakeStatus.Posted).Select(s => new { s.PostedAt, s.VarianceValue }).ToListAsync()).Where(s => s.PostedAt >= start && s.PostedAt < end).ToList();

        var topAccounts = lines.GroupBy(l => (l.Code, l.Name)).Select(g => new { g.Key.Code, g.Key.Name, amount = Sum(g, l => l.Debit - l.Credit) }).Where(x => x.amount != 0).OrderByDescending(x => x.amount).Take(8).ToList();
        return new
        {
            from, to, months,
            expenses = new { total, tagged, untagged = total - tagged, taggedShare = total != 0 ? Math.Round(100.0 * tagged / total) : (double?)null },
            byDepartment,
            maintenance = new { total = Sum(maint, l => l.Debit - l.Credit), byAsset, byType, byMonth, onJobs = Sum(maint.Where(l => l.WorkOrderId != null), l => l.Debit - l.Credit) },
            supplies = Sum(lines.Where(l => l.SystemKey == SystemAccounts.SuppliesUsed), l => l.Debit - l.Credit),
            pipeline, jobs,
            stock = new { value = stockValue, takes = takes.Count, variance = takes.Sum(s => s.VarianceValue) },
            topAccounts,
        };
    }
}
