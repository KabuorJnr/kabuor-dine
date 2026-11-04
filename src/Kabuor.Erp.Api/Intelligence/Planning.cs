// Planning: the functions ERP books say most businesses never switch on, here as plain pages.
//
// Material requirements (MRP). For everything the business makes: how much it will sell in the coming months (its own
// forecast, from its sales history), less what's on the shelf and already planned, gives what to make; exploding the
// recipes gives the materials that takes, less what's in stock and already on order, gives what to buy and by when.
//
// The monthly planning meeting (sales and operations planning). One page for the leadership: demand (the money
// forecast and what sells), supply (what to make and buy), capacity (assets available, technicians' hours against the
// maintenance planned for the next four weeks), the money (budgets, cash) and the people. The decisions are written down
// and, once agreed, go into the approvals register with the numbers they were made on.
//
// ERP health. Wallace's ABCD checklist in measurable form: is the data accurate, is the process followed, are the
// controls on, is planning used? Each check has a target and the page that fixes it; the overall class is A (90+),
// B (75+), C (50+) or D.
using System.Text.Json;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Operations;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

public sealed record MeetingInput(DateOnly? Month, string? Attendees, string? Decisions, bool? Agree);
public sealed record MrpRequestInput(Guid DepartmentId, List<MrpLineInput> Lines);
public sealed record MrpLineInput(Guid ItemId, decimal Quantity);

public sealed class PlanningService(ErpDb db, ForesightService foresight, ApprovalRegister register, RequisitionService requisitions, RequestContext ctx)
{
    static DateOnly MonthStart(DateOnly d) => new(d.Year, d.Month, 1);
    static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    // ------------------------------------------------------------------------------------------------ MRP

    public sealed record MrpProduct(Guid ItemId, string Name, string Unit, Guid RecipeId, string Recipe, decimal[] Demand, string DemandFrom, decimal OnHand, decimal Planned, decimal Safety, decimal ToMake, decimal Batches, decimal OutputPerBatch);
    public sealed record MrpMaterial(Guid ItemId, string Name, string Unit, string Use, decimal Needed, decimal OnHand, decimal OnOrder, decimal ToBuy, DateOnly OrderBy, long UnitCost, long Value, List<string> For);

    /// <summary>What to make and what to buy for the next few months.</summary>
    public async Task<(List<MrpProduct> Products, List<MrpMaterial> Materials)> Mrp(int months = 3, int leadDays = 14)
    {
        months = Math.Clamp(months, 1, 6);
        var recipes = await db.Recipes.Include(r => r.Lines).Where(r => r.Active).ToListAsync();
        var outputIds = recipes.Select(r => r.OutputItemId).Distinct().ToList();
        var componentIds = recipes.SelectMany(r => r.Lines.Select(l => l.ItemId)).Distinct().ToList();
        var allIds = outputIds.Concat(componentIds).Distinct().ToList();
        var items = await db.Items.Where(i => allIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var onHand = (await db.StockLevels.Where(l => allIds.Contains(l.ItemId)).Select(l => new { l.ItemId, l.Quantity }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var openRuns = await db.ProductionRuns.Where(r => r.Status == ProductionStatus.Planned).Select(r => new { r.RecipeId, r.OutputItemId, r.OutputQuantity, r.Batches }).ToListAsync();
        var onOrder = (await db.PurchaseLines.Join(db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent)), l => l.PurchaseDocId, d => d.Id, (l, d) => new { l.ItemId, q = l.Quantity * l.UnitFactor })
            .Where(x => x.ItemId != null && allIds.Contains(x.ItemId!.Value)).ToListAsync()).GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.q));

