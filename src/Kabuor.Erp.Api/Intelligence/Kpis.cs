// The KPI scorecard. The owner sets a target for each measure that matters; the dashboard shows where the business
// is, whether it's on pace to meet the target (a month half gone needs half the month's sales), how it moved over
// the last six periods, and links to the detail. Every value is worked out from the books, stock and CRM.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

/// <summary>What a KPI is: how to read it, which way is good, and the target most businesses start from.</summary>
public sealed record KpiDef(string Key, string Group, string Name, string Unit, bool HigherIsBetter, decimal? DefaultTarget, bool Cumulative, string Link, string Explain);

public sealed class KpiService(ErpDb db, SideBySide reads)
{
    public static readonly KpiDef[] All =
    [
        new("sales", "Sales", "Sales (before VAT)", "ksh", true, null, true, "/reports", "Invoiced in the period, before VAT."),
        new("margin", "Sales", "Gross margin", "%", true, 25, false, "/intelligence?tab=selling", "Sales minus what the goods cost, as a share of sales."),
        new("avgInvoice", "Sales", "Average invoice", "ksh", true, null, false, "/invoices", "Sales divided by the number of invoices."),
        new("newCustomers", "Customers", "New customers", "count", true, null, true, "/intelligence?tab=customers", "Customers whose first invoice was in the period."),
        new("repeat", "Customers", "Repeat buyers", "%", true, 40, false, "/intelligence?tab=customers", "Buyers who bought twice or more in the period."),
        new("conversion", "Customers", "Inquiries that buy", "%", true, 30, false, "/intelligence?tab=inquiries", "Finished inquiries that became a sale."),
        new("response", "Customers", "Time to answer an inquiry", "hours", false, 2, false, "/inquiries", "Median hours to the first reply."),
        new("collected", "Cash", "Money collected", "ksh", true, null, true, "/money-in", "Payments received in the period."),
        new("dso", "Cash", "Days to get paid", "days", false, 30, false, "/intelligence?tab=customers", "What customers owe, in days of sales (DSO)."),
        new("overdue", "Cash", "Overdue share of debts", "%", false, 15, false, "/invoices?status=overdue", "Of everything customers owe, the part past its due date."),
        new("stockDays", "Stock", "Days of stock", "days", false, 45, false, "/intelligence?tab=shelf", "Stock at cost, in days of cost of sales."),
        new("accuracy", "Stock", "Stock count accuracy", "%", true, 98, false, "/intelligence?tab=accuracy", "Counted lines that matched the system exactly."),
        new("stockouts", "Stock", "Selling items out of stock", "count", false, 0, false, "/stock?filter=out", "Items sold in the last 90 days that have none left."),
        new("payroll", "People", "Payroll cost of sales", "%", false, 20, false, "/payroll", "Gross pay plus employer NSSF, Housing Levy and NITA, as a share of sales."),
        new("etims", "Compliance", "Invoices signed by KRA", "%", true, 100, false, "/invoices", "VAT invoices in the period signed with eTIMS."),
        new("pipeline", "Customers", "Open pipeline", "ksh", true, null, false, "/pipeline", "Estimated value of leads not yet won or lost."),
    ];

    static readonly SalesDocStatus[] Issued = [SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid];
    static DateTimeOffset Start(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
    static DateTimeOffset End(DateOnly d) => new(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));

    /// <summary>The period's dates: this month, quarter or year (offset 0), or earlier ones (offset -1, -2…).</summary>
    public static (DateOnly From, DateOnly To) Period(string period, int offset, DateOnly today)
    {
        switch (period)
        {
            case "quarter":
            {
                var q = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(3 * offset);
                return (q, q.AddMonths(3).AddDays(-1));
            }
            case "year":
            {
                var y = new DateOnly(today.Year + offset, 1, 1);
                return (y, y.AddYears(1).AddDays(-1));
            }
            default:
            {
                var m = new DateOnly(today.Year, today.Month, 1).AddMonths(offset);
                return (m, m.AddMonths(1).AddDays(-1));
            }
        }
    }

