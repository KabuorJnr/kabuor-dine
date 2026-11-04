// A customer's rights under Kenya's Data Protection Act, 2019: a copy of everything held about them (section 26;
// within seven days, Data Protection (General) Regulations 2021, reg. 9(4)) and erasure (section 40; within fourteen
// days, reg. 12(3)). Tax records are the exception the law allows (reg. 12(4)): invoices and payments must be kept
// for five years (Tax Procedures Act, 2015, s. 23), so erasure removes contact details, conversations, leads,
// marketing and portal data, keeps only what a tax invoice needs, and scrubs the old values from the audit log too.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Privacy;

public sealed record EraseInput(string? Reason);

public sealed class DataRightsService(ErpDb db, RequestContext ctx)
{
    const string Gone = "[erased]";
    static readonly SalesDocStatus[] Issued = [SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid];

    async Task<Customer> Find(Guid id) => await db.Customers.SingleOrDefaultAsync(c => c.Id == id) ?? throw new AppException("Not found.", 404);

    async Task<List<Lead>> LeadsOf(Customer c) =>
        await db.Leads.Where(l => l.CustomerId == c.Id || (c.Phone != "" && l.Phone == c.Phone) || (c.Email != "" && l.Email == c.Email)).ToListAsync();

    async Task<List<Inquiry>> InquiriesOf(Customer c) =>
        await db.Inquiries.Include(i => i.Messages).Where(i => i.CustomerId == c.Id || (c.Phone != "" && i.Phone == c.Phone)).ToListAsync();

    /// <summary>Everything held about one customer, in one file they can read or take elsewhere.</summary>
    public async Task<object> Export(Guid id)
    {
        var c = await Find(id);
        var docs = await db.SalesDocs.Where(d => d.CustomerId == id).OrderBy(d => d.IssueDate).Select(d => new
        {
            d.Kind, d.Number, d.Status, d.IssueDate, d.DueDate, d.Net, d.Tax, d.Total, d.Paid, d.EtimsInvoiceNo, d.Notes,
            lines = db.SalesLines.Where(l => l.SalesDocId == d.Id).Select(l => new { l.Description, l.Quantity, l.UnitPrice, l.Total }).ToList(),
        }).ToListAsync();
        var payments = await db.Payments.Where(p => p.CustomerId == id).OrderBy(p => p.ReceivedAt).Select(p => new { p.Method, p.Amount, p.ReceivedAt, p.Reference, p.PayerName, p.PayerPhone }).ToListAsync();
        var leads = await LeadsOf(c);
        var leadIds = leads.Select(l => l.Id).ToList();
        var inquiries = await InquiriesOf(c);
        var activities = await db.Activities.Where(a => a.CustomerId == id || (a.LeadId != null && leadIds.Contains(a.LeadId.Value))).OrderBy(a => a.At).Select(a => new { a.Kind, a.Text, a.At, a.Actor }).ToListAsync();
        var messages = await db.CampaignRecipients.Where(r => r.CustomerId == id).Select(r => new { r.Name, r.Phone, r.Message }).ToListAsync();
        var company = await db.Companies.Where(x => x.Id == db.CompanyId).Select(x => new { x.Name, x.Email, x.Phone }).SingleAsync();
        return new
        {
            about = $"Personal data {company.Name} holds about {c.Name}, provided under section 26 of the Data Protection Act, 2019.",
            generatedAt = DateTimeOffset.UtcNow, controller = company,
            customer = new { c.Number, c.Name, c.Phone, c.Email, c.KraPin, c.Address, c.Notes, c.CreditLimit, c.MarketingOptOut, c.AcquisitionChannel, c.CreatedAt, c.PortalLastSeenAt },
            documents = docs, payments,
            inquiries = inquiries.Select(i => new { i.Number, i.Subject, i.Channel, i.Status, i.CreatedAt, messages = i.Messages.OrderBy(m => m.At).Select(m => new { m.From, m.Text, m.At }) }),
            leads = leads.Select(l => new { l.Number, l.Name, l.Business, l.Phone, l.Email, l.Interest, l.Stage, l.CreatedAt }),
            activities, marketingMessages = messages,
        };
    }

