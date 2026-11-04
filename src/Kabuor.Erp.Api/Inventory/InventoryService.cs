// Stock: how many of each item are where, and what they cost. Every change is a StockMove (the history) plus
// an update to StockLevel (the current count) in the same save. Cost is the moving average across all
// locations: buying at a new price blends into it; selling, losing or moving stock doesn't change it.
// Items kept by batch also hold a StockBatch per batch number and place: a bill brings a batch in with its expiry,
// a sale takes the batch that expires first, and expired stock is never sold (it has to be written off).
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Inventory;

public sealed record CountInput(Guid ItemId, decimal Counted);

public sealed class InventoryService(ErpDb db, Ledger ledger, RequestContext ctx)
{
    /// <summary>The main store. Every company has one; it's made on first use.</summary>
    public async Task<Location> DefaultLocation()
    {
        var loc = await db.Locations.Where(l => l.Active).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).FirstOrDefaultAsync()
            ?? db.Locations.Local.FirstOrDefault(l => l.IsDefault);
        if (loc is not null) return loc;
        loc = new Location { Name = "Main store", IsDefault = true };
        db.Locations.Add(loc);
        return loc;
    }

    public async Task<Location> LocationOrDefault(Guid? id) =>
        id is { } lid ? await db.Locations.SingleOrDefaultAsync(l => l.Id == lid && l.Active) ?? throw new AppException("That store no longer exists.") : await DefaultLocation();

    async Task<StockLevel> Level(Guid itemId, Guid locationId)
    {
        var level = db.StockLevels.Local.FirstOrDefault(l => l.ItemId == itemId && l.LocationId == locationId)
            ?? await db.StockLevels.SingleOrDefaultAsync(l => l.ItemId == itemId && l.LocationId == locationId);
        if (level is null)
        {
            level = new StockLevel { CompanyId = db.CompanyId, ItemId = itemId, LocationId = locationId };
            db.StockLevels.Add(level);
        }
        return level;
    }

    /// <summary>Total on hand across all locations (including changes not saved yet).</summary>
    async Task<decimal> OnHand(Guid itemId)
    {
        var saved = await db.StockLevels.Where(l => l.ItemId == itemId).Select(l => new { l.LocationId, l.Quantity }).ToListAsync();
        var local = db.StockLevels.Local.Where(l => l.ItemId == itemId).ToList();
        return local.Sum(l => l.Quantity) + saved.Where(s => local.All(l => l.LocationId != s.LocationId)).Sum(s => s.Quantity);
    }

    async Task Move(Item item, Guid locationId, decimal qty, StockMoveKind kind, decimal unitCost, string sourceType, Guid? sourceId, string reference, string note = "", string batchNo = "")
    {
        var level = await Level(item.Id, locationId);
        level.Quantity += qty;
        db.StockMoves.Add(new StockMove { ItemId = item.Id, LocationId = locationId, Kind = kind, Quantity = qty, UnitCost = unitCost, SourceType = sourceType, SourceId = sourceId, Reference = reference, Note = note, Actor = ctx.Actor, BatchNo = batchNo });
    }

    /// <summary>The item's batches at a place, including ones added in this save.</summary>
    async Task<List<StockBatch>> Batches(Guid itemId, Guid locationId)
    {
        var saved = await db.StockBatches.Where(b => b.ItemId == itemId && b.LocationId == locationId).ToListAsync();
        return saved.Concat(db.StockBatches.Local.Where(b => b.ItemId == itemId && b.LocationId == locationId && !saved.Contains(b))).ToList();
    }

    async Task AddToBatch(Item item, Guid locationId, string batchNo, DateOnly? expiry, decimal qty)
    {
        var batch = (await Batches(item.Id, locationId)).FirstOrDefault(b => b.BatchNo == batchNo);
        if (batch is null) db.StockBatches.Add(batch = new StockBatch { ItemId = item.Id, LocationId = locationId, BatchNo = batchNo, Expiry = expiry, ReceivedOn = Clock.Today, CompanyId = db.CompanyId });
        batch.Expiry ??= expiry;
        batch.Quantity += qty;
    }

    /// <summary>Stock coming in at a cost (before VAT): blends into the average cost. A batch number (items kept by
    /// batch) puts it in that batch.</summary>
    public async Task Receive(Item item, Guid locationId, decimal qty, decimal unitCost, StockMoveKind kind, string sourceType, Guid? sourceId, string reference, string batchNo = "", DateOnly? expiry = null)
    {
        var before = await OnHand(item.Id);
        item.AverageCost = before <= 0 ? unitCost : (before * item.AverageCost + qty * unitCost) / (before + qty);
        batchNo = item.TrackBatches ? batchNo.Trim() : "";
        if (batchNo.Length > 0) await AddToBatch(item, locationId, batchNo, expiry, qty);
        await Move(item, locationId, qty, kind, unitCost, sourceType, sourceId, reference, batchNo: batchNo);
    }

    /// <summary>Stock going out (sold, or used in the business: kind Consumed): returns its cost at the current
    /// average, in cents. Items kept by batch go first-to-expire first; stock with no batch is used after the batches;
    /// expired stock is refused. Stock used in the business has to be on the shelf (it's being handed over).</summary>
    public async Task<long> Issue(Item item, Guid locationId, decimal qty, string sourceType, Guid? sourceId, string reference, DateOnly? soldOn = null, StockMoveKind kind = StockMoveKind.Sold)
    {
        var day = soldOn ?? Clock.Today;
        var company = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => new { c.PreventNegativeStock }).SingleAsync();
        if (kind != StockMoveKind.Sold && item.TrackStock && (await Level(item.Id, locationId)).Quantity is var onShelf && onShelf < qty)
            throw new AppException($"Only {Math.Max(onShelf, 0):0.##} {item.Unit} of {item.Name} in this store; {qty:0.##} were asked for. Receive more, or move some here first.");
        // An invoice is for what's on the shelf: one system, one stock figure. (Selling ahead of stock is a quote, which
        // may promise what's on order or planned to be made.)
        if (item.TrackStock)
        {
            var have = (await Level(item.Id, locationId)).Quantity;
            if (have < qty) throw new AppException($"Only {Math.Max(have, 0):0.##} {item.Unit} of {item.Name} in stock here; this needs {qty:0.##}. Receive the stock first, or send a quote for what's coming.", 409);
        }
        var cost = (long)Math.Round(qty * item.AverageCost, MidpointRounding.AwayFromZero);
        if (!item.TrackBatches)
        {
            await Move(item, locationId, -qty, kind, item.AverageCost, sourceType, sourceId, reference);
            return cost;
        }
        var batches = await Batches(item.Id, locationId);
        var level = await Level(item.Id, locationId);
        var unbatched = Math.Max(0, level.Quantity - batches.Where(b => b.Quantity > 0).Sum(b => b.Quantity));
        var left = qty;
        foreach (var b in batches.Where(b => b.Quantity > 0 && !(b.Expiry < day)).OrderBy(b => b.Expiry ?? DateOnly.MaxValue).ThenBy(b => b.ReceivedOn))
        {
            var take = Math.Min(left, b.Quantity);
            b.Quantity -= take;
            left -= take;
            await Move(item, locationId, -take, kind, item.AverageCost, sourceType, sourceId, reference, batchNo: b.BatchNo);
            if (left == 0) return cost;
        }
        var plain = Math.Min(left, unbatched);
        left -= plain;
        var expired = batches.Where(b => b.Quantity > 0 && b.Expiry < day).OrderBy(b => b.Expiry).FirstOrDefault();
        if (left > 0 && expired is not null)
            throw new AppException($"Only expired {item.Name} is left (batch {expired.BatchNo}, expired {expired.Expiry:d MMM yyyy}). Write it off in Stock instead of selling it.");
        await Move(item, locationId, -(plain + left), kind, item.AverageCost, sourceType, sourceId, reference);
        return cost;
    }

    /// <summary>Puts sold stock back where it came from (a voided invoice), batch by batch.</summary>
    public async Task Return(Item item, StockMove sold, string sourceType, Guid? sourceId, string reference)
        => await Receive(item, sold.LocationId, -sold.Quantity, sold.UnitCost, StockMoveKind.Returned, sourceType, sourceId, reference, sold.BatchNo);

    /// <summary>Batches with stock, soonest expiry first: what to sell or return before it's lost.</summary>
    public async Task<object> Expiring(int days)
    {
        var today = Clock.Today;
        var until = today.AddDays(Math.Clamp(days, 1, 730));
        var batches = await db.StockBatches.Where(b => b.Quantity > 0 && b.Expiry != null && b.Expiry <= until).ToListAsync();
        var ids = batches.Select(b => b.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var places = await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);
        var rows = batches.OrderBy(b => b.Expiry).Select(b => new
        {
            b.Id, b.BatchNo, b.Expiry, b.Quantity, itemId = b.ItemId, item = items[b.ItemId].Name, unit = items[b.ItemId].Unit,
            location = places.GetValueOrDefault(b.LocationId, ""), daysLeft = b.Expiry!.Value.DayNumber - today.DayNumber,
            value = (long)Math.Round(b.Quantity * items[b.ItemId].AverageCost),
        }).ToList();
        return new { expired = rows.Count(r => r.daysLeft < 0), expiredValue = rows.Where(r => r.daysLeft < 0).Sum(r => r.value), soonValue = rows.Where(r => r.daysLeft >= 0).Sum(r => r.value), rows };
    }

    /// <summary>Ingredients used in production: returns their cost at the current average, in cents.</summary>
    public async Task<long> Consume(Item item, Guid locationId, decimal qty, string sourceType, Guid? sourceId, string reference)
    {
        await Move(item, locationId, -qty, StockMoveKind.Consumed, item.AverageCost, sourceType, sourceId, reference);
        return (long)Math.Round(qty * item.AverageCost, MidpointRounding.AwayFromZero);
    }

    /// <summary>A bill voided: what it brought in goes back out at the price it came in at.</summary>
    public async Task Unreceive(Item item, Guid locationId, decimal qty, decimal unitCost, string sourceType, Guid? sourceId, string reference, string batchNo = "")
    {
        if (item.TrackBatches && batchNo.Length > 0) await AddToBatch(item, locationId, batchNo, null, -qty);
        var before = await OnHand(item.Id);
        var after = before - qty;
        if (after > 0) item.AverageCost = Math.Max(0, (before * item.AverageCost - qty * unitCost) / after);
        await Move(item, locationId, -qty, StockMoveKind.Reversed, unitCost, sourceType, sourceId, reference);
    }

    /// <summary>Sets what was physically counted at a location; the difference is a loss or a find, posted to the books.</summary>
    public async Task<List<object>> Count(Guid? locationId, List<CountInput> counts, string note)
    {
        if (counts is not { Count: > 0 }) throw new AppException("Enter at least one count.");
        if (counts.Any(c => c.Counted < 0)) throw new AppException("Counts can't be negative.");
        var loc = await LocationOrDefault(locationId);
        var ids = counts.Select(c => c.ItemId).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id) && i.TrackStock).ToDictionaryAsync(i => i.Id);
        var reference = $"Count {Clock.Today:d MMM}, {loc.Name}";
        var changes = new List<object>();
        long value = 0, storesValue = 0;
        var sourceId = Guid.NewGuid();
        foreach (var c in counts)
        {
            var item = items.GetValueOrDefault(c.ItemId) ?? throw new AppException("One of those items isn't tracked in stock.");
            var level = await Level(item.Id, loc.Id);
            var diff = c.Counted - level.Quantity;
            db.StockCountLines.Add(new StockCountLine { CountId = sourceId, ItemId = item.Id, LocationId = loc.Id, Expected = level.Quantity, Counted = c.Counted, UnitCost = item.AverageCost, CountedBy = ctx.Actor });
            if (diff == 0) continue;
            await Move(item, loc.Id, diff, StockMoveKind.Adjusted, item.AverageCost, "Count", sourceId, reference, note);
            var v = (long)Math.Round(diff * item.AverageCost, MidpointRounding.AwayFromZero);
            if (item.Use == ItemUse.Stores) storesValue += v; else value += v;
            changes.Add(new { itemId = item.Id, item.Name, difference = diff, value = v });
        }
        await ledger.PostStockAdjustment(reference, sourceId, value);
        await ledger.PostStockAdjustment(reference, sourceId, storesValue, stores: true);
        await db.SaveChangesAsync();
        return changes;
    }

    /// <summary>Throws away what's left of a batch (expired, damaged, recalled): it leaves stock and the loss is booked.</summary>
    public async Task<object> WriteOff(Guid batchId, string note)
    {
        var batch = await db.StockBatches.SingleOrDefaultAsync(b => b.Id == batchId) ?? throw new AppException("Not found.", 404);
        if (batch.Quantity <= 0) throw new AppException("Nothing is left in that batch.");
        var item = await db.Items.SingleAsync(i => i.Id == batch.ItemId);
        var qty = batch.Quantity;
        var sourceId = Guid.NewGuid();
        var reference = $"Write-off {batch.BatchNo}";
        batch.Quantity = 0;
        await Move(item, batch.LocationId, -qty, StockMoveKind.Adjusted, item.AverageCost, "WriteOff", sourceId, reference, note.Length > 0 ? note : batch.Expiry < Clock.Today ? "Expired" : "Written off", batch.BatchNo);
        var value = -(long)Math.Round(qty * item.AverageCost, MidpointRounding.AwayFromZero);
        await ledger.PostStockAdjustment(reference, sourceId, value, item.Use == ItemUse.Stores);
        await db.SaveChangesAsync();
        return new { item = item.Name, batch.BatchNo, quantity = qty, value = -value };
    }

    /// <summary>Stock already on the shelf when the business starts using the system, at what it cost.</summary>
    public async Task Opening(Guid itemId, Guid? locationId, decimal qty, long unitCost)
    {
        if (qty <= 0) throw new AppException("Enter how many you have.");
        if (unitCost < 0) throw new AppException("Costs can't be negative.");
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId && i.TrackStock) ?? throw new AppException("Turn on stock tracking for this item first.");
        var loc = await LocationOrDefault(locationId);
        var sourceId = Guid.NewGuid();
        await Receive(item, loc.Id, qty, unitCost, StockMoveKind.Adjusted, "Opening", sourceId, $"Opening stock, {loc.Name}");
        await ledger.PostOpeningStock($"{item.Name} at {loc.Name}", sourceId, (long)Math.Round(qty * unitCost, MidpointRounding.AwayFromZero), item.Use == ItemUse.Stores);
        await db.SaveChangesAsync();
    }

    /// <summary>Moves stock between stores. No money changes hands, so the books don't change.</summary>
    public async Task Transfer(Guid itemId, Guid fromId, Guid toId, decimal qty, string note)
    {
        if (qty <= 0) throw new AppException("Enter how many to move.");
        if (fromId == toId) throw new AppException("Choose two different stores.");
        var item = await db.Items.SingleOrDefaultAsync(i => i.Id == itemId && i.TrackStock) ?? throw new AppException("That item isn't tracked in stock.");
        var from = await LocationOrDefault(fromId);
        var to = await LocationOrDefault(toId);
        var have = (await Level(item.Id, from.Id)).Quantity;
        if (qty > have) throw new AppException($"{from.Name} only has {have:0.##} {item.Unit} of {item.Name}.");
        var reference = $"{from.Name} → {to.Name}";
        var sourceId = Guid.NewGuid();
        await Move(item, from.Id, -qty, StockMoveKind.TransferOut, item.AverageCost, "Transfer", sourceId, reference, note);
        await Move(item, to.Id, qty, StockMoveKind.TransferIn, item.AverageCost, "Transfer", sourceId, reference, note);
        await db.SaveChangesAsync();
    }

    /// <summary>Everything in stock: per location, total, value at average cost, and whether it's running low.</summary>
    public async Task<object> Overview(string? filter, string? q, string? use = null)
    {
        // Two stocks, one store room: what's for sale, and stores the business runs on (spares, tools, fuel, supplies).
        var all = await db.Items.Where(i => i.TrackStock).OrderBy(i => i.Name).ToListAsync();
        var uses = new { sale = all.Count(i => i.Use == ItemUse.Sale), stores = all.Count(i => i.Use == ItemUse.Stores) };
        var items = use switch { "stores" => all.Where(i => i.Use == ItemUse.Stores).ToList(), "sale" => all.Where(i => i.Use == ItemUse.Sale).ToList(), _ => all };
        var levels = await db.StockLevels.ToListAsync();
        var locations = await db.Locations.Where(l => l.Active).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).Select(l => new { l.Id, l.Name, l.IsDefault }).ToListAsync();
        var rows = items.Select(i =>
        {
            var at = levels.Where(l => l.ItemId == i.Id).ToList();
            var total = at.Sum(l => l.Quantity);
            var state = total < 0 ? "below" : total == 0 ? "out" : total <= i.ReorderLevel ? "low" : "ok";
            return new
            {
                i.Id, i.Name, i.Brand, i.Sku, i.Barcode, i.Unit, i.ReorderLevel, i.Price, use = i.Use.ToString(), i.StoresCategory,
                averageCost = (long)Math.Round(i.AverageCost),
                total, value = (long)Math.Round(total * i.AverageCost),
                state,
                byLocation = locations.ToDictionary(l => l.Id.ToString(), l => at.FirstOrDefault(x => x.LocationId == l.Id)?.Quantity ?? 0),
            };
        }).ToList();
        var counts = new { all = rows.Count, low = rows.Count(r => r.state is "low" or "out" or "below"), @out = rows.Count(r => r.state is "out" or "below"), below = rows.Count(r => r.state == "below") };
        var shown = rows.Where(r => filter switch { "low" => r.state is "low" or "out" or "below", "out" => r.state is "out" or "below", "below" => r.state == "below", _ => true })
            .Where(r => string.IsNullOrWhiteSpace(q) || r.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || r.Brand.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || r.Barcode == q.Trim() || r.Sku.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.state == "below" ? 0 : r.state == "out" ? 1 : r.state == "low" ? 2 : 3).ThenBy(r => r.Name).ToList();
        var value = all.ToDictionary(i => i.Id, i => (long)Math.Round(levels.Where(l => l.ItemId == i.Id).Sum(l => l.Quantity) * i.AverageCost));
        return new
        {
            counts, uses, locations, totalValue = rows.Sum(r => r.value), items = shown,
            saleValue = all.Where(i => i.Use == ItemUse.Sale).Sum(i => value[i.Id]), storesValue = all.Where(i => i.Use == ItemUse.Stores).Sum(i => value[i.Id]),
        };
    }
}
