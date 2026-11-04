using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

public sealed record QuoteVarianceLine(
    Guid? ItemId,
    string Description,
    string Brand,
    decimal QuotedQty,
    long QuotedUnitPrice,
    long QuotedTotal,
    decimal InvoicedQty,
    long InvoicedUnitPrice,
    long InvoicedTotal,
    long VarianceAmount,
    double VariancePercent,
    string ChangeKind,
    string Reason
);

public sealed record QuoteVarianceReport(
    Guid QuoteId,
    string QuoteNumber,
    DateOnly QuoteDate,
    long QuotedTotal,
    Guid InvoiceId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    long InvoicedTotal,
    long VarianceAmount,
    double VariancePercent,
    string Status,
    string BroughtBy,
    string QuotedBy,
    string InvoicedBy,
    string CustomerName,
    string? CustomerPhone,
    string AgentSummary,
    List<string> AgentReasons,
    List<QuoteVarianceLine> Lines
);

public sealed class QuoteAuditService(ErpDb db, AppCache appCache, RequestContext ctx)
{
    static string Ksh(long cents) => Money.Ksh(cents);

    public async Task<QuoteVarianceReport?> AuditDoc(Guid docId)
    {
        var doc = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == docId);
        if (doc is null) return null;

        SalesDoc? quote = null;
        SalesDoc? invoice = null;