        // Monthly units sold of each product, from issued invoices (the last 25 months).
        var first = MonthStart(Clock.Today).AddMonths(-24);
        var issued = new[] { SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid };
        var sold = await db.SalesLines.Where(l => l.ItemId != null && outputIds.Contains(l.ItemId!.Value))
            .Join(db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && issued.Contains(d.Status) && d.IssueDate >= first), l => l.SalesDocId, d => d.Id, (l, d) => new { ItemId = l.ItemId!.Value, q = l.Quantity * l.UnitFactor, d.IssueDate })
            .ToListAsync();

        var products = new List<MrpProduct>();
        var gross = new Dictionary<Guid, (decimal Qty, HashSet<string> For)>();
        void Need(Guid item, decimal qty, string forWhat)
        {
            var g = gross.TryGetValue(item, out var have) ? have : (Qty: 0m, For: new HashSet<string>());
            g.For.Add(forWhat);
            gross[item] = (g.Qty + qty, g.For);
        }
        foreach (var r in recipes)
        {
            if (!items.TryGetValue(r.OutputItemId, out var output)) continue;
            var series = new double[25];
            foreach (var s in sold.Where(s => s.ItemId == r.OutputItemId))
            {
                var i = (s.IssueDate.Year - first.Year) * 12 + s.IssueDate.Month - first.Month;
                if (i is >= 0 and < 25) series[i] += (double)s.q;
            }
            var f = Forecaster.Run(series[..^1], months);
            decimal[] demand;
            string from;
            if (f is not null) (demand, from) = (f.Forecast.Select(v => Math.Ceiling((decimal)v)).ToArray(), $"{f.Model} forecast, {f.AccuracyPct ?? 0}% accurate");
            else
            {
                // Not enough history to forecast: assume the average of the months there are.
                var have = series[..^1].Where(v => v > 0).ToList();
                var avg = have.Count == 0 ? 0 : (decimal)have.Average();
                (demand, from) = (Enumerable.Repeat(Math.Ceiling(avg), months).ToArray(), have.Count == 0 ? "No sales yet" : $"Average of {have.Count} month(s) of sales");
            }
            var planned = openRuns.Where(x => x.RecipeId == r.Id).Sum(x => x.OutputQuantity);
            var have_ = onHand.GetValueOrDefault(r.OutputItemId);
            var safety = output.ReorderLevel;
            var toMake = Math.Max(0, Math.Ceiling(demand.Sum() + safety - have_ - planned)); // whole units
            var batches = r.OutputQuantity > 0 ? Math.Ceiling(toMake / r.OutputQuantity) : 0;
            products.Add(new MrpProduct(output.Id, output.Name, output.Unit, r.Id, r.Name, demand, from, have_, planned, safety, toMake, batches, r.OutputQuantity));
            // Materials for what's to be made, and for runs already planned but not yet done.
            var totalBatches = batches + openRuns.Where(x => x.RecipeId == r.Id).Sum(x => x.Batches);
            foreach (var l in r.Lines) if (totalBatches > 0) Need(l.ItemId, l.Quantity * totalBatches, output.Name);
        }
        var orderBy = Clock.Today.AddDays(Math.Max(0, 30 - leadDays));
        var materials = gross.Where(g => items.ContainsKey(g.Key)).Select(g =>
        {
            var it = items[g.Key];
            var have = onHand.GetValueOrDefault(g.Key);
            var coming = onOrder.GetValueOrDefault(g.Key);
            var toBuy = Math.Max(0, Math.Ceiling(g.Value.Qty + it.ReorderLevel - have - coming));
            var unitCost = (long)Math.Round(it.AverageCost);
            return new MrpMaterial(it.Id, it.Name, it.Unit, it.Use.ToString(), Math.Round(g.Value.Qty, 2), have, coming, toBuy, have - g.Value.Qty < 0 ? Clock.Today : orderBy, unitCost, (long)Math.Round(toBuy * unitCost), [.. g.Value.For]);
        }).OrderByDescending(m => m.ToBuy > 0).ThenBy(m => m.OrderBy).ThenByDescending(m => m.Value).ToList();
        return (products, materials);
    }

    public async Task<object> MrpView(int months, int leadDays)
    {
        var (products, materials) = await Mrp(months, leadDays);
        var next = MonthStart(Clock.Today);
        return new
        {
            months = Enumerable.Range(0, Math.Clamp(months, 1, 6)).Select(i => $"{MonthNames[next.AddMonths(i).Month - 1]} {next.AddMonths(i).Year % 100:00}"),
            products, materials, toBuyValue = materials.Sum(m => m.Value), toMake = products.Count(p => p.Batches > 0), leadDays,
        };
    }

    /// <summary>Turns chosen materials into a request from a department: it goes through procurement and finance like any other.</summary>
    public async Task<object> RaiseRequest(MrpRequestInput input)
    {
        if (input.Lines is not { Count: > 0 }) throw new AppException("Choose what to buy.");
        var r = await requisitions.Create(new RequisitionInput(input.DepartmentId, null, "Materials from the material plan (MRP)", null,
            input.Lines.Where(l => l.Quantity > 0).Select(l => new RequisitionLineInput(l.ItemId, null, l.Quantity, null, null)).ToList(), true));
        return new { r.Id, r.Number };
    }

    // ------------------------------------------------------------------------------------------------ capacity

    /// <summary>The next four weeks: maintenance hours planned against the hours technical staff have, week by week.</summary>
    public async Task<object> Capacity()
    {
        var today = Clock.Today;
        var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var live = new[] { WorkOrderStatus.Open, WorkOrderStatus.Planned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold };
        var jobs = await db.WorkOrders.Where(w => live.Contains(w.Status)).Select(w => new { w.EstimatedHours, w.PlannedStart, w.DueDate }).ToListAsync();
        var plans = await db.MaintenancePlans.Where(p => p.Active).Select(p => new { p.NextDue, p.EstimatedHours, p.Id }).ToListAsync();
        var planned = await db.WorkOrders.Where(w => w.MaintenancePlanId != null && live.Contains(w.Status)).Select(w => w.MaintenancePlanId!.Value).ToListAsync();
        var staff = await db.Employees.Where(e => e.EndDate == null || e.EndDate >= today).Select(e => new { e.Id, e.Department, e.JobTitle }).ToListAsync();
        static bool Has(string t, params string[] w) => w.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase));
        var techs = staff.Where(e => Has(e.Department, "maint", "engineer", "workshop", "technical") || Has(e.JobTitle, "technician", "engineer", "mechanic", "fundi", "electrician")).Select(e => e.Id).ToList();
        var leave = await db.LeaveRequests.Where(l => l.Status == LeaveStatus.Approved && techs.Contains(l.EmployeeId) && l.To >= start && l.From <= start.AddDays(27)).Select(l => new { l.From, l.To }).ToListAsync();
        var weeks = Enumerable.Range(0, 4).Select(i =>
        {
            var from = start.AddDays(7 * i);
            var to = from.AddDays(6);
            var demand = jobs.Where(j => (j.PlannedStart ?? j.DueDate ?? today) is var d && (i == 0 ? d <= to : d >= from && d <= to)).Sum(j => j.EstimatedHours)
                + plans.Where(p => !planned.Contains(p.Id) && p.NextDue >= from && p.NextDue <= to).Sum(p => p.EstimatedHours);
            // Eight hours a working day, five days, less approved leave.
            var leaveDays = leave.Sum(l => Enumerable.Range(0, 7).Select(k => from.AddDays(k)).Count(d => d >= l.From && d <= l.To && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
            var available = techs.Count * 40m - leaveDays * 8m;
            return new { week = $"{from:d MMM}", from, demand = Math.Round(demand, 1), available, load = available > 0 ? Math.Round(100 * demand / available) : (decimal?)null };
        }).ToList();
        var assets = await db.Assets.Where(a => a.Status != AssetStatus.Disposed && a.Status != AssetStatus.Lost).Select(a => a.Status).ToListAsync();
        return new
        {
            technicians = techs.Count, weeks,
            availability = assets.Count == 0 ? (double?)null : Math.Round(100.0 * assets.Count(s => s != AssetStatus.UnderRepair) / assets.Count, 1),
            unestimated = jobs.Count(j => j.EstimatedHours == 0),
        };
    }

    // ------------------------------------------------------------------------------------------------ the meeting

    /// <summary>Everything the monthly planning meeting looks at, and this month's decisions.</summary>
    public async Task<object> Meeting(DateOnly? month)
    {
        var m = MonthStart(month ?? Clock.Today);
        var f = (dynamic)await foresight.Foresight();
        var (products, materials) = await Mrp();
        var capacity = await Capacity();
        var cash = await foresight.Cash();
        var burn = await foresight.BudgetBurn();
        var meeting = await db.PlanningMeetings.SingleOrDefaultAsync(x => x.Month == m);
        var past = await db.PlanningMeetings.Where(x => x.AgreedAt != null).OrderByDescending(x => x.Month).Take(6).Select(x => new { x.Id, x.Month, x.AgreedBy, x.AgreedAt, x.Decisions }).ToListAsync();
        var people = new
        {
            headcount = await db.Employees.CountAsync(e => e.EndDate == null || e.EndDate >= Clock.Today),
            openPositions = await db.JobOpenings.CountAsync(j => j.Open),
            onLeave = await db.LeaveRequests.CountAsync(l => l.Status == LeaveStatus.Approved && l.From <= Clock.Today && l.To >= Clock.Today),
        };
        return new
        {
            month = m, label = $"{MonthNames[m.Month - 1]} {m.Year}",
            demand = f.money, assetRisk = f.assets,
            supply = new { toMake = products.Where(p => p.Batches > 0), toBuy = materials.Where(x => x.ToBuy > 0), toBuyValue = materials.Sum(x => x.Value) },
            capacity, money = new { cash, budgets = burn }, people,
            meeting = meeting is null ? null : new { meeting.Id, meeting.Attendees, meeting.Decisions, meeting.AgreedBy, meeting.AgreedAt },
            past, canAgree = ctx.Role is Role.Owner or Role.Accountant,
        };
    }

    /// <summary>Saves the month's decisions; agreeing them (owner or accountant) seals them in the approvals register with the numbers.</summary>
    public async Task<object> SaveMeeting(MeetingInput input)
    {
        var m = MonthStart(input.Month ?? Clock.Today);
        var meeting = await db.PlanningMeetings.SingleOrDefaultAsync(x => x.Month == m);
        if (meeting?.AgreedAt is not null) throw new AppException($"The plan for {MonthNames[m.Month - 1]} was agreed by {meeting.AgreedBy}: it can't be changed. Record changes in next month's meeting.", 409);
        meeting ??= db.PlanningMeetings.Add(new PlanningMeeting { Month = m }).Entity;
        (meeting.Attendees, meeting.Decisions) = ((input.Attendees ?? "").Trim(), (input.Decisions ?? "").Trim());
        if (input.Agree == true)
        {
            if (ctx.Role is not (Role.Owner or Role.Accountant)) throw new AppException("The owner or an accountant agrees the plan.", 403);
            if (meeting.Decisions.Length < 10) throw new AppException("Write down what was decided first.");
            var (products, materials) = await Mrp();
            var cash = await foresight.Cash();
            meeting.Snapshot = JsonSerializer.Serialize(new { cashIn30 = cash.In30, toMake = products.Count(p => p.Batches > 0), toBuyValue = materials.Sum(x => x.Value), capacity = await Capacity() });
            (meeting.AgreedBy, meeting.AgreedAt) = (ctx.Actor, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await register.Record("Plan", meeting.Id, $"Plan {MonthNames[m.Month - 1]} {m.Year}", "Agreed", 0, meeting.Decisions);
        }
        await db.SaveChangesAsync();
        return await Meeting(m);
    }

    // ------------------------------------------------------------------------------------------------ ERP health

    public sealed record Check(string Area, string Key, string Name, double? Value, double Target, string Unit, string Why, string Fix, string Link)
    {
        public double Score => Value is null ? 0 : Math.Clamp(Value.Value / Target * 100, 0, 100);
        public bool Met => Value >= Target;
    }

    /// <summary>How well the business is using its ERP: data, process, controls and planning, each measured.</summary>
    public async Task<object> Health()
    {
        var today = Clock.Today;
        var since90 = DateTimeOffset.UtcNow.AddDays(-90);
        double? Pct(int part, int whole) => whole == 0 ? null : Math.Round(100.0 * part / whole, 1);
        var checks = new List<Check>();

        // Data integrity
        var since180 = DateTimeOffset.UtcNow.AddDays(-180);
        var counted = await db.StockCountLines.Where(l => l.At >= since180).Select(l => new { l.Counted, l.Expected }).ToListAsync();
        checks.Add(new("Data", "stock", "Stock records match the shelf", Pct(counted.Count(l => l.Counted == l.Expected), counted.Count), 95, "%",
            "Planning, costing and reordering all assume the stock figures are right.", counted.Count == 0 ? "Do a blind stock take." : "Count the items that are most often off, more often.", "/stock/takes"));
        var tracked = await db.Items.Where(i => i.TrackStock && i.Active).Select(i => new { i.AverageCost, i.ReorderLevel }).ToListAsync();
        checks.Add(new("Data", "costs", "Stocked items have a cost and a reorder level", Pct(tracked.Count(i => i.AverageCost > 0 && i.ReorderLevel > 0), tracked.Count), 95, "%",
            "Without a cost, margins and stock value are wrong; without a reorder level, nobody is warned.", "Fill in costs (opening stock or bills) and reorder levels.", "/stock"));
        var assets = await db.Assets.Where(a => a.Status != AssetStatus.Disposed && a.Status != AssetStatus.Lost).Select(a => new { a.Id, a.LocationId, a.EmployeeId, a.AssignedTo, a.DepartmentId, a.Criticality }).ToListAsync();
        checks.Add(new("Data", "assets", "Assets have a custodian or place, and a department", Pct(assets.Count(a => (a.LocationId != null || a.EmployeeId != null || a.AssignedTo != "") && a.DepartmentId != null), assets.Count), 95, "%",
            "Someone answers for every asset, and its running costs land on the right department.", "Give each asset a custodian or location, and a department.", "/assets"));
        var suppliers = await db.Suppliers.Select(s => s.KraPin).ToListAsync();
        checks.Add(new("Data", "suppliers", "Suppliers have a KRA PIN", Pct(suppliers.Count(p => p != ""), suppliers.Count), 90, "%",
            "Needed for eTIMS, withholding tax and to stop duplicate or fake suppliers.", "Add the PIN to each supplier.", "/suppliers"));

        // Process discipline
        var orders = await db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && d.Status != PurchaseStatus.Void && d.CreatedAt >= since90).Select(d => new { d.RequisitionId, d.TenderId, d.WorkOrderId }).ToListAsync();
        checks.Add(new("Process", "requests", "Purchases start from a request, tender or job", Pct(orders.Count(o => o.RequisitionId != null || o.TenderId != null || o.WorkOrderId != null), orders.Count), 80, "%",
            "Buying that skips the request skips the budget and the sign-off.", "Ask departments to raise requests; buy from those.", "/procurement"));
        var maintLines = await (from l in db.JournalLines
                                join a in db.Accounts on l.AccountId equals a.Id
                                join e in db.JournalEntries on l.JournalEntryId equals e.Id
                                where a.SystemKey == SystemAccounts.Maintenance && e.Date >= today.AddDays(-90) && l.Debit > 0
                                select new { l.WorkOrderId, l.Debit }).ToListAsync();
        var maintTotal = maintLines.Sum(x => x.Debit);
        checks.Add(new("Process", "workorders", "Repair money is spent through work orders", maintTotal == 0 ? null : Math.Round(100.0 * maintLines.Where(x => x.WorkOrderId != null).Sum(x => x.Debit) / maintTotal, 1), 90, "%",
            "Only then is each asset's true cost known, and repeat failures visible.", "Record repairs as work orders, not loose expenses.", "/maintenance"));
        var pm = await db.WorkOrders.Where(w => w.Type == WorkOrderType.Preventive && w.CompletedAt != null && w.CompletedAt >= since90).Select(w => new { w.CompletedAt, w.DueDate }).ToListAsync();
        checks.Add(new("Process", "pm", "Planned maintenance done on time", Pct(pm.Count(p => p.DueDate is null || DateOnly.FromDateTime(p.CompletedAt!.Value.UtcDateTime) <= p.DueDate), pm.Count), 90, "%",
            "Late services turn into breakdowns.", pm.Count == 0 ? "Set up maintenance plans for the critical assets." : "Schedule plans with lead time; keep parts ready.", "/maintenance"));
        var critical = assets.Where(a => a.Criticality == AssetCriticality.High).Select(a => a.Id).ToList();
        var covered = await db.MaintenancePlans.Where(p => p.Active && p.AssetId != null && critical.Contains(p.AssetId!.Value)).Select(p => p.AssetId!.Value).Distinct().CountAsync();
        checks.Add(new("Process", "plans", "Critical assets have a maintenance plan", Pct(covered, critical.Count), 100, "%",
            "A critical asset without a plan is a breakdown waiting to happen.", "Add a plan (by date or by use) for each critical asset.", "/maintenance"));

        // Controls
        var depts = await db.Departments.Where(d => d.Active).Select(d => d.Id).ToListAsync();
        var budgeted = await db.Budgets.Where(b => b.Year == today.Year && b.Status == BudgetStatus.Approved).Select(b => b.DepartmentId).Distinct().ToListAsync();
        checks.Add(new("Controls", "budgets", "Departments have an approved budget", Pct(depts.Count(d => budgeted.Contains(d)), depts.Count), 100, "%",
            "No budget, no spending: departments without one are stuck.", "Prepare and approve the missing budgets.", "/finance/budgets"));
        var verify = (dynamic)await register.Verify();
        checks.Add(new("Controls", "register", "The approvals register is intact", (bool)verify.intact ? 100 : 0, 100, "%",
            "Proof that no decision about money was changed after it was made.", "Find out what changed at the record the check names.", "/finance/approvals"));
        var finance = await db.Users.Where(u => u.Role == Role.Owner || u.Role == Role.Accountant).Select(u => u.TwoFactorEnabled).ToListAsync();
        checks.Add(new("Controls", "2fa", "Owner and accountants use two-step sign-in", Pct(finance.Count(x => x), finance.Count), 100, "%",
            "These logins can move money.", "Turn it on in Sign-in security.", "/security"));
        var trail = await db.ApprovalRecords.Where(r => r.Subject == "Request" && (r.Action == "Approved by procurement" || r.Action.StartsWith("Signed off"))).Select(r => new { r.SubjectId, r.Action, r.UserId }).ToListAsync();
        var both = trail.GroupBy(r => r.SubjectId).Where(g => g.Any(x => x.Action == "Approved by procurement") && g.Any(x => x.Action.StartsWith("Signed off"))).ToList();
        var separate = both.Count(g => g.First(x => x.Action == "Approved by procurement").UserId != g.First(x => x.Action.StartsWith("Signed off")).UserId);
        checks.Add(new("Controls", "duties", "Procurement and finance are different people", Pct(separate, both.Count), 90, "%",
            "One person approving and signing off the same purchase is how fraud starts.", "Give procurement and finance to different users.", "/settings?tab=team"));

        // Planning
        var firstEntry = await db.JournalEntries.OrderBy(e => e.Date).Select(e => (DateOnly?)e.Date).FirstOrDefaultAsync();
        var months = firstEntry is { } fe ? (today.Year - fe.Year) * 12 + today.Month - fe.Month : 0;
        checks.Add(new("Planning", "history", "Months of records for forecasting", months, 12, " months",
            "Forecasts learn from history; a year lets them see the seasons.", "Bring in past invoices and bills (Import).", "/os"));
        var met = await db.PlanningMeetings.AnyAsync(x => x.Month == MonthStart(today) && x.AgreedAt != null);
        checks.Add(new("Planning", "meeting", "This month's plan agreed", met ? 100 : 0, 100, "%",
            "The monthly planning meeting is where demand, supply, capacity and money are balanced.", "Hold the planning meeting and agree the plan.", "/os/plan"));
        var recipes = await db.Recipes.CountAsync(r => r.Active);
        var madeItems = await db.ProductionRuns.Select(r => r.OutputItemId).Distinct().CountAsync();
        if (madeItems > 0 || recipes > 0)
            checks.Add(new("Planning", "recipes", "Products made have a blueprint", Pct(recipes, Math.Max(recipes, madeItems)), 98, "%",
                "What to make & buy works from blueprints; a missing one means missing materials.", "Add the blueprint for each product made.", "/production"));

        var scored = checks.Where(c => c.Value is not null).ToList();
        var overall = scored.Count == 0 ? 0 : Math.Round(scored.Average(c => c.Score));
        var grade = overall >= 90 ? "A" : overall >= 75 ? "B" : overall >= 50 ? "C" : "D";
        return new
        {
            overall, grade,
            meaning = grade switch
            {
                "A" => "Class A: the system runs the business. Data is right, the process is followed, controls are on and planning is used.",
                "B" => "Class B: the system is well used, with a few gaps. Close the red checks to reach A.",
                "C" => "Class C: the system records what happened, but isn't yet trusted to run the business.",
                _ => "Class D: the system is used for some transactions only. Start with the data and the controls.",
            },
            areas = checks.GroupBy(c => c.Area).Select(g => new { area = g.Key, score = g.Any(c => c.Value is not null) ? Math.Round(g.Where(c => c.Value is not null).Average(c => c.Score)) : (double?)null }),
            checks = checks.Select(c => new { c.Area, c.Key, c.Name, c.Value, c.Target, c.Unit, c.Why, c.Fix, c.Link, score = Math.Round(c.Score), met = c.Met, measured = c.Value is not null }),
        };
    }
}
