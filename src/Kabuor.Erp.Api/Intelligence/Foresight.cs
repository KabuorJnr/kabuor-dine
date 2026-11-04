// Foresight and the business chain: the five core modules read as one system, and what the business's own data says
// will happen next.
//
// The chain. Take an airline: it only earns while its planes fly; planes fly only when they work; they work only when
// they're maintained; maintenance needs engineers, parts and the right tools; passengers have to book; marketing brings
// them; the money has to be there; and decisions have to be made on what the numbers say. A hospital (equipment, theatre
// time, patients), a factory (machines, lines, orders) or a transporter (vehicles, trips, consignments) is the same chain
// with different words. Each link below is read from the same records the modules write, never from a separate copy,
// so a breakdown in one shows up where it hurts in the next.
//
// Foresight. Every forecast is made by a handful of simple, explainable models that compete on the business's own
// history: each one predicts every one of the last few months from the months before it (a walk-forward backtest), and
// the one that was closest wins. Its backtest error is reported with it, so nobody mistakes a guess for a promise, and
// as months of records accumulate the choice and the accuracy improve on their own: the models learn from the data.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

/// <summary>A small, honest forecaster: several models, a walk-forward backtest, the best one wins.</summary>
public static class Forecaster
{
    public sealed record Result(string Model, string Explain, double[] Forecast, double Low, double High, double? AccuracyPct, int MonthsOfHistory, int Tested);

    static readonly (string Name, string Explain, Func<double[], int, double[]> Fit)[] Models =
    [
        ("Last month", "next month looks like last month", (h, n) => Enumerable.Repeat(h[^1], n).ToArray()),
        ("3-month average", "the average of the last three months", (h, n) => Enumerable.Repeat(h.TakeLast(3).Average(), n).ToArray()),
        ("Trend", "the straight-line trend of the last twelve months", Trend),
        ("Smoothed trend", "a trend that weighs recent months more (Holt's method)", Holt),
        ("Same month last year", "last year's same months, scaled by how this year compares", Seasonal),
    ];

    static double[] Trend(double[] h, int n)
    {
        var y = h.TakeLast(12).ToArray();
        var m = y.Length;
        if (m < 3) return Enumerable.Repeat(y[^1], n).ToArray();
        double xMean = (m - 1) / 2.0, yMean = y.Average();
        double num = 0, den = 0;
        for (var i = 0; i < m; i++) { num += (i - xMean) * (y[i] - yMean); den += (i - xMean) * (i - xMean); }
        var slope = den == 0 ? 0 : num / den;
        return Enumerable.Range(0, n).Select(k => Math.Max(0, yMean + slope * (m + k - xMean))).ToArray();
    }

    static double[] Holt(double[] h, int n)
    {
        if (h.Length < 3) return Enumerable.Repeat(h[^1], n).ToArray();
        double best = double.MaxValue;
        double[] answer = [];
        foreach (var a in new[] { 0.3, 0.5, 0.8 })
            foreach (var b in new[] { 0.1, 0.3 })
            {
                double level = h[0], trend = h[1] - h[0], err = 0;
                for (var i = 1; i < h.Length; i++)
                {
                    var predicted = level + trend;
                    err += Math.Abs(h[i] - predicted);
                    var newLevel = a * h[i] + (1 - a) * (level + trend);
                    trend = b * (newLevel - level) + (1 - b) * trend;
                    level = newLevel;
                }
                if (err < best) (best, answer) = (err, Enumerable.Range(1, n).Select(k => Math.Max(0, level + k * trend)).ToArray());
            }
        return answer;
    }

    static double[] Seasonal(double[] h, int n)
    {
        if (h.Length < 15) return Trend(h, n);
        var recent = h.TakeLast(3).Sum();
        var yearAgo = h.Skip(h.Length - 15).Take(3).Sum();
        var ratio = yearAgo <= 0 ? 1 : recent / yearAgo;
        return Enumerable.Range(0, n).Select(k => Math.Max(0, h[h.Length - 12 + k % 12] * ratio)).ToArray();
    }