        if (doc.Kind == SalesDocKind.Invoice)
        {
            invoice = doc;
            if (invoice.FromQuoteId is { } qid)
            {
                quote = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == qid);
            }
            else
            {
                var inq = await db.Inquiries.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == invoice.Id && i.QuoteId != null);
                if (inq?.QuoteId is { } iqid)
                    quote = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == iqid);
            }
        }
        else if (doc.Kind == SalesDocKind.Quote)
        {
            quote = doc;
            invoice = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).FirstOrDefaultAsync(d => d.FromQuoteId == quote.Id);
            if (invoice is null)
            {
                var inq = await db.Inquiries.AsNoTracking().FirstOrDefaultAsync(i => i.QuoteId == quote.Id && i.InvoiceId != null);
                if (inq?.InvoiceId is { } inid)
                    invoice = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == inid);
            }
        }

        if (quote is null || invoice is null) return null;

        var inqDoc = await db.Inquiries.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == invoice.Id || (invoice.FromQuoteId != null && i.QuoteId == invoice.FromQuoteId) || i.Id == invoice.InquiryId || i.Id == quote.InquiryId);
        var linkId = invoice.LinkId ?? quote.LinkId ?? inqDoc?.LinkId;
        var link = linkId != null ? await db.TrackingLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId) : null;

        var itemIds = quote.Lines.Select(l => l.ItemId).Concat(invoice.Lines.Select(l => l.ItemId)).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        return BuildReportSync(quote, invoice, items, inqDoc, link);
    }

    public async Task<QuoteVarianceReport?> AuditInquiry(Guid inquiryId)
    {
        var inq = await db.Inquiries.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inquiryId);
        if (inq is null || inq.QuoteId is null || inq.InvoiceId is null) return null;

        var quote = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == inq.QuoteId.Value);
        var invoice = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == inq.InvoiceId.Value);
        if (quote is null || invoice is null) return null;

        var linkId = invoice.LinkId ?? quote.LinkId ?? inq.LinkId;
        var link = linkId != null ? await db.TrackingLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkId) : null;

        var itemIds = quote.Lines.Select(l => l.ItemId).Concat(invoice.Lines.Select(l => l.ItemId)).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        return BuildReportSync(quote, invoice, items, inq, link);
    }

    public async Task<object> AllVariances(int days = 90)
    {
        var cacheKey = $"variances:{ctx.CompanyId}:{days}";
        if (await appCache.GetAnswerAsync(cacheKey) is { } cached) return cached;

        var since = Clock.Today.AddDays(-days);
        var invoices = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer)
            .Where(d => d.Kind == SalesDocKind.Invoice && d.FromQuoteId != null && d.IssueDate >= since)
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.CreatedAt)
            .Take(100)
            .ToListAsync();

        var quoteIds = invoices.Select(i => i.FromQuoteId!.Value).Distinct().ToList();
        var quotes = await db.SalesDocs.AsNoTracking().Include(d => d.Lines).Include(d => d.Customer).Where(d => quoteIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id);

        var invoiceIds = invoices.Select(i => i.Id).ToList();

        // Batch pre-fetch all items for these invoices and quotes in a single query
        var allItemIds = invoices.SelectMany(i => i.Lines).Select(l => l.ItemId)
            .Concat(quotes.Values.SelectMany(q => q.Lines).Select(l => l.ItemId))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => allItemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        // Batch pre-fetch inquiries linked to these invoices or quotes
        var inqs = await db.Inquiries.AsNoTracking()
            .Where(i => (i.InvoiceId != null && invoiceIds.Contains(i.InvoiceId.Value)) || (i.QuoteId != null && quoteIds.Contains(i.QuoteId.Value)))
            .ToListAsync();

        var inqByInvoice = inqs.Where(i => i.InvoiceId.HasValue).GroupBy(i => i.InvoiceId!.Value).ToDictionary(g => g.Key, g => g.First());
        var inqByQuote = inqs.Where(i => i.QuoteId.HasValue).GroupBy(i => i.QuoteId!.Value).ToDictionary(g => g.Key, g => g.First());

        // Batch pre-fetch tracking links
        var linkIds = invoices.Select(i => i.LinkId)
            .Concat(quotes.Values.Select(q => q.LinkId))
            .Concat(inqs.Select(i => i.LinkId))
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var links = await db.TrackingLinks.AsNoTracking().Where(l => linkIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);

        var reports = new List<QuoteVarianceReport>();
        foreach (var inv in invoices)
        {
            if (quotes.TryGetValue(inv.FromQuoteId!.Value, out var q))
            {
                var inq = inqByInvoice.GetValueOrDefault(inv.Id) ?? inqByQuote.GetValueOrDefault(q.Id);
                var linkId = inv.LinkId ?? q.LinkId ?? inq?.LinkId;
                var link = linkId != null ? links.GetValueOrDefault(linkId.Value) : null;

                var rep = BuildReportSync(q, inv, items, inq, link);
                reports.Add(rep);
            }
        }

        var totalQuoted = reports.Sum(r => r.QuotedTotal);
        var totalInvoiced = reports.Sum(r => r.InvoicedTotal);
        var netVariance = totalInvoiced - totalQuoted;

        var result = new
        {
            days,
            count = reports.Count,
            totalQuoted,
            totalInvoiced,
            netVariance,
            discountedCount = reports.Count(r => r.VarianceAmount < -100),
            increasedCount = reports.Count(r => r.VarianceAmount > 100),
            identicalCount = reports.Count(r => Math.Abs(r.VarianceAmount) <= 100),
            reports = reports.Select(r => new
            {
                r.QuoteId, r.QuoteNumber, r.QuoteDate, r.QuotedTotal,
                r.InvoiceId, r.InvoiceNumber, r.InvoiceDate, r.InvoicedTotal,
                r.VarianceAmount, r.VariancePercent, r.Status,
                r.BroughtBy, r.QuotedBy, r.InvoicedBy,
                r.CustomerName, r.CustomerPhone,
                r.AgentSummary, r.AgentReasons,
                lineCount = r.Lines.Count
            })
        };

        await appCache.SetAnswerAsync(cacheKey, result, TimeSpan.FromSeconds(60));

        return result;
    }

    private static QuoteVarianceReport BuildReportSync(SalesDoc quote, SalesDoc invoice, Dictionary<Guid, Item> items, Inquiry? inq, TrackingLink? link)
    {
        var broughtBy = !string.IsNullOrWhiteSpace(inq?.BroughtByName) ? inq.BroughtByName
            : link != null ? (!string.IsNullOrWhiteSpace(link.OwnerName) ? $"{link.OwnerName} (via {link.Name})" : $"{link.Name} ({link.Channel})")
            : !string.IsNullOrWhiteSpace(quote.SalespersonName) ? quote.SalespersonName
            : !string.IsNullOrWhiteSpace(invoice.SalespersonName) ? invoice.SalespersonName
            : inq?.Channel.ToString() ?? invoice.Channel?.ToString() ?? "Direct / Website";

        var quotedBy = !string.IsNullOrWhiteSpace(quote.SalespersonName) ? quote.SalespersonName : "Staff";
        var invoicedBy = !string.IsNullOrWhiteSpace(invoice.SalespersonName) ? invoice.SalespersonName : "Staff";
        var custName = invoice.Customer?.Name ?? quote.Customer?.Name ?? "Customer";
        var custPhone = invoice.Customer?.Phone ?? quote.Customer?.Phone;

        var lines = new List<QuoteVarianceLine>();
        var agentReasons = new List<string>();

        var qLines = quote.Lines.ToList();
        var iLines = invoice.Lines.ToList();

        var matchedInvoiceLineIds = new HashSet<Guid>();

        foreach (var ql in qLines)
        {
            var brand = ql.ItemId is { } qid && items.TryGetValue(qid, out var it) ? it.Brand : "";
            var brandPrefix = brand.Length > 0 ? $"[{brand}] " : "";

            // Find matching line on invoice by ItemId or exact Description
            var il = iLines.FirstOrDefault(x => !matchedInvoiceLineIds.Contains(x.Id) && ((ql.ItemId != null && x.ItemId == ql.ItemId) || x.Description.Equals(ql.Description, StringComparison.OrdinalIgnoreCase)));

            if (il is not null)
            {
                matchedInvoiceLineIds.Add(il.Id);

                var varAmt = il.Total - ql.Total;
                var varPct = ql.Total == 0 ? 0 : Math.Round(100.0 * varAmt / ql.Total, 1);
                var reasons = new List<string>();
                var kind = "Unchanged";

                // Price variance
                if (il.UnitPrice < ql.UnitPrice)
                {
                    kind = "Discounted";
                    var discountPct = ql.UnitPrice == 0 ? 0 : Math.Round(100.0 * (ql.UnitPrice - il.UnitPrice) / ql.UnitPrice, 1);
                    var priceDropCents = (long)Math.Round((ql.UnitPrice - il.UnitPrice) * il.Quantity);
                    var msg = $"Unit price on {brandPrefix}{ql.Description} was discounted by {discountPct}% ({Ksh(ql.UnitPrice)} → {Ksh(il.UnitPrice)}), reducing invoice by {Ksh(priceDropCents)}";
                    reasons.Add(msg);
                    agentReasons.Add(msg);
                }
                else if (il.UnitPrice > ql.UnitPrice)
                {
                    kind = "PriceIncreased";
                    var incPct = ql.UnitPrice == 0 ? 0 : Math.Round(100.0 * (il.UnitPrice - ql.UnitPrice) / ql.UnitPrice, 1);
                    var incCents = (long)Math.Round((il.UnitPrice - ql.UnitPrice) * il.Quantity);
                    var msg = $"Unit price on {brandPrefix}{ql.Description} was increased by {incPct}% ({Ksh(ql.UnitPrice)} → {Ksh(il.UnitPrice)}), adding +{Ksh(incCents)}";
                    reasons.Add(msg);
                    agentReasons.Add(msg);
                }

                // Quantity variance
                if (il.Quantity < ql.Quantity)
                {
                    if (kind == "Unchanged") kind = "QtyReduced";
                    var qtyDrop = ql.Quantity - il.Quantity;
                    var qtyDropCents = (long)Math.Round(qtyDrop * ql.UnitPrice);
                    var msg = $"Quantity on {brandPrefix}{ql.Description} decreased from {ql.Quantity:0.##} to {il.Quantity:0.##} {ql.Unit} (-{Ksh(qtyDropCents)})";
                    reasons.Add(msg);
                    agentReasons.Add(msg);
                }
                else if (il.Quantity > ql.Quantity)
                {
                    if (kind == "Unchanged") kind = "QtyIncreased";
                    var qtyInc = il.Quantity - ql.Quantity;
                    var qtyIncCents = (long)Math.Round(qtyInc * il.UnitPrice);
                    var msg = $"Quantity on {brandPrefix}{ql.Description} increased from {ql.Quantity:0.##} to {il.Quantity:0.##} {ql.Unit} (+{Ksh(qtyIncCents)})";
                    reasons.Add(msg);
                    agentReasons.Add(msg);
                }

                if (reasons.Count == 0) reasons.Add("Invoiced exactly as quoted");

                lines.Add(new QuoteVarianceLine(
                    ql.ItemId, ql.Description, brand,
                    ql.Quantity, ql.UnitPrice, ql.Total,
                    il.Quantity, il.UnitPrice, il.Total,
                    varAmt, varPct, kind, string.Join("; ", reasons)
                ));
            }
            else
            {
                // Quoted but dropped from Invoice
                var msg = $"{brandPrefix}{ql.Description} ({ql.Quantity:0.##} {ql.Unit}) was dropped from the final order (-{Ksh(ql.Total)})";
                agentReasons.Add(msg);
                lines.Add(new QuoteVarianceLine(
                    ql.ItemId, ql.Description, brand,
                    ql.Quantity, ql.UnitPrice, ql.Total,
                    0, 0, 0,
                    -ql.Total, -100, "ItemDropped", msg
                ));
            }
        }

        // Items added on invoice not present in quote
        foreach (var il in iLines.Where(x => !matchedInvoiceLineIds.Contains(x.Id)))
        {
            var brand = il.ItemId is { } iid && items.TryGetValue(iid, out var it) ? it.Brand : "";
            var brandPrefix = brand.Length > 0 ? $"[{brand}] " : "";
            var msg = $"{brandPrefix}{il.Description} ({il.Quantity:0.##} {il.Unit}) was added to the invoice (+{Ksh(il.Total)})";
            agentReasons.Add(msg);
            lines.Add(new QuoteVarianceLine(
                il.ItemId, il.Description, brand,
                0, 0, 0,
                il.Quantity, il.UnitPrice, il.Total,
                il.Total, 100, "ItemAdded", msg
            ));
        }

        var totalVariance = invoice.Total - quote.Total;
        var totalVariancePct = quote.Total == 0 ? 0 : Math.Round(100.0 * totalVariance / quote.Total, 1);

        string overallStatus = Math.Abs(totalVariance) < 100 ? "Identical"
            : totalVariance < 0 ? "Discounted"
            : "PriceIncreased";

        string agentSummary;
        if (Math.Abs(totalVariance) < 100)
        {
            agentSummary = $"Invoiced exactly as quoted (100% adherence to quote {quote.Number}). Customer brought in by {broughtBy}.";
        }
        else if (totalVariance < 0)
        {
            var factors = agentReasons.Count > 0 ? string.Join("; ", agentReasons.Take(2)) : "Line item price or quantity adjustments";
            agentSummary = $"Invoice {invoice.Number} was issued at {Ksh(Math.Abs(totalVariance))} less ({Math.Abs(totalVariancePct):0.#}% discount) than Quote {quote.Number}. Lead brought in by {broughtBy}, finalized by {invoicedBy}. Key factors: {factors}.";
        }
        else
        {
            var factors = agentReasons.Count > 0 ? string.Join("; ", agentReasons.Take(2)) : "Additional quantities or items added";
            agentSummary = $"Invoice {invoice.Number} was issued at +{Ksh(totalVariance)} more (+{totalVariancePct:0.#}%) than Quote {quote.Number}. Lead brought in by {broughtBy}, finalized by {invoicedBy}. Key factors: {factors}.";
        }

        return new QuoteVarianceReport(
            quote.Id, quote.Number, quote.IssueDate, quote.Total,
            invoice.Id, invoice.Number, invoice.IssueDate, invoice.Total,
            totalVariance, totalVariancePct, overallStatus,
            broughtBy, quotedBy, invoicedBy,
            custName, custPhone,
            agentSummary, agentReasons, lines
        );
    }
}
