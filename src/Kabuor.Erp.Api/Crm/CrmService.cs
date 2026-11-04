// Leads, the pipeline, conversations and follow-ups. Winning a lead makes them a customer (or links the
// customer who already has their phone number), so nobody is typed in twice.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Crm;

public sealed record LeadInput(string Name, string? Business, string? Phone, string? Email, LeadSource? Source, long? EstimatedValue, string? Interest, Guid? OwnerUserId, DateOnly? FollowUpOn);
public sealed record StageInput(LeadStage Stage, string? LostReason);
public sealed record ActivityInput(Guid? LeadId, Guid? CustomerId, ActivityKind Kind, string? Text, DateOnly? FollowUpOn);

public sealed class CrmService(ErpDb db, Numbers numbers, RequestContext ctx)
{
    public async Task<Lead> GetLead(Guid id) => await db.Leads.SingleOrDefaultAsync(l => l.Id == id) ?? throw new AppException("Not found.", 404);

    public async Task<Lead> SaveLead(Guid? id, LeadInput input)
    {
        var lead = id is { } lid ? await GetLead(lid) : new Lead { Number = await numbers.Next("L"), Name = "" };
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter their name.");
        var phone = string.IsNullOrWhiteSpace(input.Phone) ? "" : Phones.ToMsisdn(input.Phone) ?? throw new AppException("That phone number doesn't look right (e.g. 0712 345 678).");
        if (input.EstimatedValue < 0) throw new AppException("The value can't be negative.");
        if (phone.Length > 0 && await db.Leads.AnyAsync(l => l.Phone == phone && l.Id != lead.Id && l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost))
            throw new AppException("There's already an open lead with that phone number.", 409);
        (lead.Name, lead.Business, lead.Phone, lead.Email, lead.Source, lead.Interest) = (name, (input.Business ?? "").Trim(), phone, (input.Email ?? "").Trim(), input.Source ?? lead.Source, (input.Interest ?? "").Trim());
        lead.EstimatedValue = input.EstimatedValue ?? lead.EstimatedValue;
        var owner = input.OwnerUserId is { } uid ? await db.Users.SingleOrDefaultAsync(u => u.Id == uid) : null;
        if (id is null || owner is not null) (lead.OwnerUserId, lead.OwnerName) = owner is not null ? (owner.Id, owner.Name) : (ctx.UserId, ctx.Actor);
        if (id is null)
        {
            db.Leads.Add(lead);
            if (input.FollowUpOn is { } on) db.Activities.Add(new Activity { LeadId = lead.Id, Kind = ActivityKind.Note, Text = "New lead", FollowUpOn = on, Actor = ctx.Actor, ActorId = ctx.UserId });
        }
        await db.SaveChangesAsync();
        return lead;
    }

    public async Task<Lead> Move(Guid id, StageInput input)
    {
        var lead = await GetLead(id);
        if (lead.Stage == input.Stage) return lead;
        if (input.Stage == LeadStage.Lost && string.IsNullOrWhiteSpace(input.LostReason)) throw new AppException("Say why it was lost, so the team learns from it.");
        if (input.Stage == LeadStage.Won) return (await Convert(id)).Lead;
        db.Activities.Add(new Activity { LeadId = lead.Id, Kind = ActivityKind.Note, Text = $"Moved from {lead.Stage} to {input.Stage}{(input.Stage == LeadStage.Lost ? $": {input.LostReason!.Trim()}" : "")}", Actor = ctx.Actor, ActorId = ctx.UserId });
        (lead.Stage, lead.StageChangedAt, lead.LostReason) = (input.Stage, DateTimeOffset.UtcNow, input.Stage == LeadStage.Lost ? input.LostReason!.Trim() : "");
        if (input.Stage == LeadStage.Lost) await CloseFollowUps(lead.Id);
        await db.SaveChangesAsync();
        return lead;
    }