    /// <summary>Forecasts the next months of a monthly series (oldest first). Needs four months of history.</summary>
    public static Result? Run(double[] history, int months = 3)
    {
        // Leading empty months are before the business started recording: they'd teach the models nothing.
        var h = history.SkipWhile(v => v == 0).ToArray();
        if (h.Length < 4) return null;
        var start = Math.Max(3, h.Length - 6);
        (string Name, string Explain, Func<double[], int, double[]> Fit) winner = Models[0];
        double bestErr = double.MaxValue, bestSpread = 0;
        var tested = h.Length - start;
        foreach (var model in Models)
        {
            var errors = new List<double>();
            var pct = new List<double>();
            for (var k = start; k < h.Length; k++)
            {
                var guess = model.Fit(h[..k], 1)[0];
                errors.Add(h[k] - guess);
                if (h[k] > 0) pct.Add(Math.Abs(h[k] - guess) / h[k]);
            }
            var mape = pct.Count > 0 ? pct.Average() : errors.Select(Math.Abs).Average();
            if (mape < bestErr) (winner, bestErr, bestSpread) = (model, mape, Math.Sqrt(errors.Select(e => e * e).Average()));
        }
        var forecast = winner.Fit(h, months);
        // An 80% band from how far the winner was off in the backtest.
        var band = 1.28 * bestSpread;
        return new Result(winner.Name, winner.Explain, forecast, Math.Max(0, forecast[0] - band), forecast[0] + band,
            bestErr == double.MaxValue ? null : Math.Round(Math.Max(0, 100 - bestErr * 100), 0), h.Length, tested);
    }
}

public sealed record CashView(long Now, long CustomersDue, long ExpectedIn, double CollectionRate, long SuppliersDue, long Payroll, long In30, long Overdue);
public sealed record BurnRow(Guid DepartmentId, string Department, long Budget, long Spent, long Projected, bool Over, double? UsedPct);
public sealed record RiskRow(Guid Id, string Tag, string Name, string Criticality, string Status, int Failures, double MtbfDays, double DaysSinceLast, double Risk30, DateOnly ExpectedAround, DateOnly? NextService, string Advice);

