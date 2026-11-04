// Kabuor Dine's API. The guest's side (/api/dine/m/{table secret}) is on the open internet: the QR's secret finds
// the table and the business, and a signed token (X-Dine-Token) holds the guest's place in their table's visit. The
// staff side (/api/dine) is behind sign-in: the floor and kitchen for the team, the menu, tables and settings for the
// owner and accountant.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Dine;

public static class DineEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---------- the guest, from the table's QR ----------
        var guest = api.MapGroup("/dine/m/{token}").RequireRateLimiting("dine").AddEndpointFilter(async (inv, next) =>
        {
            var http = inv.HttpContext;
            var token = (http.Request.RouteValues["token"] as string ?? "").Trim().ToLowerInvariant();
            if (token.Length is < 12 or > 40) return Results.NotFound(new { error = "This QR code isn't one of ours. Please ask a waiter." });
            var db = http.RequestServices.GetRequiredService<ErpDb>();
            var table = await db.DineTables.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.Token == token && t.Active);
            if (table is null) return Results.NotFound(new { error = "This table's QR code has changed. Please ask a waiter for the new one." });
            var c = await db.Companies.Where(x => x.Id == table.CompanyId).Select(x => new { x.Id, x.CreatedAt, x.TrialEndsAt, x.PaidUntil, x.AwaitingFirstPayment }).SingleAsync();
            var ctx = http.RequestServices.GetRequiredService<RequestContext>();
            (ctx.CompanyId, ctx.Actor) = (c.Id, $"{table.Name} (guest)");
            if (c.AwaitingFirstPayment || (http.Request.Method != "GET" && Billing.Plans.Locked(new Company { Name = "", CreatedAt = c.CreatedAt, TrialEndsAt = c.TrialEndsAt, PaidUntil = c.PaidUntil }, DateTimeOffset.UtcNow)))
                return Results.Json(new { error = "The restaurant isn't taking orders from the table right now. Please ask a waiter." }, statusCode: 402);
            http.Items["table"] = table;
            if (http.RequestServices.GetRequiredService<DineTokens>().Read(http.Request.Headers["X-Dine-Token"]) is { } t && t.Company == c.Id) http.Items["session"] = t.Session;
            return await next(inv);
        });
        static DineTable Table(HttpContext h) => (DineTable)h.Items["table"]!;
        static Guid? Mine(HttpContext h) => h.Items["session"] as Guid?;
        // The guest's own visit: their token must be for this table's open visit.
        static async Task<DineSession> Visit(HttpContext h, DineService s)
        {
            var open = await s.OpenSession(Table(h).Id);
            return open is not null && open.Id == Mine(h) ? open : throw new AppException("Order something first, or scan the table's QR again.", 401);
        }

        guest.MapGet("", (HttpContext h, DineService s) => s.GuestMenu(Table(h), Mine(h)));
        guest.MapPost("/orders", async (DineOrderInput input, HttpContext h, DineService s) =>
        {
            var (session, order, token) = await s.GuestOrder(Table(h), Mine(h), input);
            return new { token, order = new { order.Id, order.Number, status = order.Status.ToString() }, visit = await s.Visit(session) };
        });
        guest.MapGet("/visit", async (HttpContext h, DineService s) => await s.Visit(await Visit(h, s)));
        guest.MapPost("/call", async (DineCallInput input, HttpContext h, DineService s) => await s.Call(await Visit(h, s), input.Kind));
        guest.MapPost("/pay", async (DinePayInput input, HttpContext h, DineService s) => await s.GuestPay(await Visit(h, s), input, PublicUrl.Of(h.Request)));
        guest.MapPost("/rate", async (DineRateInput input, HttpContext h, DineService s, ErpDb db) =>
        {
            // Rating comes after paying, when the visit may already be marked paid (still this guest's).
            var id = Mine(h) ?? throw new AppException("Scan the table's QR again.", 401);
            var tableId = Table(h).Id;
            var x = await db.DineSessions.SingleOrDefaultAsync(v => v.Id == id && v.TableId == tableId) ?? throw new AppException("Scan the table's QR again.", 401);
            return await s.Rate(x, input);
        });

        // ---------- the team ----------
        var dine = api.MapGroup("/dine").RequireAuthorization(Policies.Dine);
        dine.MapGet("/floor", (DineService s) => s.Floor());
        dine.MapGet("/kitchen", (string? station, DineService s) => s.KitchenBoard(station));
        dine.MapPost("/orders/{id:guid}/status", (Guid id, DineStatusInput input, DineService s) => s.SetStatus(id, input.Status));
        dine.MapPost("/orders/{id:guid}/station-ready", (Guid id, DineStationInput input, DineService s) => s.StationReady(id, input.Station));
        dine.MapGet("/sessions/{id:guid}", (Guid id, DineService s) => s.SessionDetail(id));
        dine.MapPost("/sessions/{id:guid}/approve", (Guid id, DineService s) => s.Approve(id));
        dine.MapPost("/sessions/{id:guid}/answer", (Guid id, DineService s) => s.Answer(id));
        dine.MapPost("/sessions/{id:guid}/bill", (Guid id, DineService s) => s.PrintBill(id));
        dine.MapPost("/sessions/{id:guid}/pay", (Guid id, DineTillInput input, DineService s) => s.TakePayment(id, input));
        dine.MapPost("/sessions/{id:guid}/clear", (Guid id, bool? force, DineService s) => s.Clear(id, force == true));
        dine.MapPost("/tables/{id:guid}/seat", (Guid id, DineService s) => s.Seat(id));
        dine.MapPost("/tables/{id:guid}/orders", (Guid id, DineOrderInput input, DineService s) => s.StaffOrder(id, input));
        dine.MapGet("/menu", (DineService s) => s.StaffMenu());
        dine.MapGet("/tables", (DineService s) => s.Tables());
        dine.MapGet("/settings", (DineService s) => s.SettingsView());
        // Sold out: the floor and kitchen can take a dish off for the day without the owner.
        dine.MapPost("/menu/{id:guid}/sold-out", (Guid id, DineMenuInput input, DineService s) => s.SaveDish(id, new DineMenuInput(null, null, null, null, null, null, input.SoldOut ?? true, null, null, null, null)));

        var manage = api.MapGroup("/dine").RequireAuthorization(Policies.DineManage);
        manage.MapPost("/menu", (DineMenuInput input, DineService s) => s.SaveDish(null, input));
        manage.MapPut("/menu/{id:guid}", (Guid id, DineMenuInput input, DineService s) => s.SaveDish(id, input));
        manage.MapPost("/menu/sample", (DineService s) => s.SampleMenu());
        manage.MapPost("/tables", (DineTableInput input, DineService s) => s.AddTables(input));
        manage.MapPut("/tables/{id:guid}", (Guid id, DineTableInput input, DineService s) => s.UpdateTable(id, input));
        manage.MapPost("/tables/{id:guid}/new-code", (Guid id, DineService s) => s.NewCode(id));
        manage.MapDelete("/tables/{id:guid}", async (Guid id, DineService s) => { await s.RemoveTable(id); return Results.NoContent(); });
        manage.MapPut("/settings", (DineSettingsInput input, DineService s) => s.SaveSettings(input));
        manage.MapGet("/report", (int? days, DineService s) => s.Report(days ?? 1));
    }
}
