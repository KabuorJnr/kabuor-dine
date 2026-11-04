using Kabuor.Erp.Auth;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Etims;
using Kabuor.Erp.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Sales;

public sealed record LineInput(Guid? ItemId, string? Description, decimal Quantity, long? UnitPrice, TaxType? TaxType, string? Unit = null);
/// <summary>Currency: empty or KES for shillings, else "USD" etc. with Rate (shillings per one unit); line prices are then in that currency.</summary>
public sealed record SalesDocInput(SalesDocKind Kind, Guid CustomerId, DateOnly? IssueDate, int? DueInDays, string? Notes, List<LineInput> Lines, Guid? LocationId = null, string? Currency = null, decimal? Rate = null);

public sealed class SalesService(ErpDb db, Ledger ledger, Numbers numbers, IEtimsClient etims, InventoryService stock, RequestContext ctx)
{
    /// <summary>Set while a quote becomes an invoice: the quote's prices were already agreed and checked.</summary>
    bool _pricesAgreed;
    async Task<Company> Company() => await db.Companies.SingleAsync(c => c.Id == db.CompanyId);

    /// <summary>Creates a draft quote or invoice. Prices come from the item unless the user typed one.</summary>
    public async Task<SalesDoc> Create(SalesDocInput input)
    {
        if (input.Lines is not { Count: > 0 }) throw new AppException("Add at least one line.");
        if (input.Lines.Count > 200) throw new AppException("An invoice can have at most 200 lines.");
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == input.CustomerId) ?? throw new AppException("Choose a customer.");
        var company = await Company();
        var issue = input.IssueDate ?? Clock.Today;
        if (input.Kind == SalesDocKind.Invoice && company.AccountingLockDate is { } lockDate && issue <= lockDate)
            throw new AppException($"Accounting period on or before {lockDate:d MMM yyyy} is closed and locked.", 409);
        var doc = new SalesDoc
        {
            Kind = input.Kind,
            Number = await numbers.Next(input.Kind == SalesDocKind.Quote ? "QT" : "INV"),
            CustomerId = customer.Id,
            IssueDate = issue,
            DueDate = issue.AddDays(Math.Clamp(input.DueInDays ?? (input.Kind == SalesDocKind.Quote ? 14 : 7), 0, 365)),
            Notes = (input.Notes ?? "").Trim(),
            LocationId = input.LocationId is null ? null : (await stock.LocationOrDefault(input.LocationId)).Id,
        };
        (doc.Currency, doc.Rate) = Currencies.Of(input.Currency, input.Rate); // the editor starts from the customer's usual currency
        await SetLines(doc, input.Lines, company.VatRegistered, customer.PriceListId);
        if (doc.Kind == SalesDocKind.Quote) await CanPromise(doc);
        // Credit for the sale: the person who made it, and the link that first brought this customer.
        (doc.SalespersonUserId, doc.SalespersonName, doc.LinkId, doc.Channel) = (ctx.UserId, ctx.UserId is null ? "" : ctx.Actor, customer.FirstLinkId, customer.AcquisitionChannel);
        db.SalesDocs.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Lines with their prices: what the user typed, else the customer's price list, else the item's
    /// own price; a bigger unit (a carton) uses its own price, or the base price times how many it holds.
    /// On a foreign-currency document the prices are in that currency (a product's shilling price is converted at
    /// the rate), and each line is also kept in shillings: the unit price at the rate, so quantity × price still
    /// adds up for KRA.</summary>
    async Task SetLines(SalesDoc doc, List<LineInput> lines, bool vat, Guid? priceListId)
    {
        var ids = lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var units = await db.ItemUnits.Where(u => ids.Contains(u.ItemId)).ToListAsync();
        var listed = priceListId is null ? [] : await db.PriceListItems.Where(p => p.PriceListId == priceListId && ids.Contains(p.ItemId)).ToDictionaryAsync(p => p.ItemId, p => p.Price);
        var foreign = doc.Currency.Length > 0;
        doc.Lines.Clear();
        foreach (var l in lines)
        {
            Item? item = l.ItemId is { } id ? items.GetValueOrDefault(id) ?? throw new AppException("One of the items no longer exists.") : null;
            if (item?.Use == ItemUse.Stores) throw new AppException($"{item.Name} is kept in stores for the business's own use, not for sale. Change it to “for sale” in Stock if you sell it.");
            if (l.Quantity <= 0 || l.Quantity > 1_000_000) throw new AppException("Quantities must be more than 0.");
            var (unitName, factor, unitPrice) = ("", 1m, (long?)null);
            if (item is not null && !string.IsNullOrWhiteSpace(l.Unit) && !l.Unit.Trim().Equals(item.Unit, StringComparison.OrdinalIgnoreCase))
            {
                var u = units.FirstOrDefault(x => x.ItemId == item.Id && x.Name.Equals(l.Unit.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new AppException($"{item.Name} isn't sold by the {l.Unit.Trim()}. Add that unit to the product first.");
                (unitName, factor, unitPrice) = (u.Name, u.Factor, u.Price > 0 && !listed.ContainsKey(item.Id) ? u.Price : null);
            }
            var basePrice = item is null ? (long?)null : listed.TryGetValue(item.Id, out var lp) ? lp : item.Price;
            var kesDefault = unitPrice ?? (basePrice is { } bp ? (long)Math.Round(bp * factor, MidpointRounding.AwayFromZero) : (long?)null);
            var listPrice = kesDefault is { } kd && foreign ? (long)Math.Round(kd / doc.Rate, MidpointRounding.AwayFromZero) : kesDefault;
            // Prices come from the price list (the customer's, else the item's): the person selling doesn't type them.
            // A special price is for the owner or an accountant to give, and is kept on the line for everyone to see.
            if (item is not null && l.UnitPrice is { } typed && listPrice is { } lp2 && typed != lp2 && !_pricesAgreed && ctx.UserId is not null && !ctx.Can(Policies.Finance))
                throw new AppException($"{item.Name} is {lp2 / 100m:N2} on the price list. Only the owner or an accountant can give a special price.", 403);
            var price = (item is not null && !ctx.Can(Policies.Finance) && !_pricesAgreed ? listPrice ?? l.UnitPrice : l.UnitPrice ?? listPrice) ?? throw new AppException("Give each line a price.");
            if (price < 0) throw new AppException("Prices can't be negative.");
            var type = l.TaxType ?? item?.TaxType ?? TaxType.B_Standard16;
            var kesPrice = foreign ? Currencies.ToKes(price, doc.Rate) : price;
            var (net, tax, total) = Tax.Line(l.Quantity, kesPrice, type, vat);
            var (fxNet, fxTax, fxTotal) = foreign ? Tax.Line(l.Quantity, price, type, vat) : (0, 0, 0);
            var description = (l.Description ?? item?.Name ?? "").Trim();
            if (description.Length == 0) throw new AppException("Describe each line.");
            doc.Lines.Add(new SalesLine { ItemId = item?.Id, Description = description, Quantity = l.Quantity, UnitPrice = kesPrice, TaxType = type, Net = net, Tax = tax, Total = total, Unit = unitName, UnitFactor = factor, FxUnitPrice = foreign ? price : 0, FxNet = fxNet, FxTax = fxTax, FxTotal = fxTotal });
        }
        doc.Net = doc.Lines.Sum(l => l.Net);
        doc.Tax = doc.Lines.Sum(l => l.Tax);
        doc.Total = doc.Lines.Sum(l => l.Total);
        doc.FxNet = doc.Lines.Sum(l => l.FxNet);
        doc.FxTax = doc.Lines.Sum(l => l.FxTax);
        doc.FxTotal = doc.Lines.Sum(l => l.FxTotal);
    }

    /// <summary>A quote only promises what the business has or will have: stock on the shelf, plus what's on order from
    /// suppliers, plus what's planned to be made. Services and items not counted in stock are not limited.</summary>
    async Task CanPromise(SalesDoc doc)
    {
        var ids = doc.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
        if (ids.Count == 0) return;
        var tracked = await db.Items.Where(i => ids.Contains(i.Id) && i.TrackStock).ToDictionaryAsync(i => i.Id);
        if (tracked.Count == 0) return;
        var tids = tracked.Keys.ToList();
        var onHand = (await db.StockLevels.Where(s => tids.Contains(s.ItemId)).Select(s => new { s.ItemId, s.Quantity }).ToListAsync()).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var onOrder = (await db.PurchaseLines.Join(db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent)), l => l.PurchaseDocId, d => d.Id, (l, d) => new { l.ItemId, q = l.Quantity * l.UnitFactor })
            .Where(x => x.ItemId != null && tids.Contains(x.ItemId!.Value)).ToListAsync()).GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.q));
        var planned = (await db.ProductionRuns.Where(r => r.Status == ProductionStatus.Planned && tids.Contains(r.OutputItemId)).Select(r => new { r.OutputItemId, r.OutputQuantity }).ToListAsync())
            .GroupBy(r => r.OutputItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.OutputQuantity));
        foreach (var g in doc.Lines.Where(l => l.ItemId is { } i && tracked.ContainsKey(i)).GroupBy(l => l.ItemId!.Value))
        {
            var want = g.Sum(l => l.Quantity * l.UnitFactor);
            var have = Math.Max(0, onHand.GetValueOrDefault(g.Key));
            var coming = onOrder.GetValueOrDefault(g.Key) + planned.GetValueOrDefault(g.Key);
            if (want > have + coming)
            {
                var it = tracked[g.Key];
                throw new AppException($"You can quote {have + coming:0.##} {it.Unit} of {it.Name} at most: {have:0.##} in stock{(coming > 0 ? $" and {coming:0.##} coming (on order or planned to be made)" : "")}. Order or plan more first.", 409);
            }
        }
    }

    public async Task<SalesDoc> Get(Guid id) =>
        await db.SalesDocs.Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == id) ?? throw new AppException("Not found.", 404);

    /// <summary>Changes a draft. Issued invoices can't be edited: void and reissue instead (KRA keeps the original).</summary>
    public async Task<SalesDoc> Update(Guid id, SalesDocInput input)
    {
        var doc = await Get(id);
        if (doc.Status != SalesDocStatus.Draft) throw new AppException("Only drafts can be changed. Void this one and make a new one.");
        var company = await Company();
        var issue = input.IssueDate ?? doc.IssueDate;
        if (doc.Kind == SalesDocKind.Invoice && company.AccountingLockDate is { } lockDate && issue <= lockDate)
            throw new AppException($"Accounting period on or before {lockDate:d MMM yyyy} is closed and locked.", 409);
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == input.CustomerId) ?? throw new AppException("Choose a customer.");
        doc.CustomerId = customer.Id;
        doc.IssueDate = issue;
        doc.DueDate = doc.IssueDate.AddDays(Math.Clamp(input.DueInDays ?? doc.DueDate.DayNumber - doc.IssueDate.DayNumber, 0, 365));
        doc.Notes = (input.Notes ?? "").Trim();
        (doc.Currency, doc.Rate) = Currencies.Of(input.Currency ?? doc.Currency, input.Rate ?? (input.Currency is null ? doc.Rate : null));
        db.SalesLines.RemoveRange(doc.Lines);
        await SetLines(doc, input.Lines ?? [], company.VatRegistered, customer.PriceListId);
        if (doc.Kind == SalesDocKind.Quote) await CanPromise(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Issues an invoice: posts it to the books, signs it with eTIMS, and applies any unmatched money
    /// the customer already sent. A quote is just marked sent.</summary>
    public async Task<SalesDoc> Issue(Guid id, bool overLimit = false)
    {
        var doc = await Get(id);
        if (doc.Status != SalesDocStatus.Draft) throw new AppException("This has already been sent.");
        var company = await Company();
        if (doc.Kind == SalesDocKind.Invoice && company.AccountingLockDate is { } lockDate && doc.IssueDate <= lockDate)
            throw new AppException($"Accounting period on or before {lockDate:d MMM yyyy} is closed and locked.", 409);
        if (doc.Kind == SalesDocKind.Invoice && doc.Customer!.CreditLimit > 0 && !overLimit)
        {
            var owes = await db.SalesDocs.Where(d => d.CustomerId == doc.CustomerId && d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid)).SumAsync(d => d.Total - d.Paid);
            if (owes + doc.Total > doc.Customer.CreditLimit)
                throw new AppException($"{doc.Customer.Name} already owes {Money.Ksh(owes)}; this invoice takes them to {Money.Ksh(owes + doc.Total)}, over their {Money.Ksh(doc.Customer.CreditLimit)} limit. Take a payment first, or ask the owner or accountant to allow it.", 409);
        }
        doc.Status = SalesDocStatus.Sent;
        if (doc.Kind == SalesDocKind.Invoice)
        {
            await ledger.PostInvoice(doc);
            await TakeFromStock(doc);
            await Intelligence.InquiryService.OnInvoiceIssued(db, doc); // the inquiry that led here is won
            doc.EtimsStatus = company.VatRegistered ? EtimsStatus.Pending : EtimsStatus.NotRequired;
        }
        await db.SaveChangesAsync();
        if (doc.EtimsStatus == EtimsStatus.Pending) await Sign(doc);
        return doc;
    }

    /// <summary>Stocked items on an issued invoice leave the store; their cost moves from Stock to cost of sales.</summary>
    async Task TakeFromStock(SalesDoc doc)
    {
        var ids = doc.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id) && i.TrackStock).ToDictionaryAsync(i => i.Id);
        if (items.Count == 0) return;
        var location = await stock.LocationOrDefault(doc.LocationId);
        doc.LocationId = location.Id;
        foreach (var l in doc.Lines)
            if (l.ItemId is { } iid && items.GetValueOrDefault(iid) is { } item)
                l.Cost = await stock.Issue(item, location.Id, l.Quantity * l.UnitFactor, "Invoice", doc.Id, doc.Number, doc.IssueDate);
        await ledger.PostCostOfSales(doc.IssueDate, doc.Number, doc.Id, doc.Lines.Sum(l => l.Cost));
        await Agents.AgentKick.Kick(db, "restock"); // stock went down: the restock agent checks within a minute
    }

    /// <summary>A voided invoice: exactly what left the store goes back, at the cost it left with.</summary>
    async Task PutBackInStock(SalesDoc doc)
    {
        var moves = await db.StockMoves.Where(m => m.SourceType == "Invoice" && m.SourceId == doc.Id).ToListAsync();
        if (moves.Count == 0) return;
        var ids = moves.Select(m => m.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        foreach (var m in moves)
            await stock.Return(items[m.ItemId], m, "InvoiceVoid", doc.Id, doc.Number);
        await ledger.PostCostOfSales(Clock.Today, doc.Number, doc.Id, doc.Lines.Sum(l => l.Cost), reverse: true);
    }

    /// <summary>Sends the invoice to KRA eTIMS. A failure never blocks the sale: it stays Pending/Failed and can be retried.</summary>
    public async Task Sign(SalesDoc doc)
    {
        var company = await Company();
        try
        {
            var r = await etims.Sign(company, doc);
            (doc.EtimsStatus, doc.EtimsInvoiceNo, doc.EtimsControlCode, doc.EtimsQrUrl, doc.EtimsError) = (EtimsStatus.Signed, r.InvoiceNo, r.ControlCode, r.QrUrl, "");
        }
        catch (Exception e)
        {
            (doc.EtimsStatus, doc.EtimsError) = (EtimsStatus.Failed, e.Message);
        }
        await db.SaveChangesAsync();
    }

    public async Task<SalesDoc> Void(Guid id)
    {
        var doc = await Get(id);
        if (doc.Status is SalesDocStatus.Void) throw new AppException("Already void.");
        var company = await Company();
        if (doc.Kind == SalesDocKind.Invoice && company.AccountingLockDate is { } lockDate && doc.IssueDate <= lockDate)
            throw new AppException($"Accounting period on or before {lockDate:d MMM yyyy} is closed and locked.", 409);
        if (doc.Paid > 0) throw new AppException("This invoice has payments on it. Move the payments to another invoice first.");
        if (doc.Kind == SalesDocKind.Invoice && doc.Status != SalesDocStatus.Draft)
        {
            // A signed invoice can only be cancelled at KRA with a credit note; if KRA refuses, nothing is voided.
            try { await etims.CreditNote(company, doc); }
            catch (Exception e) { throw new AppException($"KRA eTIMS didn't accept the credit note, so {doc.Number} isn't voided yet: {e.Message}"); }
            await ledger.ReverseInvoice(doc, Clock.Today);
            await PutBackInStock(doc);
        }
        doc.Status = SalesDocStatus.Void;
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>Turns an accepted quote into a draft invoice with the same lines.</summary>
    public async Task<SalesDoc> ToInvoice(Guid quoteId)
    {
        var q = await Get(quoteId);
        if (q.Kind != SalesDocKind.Quote) throw new AppException("Only quotes can become invoices.");
        if (q.Status is SalesDocStatus.Accepted or SalesDocStatus.Void) throw new AppException("This quote has already been used or voided.");
        _pricesAgreed = true;
        var inv = await Create(new SalesDocInput(SalesDocKind.Invoice, q.CustomerId, Clock.Today, null, q.Notes,
            q.Lines.Select(l => new LineInput(l.ItemId, l.Description, l.Quantity, q.Currency.Length > 0 ? l.FxUnitPrice : l.UnitPrice, l.TaxType, l.Unit)).ToList(), null, q.Currency, q.Rate));
        inv.FromQuoteId = q.Id;
        inv.InquiryId = q.InquiryId;
        inv.LinkId = q.LinkId;
        inv.Channel = q.Channel;
        inv.SalespersonUserId = q.SalespersonUserId;
        inv.SalespersonName = q.SalespersonName;
        q.Status = SalesDocStatus.Accepted;
        await db.SaveChangesAsync();
        return inv;
    }
}
