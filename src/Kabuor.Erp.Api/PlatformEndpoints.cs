// The platform console's API (Kabuor Builds only). /me answers everyone, so the web app knows whether to show the
// console; everything else is refused unless the signed-in user is a platform admin.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Platform;

namespace Kabuor.Erp;

public static class PlatformEndpoints
{
    public static void Map(WebApplication app)
    {
        var platform = app.MapGroup("/api/platform").RequireAuthorization(Policies.Read);
        platform.MapGet("/me", async (PlatformService s) => { var (allowed, why) = await s.Access(); return new { allowed, why }; });
        var admin = platform.MapGroup("").AddEndpointFilter(async (inv, next) =>
        {
            var (allowed, why) = await inv.HttpContext.RequestServices.GetRequiredService<PlatformService>().Access();
            return allowed ? await next(inv) : Results.Json(new { error = why }, statusCode: 403);
        });
        admin.MapGet("/health", (PlatformService s, bool? demo) => s.Health(demo ?? false));
        admin.MapGet("/analytics", (PlatformService s, int? days, bool? demo) => s.Analytics(days ?? 30, demo ?? true));
        admin.MapGet("/resources", (PlatformService s) => s.Resources());
        admin.MapGet("/plans", (PlatformService s) => s.PlanList());
        admin.MapGet("/erasures", (PlatformService s) => s.Erasures());
        admin.MapGet("/businesses", (PlatformService s) => s.Businesses());
        admin.MapGet("/businesses/{id:guid}", (PlatformService s, Guid id) => s.BusinessDetail(id));
        admin.MapPost("/businesses/{id:guid}/plan", (PlatformService s, Guid id, PlatformService.UpdatePlanInput input) => s.UpdatePlan(id, input));
        admin.MapPost("/businesses/{id:guid}/wipe", (PlatformService s, Guid id, PlatformService.WipeBusinessInput input) => s.WipeBusiness(id, input));
        admin.MapGet("/issues", (PlatformService s) => s.Issues());
        admin.MapPost("/issues/{id:guid}/reply", (PlatformService s, Guid id, PlatformService.ReplyIssueInput input) => s.ReplyIssue(id, input));
        admin.MapPost("/actions/{action}", (PlatformService s, string action) => s.ExecuteAction(action));
    }
}
