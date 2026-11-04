// "Tell me": one search box for the whole app. Returns records the asker's role may see, a few of each kind,
// each with where it opens. Pages and actions are matched in the browser; records come from here.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record SearchHit(string Kind, string Title, string Subtitle, string Url);

public static class SearchEndpoints
{
    static string Plain(SalesDocStatus s, SalesDocKind k) => s switch
    {
        SalesDocStatus.Sent => k == SalesDocKind.Quote ? "sent" : "unpaid",
        SalesDocStatus.PartPaid => "part paid",
        SalesDocStatus.Paid => "paid",
        SalesDocStatus.Accepted => "accepted",
        SalesDocStatus.Void => "void",
        _ => "draft",
    };

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/search", async (string? q, ErpDb db, RequestContext ctx) =>
        {
            var term = (q ?? "").Trim();
            if (term.Length < 2) return new List<SearchHit>();
            var like = $"%{term}%";
            bool Can(string policy) => ctx.Can(policy);
            var hits = new List<SearchHit>();
            if (Can(Policies.SalesView))
            {
            hits.AddRange((await db.Customers.Where(c => EF.Functions.Like(c.Name, like) || EF.Functions.Like(c.Phone, like) || EF.Functions.Like(c.Number, like)).OrderBy(c => c.Name).Take(5).ToListAsync())
                .Select(c => new SearchHit("Customer", c.Name, c.Number, $"/customers/{c.Id}")));
            hits.AddRange((await db.SalesDocs.Where(d => EF.Functions.Like(d.Number, like) || EF.Functions.Like(d.Customer!.Name, like)).OrderByDescending(d => d.IssueDate).Take(5)
                    .Select(d => new { d.Id, d.Number, d.Kind, d.Total, d.Status, customer = d.Customer!.Name }).ToListAsync())
                .Select(d => new SearchHit(d.Kind == SalesDocKind.Quote ? "Quote" : "Invoice", d.Number, $"{d.customer} · KSh {d.Total / 100m:N0} · {Plain(d.Status, d.Kind)}", $"/sales/{d.Id}")));
            }
            hits.AddRange((await db.Items.Where(i => EF.Functions.Like(i.Name, like) || EF.Functions.Like(i.Sku, like) || EF.Functions.Like(i.Barcode, like)).OrderBy(i => i.Name).Take(5).ToListAsync())
                .Select(i => new SearchHit("Item", i.Name, $"KSh {i.Price / 100m:N0}{(i.TrackStock ? " · in stock list" : "")}", i.TrackStock && Can(Policies.StockView) ? $"/stock?q={Uri.EscapeDataString(i.Name)}" : "/settings")));
            if (Can(Policies.Crm))
            hits.AddRange((await db.Leads.Where(l => EF.Functions.Like(l.Name, like) || EF.Functions.Like(l.Business, like) || EF.Functions.Like(l.Phone, like)).OrderBy(l => l.Name).Take(5).ToListAsync())
                .Select(l => new SearchHit("Lead", l.Business.Length > 0 ? l.Business : l.Name, $"{l.Stage} · {l.Number}", $"/pipeline/{l.Id}")));
            if (Can(Policies.BuyView))
            {
                hits.AddRange((await db.Suppliers.Where(s => EF.Functions.Like(s.Name, like) || EF.Functions.Like(s.Phone, like)).OrderBy(s => s.Name).Take(5).ToListAsync())
                    .Select(s => new SearchHit("Supplier", s.Name, s.Number, $"/suppliers/{s.Id}")));
                hits.AddRange((await db.PurchaseDocs.Where(d => EF.Functions.Like(d.Number, like) || EF.Functions.Like(d.SupplierRef, like)).OrderByDescending(d => d.IssueDate).Take(5)
                        .Select(d => new { d.Id, d.Number, d.Kind, supplier = d.Supplier!.Name }).ToListAsync())
                    .Select(d => new SearchHit(d.Kind == PurchaseKind.Order ? "Purchase order" : "Bill", d.Number, d.supplier, $"/purchases/{d.Id}")));
            }
            if (Can(Policies.People))
                hits.AddRange((await db.Employees.Where(e => EF.Functions.Like(e.Name, like) || EF.Functions.Like(e.Number, like) || EF.Functions.Like(e.Phone, like)).OrderBy(e => e.Name).Take(5).ToListAsync())
                    .Select(e => new SearchHit("Employee", e.Name, string.Join(" · ", new[] { e.JobTitle, e.Number }.Where(x => x.Length > 0)), $"/people/{e.Id}")));
            return hits;
        });
    }
}
