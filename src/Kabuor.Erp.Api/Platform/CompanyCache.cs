// The read-heavy screens (KPIs, the business OS, workspaces, the month's report) ask the database 20–60 questions
// each. Their answer only changes when the business records something, and every save writes the audit trail, so
// the business's newest audit entry is a version number for all its data: one cheap question tells whether a kept
// answer is still right. Answers are kept per business, person and address (roles see different things), in the
// shared cache (Redis when configured, so every server instance agrees), and expire anyway after a few minutes
// for what changes with the clock alone (overdue, "today").
using System.Text;
using System.Text.Json;
using Kabuor.Erp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Kabuor.Erp.Platform;

public static class CompanyCache
{
    /// <summary>Keeps this GET's answer until the business records something new (or <paramref name="seconds"/> pass).</summary>
    public static RouteHandlerBuilder CachedPerCompany(this RouteHandlerBuilder endpoint, int seconds = 180) => endpoint.AddEndpointFilter(async (inv, next) =>
    {
        var http = inv.HttpContext;
        var ctx = http.RequestServices.GetRequiredService<RequestContext>();
        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        if (!ctx.HasCompany || ctx.UserId is null || !HttpMethods.IsGet(http.Request.Method) || !config.GetValue("Cache:Responses", true))
            return await next(inv);

        var db = http.RequestServices.GetRequiredService<ErpDb>();
        var cache = http.RequestServices.GetRequiredService<IDistributedCache>();
        var version = await db.AuditEntries.MaxAsync(a => (long?)a.Id) ?? 0; // this business's newest change (company filter)
        var key = $"kerp:resp:{ctx.CompanyId:N}:{ctx.UserId:N}:{version}:{http.Request.Path}{http.Request.QueryString}";

        byte[]? kept = null;
        try { kept = await cache.GetAsync(key, http.RequestAborted); } catch { /* cache down: answer from the database */ }
        if (kept is not null)
        {
            http.Response.Headers["X-Cache"] = "hit";
            return Results.Bytes(kept, "application/json; charset=utf-8");
        }

        var result = await next(inv);
        // Only plain answers are kept (not files, redirects or refusals).
        var value = result switch
        {
            null => null,
            IResult r when r is IValueHttpResult v && (r as IStatusCodeHttpResult)?.StatusCode is null or 200 => v.Value,
            IResult => null,
            _ => result,
        };
        if (value is null) return result;
        var json = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), http.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);
        try { await cache.SetAsync(key, json, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(seconds) }, http.RequestAborted); } catch { /* best effort */ }
        http.Response.Headers["X-Cache"] = "miss";
        return Results.Bytes(json, "application/json; charset=utf-8");
    });
}