    public async Task<Dictionary<string, decimal?>> Targets() =>
        ParseTargets(await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => c.KpiTargets).SingleAsync());

    static Dictionary<string, decimal?> ParseTargets(string? raw)
    {
        var saved = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw) as JsonObject ?? [];
        return All.ToDictionary(k => k.Key, k => saved[k.Key] is { } v && decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : k.DefaultTarget);
    }

    public async Task<object> SaveTargets(Dictionary<string, decimal?> input)
    {
        var clean = new JsonObject();
        foreach (var (key, value) in input)
        {
            var def = All.FirstOrDefault(k => k.Key == key) ?? throw new AppException($"No KPI called {key}.");
            if (value is null) continue;
            if (value < 0 || (def.Unit == "%" && value > 100)) throw new AppException($"{def.Name}: enter a target between 0 and {(def.Unit == "%" ? "100" : "any amount")}.");
            // Money targets are entered in shillings and kept in cents, like every other amount.
            clean[key] = def.Unit == "ksh" ? Math.Round(value.Value * 100) : value.Value;
        }
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        c.KpiTargets = clean.ToJsonString();
        await db.SaveChangesAsync();
        return await Targets();
    }

    sealed record Facts(Dictionary<string, decimal?> Values);

    // A scorecard measures seven periods (now, the one before, five more for the trend). These company-wide lists are
    // the same for every period, so they're read once per request instead of seven times.
    List<DateOnly>? firstInvoices;
    List<(long Amount, DateTimeOffset At)>? payments;
    List<(InquiryStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? FirstResponseAt)>? allInquiries;
    List<(decimal Expected, decimal Counted, DateTimeOffset At)>? countLines;

    async Task LoadShared()
    {
        firstInvoices ??= await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status)).GroupBy(d => d.CustomerId).Select(g => g.Min(d => d.IssueDate)).ToListAsync();
        payments ??= (await db.Payments.Select(p => new { p.Amount, p.ReceivedAt }).ToListAsync()).Select(p => (p.Amount, p.ReceivedAt)).ToList();
        allInquiries ??= (await db.Inquiries.Select(i => new { i.Status, i.CreatedAt, i.FirstResponseAt }).ToListAsync()).Select(i => (i.Status, i.CreatedAt, i.FirstResponseAt)).ToList();
        countLines ??= (await db.StockCountLines.Select(l => new { l.Expected, l.Counted, l.At }).ToListAsync()).Select(l => (l.Expected, l.Counted, l.At)).ToList();
    }

    // The invoices, their cost and payroll for the scorecard's whole span (seven periods), read in three queries; each
    // period is then measured from these instead of asking the database again (seven times three round trips, which
    // against a database in another region is most of the page's wait).
    (DateOnly From, DateOnly To)? span;
    List<(Guid Id, Guid CustomerId, long Net, long Total, EtimsStatus Etims, DateOnly IssueDate)> spanDocs = [];
    Dictionary<Guid, long> spanCost = [];
    Dictionary<DateOnly, long> spanPayroll = [];

    async Task LoadSpan(DateOnly spanFrom, DateOnly spanTo)
    {
        var docs = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= spanFrom && d.IssueDate <= spanTo)
            .Select(d => new { d.Id, d.CustomerId, d.Net, d.Total, d.EtimsStatus, d.IssueDate }).ToListAsync();
        spanDocs = docs.Select(d => (d.Id, d.CustomerId, d.Net, d.Total, d.EtimsStatus, d.IssueDate)).ToList();
        spanCost = await (from l in db.SalesLines
                          join d in db.SalesDocs on l.SalesDocId equals d.Id
                          where d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= spanFrom && d.IssueDate <= spanTo
                          group l by l.SalesDocId into g
                          select new { g.Key, Cost = g.Sum(x => (long?)x.Cost) ?? 0 }).ToDictionaryAsync(x => x.Key, x => x.Cost);
        var firstMonth = new DateOnly(spanFrom.Year, spanFrom.Month, 1);
        spanPayroll = (await db.PayrollRuns.Include(r => r.Payslips).Where(r => r.Period >= firstMonth && r.Period <= spanTo && r.Status != PayrollStatus.Draft).ToListAsync())
            .GroupBy(r => r.Period).ToDictionary(g => g.Key, g => g.SelectMany(r => r.Payslips).Sum(p => p.Gross + p.NssfEmployer + p.HousingLevyEmployer + p.Nita));
        span = (spanFrom, spanTo);
    }

    /// <summary>Every KPI's value for one period.</summary>
    async Task<Dictionary<string, decimal?>> Measure(DateOnly from, DateOnly to, bool current)
    {
        var today = Clock.Today;
        var (start, end) = (Start(from), End(to));
        await LoadShared();
        var inSpan = span.HasValue && span.Value.From <= from && to <= span.Value.To;
        if (!inSpan) await LoadSpan(from, to);
        var docs = spanDocs.Where(d => d.IssueDate >= from && d.IssueDate <= to).Select(d => new { d.Id, d.CustomerId, d.Net, d.Total, EtimsStatus = d.Etims }).ToList();
        var cost = docs.Sum(d => spanCost.GetValueOrDefault(d.Id));
        var sales = docs.Sum(d => d.Net);
        var buyers = docs.GroupBy(d => d.CustomerId).ToList();
        var collected = payments!.Where(p => p.At >= start && p.At < end).Sum(p => p.Amount);
        var inquiries = allInquiries!.Where(i => i.CreatedAt >= start && i.CreatedAt < end).ToList();
        var decided = inquiries.Count(i => i.Status is InquiryStatus.Won or InquiryStatus.Lost);
        var answerHours = inquiries.Where(i => i.FirstResponseAt != null).Select(i => (decimal)(i.FirstResponseAt!.Value - i.CreatedAt).TotalHours).OrderBy(h => h).ToList();
        var counts = countLines!.Where(l => l.At >= start && l.At < end).ToList();
        var months = Enumerable.Range(0, (to.Year - from.Year) * 12 + to.Month - from.Month + 1).Select(i => new DateOnly(from.Year, from.Month, 1).AddMonths(i)).ToList();
        var payroll = months.Where(spanPayroll.ContainsKey).ToList();
        var payrollCost = payroll.Sum(m => spanPayroll[m]);
        var vatDocs = docs.Where(d => d.EtimsStatus != EtimsStatus.NotRequired).ToList();

        var v = new Dictionary<string, decimal?>
        {
            ["sales"] = sales,
            // No recorded cost means the margin is unknown; showing 100% would flatter the business.
            ["margin"] = sales == 0 || cost == 0 ? null : Math.Round(100m * (sales - cost) / sales, 1),
            ["avgInvoice"] = docs.Count == 0 ? null : Math.Round((decimal)sales / docs.Count),
            ["newCustomers"] = firstInvoices!.Count(f => f >= from && f <= to),
            ["repeat"] = buyers.Count == 0 ? null : Math.Round(100m * buyers.Count(g => g.Count() >= 2) / buyers.Count, 1),
            ["conversion"] = decided == 0 ? null : Math.Round(100m * inquiries.Count(i => i.Status == InquiryStatus.Won) / decided, 1),
            ["response"] = answerHours.Count == 0 ? null : Math.Round(answerHours[answerHours.Count / 2], 1),
            ["collected"] = collected,
            ["accuracy"] = counts.Count == 0 ? null : Math.Round(100m * counts.Count(l => l.Counted == l.Expected) / counts.Count, 1),
            ["payroll"] = sales == 0 || payroll.Count == 0 ? null : Math.Round(100m * payrollCost / sales, 1),
            ["etims"] = vatDocs.Count == 0 ? null : Math.Round(100m * vatDocs.Count(d => d.EtimsStatus == EtimsStatus.Signed) / vatDocs.Count, 1),
        };
        // Balances that only make sense "now": receivables, stock and the pipeline.
        if (current)
        {
            var open = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid)).Select(d => new { balance = d.Total - d.Paid, d.DueDate }).ToListAsync();
            var owed = open.Sum(o => o.balance);
            var last90 = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate > today.AddDays(-90)).SumAsync(d => (long?)d.Total) ?? 0;
            v["dso"] = last90 == 0 ? null : Math.Round(owed / (last90 / 90m), 0);
            v["overdue"] = owed == 0 ? 0 : Math.Round(100m * open.Where(o => o.DueDate < today).Sum(o => o.balance) / owed, 1);
            var items = await db.Items.Where(i => i.TrackStock && i.Active).Select(i => new { i.Id, i.AverageCost }).ToListAsync();
            var levels = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
            var stockValue = items.Sum(i => Math.Max(0, levels.GetValueOrDefault(i.Id)) * i.AverageCost);
            var recentIds = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status) && d.IssueDate > today.AddDays(-90)).Select(d => d.Id).ToListAsync();
            var cogs90 = await db.SalesLines.Where(l => recentIds.Contains(l.SalesDocId)).SumAsync(l => (long?)l.Cost) ?? 0;
            v["stockDays"] = cogs90 == 0 ? null : Math.Round(stockValue / (cogs90 / 90m), 0);
            var soldItems = await db.SalesLines.Where(l => l.ItemId != null && recentIds.Contains(l.SalesDocId)).Select(l => l.ItemId!.Value).Distinct().ToListAsync();
            v["stockouts"] = items.Count(i => soldItems.Contains(i.Id) && levels.GetValueOrDefault(i.Id) <= 0);
            v["pipeline"] = await db.Leads.Where(l => l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost).SumAsync(l => (long?)l.EstimatedValue) ?? 0;
        }
        return v;
    }

    public async Task<object> Scorecard(string period, int offset)
    {
        period = period is "quarter" or "year" ? period : "month";
        var today = Clock.Today;
        var (from, to) = Period(period, offset, today);
        var current = offset == 0;
        var shownTo = current && to > today ? today : to;
        // The whole span at once: from the oldest trend period to the end of this one (see LoadSpan).
        await LoadSpan(Period(period, offset - 5, today).From, shownTo);
        var now = await Measure(from, shownTo, current);
        var (pFrom, pTo) = Period(period, offset - 1, today);
        // Like for like: this month so far against the same days of last month.
        var prev = await Measure(pFrom, current ? pFrom.AddDays(shownTo.DayNumber - from.DayNumber) : pTo, false);
        var history = new List<(DateOnly From, Dictionary<string, decimal?> Values)>();
        for (var i = -5; i <= 0; i++)
        {
            var (hf, ht) = Period(period, offset + i, today);
            history.Add((hf, i == 0 ? now : await Measure(hf, ht, false)));
        }
        var targets = await Targets();
        var elapsed = Math.Clamp((decimal)(shownTo.DayNumber - from.DayNumber + 1) / (to.DayNumber - from.DayNumber + 1), 0.01m, 1m);
        var kpis = All.Select(k =>
        {
            var value = now.GetValueOrDefault(k.Key);
            // Running-total targets are monthly: a quarter needs three months' worth, a year twelve.
            var target = targets.GetValueOrDefault(k.Key) is { } raw && k.Cumulative ? raw * (period == "year" ? 12 : period == "quarter" ? 3 : 1) : targets.GetValueOrDefault(k.Key);
            // A running total (sales, money collected, new customers) is judged against the share of the period gone.
            var due = target is { } t && k.Cumulative ? Math.Round(t * elapsed) : target;
            string status = value is null || due is null ? "none"
                : k.HigherIsBetter ? (value >= due ? "met" : value >= due * 0.9m ? "close" : "behind")
                : (value <= due ? "met" : value <= (due == 0 ? 1 : due * 1.1m) ? "close" : "behind");
            var before = prev.GetValueOrDefault(k.Key);
            return new
            {
                k.Key, k.Group, k.Name, k.Unit, k.HigherIsBetter, k.Link, k.Explain, value, target, dueByNow = k.Cumulative ? due : null, status,
                previous = before, change = value is { } a && before is { } b && b != 0 ? Math.Round(100m * (a - b) / Math.Abs(b), 1) : (decimal?)null,
                projected = k.Cumulative && current && value is { } x ? Math.Round(x / elapsed) : (decimal?)null,
                trend = history.Select(h => new { from = h.From, value = h.Values.GetValueOrDefault(k.Key) }),
            };
        }).ToList();
        return new
        {
            period, offset, from, to, shownTo, elapsedShare = Math.Round(elapsed * 100, 0),
            met = kpis.Count(k => k.status == "met"), close = kpis.Count(k => k.status == "close"), behind = kpis.Count(k => k.status == "behind"), withoutTarget = kpis.Count(k => k.status == "none"),
            kpis,
        };
    }

    /// <summary>The owner's charts: twelve months of sales against target and cash in against cash out, what
    /// customers owe by age, where sales come from, the best sellers, and how inquiries turn into sales.</summary>
    public async Task<object> Charts(int monthCount = 12)
    {
        monthCount = Math.Clamp(monthCount, 3, 24);
        var today = Clock.Today;
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(1 - monthCount);
        var months = Enumerable.Range(0, monthCount).Select(i => first.AddMonths(i)).ToList();
        var (start, end) = (Start(first), End(today));
        var since = today.AddDays(-89);
        var (s90, e90) = (Start(since), End(today));
        // Dated rows are filtered in the database where it can (PostgreSQL), and again here for SQLite.
        var pg = reads.Postgres;

        // Everything below is read side by side (see SideBySide), then put together here.
        var docsTask = reads.Read(d => d.SalesDocs.Where(x => x.Kind == SalesDocKind.Invoice && !x.Opening && Issued.Contains(x.Status) && x.IssueDate >= first && x.IssueDate <= today)
            .Select(x => new { x.Id, x.IssueDate, x.Net, x.Total, x.Channel, x.CustomerId }).ToListAsync());
        var linesTask = reads.Read(d => d.SalesLines.Join(d.SalesDocs, l => l.SalesDocId, x => x.Id, (l, x) => new { l, x })
            .Where(j => j.x.Kind == SalesDocKind.Invoice && !j.x.Opening && Issued.Contains(j.x.Status) && j.x.IssueDate >= first && j.x.IssueDate <= today)
            .Select(j => new { j.l.SalesDocId, j.l.ItemId, j.l.Description, j.l.Net, j.l.Cost, j.l.Quantity }).ToListAsync());
        var paymentsTask = reads.Read(d => (pg ? d.Payments.Where(p => p.ReceivedAt >= start && p.ReceivedAt < end) : d.Payments).Select(p => new { p.Amount, p.ReceivedAt }).ToListAsync());
        var suppliersTask = reads.Read(d => (pg ? d.SupplierPayments.Where(p => p.PaidAt >= start && p.PaidAt < end) : d.SupplierPayments).Select(p => new { p.Amount, p.PaidAt }).ToListAsync());
        var payrollTask = reads.Read(d => (pg ? d.PayrollRuns.Where(r => r.PaidAt >= start && r.PaidAt < end) : d.PayrollRuns.Where(r => r.PaidAt != null))
            .Select(r => new { r.PaidAt, amount = r.Payslips.Sum(p => p.Net) }).ToListAsync());
        var targetsTask = reads.Read(d => d.Companies.Where(c => c.Id == d.CompanyId).Select(c => c.KpiTargets).SingleAsync());
        var openTask = reads.Read(d => d.SalesDocs.Where(x => x.Kind == SalesDocKind.Invoice && (x.Status == SalesDocStatus.Sent || x.Status == SalesDocStatus.PartPaid))
            .Select(x => new { balance = x.Total - x.Paid, x.DueDate }).ToListAsync());
        var acquiredTask = reads.Read(d => d.Customers.Where(c => c.AcquisitionChannel != null).Select(c => new { c.Id, c.AcquisitionChannel }).ToListAsync());
        var visitsTask = reads.Read(d => (pg ? d.LinkVisits.Where(v => v.At >= s90 && v.At < e90) : d.LinkVisits).Select(v => new { v.VisitorId, v.At }).ToListAsync());
        var inquiriesTask = reads.Read(d => (pg ? d.Inquiries.Where(i => i.CreatedAt >= s90 && i.CreatedAt < e90) : d.Inquiries).Select(i => new { i.Status, i.CreatedAt, i.QuoteId, i.FirstResponseAt }).ToListAsync());
        await Task.WhenAll(docsTask, linesTask, paymentsTask, suppliersTask, payrollTask, targetsTask, openTask, acquiredTask, visitsTask, inquiriesTask);

        var docs = docsTask.Result;
        var lines = linesTask.Result;
        var costByDoc = lines.GroupBy(l => l.SalesDocId).ToDictionary(g => g.Key, g => g.Sum(l => l.Cost));
        var paymentsIn = paymentsTask.Result.Where(p => p.ReceivedAt >= start && p.ReceivedAt < end).ToList();
        var toSuppliers = suppliersTask.Result.Where(p => p.PaidAt >= start && p.PaidAt < end).ToList();
        var toStaff = payrollTask.Result.Where(r => r.PaidAt >= start && r.PaidAt < end).ToList();
        DateOnly Month(DateTimeOffset at) { var d = DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(3)).DateTime); return new(d.Year, d.Month, 1); }
        var target = ParseTargets(targetsTask.Result).GetValueOrDefault("sales");

        var monthly = months.Select(m =>
        {
            var inMonth = docs.Where(d => d.IssueDate.Year == m.Year && d.IssueDate.Month == m.Month).ToList();
            var sales = inMonth.Sum(d => d.Net);
            var cost = inMonth.Sum(d => costByDoc.GetValueOrDefault(d.Id));
            return new
            {
                month = m, sales, target, invoices = inMonth.Count,
                grossProfit = sales - cost,
                moneyIn = paymentsIn.Where(p => Month(p.ReceivedAt) == m).Sum(p => p.Amount),
                moneyOut = toSuppliers.Where(p => Month(p.PaidAt) == m).Sum(p => p.Amount) + toStaff.Where(p => Month(p.PaidAt!.Value) == m).Sum(p => p.amount),
            };
        }).ToList();

        // What customers owe, by how late it is.
        var open = openTask.Result;
        (string Label, int Min, int Max)[] buckets = [("Not yet due", int.MinValue, 0), ("1–30 days late", 1, 30), ("31–60 days late", 31, 60), ("61–90 days late", 61, 90), ("Over 90 days late", 91, int.MaxValue)];
        var ageing = buckets.Select(b =>
        {
            var inBucket = open.Where(o => { var late = today.DayNumber - o.DueDate.DayNumber; return late >= b.Min && late <= b.Max; }).ToList();
            return new { bucket = b.Label, amount = inBucket.Sum(o => o.balance), invoices = inBucket.Count };
        }).ToList();

        // Where the last 90 days of sales came from: the invoice's channel, else how the customer first found us.
        var recent = docs.Where(d => d.IssueDate >= since).ToList();
        var acquired = acquiredTask.Result.ToDictionary(c => c.Id, c => c.AcquisitionChannel);
        var channels = recent.GroupBy(d => (d.Channel ?? acquired.GetValueOrDefault(d.CustomerId))?.ToString() ?? "Direct / unknown")
            .Select(g => new { channel = g.Key, sales = g.Sum(d => d.Net), invoices = g.Count() }).OrderByDescending(x => x.sales).ToList();
        var recentIds = recent.Select(d => d.Id).ToHashSet();
        var products = lines.Where(l => recentIds.Contains(l.SalesDocId)).GroupBy(l => l.ItemId?.ToString() ?? l.Description)
            .Select(g => new { name = g.First().Description, sales = g.Sum(l => l.Net), grossProfit = g.Sum(l => l.Net - l.Cost), quantity = g.Sum(l => l.Quantity) })
            .OrderByDescending(x => x.sales).Take(8).ToList();

        // Inquiries from the last 90 days, step by step to a sale; visits are tracking-link clicks.
        var visits = visitsTask.Result.Where(v => v.At >= s90 && v.At < e90).Select(v => v.VisitorId).Distinct().Count();
        var inquiries = inquiriesTask.Result.Where(i => i.CreatedAt >= s90 && i.CreatedAt < e90).ToList();
        var funnel = new[]
        {
            new { step = "Visited a link", count = visits },
            new { step = "Asked a question", count = inquiries.Count },
            new { step = "Got an answer", count = inquiries.Count(i => i.FirstResponseAt != null || i.Status != InquiryStatus.Open) },
            new { step = "Got a quote", count = inquiries.Count(i => i.QuoteId != null || i.Status is InquiryStatus.Quoted or InquiryStatus.Won) },
            new { step = "Bought", count = inquiries.Count(i => i.Status == InquiryStatus.Won) },
        };

        return new { from = first, to = today, salesTarget = target, monthly, ageing, owed = open.Sum(o => o.balance), channels, products, funnel };
    }
}