    /// <summary>Won: they become a customer (the existing one if their phone is already on file).</summary>
    public async Task<(Lead Lead, Customer Customer)> Convert(Guid id)
    {
        var lead = await GetLead(id);
        var customer = lead.CustomerId is { } cid ? await db.Customers.SingleOrDefaultAsync(c => c.Id == cid) : null;
        if (customer is null && lead.Phone.Length > 0) customer = await db.Customers.FirstOrDefaultAsync(c => c.Phone == lead.Phone);
        if (customer is null)
        {
            customer = new Customer { Number = await numbers.Next("C"), Name = lead.Business.Length > 0 ? lead.Business : lead.Name, Phone = lead.Phone, Email = lead.Email, Notes = lead.Business.Length > 0 ? $"Contact: {lead.Name}" : "" };
            db.Customers.Add(customer);
        }
        // First touch travels with them: the link, channel and person that brought the lead now get credit for the customer.
        customer.FirstLinkId ??= lead.LinkId;
        customer.AcquisitionChannel ??= lead.Channel;
        customer.AcquiredByUserId ??= lead.OwnerUserId;
        foreach (var i in await db.Inquiries.Where(i => i.LeadId == lead.Id && i.CustomerId == null).ToListAsync()) i.CustomerId = customer.Id;
        // Their conversation history moves with them.
        foreach (var a in await db.Activities.Where(a => a.LeadId == lead.Id).ToListAsync()) a.CustomerId = customer.Id;
        db.Activities.Add(new Activity { LeadId = lead.Id, CustomerId = customer.Id, Kind = ActivityKind.Note, Text = "Won: now a customer", Actor = ctx.Actor, ActorId = ctx.UserId });
        (lead.Stage, lead.StageChangedAt, lead.CustomerId, lead.LostReason) = (LeadStage.Won, DateTimeOffset.UtcNow, customer.Id, "");
        await db.SaveChangesAsync();
        return (lead, customer);
    }

    async Task CloseFollowUps(Guid leadId)
    {
        foreach (var a in await db.Activities.Where(a => a.LeadId == leadId && a.FollowUpOn != null && !a.FollowUpDone).ToListAsync()) a.FollowUpDone = true;
    }

    public async Task<Activity> Log(ActivityInput input)
    {
        if (input.LeadId is null && input.CustomerId is null) throw new AppException("Choose a lead or a customer.");
        if (input.LeadId is { } lid && !await db.Leads.AnyAsync(l => l.Id == lid)) throw new AppException("That lead no longer exists.");
        if (input.CustomerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid)) throw new AppException("That customer no longer exists.");
        var text = (input.Text ?? "").Trim();
        if (text.Length == 0 && input.FollowUpOn is null) throw new AppException("Write what happened, or set a follow-up.");
        if (text.Length > 2000) throw new AppException("Keep it under 2,000 characters.");
        // Talking to them answers any follow-up that was due: those close by themselves.
        if (input.Kind != ActivityKind.Note)
        {
            var today = Clock.Today;
            var due = db.Activities.Where(x => x.FollowUpOn != null && !x.FollowUpDone && x.FollowUpOn <= today);
            due = input.LeadId is { } l ? due.Where(x => x.LeadId == l) : due.Where(x => x.CustomerId == input.CustomerId);
            foreach (var old in await due.ToListAsync()) old.FollowUpDone = true;
        }
        var a = new Activity { LeadId = input.LeadId, CustomerId = input.CustomerId, Kind = input.Kind, Text = text, FollowUpOn = input.FollowUpOn, Actor = ctx.Actor, ActorId = ctx.UserId };
        db.Activities.Add(a);
        // Talking to a new lead moves it along by itself.
        if (input.LeadId is { } id && input.Kind != ActivityKind.Note && await db.Leads.SingleOrDefaultAsync(l => l.Id == id) is { Stage: LeadStage.New } lead)
            (lead.Stage, lead.StageChangedAt) = (LeadStage.Contacted, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return a;
    }

    public async Task<Activity> Done(Guid id)
    {
        var a = await db.Activities.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        a.FollowUpDone = true;
        await db.SaveChangesAsync();
        return a;
    }

