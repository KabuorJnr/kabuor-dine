// Customer management (leads, pipeline, conversations, follow-ups) and marketing (segments, campaigns).
// Anyone signed in can look; owner, accountant and sales can change things and send campaigns.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Crm;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Marketing;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public static class GrowthEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---------- leads and pipeline ----------
        var crm = api.MapGroup("/crm").RequireAuthorization(Policies.Crm);
        crm.MapGet("/pipeline", (CrmService s) => s.Pipeline());
        crm.MapGet("/leads", async (ErpDb db, string? stage, string? q) =>
        {
            var query = db.Leads.AsQueryable();
            if (Enum.TryParse<LeadStage>(stage, true, out var st)) query = query.Where(l => l.Stage == st);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(l => EF.Functions.Like(l.Name, like) || EF.Functions.Like(l.Business, like) || EF.Functions.Like(l.Phone, like) || EF.Functions.Like(l.Number, like));
            }
            return await query.OrderByDescending(l => l.CreatedAt).Take(300).ToListAsync();
        });
        crm.MapGet("/leads/{id:guid}", async (Guid id, CrmService s, ErpDb db) =>
        {
            var lead = await s.GetLead(id);
            var activities = await db.Activities.Where(a => a.LeadId == id).OrderByDescending(a => a.At).Take(200).ToListAsync();
            return new { lead, activities };
        });
        crm.MapPost("/leads", async (LeadInput input, CrmService s) => Results.Created("", await s.SaveLead(null, input))).RequireAuthorization(Policies.Sell);
        crm.MapPut("/leads/{id:guid}", (Guid id, LeadInput input, CrmService s) => s.SaveLead(id, input)).RequireAuthorization(Policies.Sell);
        crm.MapPost("/leads/{id:guid}/stage", (Guid id, StageInput input, CrmService s) => s.Move(id, input)).RequireAuthorization(Policies.Sell);
        crm.MapPost("/leads/{id:guid}/convert", async (Guid id, CrmService s) =>
        {
            var (lead, customer) = await s.Convert(id);
            return new { lead, customer };
        }).RequireAuthorization(Policies.Sell);

        // ---------- conversations and follow-ups ----------
        crm.MapGet("/activities", async (ErpDb db, Guid? customerId, Guid? leadId) =>
        {
            if (customerId is null && leadId is null) throw new AppException("Say whose conversations.");
            var q = db.Activities.AsQueryable();
            if (customerId is { } c) q = q.Where(a => a.CustomerId == c);
            if (leadId is { } l) q = q.Where(a => a.LeadId == l);
            return await q.OrderByDescending(a => a.At).Take(200).ToListAsync();
        });
        crm.MapPost("/activities", async (ActivityInput input, CrmService s) => Results.Created("", await s.Log(input))).RequireAuthorization(Policies.Sell);
        crm.MapPost("/activities/{id:guid}/done", (Guid id, CrmService s) => s.Done(id)).RequireAuthorization(Policies.Sell);
        crm.MapGet("/follow-ups", (CrmService s, string? scope) => s.FollowUps(scope));

        // ---------- marketing ----------
        var mk = api.MapGroup("/marketing").RequireAuthorization(Policies.Crm);
        mk.MapGet("/audiences", (MarketingService s, int? days, int? top) => s.AudienceCounts(Math.Clamp(days ?? 60, 1, 3650), Math.Clamp(top ?? 20, 1, 5000)));
        mk.MapGet("/preview", async (MarketingService s, string audience, int? value, string? message) =>
        {
            var (people, noPhone, opted) = await s.Segment(audience, value ?? 60);
            var sample = people.Take(3).Select(p => new { p.Name, p.Phone, message = MarketingService.Fill(message ?? "", p) });
            return new { count = people.Count, noPhone, optedOut = opted, sample };
        });
        mk.MapGet("/campaigns", async (ErpDb db) =>
            await db.Campaigns.OrderByDescending(c => c.CreatedAt).Take(100)
                .Select(c => new { c.Id, c.Name, c.Channel, c.Audience, c.AudienceValue, c.Status, c.SentAt, c.TestMode, recipients = c.Recipients.Count, sent = c.Recipients.Count(r => r.Status == RecipientStatus.Sent) })
                .ToListAsync());
        mk.MapGet("/campaigns/{id:guid}", async (Guid id, ErpDb db, MarketingService s, ISmsSender sms) =>
        {
            var c = await db.Campaigns.Include(x => x.Recipients).SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            return new { campaign = c, results = await s.Results(c), smsLive = sms.Live };
        });
        mk.MapPost("/campaigns", async (CampaignInput input, MarketingService s) => Results.Created("", await s.Save(null, input))).RequireAuthorization(Policies.Sell);
        mk.MapPut("/campaigns/{id:guid}", (Guid id, CampaignInput input, MarketingService s) => s.Save(id, input)).RequireAuthorization(Policies.Sell);
        mk.MapPost("/campaigns/{id:guid}/send", (Guid id, MarketingService s, HttpRequest r) => s.Send(id, PublicUrl.Of(r))).RequireAuthorization(Policies.Sell);
        mk.MapPost("/campaigns/{id:guid}/recipients/{rid:guid}/sent", async (Guid id, Guid rid, MarketingService s) =>
        {
            await s.MarkSent(id, rid);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Sell);
        mk.MapGet("/sms-status", (ISmsSender sms) => new { live = sms.Live });
    }
}
