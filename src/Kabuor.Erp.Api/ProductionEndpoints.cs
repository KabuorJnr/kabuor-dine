// Recipes, production runs and "how many can we make". Storekeepers and up run production.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Production;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public static class ProductionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var p = api.MapGroup("/production").RequireAuthorization(Policies.Make);
        p.MapGet("/recipes", async (ErpDb db) =>
        {
            var recipes = await db.Recipes.Include(r => r.Lines).OrderBy(r => r.Name).ToListAsync();
            var ids = recipes.Select(r => r.OutputItemId).Distinct().ToList();
            var names = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => new { i.Name, i.Unit });
            return recipes.Select(r => new { r.Id, r.Name, r.OutputItemId, output = names.GetValueOrDefault(r.OutputItemId)?.Name, unit = names.GetValueOrDefault(r.OutputItemId)?.Unit, r.OutputQuantity, r.ConversionCostPerBatch, r.Active, ingredients = r.Lines.Count });
        });
        p.MapGet("/recipes/{id:guid}", async (Guid id, ProductionService s) => await s.GetRecipe(id));
        p.MapGet("/recipes/{id:guid}/capacity", async (Guid id, Guid? locationId, ProductionService s) => await s.Capacity(id, locationId));
        p.MapPost("/recipes", async (RecipeInput input, ProductionService s) => Results.Created("", await s.SaveRecipe(null, input)));
        p.MapPut("/recipes/{id:guid}", async (Guid id, RecipeInput input, ProductionService s) => await s.SaveRecipe(id, input));

        p.MapGet("/runs", async (ErpDb db, string? status) =>
        {
            var q = db.ProductionRuns.AsQueryable();
            var counts = new { planned = await q.CountAsync(r => r.Status == ProductionStatus.Planned), done = await q.CountAsync(r => r.Status == ProductionStatus.Done), all = await q.CountAsync() };
            q = status switch { "planned" => q.Where(r => r.Status == ProductionStatus.Planned), "done" => q.Where(r => r.Status == ProductionStatus.Done), _ => q };
            var runs = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync();
            var locations = await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);
            return new { counts, items = runs.Select(r => new { r.Id, r.Number, r.RecipeName, r.Batches, r.OutputQuantity, r.Status, r.PlannedFor, r.MaterialCost, r.ConversionCost, unitCost = (long)Math.Round(r.UnitCost), r.CompletedAt, r.CompletedBy, location = locations.GetValueOrDefault(r.LocationId) }) };
        });
        p.MapPost("/runs", async (RunInput input, ProductionService s) => Results.Created("", await s.Plan(input)));
        p.MapPost("/runs/{id:guid}/complete", async (Guid id, CompleteInput input, ProductionService s) => await s.Complete(id, input));
        p.MapPost("/runs/{id:guid}/cancel", async (Guid id, ProductionService s) => await s.Cancel(id));
    }
}
