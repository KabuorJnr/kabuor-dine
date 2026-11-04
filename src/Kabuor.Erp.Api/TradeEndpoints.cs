// Trade terms a distributor or wholesaler needs: price lists per customer, the other units an item is sold
// and bought in (carton, bag, crate), and what's about to expire.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record PriceListPrice(Guid ItemId, long Price);
public sealed record PriceListInput(string Name, List<PriceListPrice>? Prices);
public sealed record UnitInput(string Name, decimal Factor, long? Price);
public sealed record WriteOffInput(string? Note);

public static class TradeEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---------- price lists ----------
        var lists = api.MapGroup("/price-lists").RequireAuthorization(Policies.SalesView);
        lists.MapGet("", async (ErpDb db) =>
        {
            var all = await db.PriceLists.Include(p => p.Items).OrderBy(p => p.Name).ToListAsync();
            var customers = await db.Customers.Where(c => c.PriceListId != null).GroupBy(c => c.PriceListId).Select(g => new { id = g.Key, n = g.Count() }).ToListAsync();
            return all.Select(p => new { p.Id, p.Name, customers = customers.FirstOrDefault(c => c.id == p.Id)?.n ?? 0, prices = p.Items.Select(i => new { i.ItemId, i.Price }) });
        });
        lists.MapPost("", async (PriceListInput input, ErpDb db) =>
        {
            var p = new PriceList { Name = "" };
            await Apply(db, p, input);
            db.PriceLists.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/price-lists/{p.Id}", new { p.Id, p.Name });
        }).RequireAuthorization(Policies.Admin);
        lists.MapPut("/{id:guid}", async (Guid id, PriceListInput input, ErpDb db) =>
        {
            var p = await db.PriceLists.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            await Apply(db, p, input);
            await db.SaveChangesAsync();
            return new { p.Id, p.Name };
        }).RequireAuthorization(Policies.Admin);
        lists.MapDelete("/{id:guid}", async (Guid id, ErpDb db) =>
        {
            var p = await db.PriceLists.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            await db.Customers.Where(c => c.PriceListId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.PriceListId, (Guid?)null));
            db.PriceListItems.RemoveRange(p.Items);
            db.PriceLists.Remove(p);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // ---------- units an item comes in ----------
        api.MapGet("/items/units", async (ErpDb db) => await db.ItemUnits.OrderBy(u => u.Factor).ToListAsync()).RequireAuthorization(Policies.Read);
        api.MapPut("/items/{id:guid}/units", async (Guid id, List<UnitInput> input, ErpDb db) =>
        {
            var item = await db.Items.SingleOrDefaultAsync(i => i.Id == id) ?? throw new AppException("Not found.", 404);
            var existing = await db.ItemUnits.Where(u => u.ItemId == id).ToListAsync();
            var wanted = input.Where(u => !string.IsNullOrWhiteSpace(u.Name)).ToList();
            if (wanted.Select(u => u.Name.Trim().ToLowerInvariant()).Distinct().Count() != wanted.Count) throw new AppException("Each unit needs a different name.");
            foreach (var u in wanted)
            {
                if (u.Name.Trim().Equals(item.Unit, StringComparison.OrdinalIgnoreCase)) throw new AppException($"“{item.Unit}” is already the base unit.");
                if (u.Factor <= 1 || u.Factor > 100_000) throw new AppException($"Say how many {item.Unit} are in one {u.Name.Trim()} (more than 1).");
                if (u.Price < 0) throw new AppException("Prices can't be negative.");
            }
            db.ItemUnits.RemoveRange(existing.Where(e => wanted.All(w => !w.Name.Trim().Equals(e.Name, StringComparison.OrdinalIgnoreCase))));
            foreach (var u in wanted)
            {
                var row = existing.FirstOrDefault(e => e.Name.Equals(u.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (row is null) db.ItemUnits.Add(row = new ItemUnit { ItemId = id, Name = u.Name.Trim() });
                (row.Factor, row.Price) = (u.Factor, u.Price ?? 0);
            }
            await db.SaveChangesAsync();
            return await db.ItemUnits.Where(u => u.ItemId == id).OrderBy(u => u.Factor).ToListAsync();
        }).RequireAuthorization(Policies.Sell);

        // ---------- expiry ----------
        api.MapGet("/stock/expiring", (InventoryService s, int? days) => s.Expiring(days ?? 90)).RequireAuthorization(Policies.StockView);
        api.MapPost("/stock/batches/{id:guid}/write-off", (Guid id, WriteOffInput? input, InventoryService s) => s.WriteOff(id, (input?.Note ?? "").Trim())).RequireAuthorization(Policies.Stock);
    }

    static async Task Apply(ErpDb db, PriceList p, PriceListInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Name the price list (e.g. Wholesale).");
        if (await db.PriceLists.AnyAsync(x => x.Name == name && x.Id != p.Id)) throw new AppException("There's already a price list with that name.", 409);
        p.Name = name;
        if (input.Prices is null) return;
        var prices = input.Prices.GroupBy(x => x.ItemId).Select(g => g.Last()).ToList();
        if (prices.Any(x => x.Price < 0)) throw new AppException("Prices can't be negative.");
        var ids = prices.Select(x => x.ItemId).ToList();
        if (await db.Items.CountAsync(i => ids.Contains(i.Id)) != ids.Count) throw new AppException("One of the items no longer exists.");
        db.PriceListItems.RemoveRange(p.Items.Where(i => !ids.Contains(i.ItemId)));
        foreach (var x in prices)
        {
            var row = p.Items.FirstOrDefault(i => i.ItemId == x.ItemId);
            if (row is null) p.Items.Add(row = new PriceListItem { ItemId = x.ItemId });
            row.Price = x.Price;
        }
    }
}
