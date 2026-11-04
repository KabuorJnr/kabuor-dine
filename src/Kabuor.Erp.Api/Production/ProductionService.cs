// Recipes and production runs. Finishing a run:
//   ingredients leave stock at their average cost (Consumed), finished goods arrive at material + conversion
//   cost per unit (Produced). Both sides are Stock, so only the conversion cost touches the books:
//   Dr Stock   Cr "Production costs moved into stock" (labour, gas, power become part of the product's value).
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Production;

public sealed record RecipeLineInput(Guid ItemId, decimal Quantity);
public sealed record RecipeInput(string Name, Guid OutputItemId, decimal OutputQuantity, long? ConversionCostPerBatch, string? Notes, bool? Active, List<RecipeLineInput> Lines);
public sealed record RunInput(Guid RecipeId, decimal Batches, Guid? LocationId, DateOnly? PlannedFor, string? Notes);
public sealed record CompleteInput(decimal? ActualOutput);

public sealed class ProductionService(ErpDb db, Ledger ledger, Numbers numbers, InventoryService stock, RequestContext ctx)
{
    public async Task<Recipe> GetRecipe(Guid id) => await db.Recipes.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id) ?? throw new AppException("Not found.", 404);

    public async Task<Recipe> SaveRecipe(Guid? id, RecipeInput input)
    {
        var r = id is { } rid ? await GetRecipe(rid) : new Recipe { Name = "" };
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Name the blueprint.");
        if (input.OutputQuantity <= 0) throw new AppException("Say how many one batch makes.");
        if (input.ConversionCostPerBatch < 0) throw new AppException("Costs can't be negative.");
        if (input.Lines is not { Count: > 0 }) throw new AppException("Add at least one ingredient.");
        var ids = input.Lines.Select(l => l.ItemId).Append(input.OutputItemId).Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var output = items.GetValueOrDefault(input.OutputItemId) ?? throw new AppException("Choose what the blueprint makes.");
        if (!output.TrackStock) throw new AppException($"Turn on stock tracking for {output.Name} first.");
        if (input.Lines.Any(l => l.ItemId == input.OutputItemId)) throw new AppException("A product can't be an ingredient of itself.");
        if (input.Lines.Select(l => l.ItemId).Distinct().Count() != input.Lines.Count) throw new AppException("Each ingredient once, please: add the quantities together.");
        foreach (var l in input.Lines)
        {
            var it = items.GetValueOrDefault(l.ItemId) ?? throw new AppException("One of the ingredients no longer exists.");
            if (!it.TrackStock) throw new AppException($"Turn on stock tracking for {it.Name} so production can use it.");
            if (l.Quantity <= 0) throw new AppException($"Enter how much {it.Name} a batch uses.");
        }
        (r.Name, r.OutputItemId, r.OutputQuantity, r.ConversionCostPerBatch, r.Notes, r.Active) = (name, output.Id, input.OutputQuantity, input.ConversionCostPerBatch ?? 0, (input.Notes ?? "").Trim(), input.Active ?? r.Active);
        db.RecipeLines.RemoveRange(r.Lines);
        r.Lines = input.Lines.Select(l => new RecipeLine { ItemId = l.ItemId, Quantity = l.Quantity }).ToList();
        if (id is null) db.Recipes.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    /// <summary>For a recipe at a store: what's on hand of each ingredient, how many batches that allows, and what a
    /// unit costs to make at today's average costs (with the margin at the selling price).</summary>
    public async Task<object> Capacity(Guid recipeId, Guid? locationId)
    {
        var r = await GetRecipe(recipeId);
        var loc = await stock.LocationOrDefault(locationId);
        var ids = r.Lines.Select(l => l.ItemId).Append(r.OutputItemId).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var levels = await db.StockLevels.Where(s => s.LocationId == loc.Id && ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, s => s.Quantity);
        var lines = r.Lines.Select(l =>
        {
            var it = items[l.ItemId];
            var have = levels.GetValueOrDefault(l.ItemId);
            return new { l.ItemId, it.Name, it.Unit, perBatch = l.Quantity, onHand = have, batches = Math.Floor(Math.Max(0, have) / l.Quantity), costPerBatch = (long)Math.Round(l.Quantity * it.AverageCost) };
        }).ToList();
        var material = lines.Sum(l => l.costPerBatch);
        var unitCost = (material + r.ConversionCostPerBatch) / r.OutputQuantity;
        var output = items[r.OutputItemId];
        var netPrice = Tax.SplitInclusive(output.Price, output.TaxType).Net;
        return new
        {
            recipe = new { r.Id, r.Name, r.OutputQuantity, r.ConversionCostPerBatch, output = output.Name, output.Unit },
            location = new { loc.Id, loc.Name },
            lines,
            maxBatches = lines.Count == 0 ? 0 : lines.Min(l => l.batches),
            limitedBy = lines.MinBy(l => l.batches)?.Name,
            costPerBatch = material + r.ConversionCostPerBatch,
            unitCost = (long)Math.Round(unitCost),
            price = output.Price,
            margin = netPrice > 0 ? (int?)Math.Round(100 * (netPrice - unitCost) / netPrice) : null,
        };
    }

    public async Task<ProductionRun> Plan(RunInput input)
    {
        var r = await GetRecipe(input.RecipeId);
        if (!r.Active) throw new AppException("That blueprint is switched off.");
        if (input.Batches <= 0 || input.Batches > 10_000) throw new AppException("Enter how many batches.");
        var loc = await stock.LocationOrDefault(input.LocationId);
        var run = new ProductionRun
        {
            Number = await numbers.Next("PR"), RecipeId = r.Id, RecipeName = r.Name, OutputItemId = r.OutputItemId, Batches = input.Batches,
            LocationId = loc.Id, PlannedFor = input.PlannedFor ?? Clock.Today, OutputQuantity = r.OutputQuantity * input.Batches, Notes = (input.Notes ?? "").Trim(),
        };
        db.ProductionRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Done: ingredients out, finished goods in at cost. Refuses if the store doesn't have enough of anything.</summary>
    public async Task<ProductionRun> Complete(Guid runId, CompleteInput input)
    {
        var run = await db.ProductionRuns.SingleOrDefaultAsync(x => x.Id == runId) ?? throw new AppException("Not found.", 404);
        if (run.Status != ProductionStatus.Planned) throw new AppException("This run is already finished or cancelled.");
        var r = await GetRecipe(run.RecipeId);
        var actual = input.ActualOutput ?? r.OutputQuantity * run.Batches;
        if (actual <= 0) throw new AppException("Enter how many came out.");
        var ids = r.Lines.Select(l => l.ItemId).Append(r.OutputItemId).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var levels = await db.StockLevels.Where(s => s.LocationId == run.LocationId && ids.Contains(s.ItemId)).ToDictionaryAsync(s => s.ItemId, s => s.Quantity);
        var short_ = r.Lines.Select(l => new { item = items[l.ItemId], need = l.Quantity * run.Batches, have = levels.GetValueOrDefault(l.ItemId) }).Where(x => x.need > x.have).ToList();
        if (short_.Count > 0)
            throw new AppException("Not enough in stock: " + string.Join("; ", short_.Select(x => $"{x.item.Name} needs {x.need:0.##} {x.item.Unit}, {x.have:0.##} on hand")) + ".");

        long material = 0;
        foreach (var l in r.Lines)
            material += await stock.Consume(items[l.ItemId], run.LocationId, l.Quantity * run.Batches, "Production", run.Id, run.Number);
        var conversion = (long)Math.Round(r.ConversionCostPerBatch * run.Batches, MidpointRounding.AwayFromZero);
        var unitCost = (decimal)(material + conversion) / actual;
        await stock.Receive(items[r.OutputItemId], run.LocationId, actual, unitCost, StockMoveKind.Produced, "Production", run.Id, run.Number);
        if (conversion > 0)
            await ledger.Post(Clock.Today, $"Production {run.Number}: labour and running costs into stock", "Production", run.Id,
                (SystemAccounts.Stock, conversion, 0), (SystemAccounts.ProductionAbsorbed, 0, conversion));
        (run.Status, run.OutputQuantity, run.MaterialCost, run.ConversionCost, run.UnitCost, run.CompletedAt, run.CompletedBy) =
            (ProductionStatus.Done, actual, material, conversion, unitCost, DateTimeOffset.UtcNow, ctx.Actor);
        await db.SaveChangesAsync();
        return run;
    }

    public async Task<ProductionRun> Cancel(Guid runId)
    {
        var run = await db.ProductionRuns.SingleOrDefaultAsync(x => x.Id == runId) ?? throw new AppException("Not found.", 404);
        if (run.Status != ProductionStatus.Planned) throw new AppException("Only planned runs can be cancelled.");
        run.Status = ProductionStatus.Cancelled;
        await db.SaveChangesAsync();
        return run;
    }
}
