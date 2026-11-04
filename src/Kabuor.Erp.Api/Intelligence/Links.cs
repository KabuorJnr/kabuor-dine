// Tracking links: one per salesperson, per channel (a TikTok bio, a LinkedIn post, a WhatsApp status), per
// campaign. Opening one counts a visit and remembers the link in the visitor's browser; whatever they do next
// on the shop (an inquiry, signing up, ordering) is credited to it, and so is every sale the customer brings.
using System.Security.Cryptography;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

public sealed record LinkInput(string Name, Channel Channel, Guid? OwnerUserId, Guid? CampaignId, string? Target);

public sealed class LinkService(ErpDb db, RequestContext ctx)
{
    const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/o, 1/l/i: readable when spoken or typed

    public static string NewCode() => string.Concat(RandomNumberGenerator.GetBytes(7).Select(b => Alphabet[b % Alphabet.Length]));

    /// <summary>The business's public address, made the first time it's needed (lowercase name, made unique).</summary>
    public async Task<Company> EnsurePortal()
    {
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        if (c.Slug.Length == 0)
        {
            var baseSlug = new string(c.Name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
            while (baseSlug.Contains("--")) baseSlug = baseSlug.Replace("--", "-");
            baseSlug = baseSlug.Length == 0 ? "shop" : baseSlug.Length > 40 ? baseSlug[..40].Trim('-') : baseSlug;
            var slug = baseSlug;
            for (var n = 2; await db.Companies.IgnoreQueryFilters().AnyAsync(x => x.Slug == slug); n++) slug = $"{baseSlug}-{n}";
            c.Slug = slug;
        }
        c.PortalEnabled = true;
        await db.SaveChangesAsync();
        return c;
    }

    public async Task<TrackingLink> Create(LinkInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 2 or > 80) throw new AppException("Name the link after where it will be used (e.g. “Mercy’s WhatsApp” or “TikTok bio”).");
        // A salesperson makes links for themselves; the owner can make them for anyone.
        var owner = input.OwnerUserId;
        if (!ctx.Can(Policies.Admin)) owner = ctx.UserId;
        string ownerName = "";
        if (owner is { } uid) ownerName = await db.Users.Where(u => u.Id == uid && u.Active).Select(u => u.Name).SingleOrDefaultAsync() ?? throw new AppException("That person has no active login.");
        if (input.CampaignId is { } camp && !await db.Campaigns.AnyAsync(c => c.Id == camp)) throw new AppException("That campaign doesn't exist.");
        var target = (input.Target ?? "shop").Trim();
        if (target is not ("shop" or "ask") && !(Guid.TryParse(target, out var item) && await db.Items.AnyAsync(i => i.Id == item))) throw new AppException("Point the link at the shop, the inquiry form, or a product.");
        await EnsurePortal();
        string code;
        do code = NewCode(); while (await db.TrackingLinks.IgnoreQueryFilters().AnyAsync(l => l.Code == code));
        var link = new TrackingLink { Code = code, Name = name, Channel = input.Channel, OwnerUserId = owner, OwnerName = ownerName, CampaignId = input.CampaignId, Target = target };
        db.TrackingLinks.Add(link);
        await db.SaveChangesAsync();
        return link;
    }

    public async Task Deactivate(Guid id)
    {
        var l = await db.TrackingLinks.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (!ctx.Can(Policies.Admin) && l.OwnerUserId != ctx.UserId) throw new AppException("Only the owner can switch off someone else's link.", 403);
        l.Active = false;
        await db.SaveChangesAsync();
    }

    /// <summary>Every link with what it brought: visits, people, inquiries, customers, sales and money in.</summary>
    public async Task<object> List(DateOnly from, DateOnly to, string baseUrl)
    {
        var links = await db.TrackingLinks.ToListAsync();
        if (!ctx.Can(Policies.Admin)) links = links.Where(l => l.OwnerUserId == ctx.UserId || l.OwnerUserId == null).ToList();
        var perf = await Performance(from, to);
        var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
        return new
        {
            portal = company.PortalEnabled ? $"{baseUrl}/s/{company.Slug}" : null,
            links = links.OrderByDescending(l => l.Active).ThenByDescending(l => perf.GetValueOrDefault(l.Id)?.Revenue ?? 0).Select(l =>
            {
                var p = perf.GetValueOrDefault(l.Id) ?? new LinkPerformance();
                return new
                {
                    l.Id, l.Code, l.Name, channel = l.Channel.ToString(), l.OwnerUserId, l.OwnerName, l.CampaignId, l.Target, l.Active, l.CreatedAt,
                    url = $"{baseUrl}/l/{l.Code}",
                    p.Visits, p.People, p.Inquiries, p.Customers, p.Sales, p.Revenue,
                    conversion = p.People == 0 ? 0 : Math.Round(100.0 * p.Customers / p.People, 1),
                };
            }),
        };
    }

    public sealed class LinkPerformance
    {
        public int Visits { get; set; }
        public int People { get; set; }
        public int Inquiries { get; set; }
        public int Customers { get; set; }
        public int Sales { get; set; }
        public long Revenue { get; set; }
    }

    /// <summary>Per link, in the period: visits (and distinct visitors), inquiries, new customers, invoices and their value.</summary>
    public async Task<Dictionary<Guid, LinkPerformance>> Performance(DateOnly from, DateOnly to)
    {
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var visits = (await db.LinkVisits.Select(v => new { v.LinkId, v.VisitorId, v.At }).ToListAsync()).Where(v => v.At >= start && v.At < end).ToList();
        var inquiries = (await db.Inquiries.Where(i => i.LinkId != null).Select(i => new { i.LinkId, i.CreatedAt }).ToListAsync()).Where(i => i.CreatedAt >= start && i.CreatedAt < end).ToList();
        var customers = (await db.Customers.Where(c => c.FirstLinkId != null).Select(c => new { c.FirstLinkId, c.CreatedAt }).ToListAsync()).Where(c => c.CreatedAt >= start && c.CreatedAt < end).ToList();
        var sales = await db.SalesDocs.Where(d => d.LinkId != null && d.Kind == SalesDocKind.Invoice && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void && d.IssueDate >= from && d.IssueDate <= to)
            .Select(d => new { d.LinkId, d.Total }).ToListAsync();
        var ids = visits.Select(v => v.LinkId).Concat(inquiries.Select(i => i.LinkId!.Value)).Concat(customers.Select(c => c.FirstLinkId!.Value)).Concat(sales.Select(s => s.LinkId!.Value)).Distinct();
        return ids.ToDictionary(id => id, id => new LinkPerformance
        {
            Visits = visits.Count(v => v.LinkId == id),
            People = visits.Where(v => v.LinkId == id).Select(v => v.VisitorId).Distinct().Count(),
            Inquiries = inquiries.Count(i => i.LinkId == id),
            Customers = customers.Count(c => c.FirstLinkId == id),
            Sales = sales.Count(s => s.LinkId == id),
            Revenue = sales.Where(s => s.LinkId == id).Sum(s => s.Total),
        });
    }
}
