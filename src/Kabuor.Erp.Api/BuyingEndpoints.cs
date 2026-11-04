// Endpoints for buying (suppliers, purchase orders, bills, paying suppliers) and stock.
// Who can do what: storekeepers and up raise orders, receive goods and count stock; only the owner or an
// accountant posts bills (they create debt) and pays suppliers (money out).
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Kabuor.Erp.Purchases;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record SupplierInput(string Name, string? Phone, string? Email, string? KraPin, string? Address, string? PaymentDetails, string? Notes);
public sealed record CountRequest(Guid? LocationId, List<CountInput> Counts, string? Note);
public sealed record TransferRequest(Guid ItemId, Guid From, Guid To, decimal Quantity, string? Note);
public sealed record OpeningRequest(Guid ItemId, Guid? LocationId, decimal Quantity, long UnitCost);
public sealed record LocationInput(string Name);

public static class BuyingEndpoints
{
    static readonly PurchaseStatus[] Open = [PurchaseStatus.Open, PurchaseStatus.PartPaid];

    public static void Map(RouteGroupBuilder api)
    {
        // ---------- suppliers ----------
        var suppliers = api.MapGroup("/suppliers").RequireAuthorization(Policies.BuyView);
        suppliers.MapGet("", async (ErpDb db, string? q, string? filter) =>
        {
            var today = Clock.Today;
            var query = db.Suppliers.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(s => EF.Functions.Like(s.Name, like) || EF.Functions.Like(s.Phone, like) || EF.Functions.Like(s.Number, like) || EF.Functions.Like(s.KraPin, like));
            }
            var rows = await query.Select(s => new
            {
                s.Id, s.Number, s.Name, s.Phone, s.KraPin,
                weOwe = db.PurchaseDocs.Where(d => d.SupplierId == s.Id && d.Kind == PurchaseKind.Bill && (d.Status == PurchaseStatus.Open || d.Status == PurchaseStatus.PartPaid)).Sum(d => d.Total - d.Paid),
                overdue = db.PurchaseDocs.Where(d => d.SupplierId == s.Id && d.Kind == PurchaseKind.Bill && (d.Status == PurchaseStatus.Open || d.Status == PurchaseStatus.PartPaid) && d.DueDate < today).Sum(d => d.Total - d.Paid),
            }).ToListAsync();
            var counts = new { all = rows.Count, owed = rows.Count(r => r.weOwe > 0), overdue = rows.Count(r => r.overdue > 0) };
            var shown = (filter switch { "owed" => rows.Where(r => r.weOwe > 0), "overdue" => rows.Where(r => r.overdue > 0), _ => rows.AsEnumerable() })
                .OrderByDescending(r => r.overdue).ThenByDescending(r => r.weOwe).ThenBy(r => r.Name).Take(200).ToList();
            return new { counts, items = shown };
        });
        suppliers.MapGet("/{id:guid}", async (Guid id, ErpDb db) =>
        {
            var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            var docs = await db.PurchaseDocs.Where(d => d.SupplierId == id).OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number)
                .Select(d => new { d.Id, d.Kind, d.Number, d.SupplierRef, d.Status, d.IssueDate, d.DueDate, d.Total, d.Paid }).Take(200).ToListAsync();
            var payments = await db.SupplierPayments.Where(p => p.SupplierId == id).OrderByDescending(p => p.PaidAt).Take(100).ToListAsync();
            var open = docs.Where(d => d.Kind == PurchaseKind.Bill && Open.Contains(d.Status)).ToList();
            return new
            {
                supplier = s, documents = docs, payments,
                weOwe = open.Sum(d => d.Total - d.Paid),
                overdue = open.Where(d => d.DueDate < Clock.Today).Sum(d => d.Total - d.Paid),
                boughtTotal = docs.Where(d => d.Kind == PurchaseKind.Bill && d.Status != PurchaseStatus.Void && d.Status != PurchaseStatus.Draft).Sum(d => d.Total),
            };
        });
        suppliers.MapPost("", async (SupplierInput input, ErpDb db, Numbers numbers) =>
        {
            var s = new Supplier { Number = await numbers.Next("S"), Name = "" };
            await Apply(db, s, input);
            db.Suppliers.Add(s);
            await db.SaveChangesAsync();
            return Results.Created($"/api/suppliers/{s.Id}", s);
        }).RequireAuthorization(Policies.Buy);
        suppliers.MapPut("/{id:guid}", async (Guid id, SupplierInput input, ErpDb db) =>
        {
            var s = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            await Apply(db, s, input);
            await db.SaveChangesAsync();
            return s;
        }).RequireAuthorization(Policies.Buy);

        // ---------- purchase orders and bills ----------
        var buy = api.MapGroup("/purchases").RequireAuthorization(Policies.BuyView);
        buy.MapGet("", async (ErpDb db, PurchaseKind? kind, string? status, string? q, int? skip, int? take) =>
        {
            var today = Clock.Today;
            var (s, t) = Paging.Of(skip, take);
            var query = db.PurchaseDocs.Where(d => d.Kind == (kind ?? PurchaseKind.Bill));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(d => EF.Functions.Like(d.Number, like) || EF.Functions.Like(d.SupplierRef, like) || EF.Functions.Like(d.Supplier!.Name, like));
            }
            var counts = new
            {
                all = await query.CountAsync(),
                draft = await query.CountAsync(d => d.Status == PurchaseStatus.Draft),
                unpaid = await query.CountAsync(d => Open.Contains(d.Status)),
                overdue = await query.CountAsync(d => Open.Contains(d.Status) && d.DueDate < today),
                paid = await query.CountAsync(d => d.Status == PurchaseStatus.Paid),
                sent = await query.CountAsync(d => d.Status == PurchaseStatus.Sent),
                received = await query.CountAsync(d => d.Status == PurchaseStatus.Received),
            };
            query = status switch
            {
                "draft" => query.Where(d => d.Status == PurchaseStatus.Draft),
                "unpaid" => query.Where(d => Open.Contains(d.Status)),
                "overdue" => query.Where(d => Open.Contains(d.Status) && d.DueDate < today),
                "paid" => query.Where(d => d.Status == PurchaseStatus.Paid),
                "sent" => query.Where(d => d.Status == PurchaseStatus.Sent),
                "received" => query.Where(d => d.Status == PurchaseStatus.Received),
                _ => query,
            };
            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number).Skip(s).Take(t)
                .Select(d => new { d.Id, d.Kind, d.Number, d.SupplierRef, d.Status, d.IssueDate, d.DueDate, d.Total, d.Paid, supplier = d.Supplier!.Name, supplierId = d.SupplierId })
                .ToListAsync();
            return new { counts, page = new Page<object>(rows.Cast<object>().ToList(), total, s, t) };
        });
        buy.MapGet("/{id:guid}", async (Guid id, PurchaseService svc, ErpDb db) =>
        {
            var d = await svc.Get(id);
            var payments = await db.SupplierPayments.Where(p => p.PurchaseDocId == id).OrderBy(p => p.PaidAt).ToListAsync();
            var location = d.LocationId is { } lid ? await db.Locations.Where(l => l.Id == lid).Select(l => l.Name).SingleOrDefaultAsync() : null;
            var waitingForFinance = d.Status == PurchaseStatus.Draft && await svc.WaitingForFinance(d);
            return new { document = d, payments, location, waitingForFinance };
        });
        buy.MapPost("/{id:guid}/sign-off", async (Guid id, Kabuor.Erp.Operations.ReasonInput input, PurchaseService svc) => await svc.SignOff(id, input.Reason)).RequireAuthorization(Policies.Finance);
        buy.MapPost("", async (PurchaseInput input, PurchaseService svc, RequestContext ctx) =>
        {
            if (input.Kind == PurchaseKind.Bill) RequireFinance(ctx);
            return Results.Created("", await svc.Create(input));
        }).RequireAuthorization(Policies.Buy);
        buy.MapPut("/{id:guid}", (Guid id, PurchaseInput input, PurchaseService svc, RequestContext ctx) =>
        {
            if (input.Kind == PurchaseKind.Bill) RequireFinance(ctx);
            return svc.Update(id, input);
        }).RequireAuthorization(Policies.Buy);
        buy.MapPost("/{id:guid}/post", async (Guid id, PurchaseService svc, RequestContext ctx) =>
        {
            var d = await svc.Get(id);
            if (d.Kind == PurchaseKind.Bill) RequireFinance(ctx);
            return await svc.Post(id);
        }).RequireAuthorization(Policies.Buy);
        buy.MapPost("/{id:guid}/receive", async (Guid id, PurchaseService svc) => await svc.ToBill(id)).RequireAuthorization(Policies.Stock);
        buy.MapPost("/{id:guid}/pay", async (Guid id, SupplierPaymentInput input, PurchaseService svc) => await svc.Pay(id, input)).RequireAuthorization(Policies.Finance);
        buy.MapPost("/{id:guid}/void", async (Guid id, PurchaseService svc) => await svc.Void(id)).RequireAuthorization(Policies.Finance);
        buy.MapGet("/{id:guid}/match-check", async (Guid id, PurchaseService svc) => await svc.MatchCheck(id)).RequireAuthorization(Policies.BuyView);

        // Expense accounts a bill line can go to (the plain-words chart).
        api.MapGet("/expense-accounts", async (ErpDb db) =>
            await db.Accounts.Where(a => a.Type == AccountType.Expense && a.SystemKey != "COGS" && a.SystemKey != "STOCK_ADJ").OrderBy(a => a.Code).Select(a => new { a.Id, a.Code, a.Name }).ToListAsync());

        // ---------- stock ----------
        var stock = api.MapGroup("/stock").RequireAuthorization(Policies.StockView);
        stock.MapGet("", (InventoryService inv, string? filter, string? q, string? use) => inv.Overview(filter, q, use));
        stock.MapGet("/{itemId:guid}/moves", async (Guid itemId, ErpDb db) =>
        {
            var locations = await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);
            var moves = await db.StockMoves.Where(m => m.ItemId == itemId).OrderByDescending(m => m.At).Take(200).ToListAsync();
            return moves.Select(m => new { m.Id, kind = m.Kind.ToString(), m.Quantity, unitCost = (long)Math.Round(m.UnitCost), m.Reference, m.Note, m.Actor, m.At, location = locations.GetValueOrDefault(m.LocationId, "?"), m.SourceType, m.SourceId });
        });
        stock.MapPost("/count", async (CountRequest r, InventoryService inv) => new { changes = await inv.Count(r.LocationId, r.Counts ?? [], (r.Note ?? "").Trim()) }).RequireAuthorization(Policies.Stock);
        stock.MapPost("/transfer", async (TransferRequest r, InventoryService inv) =>
        {
            await inv.Transfer(r.ItemId, r.From, r.To, r.Quantity, (r.Note ?? "").Trim());
            return Results.NoContent();
        }).RequireAuthorization(Policies.Stock);
        stock.MapPost("/opening", async (OpeningRequest r, InventoryService inv) =>
        {
            await inv.Opening(r.ItemId, r.LocationId, r.Quantity, r.UnitCost);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Finance);

        var locations = api.MapGroup("/locations");
        locations.MapGet("", async (ErpDb db, InventoryService inv) =>
        {
            if (!await db.Locations.AnyAsync())
            {
                await inv.DefaultLocation();
                await db.SaveChangesAsync();
            }
            return await db.Locations.Where(l => l.Active).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).ToListAsync();
        });
        locations.MapPost("", async (LocationInput input, ErpDb db) =>
        {
            var name = (input.Name ?? "").Trim();
            if (name.Length < 2) throw new AppException("Name the store, e.g. “Thika branch”.");
            if (await db.Locations.AnyAsync(l => l.Name == name)) throw new AppException("You already have a store with that name.", 409);
            var l = new Location { Name = name, IsDefault = !await db.Locations.AnyAsync() };
            db.Locations.Add(l);
            await db.SaveChangesAsync();
            return Results.Created("", l);
        }).RequireAuthorization(Policies.Admin);
    }

    /// <summary>For rules that depend on the request (bills create debt; orders don't).</summary>
    static void RequireFinance(RequestContext ctx)
    {
        if (!Policies.Roles[Policies.Finance].Contains(ctx.Role)) throw new AppException("Only the owner or an accountant can enter bills.", 403);
    }

    static async Task Apply(ErpDb db, Supplier s, SupplierInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter the supplier's name.");
        var phone = string.IsNullOrWhiteSpace(input.Phone) ? "" : Phones.ToMsisdn(input.Phone) ?? throw new AppException("That phone number doesn't look right (e.g. 0712 345 678).");
        var pin = (input.KraPin ?? "").Trim().ToUpperInvariant();
        if (pin.Length > 0 && !Etims.SandboxEtimsClient.IsKraPin(pin)) throw new AppException("That KRA PIN doesn't look right (e.g. P051234567X).");
        if (await db.Suppliers.AnyAsync(x => x.Name == name && x.Id != s.Id)) throw new AppException("You already have a supplier with that name.", 409);
        (s.Name, s.Phone, s.Email, s.KraPin, s.Address, s.PaymentDetails, s.Notes) = (name, phone, (input.Email ?? "").Trim(), pin, (input.Address ?? "").Trim(), (input.PaymentDetails ?? "").Trim(), (input.Notes ?? "").Trim());
    }
}
