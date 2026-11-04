// Tenders and requests for quotations. Procurement writes what's wanted (lines, specifications, conditions, what
// bidders must send), sets the closing time, and publishes it: a quotation request goes to chosen suppliers by link;
// an open tender is also listed on the business's public tenders page. Suppliers bid on the public page without an
// account. Until the closing time has passed and someone opens the tender, nobody in the business can see who bid or
// at what price (only how many bids are in), as at a tender opening. Then each bid is checked (does it meet the
// mandatory requirements?), scored if the evaluation weighs quality too, and ranked; the award makes a draft purchase
// order for the winner, the award and regret notices are ready to send, and the order can't go to the supplier until
// the standstill period has passed.
using System.Security.Cryptography;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Purchases;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record TenderLineInput(Guid? ItemId, string? Description, string? Specification, decimal Quantity, string? Unit);
public sealed record TenderInput(string Title, string? Scope, string? Requirements, TenderMethod? Method, TenderEvaluation? Evaluation, int? TechnicalWeight, int? TechnicalPassMark,
    DateTimeOffset ClosesAt, int? StandstillDays, Guid? DepartmentId, List<TenderLineInput> Lines);
public sealed record FromRequisitionInput(Guid RequisitionId, string? Title, TenderMethod? Method, DateTimeOffset ClosesAt, List<Guid>? LineIds);
public sealed record EvaluateInput(bool Responsive, int? TechnicalScore, string? Note);
public sealed record AwardInput(Guid BidId, string? Reason);
public sealed record BidLineInput(Guid TenderLineId, long UnitPrice, string? Note);
public sealed record BidInput(string BidderName, string? KraPin, string? ContactName, string? Phone, string? Email, int? DeliveryDays, int? ValidityDays, string? Notes, List<BidLineInput> Lines);

public sealed class TenderService(ErpDb db, Numbers numbers, PurchaseService purchases, RequisitionService requisitions, ApprovalRegister register, RequestContext ctx)
{
    async Task<Tender> Load(Guid id) => await db.Tenders.Include(t => t.Lines).SingleOrDefaultAsync(t => t.Id == id) ?? throw new AppException("Not found.", 404);

