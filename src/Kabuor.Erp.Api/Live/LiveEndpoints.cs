// The live stream (/api/live) and each module workspace's home (/api/workspaces/{module}): the module's links of the
// chain, what happened in it, and what the other modules handed it.
using System.Text.Json;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Intelligence;
using Microsoft.AspNetCore.Http.Features;
using Kabuor.Erp.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Live;

public static class LiveEndpoints
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, string> ChainModule = new()
    {
        ["assets"] = "operations", ["maintenance"] = "operations", ["supply"] = "procurement", ["people"] = "people",
        ["sales"] = "sales", ["marketing"] = "sales", ["finance"] = "finance", ["decide"] = "intelligence",
    };

    public static void Map(WebApplication app)
    {
        var staff = app.MapGroup("/api").RequireAuthorization(Policies.Read);

        // Server-Sent Events: one "change" event per save in this business, a comment every 20 s to keep proxies from
        // closing an idle line. Changes in modules this person can't open arrive without their labels or who made them,
        // just enough for the screen to know it may be out of date.
        staff.MapGet("/live", async (HttpContext http, RequestContext ctx, LiveFeed feed) =>
        {
            var ct = http.RequestAborted;
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache, no-transform";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            using var sub = feed.Subscribe(ctx.CompanyId);
            var role = ctx.Role;
            try
            {
                await http.Response.WriteAsync("retry: 3000\n: connected\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    beat.CancelAfter(TimeSpan.FromSeconds(20));
                    bool more;
                    try { more = await sub.Reader.WaitToReadAsync(beat.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": ping\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        continue;
                    }
                    if (!more) break;
                    while (sub.Reader.TryRead(out var e))
                    {
                        var payload = JsonSerializer.Serialize(ForViewer(e, role), Json);
                        await http.Response.WriteAsync($"event: change\ndata: {payload}\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* the screen closed or moved on */ }
            catch (IOException) { }
        });

        // A module workspace's home. 403 for a module this person can't open, as its pages would answer.
        staff.MapGet("/workspaces/{module}", async (string module, RequestContext ctx, ErpDb db, ForesightService foresight) =>
        {
            module = module.ToLowerInvariant();
            if (!ModuleMap.Order.Contains(module)) throw new AppException("There's no such workspace.", 404);
            if (!ModuleMap.CanSee(ctx.Role, module)) throw new AppException("You don’t have access to this workspace.", 403);

            var chain = JsonSerializer.SerializeToElement(await foresight.Chain(), Json);
            var links = chain.GetProperty("links").EnumerateArray()
                .Where(l => ChainModule.GetValueOrDefault(l.GetProperty("key").GetString() ?? "") == module).ToList();

            var own = ModuleMap.EntitiesOf(module).Where(e => !ModuleMap.Quiet.Contains(e)).ToList();
            var inbound = ModuleMap.Inbound[module].Where(e => ModuleMap.Of(e) is { } m && m != module && ModuleMap.CanSee(ctx.Role, m)).ToList();
            var since = DateTimeOffset.UtcNow.AddDays(-30);
            var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

            var ownRows = await db.AuditEntries.AsNoTracking().Where(a => a.At >= since && own.Contains(a.Entity)).OrderByDescending(a => a.Id).Take(25).ToListAsync();
            var inRows = inbound.Count == 0 ? [] : await db.AuditEntries.AsNoTracking().Where(a => a.At >= since && inbound.Contains(a.Entity)).OrderByDescending(a => a.Id).Take(25).ToListAsync();
            var changesToday = await db.AuditEntries.Where(a => a.At >= today && own.Contains(a.Entity)).CountAsync();
            var people = await db.AuditEntries.Where(a => a.At >= today && own.Contains(a.Entity)).Select(a => a.Actor).Distinct().CountAsync();

            object Row(Domain.AuditEntry a) => new { a.Id, a.At, a.Actor, entity = a.Entity, action = a.Action, a.EntityId, module = ModuleMap.Of(a.Entity), what = ModuleMap.What(a.Entity, a.Action, a.Changes) };
            return Results.Ok(new
            {
                module,
                industry = chain.TryGetProperty("industry", out var ind) ? ind.GetString() : "general",
                links,
                today = new { changes = changesToday, people },
                activity = ownRows.Select(Row),
                handoffs = inRows.Select(Row),
                watches = inbound.Select(e => new { entity = e, module = ModuleMap.Of(e), name = ModuleMap.Name(e) }),
                asOf = DateTimeOffset.UtcNow,
            });
        }).CachedPerCompany();

        // Which workspaces this person can open, for the switcher.
        staff.MapGet("/workspaces", (RequestContext ctx) => ModuleMap.Order.Where(m => ModuleMap.CanSee(ctx.Role, m)));
    }

    static LiveEvent ForViewer(LiveEvent e, Domain.Role role)
    {
        if (e.Modules.All(m => ModuleMap.CanSee(role, m))) return e;
        var visible = e.Changes.Where(c => ModuleMap.CanSee(role, c.Module)).ToArray();
        var anyVisible = visible.Length > 0;
        return e with
        {
            Actor = anyVisible ? e.Actor : "",
            UserId = anyVisible ? e.UserId : null,
            Changes = visible,
            Total = visible.Length,
        };
    }
}