public sealed class ForesightService(ErpDb db, RequestContext ctx)
{
    static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);
    static string Label(DateOnly d) => $"{MonthNames[d.Month - 1]} {d.Year % 100:00}";

    /// <summary>Income and expenses per month from the books, oldest first (the last month is the current, partial one).</summary>
    async Task<(DateOnly[] Months, double[] Income, double[] Expense)> Monthly(int count)
    {
        var first = MonthStart(Clock.Today).AddMonths(-(count - 1));
        var rows = await db.JournalLines
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.AccountId, l.Debit, l.Credit, e.Date })
            .Join(db.Accounts, x => x.AccountId, a => a.Id, (x, a) => new { x.Debit, x.Credit, x.Date, a.Type, a.SystemKey })
            .Where(x => x.Date >= first && (x.Type == AccountType.Income || x.Type == AccountType.Expense) && x.SystemKey != SystemAccounts.Fx)
            .Select(x => new { x.Debit, x.Credit, x.Date, x.Type }).ToListAsync();
        var months = Enumerable.Range(0, count).Select(i => first.AddMonths(i)).ToArray();
        int Index(DateOnly d) => (d.Year - first.Year) * 12 + d.Month - first.Month;
        var income = new double[count];
        var expense = new double[count];
        foreach (var r in rows)
        {
            var i = Index(r.Date);
            if (i < 0 || i >= count) continue;
            if (r.Type == AccountType.Income) income[i] += r.Credit - r.Debit; else expense[i] += r.Debit - r.Credit;
        }
        return (months, income, expense);
    }

    object Series(string key, string name, DateOnly[] months, double[] values)
    {
        // The current month is still running: the models learn from complete months only.
        var complete = values[..^1];
        var f = Forecaster.Run(complete);
        var next = MonthStart(Clock.Today);
        return new
        {
            key, name,
            history = months[..^1].Select((m, i) => new { month = Label(m), value = (long)Math.Round(complete[i]) }).TakeLast(12),
            soFar = (long)Math.Round(values[^1]),
            forecast = f is null ? null : new
            {
                model = f.Model, explain = f.Explain, accuracy = f.AccuracyPct, months = f.MonthsOfHistory, tested = f.Tested,
                low = (long)Math.Round(f.Low), high = (long)Math.Round(f.High),
                values = f.Forecast.Select((v, i) => new { month = Label(next.AddMonths(i)), value = (long)Math.Round(v) }),
            },
        };
    }

    /// <summary>Everything foresight knows: money forecasts, assets likely to fail, stores about to run out, budgets that
    /// will overrun, and cash for the next 30 days.</summary>
    public async Task<object> Foresight()
    {
        var (months, income, expense) = await Monthly(25);
        var profit = income.Zip(expense, (a, b) => a - b).ToArray();
        return new
        {
            money = new[] { Series("income", "Income", months, income), Series("expense", "Expenses", months, expense), Series("profit", "Profit", months, profit) },
            assets = await AssetRisk(),
            stores = await StoresRunout(),
            budgets = await BudgetBurn(),
            cash = await Cash(),
        };
    }

    /// <summary>Assets most likely to break in the next 30 days, from the gaps between their past breakdowns.</summary>
    public async Task<List<RiskRow>> AssetRisk()
    {
        var failures = await db.WorkOrders.Where(w => w.AssetId != null && (w.Type == WorkOrderType.Breakdown || w.Type == WorkOrderType.Corrective) && w.Status != WorkOrderStatus.Cancelled)
            .Select(w => new { AssetId = w.AssetId!.Value, At = w.DownFrom ?? w.CreatedAt }).ToListAsync();
        var plans = await db.MaintenancePlans.Where(p => p.Active && p.AssetId != null).Select(p => new { AssetId = p.AssetId!.Value, p.NextDue }).ToListAsync();
        var assets = await db.Assets.Where(a => a.Status != AssetStatus.Disposed && a.Status != AssetStatus.Lost).Select(a => new { a.Id, a.Tag, a.Name, a.Criticality, a.Status }).ToDictionaryAsync(a => a.Id);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<(double Score, RiskRow Row)>();
        foreach (var g in failures.GroupBy(f => f.AssetId))
        {
            if (!assets.TryGetValue(g.Key, out var a)) continue;
            var times = g.Select(x => x.At).OrderBy(x => x).ToList();
            if (times.Count < 2) continue;
            var gaps = times.Zip(times.Skip(1), (x, y) => (y - x).TotalDays).Where(d => d > 0.5).ToList();
            if (gaps.Count == 0) continue;
            var mtbf = gaps.Average();
            var since = (now - times[^1]).TotalDays;
            // Chance of at least one failure in the next 30 days if failures keep coming at the rate seen so far.
            var risk = 1 - Math.Exp(-30 / Math.Max(1, mtbf));
            if (since > mtbf) risk = Math.Min(0.99, risk * 1.25); // past its usual gap: due by its own pattern
            var weight = a.Criticality switch { AssetCriticality.High => 1.5, AssetCriticality.Medium => 1.0, _ => 0.6 };
            var nextPm = plans.Where(p => p.AssetId == a.Id).Select(p => (DateOnly?)p.NextDue).Min();
            var expected = times[^1].AddDays(mtbf);
            var around = DateOnly.FromDateTime(expected.UtcDateTime);
            rows.Add((risk * weight, new RiskRow(a.Id, a.Tag, a.Name, a.Criticality.ToString(), a.Status.ToString(), times.Count, Math.Round(mtbf, 1), Math.Round(since, 0), Math.Round(risk * 100), around, nextPm,
                nextPm is { } pm && pm <= around ? "Its next service comes first: keep to it." : $"Service it before {around:d MMM}: past breakdowns came about every {Math.Round(mtbf)} days.")));
        }
        return rows.OrderByDescending(r => r.Score).Take(8).Select(r => r.Row).ToList();
    }

    /// <summary>Stores (spares, tools, supplies) that will run out at the rate they've been used, and jobs waiting on them.</summary>
    public async Task<List<object>> StoresRunout()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var items = await db.Items.Where(i => i.TrackStock && i.Use == ItemUse.Stores && i.Active).Select(i => new { i.Id, i.Name, i.Unit, i.ReorderLevel, i.StoresCategory }).ToListAsync();
        var ids = items.Select(i => i.Id).ToList();
        var used = (await db.StockMoves.Where(m => ids.Contains(m.ItemId) && m.Kind == StockMoveKind.Consumed && m.At >= since).Select(m => new { m.ItemId, m.Quantity }).ToListAsync())
            .GroupBy(m => m.ItemId).ToDictionary(g => g.Key, g => -g.Sum(m => m.Quantity));
        var onHand = (await db.StockLevels.Where(l => ids.Contains(l.ItemId)).Select(l => new { l.ItemId, l.Quantity }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var live = new[] { WorkOrderStatus.Open, WorkOrderStatus.Planned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold };
        var demand = (await db.WorkOrderParts.Join(db.WorkOrders.Where(w => live.Contains(w.Status)), p => p.WorkOrderId, w => w.Id, (p, w) => new { p.ItemId, need = p.PlannedQuantity - p.IssuedQuantity })
            .Where(x => x.ItemId != null && x.need > 0).ToListAsync()).GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.need));
        return items.Select(i =>
        {
            var daily = used.GetValueOrDefault(i.Id) / 90m;
            var have = onHand.GetValueOrDefault(i.Id);
            var need = demand.GetValueOrDefault(i.Id);
            decimal? days = daily > 0 ? Math.Round(Math.Max(0, have - need) / daily, 0) : null;
            return new { i.Id, i.Name, i.Unit, category = i.StoresCategory, onHand = have, jobsNeed = need, perWeek = Math.Round(daily * 7, 1), daysLeft = days, runsOutOn = days is { } d ? Clock.Today.AddDays((int)Math.Min(d, 3650)) : (DateOnly?)null, low = have <= i.ReorderLevel || need > have };
        }).Where(x => x.low || x.daysLeft is < 30).OrderBy(x => x.daysLeft ?? (x.jobsNeed > x.onHand ? -1 : 9999)).Take(10).Cast<object>().ToList();
    }

    /// <summary>Departments heading over this month's approved budget at the rate they're spending.</summary>
    public async Task<List<BurnRow>> BudgetBurn()
    {
        var today = Clock.Today;
        var first = MonthStart(today);
        var budgets = await db.Budgets.Where(b => b.Year == today.Year && b.Status == BudgetStatus.Approved).Select(b => new { b.DepartmentId, b.Months }).ToListAsync();
        if (budgets.Count == 0) return [];
        var spent = (await db.JournalLines.Where(l => l.DepartmentId != null)
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.DepartmentId, l.AccountId, l.Debit, l.Credit, e.Date })
            .Join(db.Accounts, x => x.AccountId, a => a.Id, (x, a) => new { x.DepartmentId, x.Debit, x.Credit, x.Date, a.Type })
            .Where(x => x.Type == AccountType.Expense && x.Date >= first && x.Date <= today).Select(x => new { x.DepartmentId, x.Debit, x.Credit }).ToListAsync())
            .GroupBy(x => x.DepartmentId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Debit - x.Credit));
        var names = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var days = DateTime.DaysInMonth(today.Year, today.Month);
        var elapsed = Math.Max(1, today.Day);
        return budgets.Select(b =>
        {
            var budget = b.Months.ElementAtOrDefault(today.Month - 1);
            var s = spent.GetValueOrDefault(b.DepartmentId);
            // Early in the month one big bill (the fuel uplift) would project wildly: run-rate only after ten days.
            var projected = elapsed < 10 ? s : Math.Max(s, (long)Math.Round((double)s / elapsed * days));
            return new BurnRow(b.DepartmentId, names.GetValueOrDefault(b.DepartmentId, ""), budget, s, projected, budget > 0 && projected > budget, budget > 0 ? Math.Round(100.0 * s / budget) : null);
        }).OrderByDescending(x => x.Budget > 0 ? (double)x.Projected / x.Budget : 0).ToList();
    }

    /// <summary>Cash now and in 30 days: money in the bank and M-Pesa, what customers should pay, what's owed to suppliers, and pay.</summary>
    public async Task<CashView> Cash()
    {
        var today = Clock.Today;
        var keys = new[] { SystemAccounts.Cash, SystemAccounts.Mpesa, SystemAccounts.Bank };
        var cash = await db.JournalLines.Join(db.Accounts.Where(a => a.SystemKey != null && keys.Contains(a.SystemKey)), l => l.AccountId, a => a.Id, (l, a) => l.Debit - l.Credit).SumAsync(x => (long?)x) ?? 0;
        var horizon = today.AddDays(30);
        var open = new[] { SalesDocStatus.Sent, SalesDocStatus.PartPaid };
        var ar = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && open.Contains(d.Status)).Select(d => new { d.DueDate, owed = d.Total - d.Paid }).ToListAsync();
        var ap = await db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Bill && (d.Status == PurchaseStatus.Open || d.Status == PurchaseStatus.PartPaid)).Select(d => new { d.DueDate, owed = d.Total - d.Paid }).ToListAsync();
        // How much of what falls due customers have actually paid on time, from the last six months of settled invoices.
        var settled = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && d.Status == SalesDocStatus.Paid && d.DueDate >= today.AddMonths(-6)).CountAsync();
        var lateOrOpen = ar.Count(x => x.DueDate < today);
        var collect = settled + lateOrOpen == 0 ? 0.8 : Math.Clamp((double)settled / (settled + lateOrOpen), 0.3, 1);
        var lastMonth = MonthStart(today).AddMonths(-1);
        var payroll = await db.JournalLines.Join(db.JournalEntries.Where(e => e.Date >= lastMonth && e.Date < MonthStart(today)), l => l.JournalEntryId, e => e.Id, (l, e) => l)
            .Join(db.Accounts.Where(a => a.SystemKey == SystemAccounts.Salaries || a.SystemKey == SystemAccounts.EmployerStatutory), l => l.AccountId, a => a.Id, (l, a) => l.Debit - l.Credit).SumAsync(x => (long?)x) ?? 0;
        var arDue = ar.Where(x => x.DueDate <= horizon).Sum(x => x.owed);
        var apDue = ap.Where(x => x.DueDate <= horizon).Sum(x => x.owed);
        var expected = (long)Math.Round(arDue * collect);
        return new CashView(cash, arDue, expected, Math.Round(collect * 100), apDue, payroll, cash + expected - apDue - payroll, ar.Where(x => x.DueDate < today).Sum(x => x.owed));
    }

    // ------------------------------------------------------------------------------------------------ the chain

    static object Metric(string label, object value, string? unit = null) => new { label, value, unit };

    /// <summary>The business as one chain, link by link, each read from the module that owns it, with what's holding it back.</summary>
    public async Task<object> Chain()
    {
        var today = Clock.Today;
        var now = DateTimeOffset.UtcNow;
        var company = await db.Companies.Where(c => c.Id == ctx.CompanyId).Select(c => new { c.Name, c.Industry }).SingleOrDefaultAsync();
        var monthStart = MonthStart(today);
        var lastMonthStart = monthStart.AddMonths(-1);
        var sameDayLastMonth = lastMonthStart.AddDays(Math.Min(today.Day, DateTime.DaysInMonth(lastMonthStart.Year, lastMonthStart.Month)) - 1);

        // 1. Assets working
        var assets = await db.Assets.Where(a => a.Status != AssetStatus.Disposed && a.Status != AssetStatus.Lost).Select(a => new { a.Id, a.Name, a.Status, a.Criticality }).ToListAsync();
        var down = assets.Where(a => a.Status == AssetStatus.UnderRepair).ToList();
        var criticalDown = down.Where(a => a.Criticality == AssetCriticality.High).ToList();
        var availability = assets.Count == 0 ? (double?)null : Math.Round(100.0 * (assets.Count - down.Count) / assets.Count, 1);

        // 2. Maintained
        var live = new[] { WorkOrderStatus.Open, WorkOrderStatus.Planned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold };
        var jobs = await db.WorkOrders.Where(w => live.Contains(w.Status)).Select(w => new { w.Id, w.Type, w.DueDate, w.Status, w.AssignedTo, w.SupplierId }).ToListAsync();
        var overdue = jobs.Count(j => j.DueDate < today);
        var unassigned = jobs.Count(j => j.AssignedTo == "" && j.SupplierId == null);
        var pmDone = await db.WorkOrders.Where(w => w.Type == WorkOrderType.Preventive && w.CompletedAt != null && w.CompletedAt >= now.AddDays(-90)).Select(w => new { w.CompletedAt, w.DueDate }).ToListAsync();
        var pmOnTime = pmDone.Count == 0 ? (double?)null : Math.Round(100.0 * pmDone.Count(p => p.DueDate is null || DateOnly.FromDateTime(p.CompletedAt!.Value.UtcDateTime) <= p.DueDate) / pmDone.Count);
        var openFaults = await db.FaultReports.CountAsync(f => f.Status == FaultStatus.New);

        // 3. Parts and tools (procurement and stores)
        var runout = await StoresRunout();
        var jobIds = jobs.Select(j => j.Id).ToList();
        var toolsBusy = await db.WorkOrderTools.CountAsync(t => t.CheckedOutAt != null && t.ReturnedAt == null);
        var shortParts = (await db.WorkOrderParts.Where(p => jobIds.Contains(p.WorkOrderId) && p.ItemId != null && p.PlannedQuantity > p.IssuedQuantity).Select(p => new { p.ItemId, need = p.PlannedQuantity - p.IssuedQuantity }).ToListAsync());
        var shortIds = shortParts.Select(p => p.ItemId!.Value).Distinct().ToList();
        var have = (await db.StockLevels.Where(l => shortIds.Contains(l.ItemId)).Select(l => new { l.ItemId, l.Quantity }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var jobsWaitingParts = shortParts.GroupBy(p => p.ItemId!.Value).Count(g => g.Sum(x => x.need) > have.GetValueOrDefault(g.Key));
        var reqWaiting = await db.Requisitions.Where(r => r.Status == RequisitionStatus.WithBuying || r.Status == RequisitionStatus.WithHead || r.Status == RequisitionStatus.WithFinance).Select(r => new { r.Status, r.SubmittedAt }).ToListAsync();
        var reqOld = reqWaiting.Count(r => r.SubmittedAt < now.AddDays(-3));

        // 4. People
        var staff = await db.Employees.Where(e => e.EndDate == null || e.EndDate >= today).Select(e => new { e.Id, e.Department, e.JobTitle }).ToListAsync();
        var onLeave = await db.LeaveRequests.CountAsync(l => l.Status == LeaveStatus.Approved && l.From <= today && l.To >= today);
        var technicians = staff.Count(e => Has(e.Department, "maint", "engineer", "workshop", "technical") || Has(e.JobTitle, "technician", "engineer", "mechanic", "fundi", "electrician"));
        var openings = await db.JobOpenings.CountAsync(j => j.Open);

        // 5. Customers buy (sales)
        var issued = new[] { SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid };
        var thisMonth = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && issued.Contains(d.Status) && d.IssueDate >= monthStart && d.IssueDate <= today).Select(d => new { d.Net, d.CustomerId }).ToListAsync();
        var lastPace = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && issued.Contains(d.Status) && d.IssueDate >= lastMonthStart && d.IssueDate <= sameDayLastMonth).SumAsync(d => (long?)d.Net) ?? 0;
        var revenue = thisMonth.Sum(d => d.Net);
        var pace = lastPace == 0 ? (double?)null : Math.Round(100.0 * (revenue - lastPace) / lastPace);
        var openQuotes = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Quote && d.Status == SalesDocStatus.Sent).SumAsync(d => (long?)d.Net) ?? 0;

        // 6. Marketing brings them
        var leadsNow = await db.Leads.CountAsync(l => l.CreatedAt >= new DateTimeOffset(monthStart.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)));
        var leadsBefore = await db.Leads.CountAsync(l => l.CreatedAt >= new DateTimeOffset(lastMonthStart.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)) && l.CreatedAt < new DateTimeOffset(sameDayLastMonth.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)));
        var inquiries = await db.Inquiries.CountAsync(i => i.CreatedAt >= new DateTimeOffset(monthStart.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)));
        var closed = await db.Leads.Where(l => (l.Stage == LeadStage.Won || l.Stage == LeadStage.Lost) && l.StageChangedAt >= now.AddDays(-90)).Select(l => l.Stage).ToListAsync();
        var winRate = closed.Count == 0 ? (double?)null : Math.Round(100.0 * closed.Count(s => s == LeadStage.Won) / closed.Count);
        var pipeline = await db.Leads.Where(l => l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost).SumAsync(l => (long?)l.EstimatedValue) ?? 0;

        // 7. Money
        var c = await Cash();
        var depts = await db.Departments.Where(d => d.Active).Select(d => d.Id).ToListAsync();
        var budgeted = await db.Budgets.Where(b => b.Year == today.Year && b.Status == BudgetStatus.Approved).Select(b => b.DepartmentId).ToListAsync();
        var noBudget = depts.Count(d => !budgeted.Contains(d));
        var forFinance = reqWaiting.Count(r => r.Status == RequisitionStatus.WithFinance) + await db.Budgets.CountAsync(b => b.Status == BudgetStatus.Submitted);
        var burn = await BudgetBurn();

        // 8. Decisions and predictions
        var (months, income, _) = await Monthly(25);
        var f = Forecaster.Run(income[..^1]);
        var risky = await AssetRisk();

        string Health(bool bad, bool warn) => bad ? "bad" : warn ? "warn" : "good";
        var links = new List<object>
        {
            Link("assets", "Operations", Health(criticalDown.Count > 0, availability < 95 || down.Count > 0),
                availability is null ? "No assets registered yet" : $"{availability}% available",
                [Metric("In service", assets.Count - down.Count), Metric("Down", down.Count), Metric("Critical down", criticalDown.Count)],
                [.. criticalDown.Take(3).Select(a => $"{a.Name} is down and it's critical"), .. down.Count > criticalDown.Count ? new[] { $"{down.Count - criticalDown.Count} other asset(s) under repair" } : []], "/assets"),
            Link("maintenance", "Operations", Health(overdue > 3, overdue > 0 || unassigned > 0 || openFaults > 0),
                jobs.Count == 0 ? "No open jobs" : $"{jobs.Count} open job(s)",
                [Metric("Overdue", overdue), Metric("Unassigned", unassigned), Metric("Faults not looked at", openFaults), Metric("Preventive on time", pmOnTime ?? (object)"–", pmOnTime is null ? null : "%")],
                [.. overdue > 0 ? new[] { $"{overdue} job(s) past due" } : [], .. unassigned > 0 ? new[] { $"{unassigned} job(s) with nobody assigned" } : [], .. openFaults > 0 ? new[] { $"{openFaults} fault report(s) waiting" } : []], "/maintenance"),
            Link("supply", "Procurement", Health(jobsWaitingParts > 0, runout.Count > 0 || reqOld > 0),
                jobsWaitingParts > 0 ? $"{jobsWaitingParts} part(s) short for open jobs" : "Parts and tools in hand",
                [Metric("Short for jobs", jobsWaitingParts), Metric("Stores running low", runout.Count), Metric("Tools out on jobs", toolsBusy), Metric("Requests waiting", reqWaiting.Count)],
                [.. jobsWaitingParts > 0 ? new[] { "Jobs can't finish until the parts arrive" } : [], .. reqOld > 0 ? new[] { $"{reqOld} request(s) waiting more than 3 days" } : []], "/procurement"),
            Link("people", "Human resources", Health(staff.Count == 0, staff.Count > 0 && onLeave * 100 / Math.Max(1, staff.Count) > 15 || technicians == 0 && jobs.Count > 0),
                $"{staff.Count} people", [Metric("On leave today", onLeave), Metric("Technical staff", technicians), Metric("Open positions", openings)],
                [.. technicians == 0 && jobs.Count > 0 ? new[] { "Jobs are open but no technical staff are on record" } : [], .. openings > 0 ? new[] { $"{openings} position(s) to fill" } : []], "/people"),
            Link("sales", "Sales & marketing", Health(pace < -30, pace < -10),
                $"{Ksh(revenue)} sold this month", [Metric("vs last month so far", pace ?? (object)"–", pace is null ? null : "%"), Metric("Customers this month", thisMonth.Select(d => d.CustomerId).Distinct().Count()), Metric("Quotes out", Ksh(openQuotes))],
                [.. pace < -10 ? new[] { $"Selling {Math.Abs(pace!.Value)}% slower than this time last month" } : []], "/invoices"),
            Link("marketing", "Sales & marketing", Health(false, leadsBefore > 0 && leadsNow < leadsBefore * 0.7),
                $"{leadsNow + inquiries} new lead(s) and inquiries", [Metric("Leads this month", leadsNow), Metric("Inquiries", inquiries), Metric("Win rate (90 days)", winRate ?? (object)"–", winRate is null ? null : "%"), Metric("Pipeline", Ksh(pipeline))],
                [.. leadsBefore > 0 && leadsNow < leadsBefore * 0.7 ? new[] { $"Fewer leads than this time last month ({leadsNow} vs {leadsBefore})" } : []], "/pipeline"),
            Link("finance", "Finance", Health(c.In30 < 0, noBudget > 0 || forFinance > 0 || c.Overdue > 0),
                $"{Ksh(c.Now)} cash, {Ksh(c.In30)} in 30 days",
                [Metric("Overdue from customers", Ksh(c.Overdue)), Metric("Departments without a budget", noBudget), Metric("Waiting for finance", forFinance), Metric("Budgets heading over", burn.Count(b => b.Over))],
                [.. c.In30 < 0 ? new[] { "Cash runs short within 30 days at this rate" } : [], .. noBudget > 0 ? new[] { $"{noBudget} department(s) can't spend: no approved budget" } : [], .. forFinance > 0 ? new[] { $"{forFinance} decision(s) waiting for finance" } : []], "/finance"),
            Link("decide", "Intelligence", Health(false, f is null || f.AccuracyPct < 70 || risky.Count > 0),
                f is null ? "Forecasts start after four months of records" : $"Next month ≈ {Ksh((long)Math.Round(f.Forecast[0]))}",
                [Metric("Forecast accuracy", f?.AccuracyPct ?? (object)"–", f?.AccuracyPct is null ? null : "%"), Metric("Model", f?.Model ?? "–"), Metric("Assets likely to fail (30 days)", risky.Count)],
                [.. risky.Take(2).Select(r => $"{r.Name}: {r.Risk30}% chance of a breakdown in 30 days")], "/os?tab=forecasts"),
        };
        return new { company = company?.Name ?? "", industry = company?.Industry ?? "general", links, asOf = now };
    }

    static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    /// <summary>Short money for headlines: KSh 1.71B, KSh 125.1M, KSh 48,000.</summary>
    static string Ksh(long cents)
    {
        var v = cents / 100m;
        var a = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        return a >= 1_000_000_000 ? $"KSh {sign}{a / 1_000_000_000:0.##}B" : a >= 1_000_000 ? $"KSh {sign}{a / 1_000_000:0.#}M" : $"KSh {v:N0}";
    }

    static object Link(string key, string module, string health, string headline, object[] metrics, string[] blockers, string to) =>
        new { key, module, health, headline, metrics, blockers, to };
}