    static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public async Task<Tender> Create(TenderInput input)
    {
        var t = new Tender { Number = await numbers.Next("TND"), Title = "", Token = NewToken() };
        await Apply(t, input);
        db.Tenders.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    public async Task<Tender> Update(Guid id, TenderInput input)
    {
        var t = await Load(id);
        if (t.Status != TenderStatus.Draft) throw new AppException("A published tender can't be changed: bidders have priced what it said. Cancel it and issue a new one.");
        db.TenderLines.RemoveRange(t.Lines);
        await Apply(t, input);
        await db.SaveChangesAsync();
        return t;
    }

    async Task Apply(Tender t, TenderInput input)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length < 5) throw new AppException("Give it a clear title, e.g. “Supply of office stationery, 2027”.");
        if (input.Lines is not { Count: > 0 }) throw new AppException("Add at least one line to price.");
        if (input.Lines.Count > 200) throw new AppException("A tender can have at most 200 lines.");
        var weight = input.TechnicalWeight ?? 30;
        var pass = input.TechnicalPassMark ?? 70;
        if (weight is < 0 or > 90 || pass is < 0 or > 100) throw new AppException("The technical weight is 0-90% and the pass mark 0-100.");
        if (input.StandstillDays is < 0 or > 30) throw new AppException("The standstill is 0 to 30 days.");
        if (input.DepartmentId is { } d && !await db.Departments.AnyAsync(x => x.Id == d)) throw new AppException("Choose a department from the list.");
        var ids = input.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        (t.Title, t.Scope, t.Requirements) = (title, (input.Scope ?? "").Trim(), (input.Requirements ?? "").Trim());
        (t.Method, t.Evaluation, t.TechnicalWeight, t.TechnicalPassMark) = (input.Method ?? TenderMethod.Quotation, input.Evaluation ?? TenderEvaluation.LowestPrice, weight, pass);
        (t.ClosesAt, t.StandstillDays, t.DepartmentId) = (input.ClosesAt, input.StandstillDays ?? 14, input.DepartmentId);
        t.Lines.Clear();
        foreach (var l in input.Lines)
        {
            var item = l.ItemId is { } i ? items.GetValueOrDefault(i) ?? throw new AppException("One of the items no longer exists.") : null;
            var description = (l.Description ?? item?.Name ?? "").Trim();
            if (description.Length < 2) throw new AppException("Describe each line.");
            if (l.Quantity <= 0) throw new AppException($"Enter the quantity for “{description}”.");
            t.Lines.Add(new TenderLine { TenderId = t.Id, ItemId = item?.Id, Description = description, Specification = (l.Specification ?? "").Trim(), Unit = (l.Unit ?? item?.Unit ?? "").Trim(), Quantity = l.Quantity });
        }
    }

    /// <summary>Puts an approved request's outstanding lines (or the ones chosen) out to tender.</summary>
    public async Task<Tender> FromRequisition(FromRequisitionInput input)
    {
        var r = await db.Requisitions.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == input.RequisitionId) ?? throw new AppException("Not found.", 404);
        if (r.Status != RequisitionStatus.Approved) throw new AppException("Only approved requests can go to tender.");
        var lines = r.Lines.Where(l => l.Outstanding > 0 && (input.LineIds is not { Count: > 0 } || input.LineIds.Contains(l.Id))).ToList();
        if (lines.Count == 0) throw new AppException("Nothing is left to buy on this request.");
        var t = await Create(new TenderInput((input.Title ?? "").Trim() is { Length: >= 5 } title ? title : $"Supply for request {r.Number}{(r.Purpose.Length > 0 ? $": {r.Purpose}" : "")}",
            r.Purpose, null, input.Method, null, null, null, input.ClosesAt, null, r.DepartmentId,
            lines.Select(l => new TenderLineInput(l.ItemId, l.Description, null, l.Outstanding, l.Unit)).ToList()));
        t.RequisitionId = r.Id;
        await requisitions.MarkTendered(r.Id, lines.Select(l => l.Id));
        await db.SaveChangesAsync();
        return t;
    }

    public async Task<Tender> Publish(Guid id)
    {
        var t = await Load(id);
        if (t.Status != TenderStatus.Draft) throw new AppException("It's already been published.");
        if (t.ClosesAt < DateTimeOffset.UtcNow.AddHours(24)) throw new AppException("Give bidders at least a day: set the closing time later.");
        (t.Status, t.PublishedAt) = (TenderStatus.Published, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Opens the bids once the closing time has passed: from now on they can be seen and evaluated.</summary>
    public async Task<object> Open(Guid id)
    {
        var t = await Load(id);
        if (t.Status != TenderStatus.Published) throw new AppException("Only a published tender can be opened.");
        if (t.ClosesAt > DateTimeOffset.UtcNow) throw new AppException($"Bids stay sealed until it closes ({t.ClosesAt.ToOffset(TimeSpan.FromHours(3)):d MMM yyyy, HH:mm}).", 409);
        (t.Status, t.OpenedAt, t.OpenedBy) = (TenderStatus.Opened, DateTimeOffset.UtcNow, ctx.Actor);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> Evaluate(Guid id, Guid bidId, EvaluateInput input)
    {
        var t = await Load(id);
        if (t.Status != TenderStatus.Opened) throw new AppException("Bids are evaluated after opening and before the award.");
        var b = await db.Bids.SingleOrDefaultAsync(x => x.Id == bidId && x.TenderId == id) ?? throw new AppException("Not found.", 404);
        if (input.TechnicalScore is < 0 or > 100) throw new AppException("Score 0 to 100.");
        if (!input.Responsive && (input.Note ?? "").Trim().Length < 3) throw new AppException("Say which requirement it didn't meet.");
        (b.Responsive, b.TechnicalScore, b.EvaluationNote) = (input.Responsive, t.Evaluation == TenderEvaluation.Weighted ? input.TechnicalScore : null, (input.Note ?? "").Trim());
        await db.SaveChangesAsync();
        return await Get(id);
    }

    sealed record Ranked(Bid Bid, double? Financial, double? Combined, bool Qualifies, int? Rank, string Why);

    /// <summary>Lowest price: the cheapest responsive bid ranks first. Weighted: a bid must reach the technical pass mark;
    /// then technical score × its weight plus price score (lowest price ÷ this price × 100) × the rest.</summary>
    static List<Ranked> Rank(Tender t, List<Bid> bids)
    {
        bool Qualifies(Bid b) => b.Responsive == true && (t.Evaluation == TenderEvaluation.LowestPrice || (b.TechnicalScore ?? -1) >= t.TechnicalPassMark);
        var eligible = bids.Where(Qualifies).ToList();
        var lowest = eligible.Count > 0 ? eligible.Min(b => b.Total) : 0;
        double Fin(Bid b) => b.Total > 0 ? Math.Round(100.0 * lowest / b.Total, 1) : 0;
        double Comb(Bid b) => t.Evaluation == TenderEvaluation.LowestPrice ? Fin(b) : Math.Round((b.TechnicalScore ?? 0) * t.TechnicalWeight / 100.0 + Fin(b) * (100 - t.TechnicalWeight) / 100.0, 1);
        var order = eligible.OrderByDescending(Comb).ThenBy(b => b.Total).ThenBy(b => b.SubmittedAt).ToList();
        return bids.Select(b =>
        {
            var q = Qualifies(b);
            var why = b.Responsive is null ? "Not evaluated yet" : b.Responsive == false ? "Not responsive" : !q ? $"Below the technical pass mark ({t.TechnicalPassMark})" : "";
            return new Ranked(b, q ? Fin(b) : null, q ? Comb(b) : null, q, q ? order.IndexOf(b) + 1 : null, why);
        }).OrderBy(r => r.Rank ?? int.MaxValue).ThenBy(r => r.Bid.Total).ToList();
    }

    /// <summary>The award: a draft purchase order for the winner, at their prices. Choosing anyone but the top-ranked
    /// bid needs a reason, for the record.</summary>
    public async Task<object> Award(Guid id, AwardInput input)
    {
        var t = await Load(id);
        if (t.Status != TenderStatus.Opened) throw new AppException("Open the bids and evaluate them before awarding.");
        var bids = await db.Bids.Include(b => b.Lines).Where(b => b.TenderId == id).ToListAsync();
        var ranked = Rank(t, bids);
        var pick = ranked.SingleOrDefault(r => r.Bid.Id == input.BidId) ?? throw new AppException("Not found.", 404);
        if (!pick.Qualifies) throw new AppException($"{pick.Bid.BidderName}'s bid can't win: {pick.Why.ToLowerInvariant()}.");
        var reason = (input.Reason ?? "").Trim();
        if (pick.Rank != 1 && reason.Length < 10) throw new AppException("This isn't the top-ranked bid: give the reason for choosing it.");
        var supplier = await SupplierFor(pick.Bid);
        var lines = t.Lines.Select(l =>
        {
            var priced = pick.Bid.Lines.Single(x => x.TenderLineId == l.Id);
            return new PurchaseLineInput(l.ItemId, null, l.Description, l.Quantity, priced.UnitPrice, null);
        }).ToList();
        var po = await purchases.Create(new PurchaseInput(PurchaseKind.Order, supplier.Id, Clock.Today, 30, null, null, $"Awarded under tender {t.Number}: {t.Title}", lines, t.DepartmentId, null));
        po.TenderId = t.Id;
        po.RequisitionId = t.RequisitionId;
        (t.Status, t.AwardedAt, t.AwardedBy, t.AwardedBidId, t.AwardReason, t.PurchaseOrderId) = (TenderStatus.Awarded, DateTimeOffset.UtcNow, ctx.Actor, pick.Bid.Id, reason, po.Id);
        pick.Bid.SupplierId = supplier.Id;
        await register.Record("Tender", t.Id, t.Number, $"Awarded to {pick.Bid.BidderName}", pick.Bid.Total, reason);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>The winner as a supplier: the existing one with the same KRA PIN (or name), else a new one from the bid.</summary>
    async Task<Supplier> SupplierFor(Bid b)
    {
        var pin = b.KraPin.Trim().ToUpperInvariant();
        var found = (pin.Length > 0 ? await db.Suppliers.FirstOrDefaultAsync(s => s.KraPin == pin) : null)
            ?? await db.Suppliers.FirstOrDefaultAsync(s => s.Name == b.BidderName);
        if (found is not null) return found;
        var s = new Supplier { Number = await numbers.Next("S"), Name = b.BidderName, KraPin = pin, Phone = b.Phone, Email = b.Email, Notes = $"Added from a winning bid ({b.Receipt})" };
        db.Suppliers.Add(s);
        await db.SaveChangesAsync(); // the order is raised against a saved supplier
        return s;
    }

    public async Task<Tender> Cancel(Guid id, string? reason)
    {
        var t = await Load(id);
        if (t.Status is TenderStatus.Awarded or TenderStatus.Cancelled) throw new AppException("It's already finished.");
        var why = (reason ?? "").Trim();
        if (why.Length < 5) throw new AppException("Say why it's cancelled: bidders are told.");
        (t.Status, t.CancelReason) = (TenderStatus.Cancelled, why);
        // What the request had put out to tender is outstanding again.
        if (t.RequisitionId is { } rid && await db.Requisitions.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == rid) is { } req)
        {
            foreach (var tl in t.Lines)
                if (req.Lines.FirstOrDefault(l => l.Description == tl.Description && l.OrderedQuantity >= tl.Quantity) is { } rl) rl.OrderedQuantity -= tl.Quantity;
            if (req.Status == RequisitionStatus.Fulfilled && req.Lines.Any(l => l.Outstanding > 0)) req.Status = RequisitionStatus.Approved;
        }
        await db.SaveChangesAsync();
        return t;
    }

    public async Task<object> List(string? view)
    {
        var rows = await db.Tenders.Include(t => t.Lines).OrderByDescending(t => t.CreatedAt).Take(300).ToListAsync();
        var bidCounts = await db.Bids.GroupBy(b => b.TenderId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        var now = DateTimeOffset.UtcNow;
        var counts = new
        {
            drafts = rows.Count(t => t.Status == TenderStatus.Draft), open = rows.Count(t => t.Status == TenderStatus.Published && t.ClosesAt > now),
            toOpen = rows.Count(t => t.Status == TenderStatus.Published && t.ClosesAt <= now), evaluating = rows.Count(t => t.Status == TenderStatus.Opened),
            done = rows.Count(t => t.Status is TenderStatus.Awarded or TenderStatus.Cancelled),
        };
        var shown = rows.Where(t => view switch
        {
            "drafts" => t.Status == TenderStatus.Draft,
            "done" => t.Status is TenderStatus.Awarded or TenderStatus.Cancelled,
            "all" => true,
            _ => t.Status is TenderStatus.Published or TenderStatus.Opened,
        });
        return new
        {
            counts,
            items = shown.Select(t => new
            {
                t.Id, t.Number, t.Title, method = t.Method.ToString(), status = t.Status.ToString(), t.ClosesAt, t.PublishedAt, t.OpenedAt, t.AwardedAt,
                bids = bidCounts.GetValueOrDefault(t.Id), closed = t.ClosesAt <= now, lines = t.Lines.Count,
            }),
        };
    }

    public async Task<object> Get(Guid id)
    {
        var t = await Load(id);
        var bids = await db.Bids.Include(b => b.Lines).Where(b => b.TenderId == id).OrderBy(b => b.SubmittedAt).ToListAsync();
        var sealed_ = t.Status is TenderStatus.Draft or TenderStatus.Published;
        var ranked = sealed_ ? new List<Ranked>() : Rank(t, bids);
        var dept = t.DepartmentId is { } d ? await db.Departments.Where(x => x.Id == d).Select(x => x.Name).SingleOrDefaultAsync() : null;
        var po = t.PurchaseOrderId is { } p ? await db.PurchaseDocs.Where(x => x.Id == p).Select(x => new { x.Id, x.Number, status = x.Status.ToString(), x.Total }).SingleOrDefaultAsync() : null;
        var request = t.RequisitionId is { } r ? await db.Requisitions.Where(x => x.Id == r).Select(x => new { x.Id, x.Number }).SingleOrDefaultAsync() : null;
        var company = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => new { c.Name, c.Slug }).SingleAsync();
        var winner = ranked.FirstOrDefault(x => x.Bid.Id == t.AwardedBidId)?.Bid;
        return new
        {
            t.Id, t.Number, t.Title, t.Scope, t.Requirements, method = t.Method.ToString(), status = t.Status.ToString(), evaluation = t.Evaluation.ToString(),
            t.TechnicalWeight, t.TechnicalPassMark, t.ClosesAt, t.StandstillDays, t.PublishedAt, t.OpenedAt, t.OpenedBy, t.AwardedAt, t.AwardedBy, t.AwardReason, t.CancelReason,
            standstillEnds = t.StandstillEnds, t.Token, t.DepartmentId, department = dept ?? "", request, purchaseOrder = po, closed = t.ClosesAt <= DateTimeOffset.UtcNow,
            companySlug = company.Slug,
            lines = t.Lines.Select(l => new { l.Id, l.ItemId, l.Description, l.Specification, l.Unit, l.Quantity }),
            bidCount = bids.Count,
            // Sealed until opened: no names, no prices.
            bids = sealed_ ? null : ranked.Select(x => new
            {
                x.Bid.Id, x.Bid.BidderName, x.Bid.KraPin, x.Bid.ContactName, x.Bid.Phone, x.Bid.Email, x.Bid.DeliveryDays, x.Bid.ValidityDays, x.Bid.Notes, x.Bid.Total, x.Bid.Receipt, x.Bid.SubmittedAt,
                x.Bid.Responsive, x.Bid.TechnicalScore, x.Bid.EvaluationNote, financialScore = x.Financial, combinedScore = x.Combined, x.Rank, x.Qualifies, why = x.Why,
                awarded = x.Bid.Id == t.AwardedBidId,
                lines = x.Bid.Lines.Select(l => new { l.TenderLineId, l.UnitPrice, l.Total, l.Note }),
            }),
            notices = winner is null ? null : new
            {
                award = $"{company.Name}: tender {t.Number} ({t.Title}) is awarded to {winner.BidderName} at KSh {winner.Total / 100m:N2}. A purchase order follows after the standstill period ends on {t.StandstillEnds?.ToOffset(TimeSpan.FromHours(3)):d MMM yyyy}.",
                regret = $"{company.Name}: thank you for bidding for tender {t.Number} ({t.Title}). Your bid was not successful; it was awarded to {winner.BidderName} at KSh {winner.Total / 100m:N2}. If you have a concern about the award, tell us before {t.StandstillEnds?.ToOffset(TimeSpan.FromHours(3)):d MMM yyyy}.",
                others = bids.Where(b => b.Id != winner.Id).Select(b => new { b.BidderName, b.Phone, b.Email }),
                winner = new { winner.BidderName, winner.Phone, winner.Email },
            },
        };
    }

    // ------------------------------------------------------------------------------------------------ public (no sign-in)

    /// <summary>The tender as bidders see it: what's wanted and the closing time, never the other bids.</summary>
    public async Task<object> PublicView(Tender t)
    {
        var company = await db.Companies.Where(c => c.Id == t.CompanyId).Select(c => new { c.Name, c.Phone, c.Email, c.County }).SingleAsync();
        return new
        {
            company, t.Number, t.Title, t.Scope, t.Requirements, method = t.Method.ToString(), evaluation = t.Evaluation.ToString(), t.TechnicalWeight, t.TechnicalPassMark,
            t.ClosesAt, status = t.Status.ToString(), taking = t.Status == TenderStatus.Published && t.ClosesAt > DateTimeOffset.UtcNow,
            lines = t.Lines.Select(l => new { l.Id, l.Description, l.Specification, l.Unit, l.Quantity }),
        };
    }

    public async Task<object> SubmitBid(Tender t, BidInput input)
    {
        if (t.Status != TenderStatus.Published || t.ClosesAt <= DateTimeOffset.UtcNow) throw new AppException("This tender isn't taking bids.", 409);
        var name = (input.BidderName ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter your company's name.");
        var pin = (input.KraPin ?? "").Trim().ToUpperInvariant();
        if (pin.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(pin, "^[AP][0-9]{9}[A-Z]$")) throw new AppException("That isn't a KRA PIN (it looks like P051234567X).");
        var phone = (input.Phone ?? "").Trim();
        if (phone.Length < 9) throw new AppException("Enter a phone number we can reach you on.");
        if (input.Lines is not { Count: > 0 } || input.Lines.Count != t.Lines.Count || t.Lines.Any(l => input.Lines.All(x => x.TenderLineId != l.Id)))
            throw new AppException("Price every line.");
        if (input.Lines.Any(l => l.UnitPrice <= 0 || l.UnitPrice > 100_000_000_00)) throw new AppException("Enter a price for every line.");
        if (await db.Bids.AnyAsync(b => b.TenderId == t.Id && ((pin.Length > 0 && b.KraPin == pin) || b.BidderName == name)))
            throw new AppException("A bid from your company is already in. To change it, contact the procurement office before the closing time.", 409);
        var bid = new Bid
        {
            TenderId = t.Id, BidderName = name, KraPin = pin, ContactName = (input.ContactName ?? "").Trim(), Phone = phone, Email = (input.Email ?? "").Trim(),
            DeliveryDays = Math.Clamp(input.DeliveryDays ?? 0, 0, 365), ValidityDays = Math.Clamp(input.ValidityDays ?? 90, 1, 365), Notes = (input.Notes ?? "").Trim(),
            Receipt = $"{t.Number}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}",
        };
        foreach (var l in t.Lines)
        {
            var priced = input.Lines.First(x => x.TenderLineId == l.Id);
            bid.Lines.Add(new BidLine { BidId = bid.Id, TenderLineId = l.Id, UnitPrice = priced.UnitPrice, Total = (long)Math.Round(priced.UnitPrice * l.Quantity), Note = (priced.Note ?? "").Trim() });
        }
        bid.Total = bid.Lines.Sum(l => l.Total);
        db.Bids.Add(bid);
        await db.SaveChangesAsync();
        return new { receipt = bid.Receipt, total = bid.Total, at = bid.SubmittedAt };
    }

    /// <summary>A business's open tenders, for its public tenders page.</summary>
    public async Task<object> Board(Guid companyId)
    {
        var company = await db.Companies.Where(c => c.Id == companyId).Select(c => new { c.Name, c.County }).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var open = await db.Tenders.Where(t => t.Method == TenderMethod.Open && t.Status == TenderStatus.Published).ToListAsync();
        return new { company, tenders = open.Where(t => t.ClosesAt > now).OrderBy(t => t.ClosesAt).Select(t => new { t.Number, t.Title, t.ClosesAt, t.Token }) };
    }
}
