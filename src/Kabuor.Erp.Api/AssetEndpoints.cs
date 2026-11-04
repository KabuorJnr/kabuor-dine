// The asset register's API. Anyone who can see stock or maintenance can look things up and scan labels; adding,
// moving, giving to a custodian and writing things off needs the assets role (stores, procurement, maintenance).
using Kabuor.Erp.Assets;
using Kabuor.Erp.Auth;

namespace Kabuor.Erp;

public static class AssetEndpoints
{
    public static void Map(WebApplication app)
    {
        var assets = app.MapGroup("/api/assets").RequireAuthorization(Policies.StockView);
        assets.MapGet("", (AssetService s, string? q, string? status, string? category, Guid? locationId) => s.List(q, status, category, locationId));
        assets.MapGet("/{id:guid}", (Guid id, AssetService s) => s.Get(id));
        assets.MapGet("/scan", (AssetService s, string code, bool? seen) => s.Scan(code, seen == true));
        assets.MapPost("", async (AssetInput input, AssetService s) => Results.Created("", await s.Add(input))).RequireAuthorization(Policies.Assets);
        assets.MapPut("/{id:guid}", (Guid id, AssetInput input, AssetService s) => s.Update(id, input)).RequireAuthorization(Policies.Assets);
        assets.MapPost("/{id:guid}/actions", (Guid id, AssetAction input, AssetService s) => s.Act(id, input)).RequireAuthorization(Policies.Assets);
        assets.MapGet("/types", async (AssetService s) => (await s.Types()).Select(t => new { code = t.Code, name = t.Name, custom = t.Custom }));
        assets.MapPost("/types", (AssetTypeInput input, AssetService s) => s.AddType(input)).RequireAuthorization(Policies.Assets);
    }
}
