// The document trail: every record a piece of business touched, from the first contact to the books, so anyone can
// follow it forwards and backwards. One system means one chain of documents, each made from the one before:
//   selling:     lead → inquiry → quote → invoice → payment received → stock out → the books (and KRA eTIMS)
//   buying:      fault → work order → request → tender → order → bill → supplier paid → stock in → the books
//   spending:    cash advance → paid out → accounted for → the books
// Starting from any one document, the trail walks every link it can find (both ways) and returns them in order.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

public sealed record TraceNode(string Key, string Kind, Guid Id, string Number, string Title, string Status, DateTimeOffset At, long Amount, string To, int Stage);

public sealed class TraceService(ErpDb db)
{
    // The order stages are shown in, left to right.
    static readonly string[] Stages = ["Lead", "Inquiry", "Fault", "Work order", "Request", "Tender", "Quote", "Order", "Invoice", "Bill", "Advance", "Payment", "Supplier payment", "Stock", "Books"];
    static int StageOf(string kind) => Math.Max(0, Array.IndexOf(Stages, kind));
    static DateTimeOffset At(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));

    public async Task<object> Trail(string kind, Guid id)
    {
        var found = new Dictionary<string, TraceNode>();
        var queue = new Queue<TraceNode>();
        var start = await Node(kind, id) ?? throw new AppException("Not found.", 404);
        found[start.Key] = start;
        queue.Enqueue(start);
        while (queue.Count > 0 && found.Count < 80)
        {
            foreach (var n in await Neighbours(queue.Dequeue()))
                if (found.TryAdd(n.Key, n)) queue.Enqueue(n);
        }
        // The books: every journal entry posted from anything on the trail.
        var ids = found.Values.Select(n => n.Id).ToList();
        var entries = await db.JournalEntries.Where(e => e.SourceId != null && ids.Contains(e.SourceId!.Value))
            .Select(e => new { e.Id, e.Date, e.Memo, e.SourceType, amount = e.Lines.Sum(l => l.Debit) }).ToListAsync();
        foreach (var e in entries) found.TryAdd($"books:{e.Id}", new TraceNode($"books:{e.Id}", "Books", e.Id, "", e.Memo, e.SourceType, At(e.Date), e.amount, "/finance/ledger", StageOf("Books")));
        var stages = found.Values.GroupBy(n => n.Kind).OrderBy(g => StageOf(g.Key))
            .Select(g => new { stage = g.Key, items = g.OrderBy(n => n.At).Select(n => new { n.Kind, n.Id, n.Number, n.Title, n.Status, n.At, n.Amount, n.To, current = n.Key == start.Key }) });
        return new { start = start.Key, count = found.Count, stages };
    }

    async Task<TraceNode?> Node(string kind, Guid id) => kind switch
    {
        "invoice" or "quote" or "sale" => await db.SalesDocs.Where(d => d.Id == id).Select(d => new { d.Id, d.Kind, d.Number, d.Status, d.IssueDate, d.Total, customer = d.Customer!.Name }).SingleOrDefaultAsync() is { } d
            ? new TraceNode($"sale:{d.Id}", d.Kind == SalesDocKind.Quote ? "Quote" : "Invoice", d.Id, d.Number, d.customer, d.Status.ToString(), At(d.IssueDate), d.Total, $"/sales/{d.Id}", StageOf(d.Kind == SalesDocKind.Quote ? "Quote" : "Invoice")) : null,
        "purchase" or "order" or "bill" => await db.PurchaseDocs.Where(d => d.Id == id).Select(d => new { d.Id, d.Kind, d.Number, d.Status, d.IssueDate, d.Total, supplier = d.Supplier!.Name }).SingleOrDefaultAsync() is { } p
            ? new TraceNode($"purchase:{p.Id}", p.Kind == PurchaseKind.Order ? "Order" : "Bill", p.Id, p.Number, p.supplier, p.Status.ToString(), At(p.IssueDate), p.Total, $"/purchases/{p.Id}", StageOf(p.Kind == PurchaseKind.Order ? "Order" : "Bill")) : null,
        "request" => await db.Requisitions.Where(r => r.Id == id).Select(r => new { r.Id, r.Number, r.Purpose, r.Status, r.CreatedAt, r.EstimatedTotal }).SingleOrDefaultAsync() is { } r
            ? new TraceNode($"request:{r.Id}", "Request", r.Id, r.Number, r.Purpose, r.Status.ToString(), r.CreatedAt, r.EstimatedTotal, $"/procurement?id={r.Id}", StageOf("Request")) : null,
        "tender" => await db.Tenders.Where(t => t.Id == id).Select(t => new { t.Id, t.Number, t.Title, t.Status, t.CreatedAt }).SingleOrDefaultAsync() is { } t
            ? new TraceNode($"tender:{t.Id}", "Tender", t.Id, t.Number, t.Title, t.Status.ToString(), t.CreatedAt, 0, $"/procurement/tenders?id={t.Id}", StageOf("Tender")) : null,
        "workorder" => await db.WorkOrders.Where(w => w.Id == id).Select(w => new { w.Id, w.Number, w.Title, w.Status, w.CreatedAt, cost = w.PartsCost + w.LabourCost + w.ServicesCost }).SingleOrDefaultAsync() is { } w
            ? new TraceNode($"workorder:{w.Id}", "Work order", w.Id, w.Number, w.Title, w.Status.ToString(), w.CreatedAt, w.cost, $"/maintenance?wo={w.Id}", StageOf("Work order")) : null,
        "fault" => await db.FaultReports.Where(f => f.Id == id).Select(f => new { f.Id, f.Number, f.Title, f.Status, f.CreatedAt }).SingleOrDefaultAsync() is { } f
            ? new TraceNode($"fault:{f.Id}", "Fault", f.Id, f.Number, f.Title, f.Status.ToString(), f.CreatedAt, 0, "/maintenance?tab=faults", StageOf("Fault")) : null,
        "lead" => await db.Leads.Where(l => l.Id == id).Select(l => new { l.Id, l.Number, l.Name, l.Business, l.Stage, l.CreatedAt, l.EstimatedValue }).SingleOrDefaultAsync() is { } l
            ? new TraceNode($"lead:{l.Id}", "Lead", l.Id, l.Number, l.Business.Length > 0 ? l.Business : l.Name, l.Stage.ToString(), l.CreatedAt, l.EstimatedValue, "/pipeline", StageOf("Lead")) : null,
        "inquiry" => await db.Inquiries.Where(i => i.Id == id).Select(i => new { i.Id, i.Number, i.Subject, i.Name, i.Status, i.CreatedAt }).SingleOrDefaultAsync() is { } q
            ? new TraceNode($"inquiry:{q.Id}", "Inquiry", q.Id, q.Number, q.Subject.Length > 0 ? q.Subject : q.Name, q.Status.ToString(), q.CreatedAt, 0, "/inquiries", StageOf("Inquiry")) : null,
        "payment" => await db.Payments.Where(p => p.Id == id).Select(p => new { p.Id, p.Reference, p.Method, p.Status, p.ReceivedAt, p.Amount }).SingleOrDefaultAsync() is { } m
            ? new TraceNode($"payment:{m.Id}", "Payment", m.Id, m.Reference, m.Method.ToString(), m.Status.ToString(), m.ReceivedAt, m.Amount, "/money-in", StageOf("Payment")) : null,
        "advance" => await db.CashAdvances.Where(a => a.Id == id).Select(a => new { a.Id, a.Number, a.Purpose, a.Status, a.CreatedAt, a.Amount }).SingleOrDefaultAsync() is { } a
            ? new TraceNode($"advance:{a.Id}", "Advance", a.Id, a.Number, a.Purpose, a.Status.ToString(), a.CreatedAt, a.Amount, "/finance/advances", StageOf("Advance")) : null,
        _ => null,
    };

    async Task<List<TraceNode>> Many(string kind, IEnumerable<Guid> ids)
    {
        var list = new List<TraceNode>();
        foreach (var id in ids.Distinct().Take(20)) if (await Node(kind, id) is { } n) list.Add(n);
        return list;
    }

    async Task<List<TraceNode>> StockMoves(Guid sourceId)
    {
        var moves = await db.StockMoves.Where(m => m.SourceId == sourceId).Join(db.Items, m => m.ItemId, i => i.Id, (m, i) => new { m.Id, m.Quantity, m.Kind, m.At, m.UnitCost, i.Name, i.Unit }).Take(20).ToListAsync();
        return moves.Select(m => new TraceNode($"stock:{m.Id}", "Stock", m.Id, "", $"{(m.Quantity > 0 ? "+" : "")}{m.Quantity:0.##} {m.Unit} {m.Name}", m.Kind.ToString(), m.At, (long)Math.Round(Math.Abs(m.Quantity) * m.UnitCost), "/stock", StageOf("Stock"))).ToList();
    }

    async Task<List<TraceNode>> Neighbours(TraceNode n)
    {
        var r = new List<TraceNode>();
        switch (n.Kind)
        {
            case "Quote" or "Invoice":
            {
                var d = await db.SalesDocs.Where(x => x.Id == n.Id).Select(x => new { x.FromQuoteId, x.InquiryId }).SingleAsync();
                if (d.FromQuoteId is { } q) r.AddRange(await Many("quote", [q]));
                r.AddRange(await Many("invoice", await db.SalesDocs.Where(x => x.FromQuoteId == n.Id).Select(x => x.Id).ToListAsync()));
                if (d.InquiryId is { } i) r.AddRange(await Many("inquiry", [i]));
                r.AddRange(await Many("payment", await db.PaymentAllocations.Where(a => a.SalesDocId == n.Id).Select(a => a.PaymentId).ToListAsync()));
                r.AddRange(await StockMoves(n.Id));
                break;
            }
            case "Inquiry":
            {
                var i = await db.Inquiries.Where(x => x.Id == n.Id).Select(x => new { x.LeadId }).SingleAsync();
                if (i.LeadId is { } l) r.AddRange(await Many("lead", [l]));
                r.AddRange(await Many("sale", await db.SalesDocs.Where(x => x.InquiryId == n.Id).Select(x => x.Id).ToListAsync()));
                break;
            }
            case "Lead":
            {
                var l = await db.Leads.Where(x => x.Id == n.Id).Select(x => new { x.InquiryId, x.CustomerId }).SingleAsync();
                if (l.InquiryId is { } i) r.AddRange(await Many("inquiry", [i]));
                r.AddRange(await Many("inquiry", await db.Inquiries.Where(x => x.LeadId == n.Id).Select(x => x.Id).ToListAsync()));
                // Once won: the customer's first documents.
                if (l.CustomerId is { } c) r.AddRange(await Many("sale", await db.SalesDocs.Where(x => x.CustomerId == c).OrderBy(x => x.IssueDate).Select(x => x.Id).Take(5).ToListAsync()));
                break;
            }
            case "Payment":
                r.AddRange(await Many("sale", await db.PaymentAllocations.Where(a => a.PaymentId == n.Id).Select(a => a.SalesDocId).ToListAsync()));
                break;
            case "Order" or "Bill":
            {
                var p = await db.PurchaseDocs.Where(x => x.Id == n.Id).Select(x => new { x.FromOrderId, x.RequisitionId, x.TenderId, x.WorkOrderId }).SingleAsync();
                if (p.FromOrderId is { } o) r.AddRange(await Many("purchase", [o]));
                r.AddRange(await Many("purchase", await db.PurchaseDocs.Where(x => x.FromOrderId == n.Id).Select(x => x.Id).ToListAsync()));
                if (p.RequisitionId is { } q) r.AddRange(await Many("request", [q]));
                if (p.TenderId is { } t) r.AddRange(await Many("tender", [t]));
                if (p.WorkOrderId is { } w) r.AddRange(await Many("workorder", [w]));
                var paid = await db.SupplierPayments.Where(x => x.PurchaseDocId == n.Id).Select(x => new { x.Id, x.Reference, x.Method, x.PaidAt, x.Amount }).ToListAsync();
                r.AddRange(paid.Select(x => new TraceNode($"supplierpayment:{x.Id}", "Supplier payment", x.Id, x.Reference, x.Method.ToString(), "Paid", x.PaidAt, x.Amount, $"/purchases/{n.Id}", StageOf("Supplier payment"))));
                r.AddRange(await StockMoves(n.Id));
                break;
            }
            case "Request":
            {
                var q = await db.Requisitions.Where(x => x.Id == n.Id).Select(x => new { x.WorkOrderId }).SingleAsync();
                if (q.WorkOrderId is { } w) r.AddRange(await Many("workorder", [w]));
                r.AddRange(await Many("purchase", await db.PurchaseDocs.Where(x => x.RequisitionId == n.Id).Select(x => x.Id).ToListAsync()));
                r.AddRange(await Many("tender", await db.Tenders.Where(x => x.RequisitionId == n.Id).Select(x => x.Id).ToListAsync()));
                r.AddRange(await StockMoves(n.Id));
                break;
            }
            case "Tender":
            {
                var t = await db.Tenders.Where(x => x.Id == n.Id).Select(x => new { x.RequisitionId, x.PurchaseOrderId }).SingleAsync();
                if (t.RequisitionId is { } q) r.AddRange(await Many("request", [q]));
                if (t.PurchaseOrderId is { } o) r.AddRange(await Many("purchase", [o]));
                break;
            }
            case "Work order":
            {
                var w = await db.WorkOrders.Where(x => x.Id == n.Id).Select(x => new { x.FaultReportId }).SingleAsync();
                if (w.FaultReportId is { } f) r.AddRange(await Many("fault", [f]));
                r.AddRange(await Many("request", await db.Requisitions.Where(x => x.WorkOrderId == n.Id).Select(x => x.Id).ToListAsync()));
                r.AddRange(await Many("purchase", await db.PurchaseDocs.Where(x => x.WorkOrderId == n.Id).Select(x => x.Id).ToListAsync()));
                r.AddRange(await StockMoves(n.Id));
                // Direct payments for the job are posted under their own id.
                var costs = await db.WorkOrderCosts.Where(c => c.WorkOrderId == n.Id).Select(c => new { c.Id, c.Description, c.Amount, c.Date, c.Reference }).ToListAsync();
                r.AddRange(costs.Select(c => new TraceNode($"cost:{c.Id}", "Supplier payment", c.Id, c.Reference, c.Description, "Paid", At(c.Date), c.Amount, $"/maintenance?wo={n.Id}", StageOf("Supplier payment"))));
                break;
            }
            case "Fault":
            {
                var f = await db.FaultReports.Where(x => x.Id == n.Id).Select(x => new { x.WorkOrderId }).SingleAsync();
                if (f.WorkOrderId is { } w) r.AddRange(await Many("workorder", [w]));
                break;
            }
        }
        return r;
    }
}