    /// <summary>Erases a customer's personal data, keeping only what the tax law requires.</summary>
    public async Task<object> Erase(Guid id, EraseInput input)
    {
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length < 3) throw new AppException("Say why (for example \"Customer asked by email on 3 March\"). It goes in the audit log.");
        var c = await Find(id);
        var today = Clock.Today;
        // Issued invoices from the last five years are tax records: their buyer's name and KRA PIN stay with them.
        var taxRecords = await db.SalesDocs.CountAsync(d => d.CustomerId == id && d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status) && d.IssueDate >= today.AddYears(-5));
        var owing = await db.SalesDocs.Where(d => d.CustomerId == id && d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid)).SumAsync(d => (long?)(d.Total - d.Paid)) ?? 0;

        var leads = await LeadsOf(c);
        var inquiries = await InquiriesOf(c);
        var leadIds = leads.Select(l => l.Id).ToList();
        var activities = await db.Activities.Where(a => a.CustomerId == id || (a.LeadId != null && leadIds.Contains(a.LeadId.Value))).ToListAsync();
        var recipients = await db.CampaignRecipients.Where(r => r.CustomerId == id || (r.LeadId != null && leadIds.Contains(r.LeadId.Value))).ToListAsync();
        var payments = await db.Payments.Where(p => p.CustomerId == id).ToListAsync();
        var codes = c.Phone.Length > 0 ? await db.PortalCodes.Where(p => p.Phone == c.Phone).ToListAsync() : [];

        if (taxRecords == 0) { c.Name = $"Erased customer {c.Number}"; c.KraPin = ""; }
        (c.Phone, c.Email, c.Address, c.Notes, c.MarketingOptOut, c.PortalLastSeenAt) = ("", "", "", "", true, null);
        foreach (var p in payments) (p.PayerName, p.PayerPhone) = ("", "");
        foreach (var l in leads) (l.Name, l.Business, l.Phone, l.Email, l.Interest) = (Gone, "", "", "", "");
        foreach (var i in inquiries)
        {
            (i.Name, i.Phone, i.Email, i.Subject) = (Gone, "", "", Gone);
            foreach (var m in i.Messages) m.Text = Gone;
        }
        foreach (var a in activities) a.Text = Gone;
        foreach (var r in recipients) (r.Name, r.Phone, r.Message) = ("", "", Gone);
        db.PortalCodes.RemoveRange(codes);
        await db.SaveChangesAsync();

        // The audit log still holds the old values (including the entries just written): replace them, then say what happened.
        var ids = new List<string> { c.Id.ToString() };
        ids.AddRange(payments.Select(p => p.Id.ToString()));
        ids.AddRange(leads.Select(l => l.Id.ToString()));
        ids.AddRange(inquiries.Select(i => i.Id.ToString()));
        ids.AddRange(inquiries.SelectMany(i => i.Messages).Select(m => m.Id.ToString()));
        ids.AddRange(activities.Select(a => a.Id.ToString()));
        ids.AddRange(recipients.Select(r => r.Id.ToString()));
        ids.AddRange(codes.Select(p => p.Id.ToString()));
        var scrubbed = 0;
        foreach (var chunk in ids.Chunk(400))
            scrubbed += await db.AuditEntries.Where(a => chunk.Contains(a.EntityId)).ExecuteUpdateAsync(s => s.SetProperty(a => a.Changes, $"[personal data erased {today:yyyy-MM-dd}]"));
        var kept = taxRecords > 0 ? $"name and KRA PIN kept on {taxRecords} invoice(s) as tax records (Tax Procedures Act s. 23)" : "nothing kept";
        db.AuditEntries.Add(new AuditEntry
        {
            CompanyId = db.CompanyId, UserId = ctx.UserId, Actor = ctx.Actor, Action = "Erased", Entity = nameof(Customer), EntityId = c.Id.ToString(),
            Changes = $"Personal data erased on request (Data Protection Act s. 40). Reason: {reason[..Math.Min(reason.Length, 300)]}. {kept}.",
        });
        await db.SaveChangesAsync();
        return new
        {
            erased = true, customer = c.Number, keptForTax = taxRecords, stillOwes = owing,
            removed = new { leads = leads.Count, inquiries = inquiries.Count, activities = activities.Count, marketingMessages = recipients.Count, paymentDetails = payments.Count, auditEntries = scrubbed },
            note = taxRecords > 0
                ? $"Contact details, conversations and marketing data are gone. The name and KRA PIN stay on {taxRecords} invoice(s) because tax law requires keeping them for five years."
                : "All personal data for this customer is gone.",
        };
    }
}