    /// <summary>Follow-ups with who they're for and how to reach them, soonest first.</summary>
    public async Task<object> FollowUps(string? scope)
    {
        var today = Clock.Today;
        var q = db.Activities.Where(a => a.FollowUpOn != null && !a.FollowUpDone);
        q = scope switch { "all" => q, "upcoming" => q.Where(a => a.FollowUpOn > today), _ => q.Where(a => a.FollowUpOn <= today) };
        var rows = await q.OrderBy(a => a.FollowUpOn).Take(200).ToListAsync();
        var leadIds = rows.Where(r => r.LeadId != null).Select(r => r.LeadId!.Value).Distinct().ToList();
        var custIds = rows.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).Distinct().ToList();
        var leads = await db.Leads.Where(l => leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        var customers = await db.Customers.Where(c => custIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        return new
        {
            counts = new
            {
                due = await db.Activities.CountAsync(a => a.FollowUpOn != null && !a.FollowUpDone && a.FollowUpOn <= today),
                upcoming = await db.Activities.CountAsync(a => a.FollowUpOn != null && !a.FollowUpDone && a.FollowUpOn > today),
            },
            items = rows.Select(a =>
            {
                var lead = a.LeadId is { } l ? leads.GetValueOrDefault(l) : null;
                var cust = a.CustomerId is { } c ? customers.GetValueOrDefault(c) : null;
                return new
                {
                    a.Id, a.Kind, a.Text, a.FollowUpOn, a.Actor, late = a.FollowUpOn < today,
                    who = cust?.Name ?? (lead is null ? "?" : lead.Business.Length > 0 ? $"{lead.Name} ({lead.Business})" : lead.Name),
                    phone = cust?.Phone ?? lead?.Phone ?? "",
                    leadId = lead?.Id, customerId = cust?.Id, stage = lead?.Stage,
                };
            }),
        };
    }

    public async Task<object> Pipeline()
    {
        var monthStart = new DateOnly(Clock.Today.Year, Clock.Today.Month, 1).ToDateTime(TimeOnly.MinValue);
        var since = new DateTimeOffset(monthStart, TimeSpan.FromHours(3));
        var open = await db.Leads.Where(l => l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost).OrderByDescending(l => l.EstimatedValue).ToListAsync();
        var closed = await db.Leads.Where(l => (l.Stage == LeadStage.Won || l.Stage == LeadStage.Lost) && l.StageChangedAt >= since).ToListAsync();
        var nextFollowUps = await db.Activities.Where(a => a.LeadId != null && a.FollowUpOn != null && !a.FollowUpDone)
            .GroupBy(a => a.LeadId!.Value).Select(g => new { g.Key, next = g.Min(a => a.FollowUpOn) }).ToDictionaryAsync(x => x.Key, x => x.next);
        var stages = new[] { LeadStage.New, LeadStage.Contacted, LeadStage.Quoted };
        var won = closed.Where(l => l.Stage == LeadStage.Won).ToList();
        var lost = closed.Where(l => l.Stage == LeadStage.Lost).ToList();
        return new
        {
            stages = stages.Select(s => new
            {
                stage = s,
                count = open.Count(l => l.Stage == s),
                value = open.Where(l => l.Stage == s).Sum(l => l.EstimatedValue),
                leads = open.Where(l => l.Stage == s).Select(l => new { l.Id, l.Number, l.Name, l.Business, l.Phone, l.EstimatedValue, l.Source, l.OwnerName, l.Interest, l.StageChangedAt, nextFollowUp = nextFollowUps.GetValueOrDefault(l.Id) }),
            }),
            thisMonth = new
            {
                won = won.Count, wonValue = won.Sum(l => l.EstimatedValue), lost = lost.Count,
                winRate = won.Count + lost.Count == 0 ? (int?)null : (int)Math.Round(100.0 * won.Count / (won.Count + lost.Count)),
                lostReasons = lost.GroupBy(l => l.LostReason).Select(g => new { reason = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
            },
            sources = open.GroupBy(l => l.Source).Select(g => new { source = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
        };
    }
}
