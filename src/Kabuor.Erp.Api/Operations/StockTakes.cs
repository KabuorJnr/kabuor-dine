// Stock takes: a full count of one store as a piece of work, not a single edit. Starting one notes what the system
// expects of every stocked item there. Counters enter what's on the shelves, over as many sittings as it takes, and in a
// blind count they never see the expected figure (so nobody "finds" the number they were told). Then someone reviews
// the differences and posts them: stock is set to what was counted and the gain or loss goes to the books, through the
// same count posting as a quick count. Items not counted are left as they were.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record StockTakeInput(Guid? LocationId, bool? Blind, string? Note);
public sealed record CountLineInput(Guid ItemId, decimal? Counted);

public sealed class StockTakeService(ErpDb db, Numbers numbers, InventoryService stock, RequestContext ctx)
{
    async Task<StockTake> Load(Guid id) => await db.StockTakes.Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == id) ?? throw new AppException("Not found.", 404);

    public async Task<object> Start(StockTakeInput input)
    {
        var loc = await stock.LocationOrDefault(input.LocationId);
        if (await db.StockTakes.AnyAsync(s => s.LocationId == loc.Id && s.Status == StockTakeStatus.Counting))
            throw new AppException($"{loc.Name} already has a count going: finish or cancel it first.", 409);
        var items = await db.Items.Where(i => i.TrackStock && i.Active).Select(i => new { i.Id, i.AverageCost }).ToListAsync();
        if (items.Count == 0) throw new AppException("No items are tracked in stock yet.");
        var levels = await db.StockLevels.Where(l => l.LocationId == loc.Id).ToDictionaryAsync(l => l.ItemId, l => l.Quantity);
        var take = new StockTake { Number = await numbers.Next("ST"), LocationId = loc.Id, Blind = input.Blind ?? true, Note = (input.Note ?? "").Trim(), StartedBy = ctx.Actor };
        foreach (var i in items) take.Lines.Add(new StockTakeLine { StockTakeId = take.Id, ItemId = i.Id, Expected = levels.GetValueOrDefault(i.Id), UnitCost = i.AverageCost });
        db.StockTakes.Add(take);
        await db.SaveChangesAsync();
        return await Get(take.Id, false);
    }

    /// <summary>The count sheet. Expected figures (and so the differences) show once the count is reviewed, or always for an open count.</summary>
    public async Task<object> Get(Guid id, bool review)
    {
        var t = await Load(id);
        var ids = t.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.Name, i.Sku, i.Barcode, i.Unit, i.Brand }).ToDictionaryAsync(i => i.Id);
        var place = await db.Locations.Where(l => l.Id == t.LocationId).Select(l => l.Name).SingleAsync();
        var show = review || !t.Blind || t.Status != StockTakeStatus.Counting;
        var counted = t.Lines.Where(l => l.Counted is not null).ToList();
        long Value(StockTakeLine l) => (long)Math.Round(((l.Counted ?? l.Expected) - l.Expected) * l.UnitCost);
        return new
        {
            t.Id, t.Number, t.LocationId, location = place, status = t.Status.ToString(), t.Blind, t.Note, t.StartedBy, t.StartedAt, t.PostedBy, t.PostedAt, t.VarianceValue,
            showsExpected = show,
            progress = new { counted = counted.Count, total = t.Lines.Count },
            summary = show ? new
            {
                matched = counted.Count(l => l.Counted == l.Expected), short_ = counted.Count(l => l.Counted < l.Expected), over = counted.Count(l => l.Counted > l.Expected),
                value = counted.Sum(Value), accuracy = counted.Count > 0 ? Math.Round(100.0 * counted.Count(l => l.Counted == l.Expected) / counted.Count, 1) : (double?)null,
            } : null,
            lines = t.Lines.Select(l => new
            {
                l.ItemId, name = items.GetValueOrDefault(l.ItemId)?.Name ?? "", sku = items.GetValueOrDefault(l.ItemId)?.Sku ?? "", barcode = items.GetValueOrDefault(l.ItemId)?.Barcode ?? "",
                unit = items.GetValueOrDefault(l.ItemId)?.Unit ?? "", l.Counted, l.CountedBy, l.CountedAt,
                expected = show ? l.Expected : (decimal?)null, difference = show && l.Counted is { } c ? c - l.Expected : (decimal?)null,
                value = show && l.Counted is not null ? Value(l) : (long?)null,
            }).OrderBy(l => l.name),
        };
    }

    public async Task<object> Count(Guid id, List<CountLineInput> counts)
    {
        var t = await Load(id);
        if (t.Status != StockTakeStatus.Counting) throw new AppException("This count is finished.");
        if (counts is not { Count: > 0 }) throw new AppException("Enter at least one count.");
        foreach (var c in counts)
        {
            var line = t.Lines.SingleOrDefault(l => l.ItemId == c.ItemId) ?? throw new AppException("One of those items isn't on this count.");
            if (c.Counted is < 0) throw new AppException("Counts can't be negative.");
            (line.Counted, line.CountedBy, line.CountedAt) = (c.Counted, c.Counted is null ? "" : ctx.Actor, c.Counted is null ? null : DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync();
        return await Get(id, false);
    }

    /// <summary>Books the differences: stock becomes what was counted, the gain or loss goes to the books.</summary>
    public async Task<object> Post(Guid id)
    {
        var t = await Load(id);
        if (t.Status != StockTakeStatus.Counting) throw new AppException("This count is finished.");
        var counted = t.Lines.Where(l => l.Counted is not null).ToList();
        if (counted.Count == 0) throw new AppException("Nothing has been counted yet.");
        // The value booked is against stock as it is now (what the count posting changes), at average cost.
        var ids = counted.Select(l => l.ItemId).ToList();
        var now = await db.StockLevels.Where(l => l.LocationId == t.LocationId && ids.Contains(l.ItemId)).ToDictionaryAsync(l => l.ItemId, l => l.Quantity);
        var costs = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.AverageCost);
        t.VarianceValue = counted.Sum(l => (long)Math.Round((l.Counted!.Value - now.GetValueOrDefault(l.ItemId)) * costs.GetValueOrDefault(l.ItemId), MidpointRounding.AwayFromZero));
        // Marked posted before the count posting saves, so the stock, the books and the stock take change in one save.
        (t.Status, t.PostedBy, t.PostedAt) = (StockTakeStatus.Posted, ctx.Actor, DateTimeOffset.UtcNow);
        await stock.Count(t.LocationId, counted.Select(l => new CountInput(l.ItemId, l.Counted!.Value)).ToList(), $"Stock take {t.Number}");
        return await Get(id, true);
    }

    public async Task<object> Cancel(Guid id)
    {
        var t = await Load(id);
        if (t.Status != StockTakeStatus.Counting) throw new AppException("This count is finished.");
        t.Status = StockTakeStatus.Cancelled;
        await db.SaveChangesAsync();
        return await Get(id, true);
    }

    public async Task<object> List()
    {
        var takes = await db.StockTakes.Include(s => s.Lines).OrderByDescending(s => s.StartedAt).Take(100).ToListAsync();
        var places = await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);
        return takes.Select(t => new
        {
            t.Id, t.Number, location = places.GetValueOrDefault(t.LocationId, ""), status = t.Status.ToString(), t.Blind, t.StartedBy, t.StartedAt, t.PostedBy, t.PostedAt, t.VarianceValue,
            counted = t.Lines.Count(l => l.Counted is not null), total = t.Lines.Count,
            accuracy = t.Status == StockTakeStatus.Posted && t.Lines.Any(l => l.Counted is not null)
                ? Math.Round(100.0 * t.Lines.Count(l => l.Counted is not null && l.Counted == l.Expected) / t.Lines.Count(l => l.Counted is not null), 1) : (double?)null,
        });
    }
}
