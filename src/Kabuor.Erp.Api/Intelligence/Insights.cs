// Kabuor Intelligence: the business's own data, turned into answers to the owner's questions. Every figure is
// worked out from the books, stock, CRM and tracking links, never estimated by the AI; the assistant and agents
// read these same answers through their tools. All money is in cents.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Kabuor.Erp.Intelligence;

public sealed class InsightsService(ErpDb db, LinkService links, IMemoryCache memoryCache, AppCache appCache, RequestContext ctx)
{
    static readonly SalesDocStatus[] Issued = [SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid];

    static DateTimeOffset Start(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
    static DateTimeOffset End(DateOnly d) => new(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));

    sealed record SoldLine(Guid? ItemId, string Description, decimal Qty, long Revenue, long Cost, DateOnly Date, Guid CustomerId, Guid DocId);

    async Task<List<SoldLine>> Sold(DateOnly from, DateOnly to) =>
        await db.SalesLines.Join(db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= from && d.IssueDate <= to),
                l => l.SalesDocId, d => d.Id, (l, d) => new SoldLine(l.ItemId, l.Description, l.Quantity * l.UnitFactor, l.Total, l.Cost, d.IssueDate, d.CustomerId, d.Id))
            .ToListAsync();

    // ---------- leads: where customers come from ----------

    /// <summary>Leads in a period: the funnel, what each source brings in (leads, wins, money), how long winning takes,
    /// why leads are lost, and the money won customers have spent since.</summary>
    public async Task<object> Leads(DateOnly from, DateOnly to)
    {
        var start = Start(from);
        var end = End(to);
        var leads = await db.Leads.Where(l => l.CreatedAt >= start && l.CreatedAt < end)
            .Select(l => new { l.Id, l.Source, l.Stage, l.EstimatedValue, l.CreatedAt, l.StageChangedAt, l.CustomerId, l.LostReason, l.OwnerName }).ToListAsync();
        var customerIds = leads.Where(l => l.CustomerId != null).Select(l => l.CustomerId!.Value).Distinct().ToList();
        var revenue = (await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status) && customerIds.Contains(d.CustomerId)).Select(d => new { d.CustomerId, d.Net }).ToListAsync())
            .GroupBy(d => d.CustomerId).ToDictionary(g => g.Key, g => g.Sum(x => x.Net));
        var stages = new[] { LeadStage.New, LeadStage.Contacted, LeadStage.Quoted, LeadStage.Won };
        // Reached a stage = is at it, or past it (won counts as having been contacted and quoted).
        int Reached(LeadStage s) => leads.Count(l => l.Stage != LeadStage.Lost ? (int)l.Stage >= (int)s : s == LeadStage.New);
        var won = leads.Where(l => l.Stage == LeadStage.Won).ToList();
        var closed = leads.Count(l => l.Stage is LeadStage.Won or LeadStage.Lost);
        return new
        {
            total = leads.Count, won = won.Count, lost = leads.Count(l => l.Stage == LeadStage.Lost), open = leads.Count(l => l.Stage is not (LeadStage.Won or LeadStage.Lost)),
            winRate = closed == 0 ? (double?)null : Math.Round(100.0 * won.Count / closed),
            pipeline = leads.Where(l => l.Stage is not (LeadStage.Won or LeadStage.Lost)).Sum(l => l.EstimatedValue),
            daysToWin = won.Count == 0 ? (double?)null : Math.Round(won.Average(l => (l.StageChangedAt - l.CreatedAt).TotalDays), 1),
            wonRevenue = won.Sum(l => l.CustomerId is { } c ? revenue.GetValueOrDefault(c) : 0),
            funnel = stages.Select(s => new { stage = s.ToString(), count = Reached(s) }),
            sources = leads.GroupBy(l => l.Source).Select(g =>
            {
                var w = g.Where(l => l.Stage == LeadStage.Won).ToList();
                var c = g.Count(l => l.Stage is LeadStage.Won or LeadStage.Lost);
                return new { source = g.Key.ToString(), leads = g.Count(), won = w.Count, winRate = c == 0 ? (double?)null : Math.Round(100.0 * w.Count / c), revenue = w.Sum(l => l.CustomerId is { } cid ? revenue.GetValueOrDefault(cid) : 0) };
            }).OrderByDescending(x => x.revenue).ThenByDescending(x => x.leads),
            lostReasons = leads.Where(l => l.Stage == LeadStage.Lost).GroupBy(l => string.IsNullOrWhiteSpace(l.LostReason) ? "No reason given" : l.LostReason.Trim()).Select(g => new { reason = g.Key, count = g.Count() }).OrderByDescending(x => x.count).Take(6),
            owners = leads.GroupBy(l => string.IsNullOrWhiteSpace(l.OwnerName) ? "Unassigned" : l.OwnerName).Select(g => new { owner = g.Key, leads = g.Count(), won = g.Count(l => l.Stage == LeadStage.Won) }).OrderByDescending(x => x.won),
        };
    }

    // ---------- what sells ----------

    public async Task<object> Selling(DateOnly from, DateOnly to)
    {
        var sold = await Sold(from, to);
        var items = await db.Items.ToDictionaryAsync(i => i.Id);
        var rows = sold.GroupBy(s => s.ItemId?.ToString() ?? s.Description).Select(g =>
        {
            var item = g.First().ItemId is { } id ? items.GetValueOrDefault(id) : null;
            var revenue = g.Sum(x => x.Revenue);
            var cost = g.Sum(x => x.Cost);
            return new
            {
                itemId = item?.Id, name = item?.Name ?? g.First().Description, unit = item?.Unit ?? "", quantity = g.Sum(x => x.Qty), revenue, cost,
                margin = item?.TrackStock == true ? revenue - cost : (long?)null, customers = g.Select(x => x.CustomerId).Distinct().Count(), invoices = g.Select(x => x.DocId).Distinct().Count(),
            };
        }).OrderByDescending(r => r.revenue).ToList();
        var total = rows.Sum(r => r.revenue);
        return new { from, to, total, items = rows.Take(25).Select(r => new { r.itemId, r.name, r.unit, r.quantity, r.revenue, r.margin, r.customers, r.invoices, share = total == 0 ? 0 : Math.Round(100.0 * r.revenue / total, 1) }) };
    }

    // ---------- what sits on the shelf ----------

    public async Task<object> Shelf()
    {
        var today = Clock.Today;
        var sold = await Sold(today.AddDays(-90), today);
        var items = await db.Items.Where(i => i.TrackStock && i.Active).ToListAsync();
        var onHand = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var lastSold = (await db.SalesLines.Where(l => l.ItemId != null).Join(db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status)), l => l.SalesDocId, d => d.Id, (l, d) => new { l.ItemId, d.IssueDate }).ToListAsync())
            .GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.Max(x => x.IssueDate));
        var rows = items.Select(i =>
        {
            var have = onHand.GetValueOrDefault(i.Id);
            var qty90 = sold.Where(s => s.ItemId == i.Id).Sum(s => s.Qty);
            var perDay = qty90 / 90m;
            var cover = perDay > 0 ? (int?)Math.Round(have / perDay) : null;
            var state = have <= 0 ? "none" : qty90 == 0 ? "dead" : cover > 120 ? "slow" : cover < 14 ? "fast" : "ok";
            return new
            {
                i.Id, i.Name, i.Unit, onHand = have, value = (long)Math.Round(Math.Max(0, have) * i.AverageCost), sold90 = qty90, daysOfCover = cover,
                lastSold = lastSold.TryGetValue(i.Id, out var d) ? d : (DateOnly?)null, state,
            };
        }).ToList();
        var stuck = rows.Where(r => r.state is "dead" or "slow").OrderByDescending(r => r.value).ToList();
        return new
        {
            stockValue = rows.Sum(r => r.value), tiedUp = stuck.Sum(r => r.value), dead = rows.Count(r => r.state == "dead"), slow = rows.Count(r => r.state == "slow"), fast = rows.Count(r => r.state == "fast"),
            stuck = stuck.Take(25), fastest = rows.Where(r => r.state == "fast").OrderBy(r => r.daysOfCover).Take(10),
        };
    }

    // ---------- what to buy more of ----------

    public async Task<object> BuyMore(int leadDays = 7, int safetyDays = 7)
    {
        var today = Clock.Today;
        var sold = await Sold(today.AddDays(-60), today);
        var items = await db.Items.Where(i => i.TrackStock && i.Active).ToListAsync();
        var onHand = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var onOrder = (await db.PurchaseLines.Where(l => l.ItemId != null).Join(db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent)), l => l.PurchaseDocId, d => d.Id, (l, d) => new { l.ItemId, q = l.Quantity * l.UnitFactor }).ToListAsync())
            .GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.q));
        var lastBuy = (await db.PurchaseLines.Where(l => l.ItemId != null).Join(db.PurchaseDocs.Where(d => d.Status != PurchaseStatus.Void && d.Status != PurchaseStatus.Draft), l => l.PurchaseDocId, d => d.Id, (l, d) => new { l.ItemId, d.SupplierId, d.IssueDate, cost = l.UnitCost }).ToListAsync())
            .GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IssueDate).First());
        var suppliers = await db.Suppliers.ToDictionaryAsync(s => s.Id, s => s.Name);
        // Asked for in inquiries while out of stock: demand that is being turned away.
        var since = DateTimeOffset.UtcNow.AddDays(-60);
        var asked = await db.Inquiries.AsNoTracking().Where(q => q.ItemId != null && q.CreatedAt >= since).Select(q => new { q.ItemId, q.CreatedAt, q.Status, q.LostReason }).ToListAsync();
        var rows = items.Select(i =>
        {
            var perDay = sold.Where(s => s.ItemId == i.Id).Sum(s => s.Qty) / 60m;
            var have = onHand.GetValueOrDefault(i.Id);
            var coming = onOrder.GetValueOrDefault(i.Id);
            var target = Math.Ceiling(perDay * (leadDays + safetyDays));
            var buy = Math.Max(0, target - have - coming);
            var last = lastBuy.GetValueOrDefault(i.Id);
            return new
            {
                i.Id, i.Name, i.Brand, i.Unit, perDay = Math.Round(perDay, 2), onHand = have, onOrder = coming, daysLeft = perDay > 0 ? (int?)Math.Floor(Math.Max(0, have) / perDay) : null,
                buy, estimatedCost = last is null ? (long?)null : (long)Math.Round(buy * last.cost), supplier = last is null ? null : suppliers.GetValueOrDefault(last.SupplierId),
                turnedAway = asked.Count(q => q.ItemId == i.Id && (q.LostReason == "Out of stock" || have <= 0)),
            };
        }).ToList();
        var needed = rows.Where(r => r.buy > 0).OrderBy(r => r.daysLeft ?? int.MaxValue).ToList();
        return new { leadDays, safetyDays, count = needed.Count, totalCost = needed.Sum(r => r.estimatedCost ?? 0), items = needed.Take(30), turnedAway = rows.Where(r => r.turnedAway > 0).OrderByDescending(r => r.turnedAway).Take(10) };
    }

    // ---------- who buys ----------

    /// <summary>Recency, frequency and money over the last year, scored 1–5 against the other customers, with segments.</summary>
    public async Task<object> Customers()
    {
        var today = Clock.Today;
        var docs = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= today.AddDays(-365))
            .Select(d => new { d.Id, d.CustomerId, d.IssueDate, d.Total, d.Paid }).ToListAsync();
        var customers = await db.Customers.ToDictionaryAsync(c => c.Id);
        // How long they take to pay: invoice date to the day the money that settled it was received.
        var payDays = (await db.PaymentAllocations.Join(db.Payments, a => a.PaymentId, p => p.Id, (a, p) => new { a.SalesDocId, p.ReceivedAt }).ToListAsync())
            .Join(docs, a => a.SalesDocId, d => d.Id, (a, d) => new { d.CustomerId, days = Math.Max(0, DateOnly.FromDateTime(a.ReceivedAt.ToOffset(TimeSpan.FromHours(3)).DateTime).DayNumber - d.IssueDate.DayNumber) })
            .GroupBy(x => x.CustomerId).ToDictionary(g => g.Key, g => Math.Round(g.Average(x => x.days), 1));
        var stats = docs.GroupBy(d => d.CustomerId).Select(g => new
        {
            id = g.Key, recency = today.DayNumber - g.Max(d => d.IssueDate).DayNumber, frequency = g.Count(), monetary = g.Sum(d => d.Total), owes = g.Sum(d => d.Total - d.Paid),
        }).ToList();
        int Score<T>(IEnumerable<T> all, T v, bool lowIsGood) where T : IComparable<T>
        {
            var sorted = all.OrderBy(x => x).ToList();
            if (sorted.Count == 0) return 3;
            var rank = sorted.FindIndex(x => x.CompareTo(v) >= 0);
            var q = (int)Math.Ceiling(5.0 * (rank + 1) / sorted.Count);
            return lowIsGood ? 6 - Math.Clamp(q, 1, 5) : Math.Clamp(q, 1, 5);
        }
        var rows = stats.Select(s =>
        {
            var (r, f, m) = (Score(stats.Select(x => x.recency), s.recency, true), Score(stats.Select(x => x.frequency), s.frequency, false), Score(stats.Select(x => x.monetary), s.monetary, false));
            var c = customers.GetValueOrDefault(s.id);
            var isNew = c is not null && c.CreatedAt > DateTimeOffset.UtcNow.AddDays(-30);
            var segment = isNew && s.frequency <= 2 ? "New" : r >= 4 && f >= 4 ? "Champions" : r <= 2 && f >= 3 ? "At risk" : r <= 2 ? "Sleeping" : f >= 4 ? "Loyal" : m >= 5 ? "Big spenders" : "Promising";
            return new
            {
                s.id, name = c?.Name ?? "", phone = c?.Phone ?? "", s.recency, s.frequency, s.monetary, s.owes, r, f, m, segment,
                averageDaysToPay = payDays.TryGetValue(s.id, out var d) ? d : (double?)null, channel = c?.AcquisitionChannel?.ToString(),
            };
        }).OrderByDescending(x => x.monetary).ToList();
        var segments = new[] { "Champions", "Loyal", "Big spenders", "Promising", "New", "At risk", "Sleeping" }
            .Select(s => new { segment = s, customers = rows.Count(x => x.segment == s), revenue = rows.Where(x => x.segment == s).Sum(x => x.monetary) });
        var neverBought = customers.Keys.Except(stats.Select(s => s.id)).Count();
        return new { totalRevenue = rows.Sum(x => x.monetary), buyers = rows.Count, neverBought, segments, top = rows.Take(25), atRisk = rows.Where(x => x.segment == "At risk").Take(15), slowPayers = rows.Where(x => x.averageDaysToPay > 30).OrderByDescending(x => x.averageDaysToPay).Take(10) };
    }

    /// <summary>One customer, profiled: their segment, value, habits, what they buy, and where they came from.</summary>
    public async Task<object> Customer(Guid id)
    {
        var all = (dynamic)await Customers();
        var c = await db.Customers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new Common.AppException("Not found.", 404);
        var mine = ((IEnumerable<dynamic>)all.top).Concat((IEnumerable<dynamic>)all.atRisk).FirstOrDefault(x => x.id == id);
        var today = Clock.Today;
        var lines = (await Sold(today.AddDays(-365), today)).Where(s => s.CustomerId == id).ToList();
        var items = await db.Items.ToDictionaryAsync(i => i.Id, i => i.Name);
        var link = c.FirstLinkId is { } lid ? await db.TrackingLinks.SingleOrDefaultAsync(l => l.Id == lid) : null;
        var inquiries = await db.Inquiries.Where(i => i.CustomerId == id).Select(i => new { i.Status }).ToListAsync();
        var firstInvoice = await db.SalesDocs.Where(d => d.CustomerId == id && d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status)).OrderBy(d => d.IssueDate).Select(d => (DateOnly?)d.IssueDate).FirstOrDefaultAsync();
        return new
        {
            segment = (string?)mine?.segment ?? (lines.Count == 0 ? "Never bought" : "Promising"),
            lifetimeValue = await db.SalesDocs.Where(d => d.CustomerId == id && d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status)).SumAsync(d => d.Total),
            yearValue = lines.Sum(l => l.Revenue), invoicesThisYear = lines.Select(l => l.DocId).Distinct().Count(), averageDaysToPay = (double?)mine?.averageDaysToPay, firstBought = firstInvoice,
            favourites = lines.Where(l => l.ItemId != null).GroupBy(l => l.ItemId!.Value).Select(g => new { name = items.GetValueOrDefault(g.Key, ""), quantity = g.Sum(x => x.Qty), revenue = g.Sum(x => x.Revenue) }).OrderByDescending(x => x.revenue).Take(5),
            cameFrom = new { channel = c.AcquisitionChannel?.ToString(), link = link?.Name, by = link?.OwnerName },
            inquiries = new { total = inquiries.Count, won = inquiries.Count(i => i.Status == InquiryStatus.Won), lost = inquiries.Count(i => i.Status == InquiryStatus.Lost) },
            lastPortalVisit = c.PortalLastSeenAt,
        };
    }

    // ---------- why people ask but don't buy ----------

    public async Task<object> Inquiries(DateOnly from, DateOnly to)
    {
        var (start, end) = (Start(from), End(to));
        var all = (await db.Inquiries.ToListAsync()).Where(i => i.CreatedAt >= start && i.CreatedAt < end).ToList();
        var items = await db.Items.ToDictionaryAsync(i => i.Id, i => i.Name);
        double? Rate(IEnumerable<Inquiry> s) { var l = s.ToList(); var done = l.Count(i => i.Status is InquiryStatus.Won or InquiryStatus.Lost); return done == 0 ? null : Math.Round(100.0 * l.Count(i => i.Status == InquiryStatus.Won) / done, 1); }
        var responded = all.Where(i => i.FirstResponseAt != null).Select(i => (i.FirstResponseAt!.Value - i.CreatedAt).TotalHours).OrderBy(h => h).ToList();
        string Bucket(Inquiry i) => i.FirstResponseAt is null ? "Never answered" : (i.FirstResponseAt.Value - i.CreatedAt).TotalHours switch { < 1 => "Within an hour", < 24 => "Same day", _ => "After a day" };
        return new
        {
            from, to, total = all.Count, open = all.Count(i => i.Status is InquiryStatus.Open or InquiryStatus.Answered or InquiryStatus.Quoted),
            won = all.Count(i => i.Status == InquiryStatus.Won), lost = all.Count(i => i.Status == InquiryStatus.Lost), conversion = Rate(all),
            medianHoursToAnswer = responded.Count == 0 ? (double?)null : Math.Round(responded[responded.Count / 2], 1),
            lostReasons = all.Where(i => i.Status == InquiryStatus.Lost).GroupBy(i => i.LostReason.Length > 0 ? i.LostReason : "No reason given").Select(g => new { reason = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
            bySpeed = all.GroupBy(Bucket).Select(g => new { answered = g.Key, inquiries = g.Count(), conversion = Rate(g) }),
            byChannel = all.GroupBy(i => i.Channel).Select(g => new { channel = g.Key.ToString(), inquiries = g.Count(), conversion = Rate(g) }).OrderByDescending(x => x.inquiries),
            products = all.Where(i => i.ItemId != null).GroupBy(i => i.ItemId!.Value).Select(g => new { name = items.GetValueOrDefault(g.Key, ""), asked = g.Count(), conversion = Rate(g) }).OrderByDescending(x => x.asked).Take(10),
            unanswered = all.Count(i => i.FirstResponseAt == null && i.Status == InquiryStatus.Open),
        };
    }

    // ---------- which channel brings people ----------

    public async Task<object> Channels(DateOnly from, DateOnly to)
    {
        var perf = await links.Performance(from, to);
        var allLinks = await db.TrackingLinks.ToDictionaryAsync(l => l.Id);
        var (start, end) = (Start(from), End(to));
        var inquiries = (await db.Inquiries.Select(i => new { i.Channel, i.CreatedAt }).ToListAsync()).Where(i => i.CreatedAt >= start && i.CreatedAt < end).ToList();
        var newCustomers = (await db.Customers.Select(c => new { c.AcquisitionChannel, c.CreatedAt }).ToListAsync()).Where(c => c.CreatedAt >= start && c.CreatedAt < end).ToList();
        var customers = await db.Customers.ToDictionaryAsync(c => c.Id, c => c.AcquisitionChannel);
        var sales = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= from && d.IssueDate <= to).Select(d => new { d.Channel, d.CustomerId, d.Total }).ToListAsync();
        string Name(Channel? c) => c?.ToString() ?? "Unknown / direct";
        var channels = Enum.GetValues<Channel>().Select(c => (Channel?)c).Append(null);
        var rows = channels.Select(c => new
        {
            channel = Name(c),
            visits = perf.Where(p => allLinks.TryGetValue(p.Key, out var l) && (Channel?)l.Channel == c).Sum(p => p.Value.Visits),
            people = perf.Where(p => allLinks.TryGetValue(p.Key, out var l) && (Channel?)l.Channel == c).Sum(p => p.Value.People),
            inquiries = inquiries.Count(i => (Channel?)i.Channel == c),
            newCustomers = newCustomers.Count(n => n.AcquisitionChannel == c),
            sales = sales.Count(s => (s.Channel ?? customers.GetValueOrDefault(s.CustomerId)) == c),
            revenue = sales.Where(s => (s.Channel ?? customers.GetValueOrDefault(s.CustomerId)) == c).Sum(s => s.Total),
        }).Where(r => r.visits + r.inquiries + r.newCustomers + r.sales > 0).OrderByDescending(r => r.revenue).ThenByDescending(r => r.newCustomers).ToList();
        var known = rows.Where(r => r.channel != "Unknown / direct").ToList();
        return new
        {
            from, to, channels = rows, best = known.FirstOrDefault()?.channel, mostPeople = known.OrderByDescending(r => r.people + r.inquiries).FirstOrDefault()?.channel,
            totalRevenue = rows.Sum(r => r.revenue),
        };
    }

    // ---------- who sells ----------

    public async Task<object> Salespeople(int year)
    {
        var (from, to) = (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        var users = await db.Users.Where(u => u.Active && u.Role != Role.Employee).Select(u => new { u.Id, u.Name, role = u.Role.ToString() }).ToListAsync();
        var sales = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= from && d.IssueDate <= to)
            .Select(d => new { d.SalespersonUserId, d.CustomerId, d.Total, d.Paid }).ToListAsync();
        var brought = await db.Customers.Where(c => c.AcquiredByUserId != null).Select(c => new { c.Id, c.AcquiredByUserId }).ToListAsync();
        var broughtBy = brought.ToDictionary(c => c.Id, c => c.AcquiredByUserId);
        var (start, end) = (Start(from), End(to));
        var inquiries = (await db.Inquiries.Select(i => new { i.AssignedUserId, i.Status, i.CreatedAt, i.FirstResponseAt }).ToListAsync()).Where(i => i.CreatedAt >= start && i.CreatedAt < end).ToList();
        var perf = await links.Performance(from, to);
        var linkOwners = await db.TrackingLinks.Where(l => l.OwnerUserId != null).ToDictionaryAsync(l => l.Id, l => l.OwnerUserId);
        var rows = users.Select(u =>
        {
            var mine = sales.Where(s => s.SalespersonUserId == u.Id).ToList();
            var theirCustomers = sales.Where(s => broughtBy.GetValueOrDefault(s.CustomerId) == u.Id).ToList();
            var asked = inquiries.Where(i => i.AssignedUserId == u.Id).ToList();
            var decided = asked.Count(i => i.Status is InquiryStatus.Won or InquiryStatus.Lost);
            var answered = asked.Where(i => i.FirstResponseAt != null).Select(i => (i.FirstResponseAt!.Value - i.CreatedAt).TotalHours).ToList();
            return new
            {
                u.Id, u.Name, u.role, invoices = mine.Count, sold = mine.Sum(s => s.Total), collected = mine.Sum(s => s.Paid),
                customersBrought = brought.Count(c => c.AcquiredByUserId == u.Id), broughtRevenue = theirCustomers.Sum(s => s.Total),
                linkVisits = perf.Where(p => linkOwners.GetValueOrDefault(p.Key) == u.Id).Sum(p => p.Value.Visits),
                inquiries = asked.Count, conversion = decided == 0 ? (double?)null : Math.Round(100.0 * asked.Count(i => i.Status == InquiryStatus.Won) / decided, 1),
                averageHoursToAnswer = answered.Count == 0 ? (double?)null : Math.Round(answered.Average(), 1),
            };
        }).Where(r => r.invoices + r.customersBrought + r.inquiries + r.linkVisits > 0).OrderByDescending(r => r.sold + r.broughtRevenue).ToList();
        return new { year, leader = rows.FirstOrDefault(), people = rows };
    }

    // ---------- does the system match the warehouse ----------

    public async Task<object> StockAccuracy(int days = 90)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var lines = await db.StockCountLines.AsNoTracking().Where(l => l.At >= since).ToListAsync();
        var items = await db.Items.AsNoTracking().Where(i => i.TrackStock && i.Active).ToListAsync();
        var lastCount = (await db.StockCountLines.AsNoTracking().Select(l => new { l.ItemId, l.At }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Max(x => x.At));
        var onHand = await db.StockLevels.AsNoTracking().GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var exact = lines.Count(l => l.Counted == l.Expected);
        var value = lines.Sum(l => (long)Math.Round(Math.Abs(l.Counted - l.Expected) * l.UnitCost));
        var missing = lines.Sum(l => (long)Math.Round(Math.Max(0, l.Expected - l.Counted) * l.UnitCost));
        // What to count next: the most valuable stock that hasn't been counted for a month (or ever).
        var countNext = items.Where(i => !lastCount.TryGetValue(i.Id, out var at) || at < DateTimeOffset.UtcNow.AddDays(-30))
            .Select(i => new { i.Id, i.Name, i.Unit, onHand = onHand.GetValueOrDefault(i.Id), value = (long)Math.Round(Math.Max(0, onHand.GetValueOrDefault(i.Id)) * i.AverageCost), lastCounted = lastCount.TryGetValue(i.Id, out var at) ? at : (DateTimeOffset?)null })
            .OrderByDescending(x => x.value).Take(15);
        var off = lines.Where(l => l.Counted != l.Expected).GroupBy(l => l.ItemId).Select(g => new
        {
            name = items.FirstOrDefault(i => i.Id == g.Key)?.Name ?? "", times = g.Count(), short_ = g.Sum(l => Math.Max(0, l.Expected - l.Counted)), over = g.Sum(l => Math.Max(0, l.Counted - l.Expected)),
            value = g.Sum(l => (long)Math.Round(Math.Abs(l.Counted - l.Expected) * l.UnitCost)),
        }).OrderByDescending(x => x.value).Take(10);
        return new
        {
            days, linesCounted = lines.Count, exact, accuracy = lines.Count == 0 ? (double?)null : Math.Round(100.0 * exact / lines.Count, 1), differenceValue = value, missingValue = missing,
            neverCounted = items.Count(i => !lastCount.ContainsKey(i.Id)), countNext, mostOff = off,
        };
    }

    // ---------- the whole journey of one customer ----------

    public async Task<object> Journey(Guid customerId)
    {
        var c = await db.Customers.SingleOrDefaultAsync(x => x.Id == customerId) ?? throw new Common.AppException("Not found.", 404);
        var events = new List<(DateTimeOffset At, string Kind, string Text, string? Link)>();
        if (c.FirstLinkId is { } lid && await db.TrackingLinks.SingleOrDefaultAsync(l => l.Id == lid) is { } link)
        {
            var first = (await db.LinkVisits.Where(v => v.LinkId == lid).Select(v => v.At).ToListAsync()).DefaultIfEmpty(c.CreatedAt).Min();
            events.Add((first < c.CreatedAt ? first : c.CreatedAt, "found", $"Came through {link.Name} ({link.Channel}){(link.OwnerName.Length > 0 ? $", {link.OwnerName}'s link" : "")}", null));
        }
        events.Add((c.CreatedAt, "customer", $"Became a customer ({c.Number})", $"/customers/{c.Id}"));
        var leadIds = await db.Leads.Where(l => l.CustomerId == c.Id).Select(l => l.Id).ToListAsync();
        foreach (var l in await db.Leads.Where(l => l.CustomerId == c.Id).ToListAsync()) events.Add((l.CreatedAt, "lead", $"Lead {l.Number}: {l.Interest}", $"/pipeline/{l.Id}"));
        foreach (var i in await db.Inquiries.Include(x => x.Messages).Where(x => x.CustomerId == c.Id || (x.LeadId != null && leadIds.Contains(x.LeadId.Value))).ToListAsync())
        {
            events.Add((i.CreatedAt, "inquiry", $"Asked: {i.Subject} ({i.Channel})", $"/inquiries/{i.Id}"));
            if (i.FirstResponseAt is { } fr) events.Add((fr, "reply", $"{i.Number} answered by {i.AssignedName} after {Math.Round((fr - i.CreatedAt).TotalHours, 1)} h", $"/inquiries/{i.Id}"));
            if (i.ClosedAt is { } ca) events.Add((ca, i.Status == InquiryStatus.Won ? "won" : "lost", i.Status == InquiryStatus.Won ? $"{i.Number} won" : $"{i.Number} lost: {i.LostReason}", $"/inquiries/{i.Id}"));
        }
        foreach (var a in await db.Activities.Where(a => a.CustomerId == c.Id).ToListAsync()) events.Add((a.At, "activity", $"{a.Kind}: {a.Text}", null));
        var docs = await db.SalesDocs.Include(d => d.Lines).Where(d => d.CustomerId == c.Id && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void).ToListAsync();
        foreach (var d in docs)
            events.Add((Start(d.IssueDate).AddHours(9), d.Kind == SalesDocKind.Quote ? "quote" : "invoice", $"{(d.Kind == SalesDocKind.Quote ? "Quote" : "Invoice")} {d.Number}: {Common.Money.Ksh(d.Total)} ({string.Join(", ", d.Lines.Take(3).Select(l => l.Description))}{(d.Lines.Count > 3 ? "…" : "")})", $"/sales/{d.Id}"));
        foreach (var p in await db.Payments.Where(p => p.CustomerId == c.Id).ToListAsync()) events.Add((p.ReceivedAt, "payment", $"Paid {Common.Money.Ksh(p.Amount)} by {p.Method} {p.Reference}", null));
        // Stock: what left the shelf for them, from which batch, and the supplier bill that had brought it in.
        var docIds = docs.Where(d => d.Kind == SalesDocKind.Invoice).Select(d => d.Id).ToList();
        var moves = await db.StockMoves.Where(m => m.SourceType == "Invoice" && m.SourceId != null && docIds.Contains(m.SourceId.Value)).ToListAsync();
        var itemNames = await db.Items.ToDictionaryAsync(i => i.Id, i => i.Name);
        foreach (var g in moves.GroupBy(m => m.SourceId))
        {
            var text = string.Join("; ", g.Select(m => $"{-m.Quantity:0.##} {itemNames.GetValueOrDefault(m.ItemId, "")}{(m.BatchNo.Length > 0 ? $" (batch {m.BatchNo})" : "")}"));
            events.Add((g.Max(m => m.At), "stock", $"Left the shelf: {text}", null));
            foreach (var m in g.Where(m => m.BatchNo.Length > 0))
                if (await db.StockMoves.Where(r => r.ItemId == m.ItemId && r.BatchNo == m.BatchNo && r.Kind == StockMoveKind.Received).Select(r => r.Reference).FirstOrDefaultAsync() is { } bill)
                    events.Add((m.At.AddSeconds(1), "supply", $"Batch {m.BatchNo} had come in on {bill}", null));
        }
        return new { customer = new { c.Id, c.Name, c.Number }, events = events.OrderBy(e => e.At).Select(e => new { at = e.At, kind = e.Kind, text = e.Text, link = e.Link }) };
    }

    // ---------- the front page ----------

    public async Task<object> Overview()
    {
        var cacheKey = $"insights_overview_{ctx.CompanyId}";
        if (memoryCache.TryGetValue(cacheKey, out object? memCached) && memCached != null)
        {
            return memCached;
        }

        if (await appCache.GetAnswerAsync(cacheKey) is { } shared)
        {
            memoryCache.Set(cacheKey, shared, TimeSpan.FromSeconds(30));
            return shared;
        }

        var today = Clock.Today;
        var from = today.AddDays(-89);
        var selling = (dynamic)await Selling(from, today);
        var shelf = (dynamic)await Shelf();
        var buy = (dynamic)await BuyMore();
        var customers = (dynamic)await Customers();
        var inquiries = (dynamic)await Inquiries(from, today);
        var channels = (dynamic)await Channels(from, today);
        var people = (dynamic)await Salespeople(today.Year);
        var accuracy = (dynamic)await StockAccuracy();
        var topItem = ((IEnumerable<dynamic>)selling.items).FirstOrDefault();
        var topCustomer = ((IEnumerable<dynamic>)customers.top).FirstOrDefault();
        var stuck = ((IEnumerable<dynamic>)shelf.stuck).FirstOrDefault();
        // Only a reason that clearly leads is "the" reason; a tie isn't.
        var reasons = ((IEnumerable<dynamic>)inquiries.lostReasons).ToList();
        var topLost = reasons.Count > 0 && (reasons.Count == 1 || (int)reasons[0].count > (int)reasons[1].count) ? reasons[0] : null;
        var result = new
        {
            period = new { from, to = today },
            bestSeller = topItem is null ? null : new { topItem.name, topItem.revenue, topItem.share },
            stuckStock = new { value = (long)shelf.tiedUp, items = (int)shelf.dead + (int)shelf.slow, worst = stuck is null ? null : new { stuck.Name, stuck.value } },
            buyNow = new { count = (int)buy.count, cost = (long)buy.totalCost },
            bestCustomer = topCustomer is null ? null : new { topCustomer.name, topCustomer.monetary, topCustomer.segment },
            atRisk = ((IEnumerable<dynamic>)customers.atRisk).Count(),
            inquiries = new { total = (int)inquiries.total, conversion = (double?)inquiries.conversion, topLostReason = topLost?.reason, unanswered = (int)inquiries.unanswered },
            bestChannel = (string?)channels.best,
            salespersonOfTheYear = people.leader is null ? null : new { name = (string)people.leader.Name, sold = (long)people.leader.sold, brought = (long)people.leader.broughtRevenue },
            stockAccuracy = (double?)accuracy.accuracy,
        };
        memoryCache.Set(cacheKey, result, TimeSpan.FromSeconds(30));
        await appCache.SetAnswerAsync(cacheKey, result, TimeSpan.FromSeconds(60));
        return result;
    }
}
