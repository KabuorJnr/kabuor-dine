// Campaigns. The person picks who (a segment, with the count shown before sending), writes the message once
// (personalised per customer), and sends: SMS through Africa's Talking, or WhatsApp one tap at a time (bulk
// WhatsApp needs the WhatsApp Business API, which comes later). Customers who opted out are never included,
// every SMS says how to stop, and results show what those customers bought in the 14 days after.
using System.Net.Http.Json;
using System.Text.Json;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Marketing;

public sealed record CampaignInput(string Name, CampaignChannel Channel, string Audience, int? AudienceValue, string Message);
public sealed record Person(Guid? CustomerId, Guid? LeadId, string Name, string Phone, long Balance);

public interface ISmsSender
{
    bool Live { get; }
    Task<(bool Ok, string Error)> Send(string msisdn, string message);
}

/// <summary>Africa's Talking bulk SMS. Needs AfricasTalking:Username and :ApiKey (and optionally :SenderId).</summary>
public sealed class AfricasTalkingSms(IHttpClientFactory http, IConfiguration config) : ISmsSender
{
    public bool Live => !string.IsNullOrEmpty(config["AfricasTalking:ApiKey"]) && !string.IsNullOrEmpty(config["AfricasTalking:Username"]);

    public async Task<(bool Ok, string Error)> Send(string msisdn, string message)
    {
        if (!Live) return (true, ""); // test mode: nothing leaves the server
        var form = new Dictionary<string, string> { ["username"] = config["AfricasTalking:Username"]!, ["to"] = $"+{msisdn}", ["message"] = message };
        if (config["AfricasTalking:SenderId"] is { Length: > 0 } from) form["from"] = from;
        using var req = new HttpRequestMessage(HttpMethod.Post, config["AfricasTalking:Username"] == "sandbox" ? "https://api.sandbox.africastalking.com/version1/messaging" : "https://api.africastalking.com/version1/messaging") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("apiKey", config["AfricasTalking:ApiKey"]);
        req.Headers.Add("Accept", "application/json");
        try
        {
            using var res = await http.CreateClient().SendAsync(req);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            var status = body.TryGetProperty("SMSMessageData", out var d) && d.TryGetProperty("Recipients", out var r) && r.GetArrayLength() > 0 ? r[0].GetProperty("status").GetString() : null;
            return status == "Success" ? (true, "") : (false, status ?? $"HTTP {(int)res.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, "Couldn't reach the SMS service.");
        }
    }
}

public sealed class MarketingService(ErpDb db, ISmsSender sms, RequestContext ctx, Intelligence.LinkService links)
{
    public static readonly string[] Audiences = ["all", "recent", "lapsed", "top", "owing", "never-bought", "leads"];
    const int MaxRecipients = 5000;

    /// <summary>The people in a segment, with phones, opt-outs removed. Also how many were left out and why.</summary>
    public async Task<(List<Person> People, int NoPhone, int OptedOut)> Segment(string audience, int value)
    {
        if (!Audiences.Contains(audience)) throw new AppException("Choose who it goes to.");
        if (audience == "leads")
        {
            var leads = await db.Leads.Where(l => l.Stage != LeadStage.Won && l.Stage != LeadStage.Lost).ToListAsync();
            return (leads.Where(l => l.Phone.Length > 0).Select(l => new Person(null, l.Id, l.Name, l.Phone, 0)).ToList(), leads.Count(l => l.Phone.Length == 0), 0);
        }
        var today = Clock.Today;
        var invoices = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void)
            .Select(d => new { d.CustomerId, d.IssueDate, d.Total, d.Paid, d.Status }).ToListAsync();
        var byCustomer = invoices.GroupBy(i => i.CustomerId).ToDictionary(g => g.Key, g => new
        {
            last = g.Max(i => i.IssueDate),
            spendYear = g.Where(i => i.IssueDate >= today.AddDays(-365)).Sum(i => i.Total),
            owes = g.Where(i => i.Status is SalesDocStatus.Sent or SalesDocStatus.PartPaid).Sum(i => i.Total - i.Paid),
        });
        var customers = await db.Customers.ToListAsync();
        IEnumerable<Customer> chosen = audience switch
        {
            "recent" => customers.Where(c => byCustomer.TryGetValue(c.Id, out var s) && s.last >= today.AddDays(-value)),
            "lapsed" => customers.Where(c => byCustomer.TryGetValue(c.Id, out var s) && s.last < today.AddDays(-value)),
            "top" => customers.Where(c => byCustomer.TryGetValue(c.Id, out var s) && s.spendYear > 0).OrderByDescending(c => byCustomer[c.Id].spendYear).Take(Math.Max(1, value)),
            "owing" => customers.Where(c => byCustomer.TryGetValue(c.Id, out var s) && s.owes > 0),
            "never-bought" => customers.Where(c => !byCustomer.ContainsKey(c.Id)),
            _ => customers,
        };
        var list = chosen.ToList();
        var people = list.Where(c => !c.MarketingOptOut && c.Phone.Length > 0)
            .Select(c => new Person(c.Id, null, c.Name, c.Phone, byCustomer.TryGetValue(c.Id, out var s) ? s.owes : 0)).ToList();
        return (people, list.Count(c => c.Phone.Length == 0), list.Count(c => c.MarketingOptOut));
    }

    public async Task<object> AudienceCounts(int days, int top)
    {
        var result = new Dictionary<string, object>();
        foreach (var a in Audiences)
        {
            var (people, noPhone, opted) = await Segment(a, a == "top" ? top : days);
            result[a] = new { count = people.Count, noPhone, optedOut = opted };
        }
        return result;
    }

    async Task<string> StopLine()
    {
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        return c.Phone.Length > 0 ? $" To stop these messages, call or WhatsApp 0{c.Phone[3..]}." : " Reply STOP to stop these messages.";
    }

    public static string Fill(string template, Person p) =>
        template.Replace("{name}", p.Name).Replace("{first_name}", p.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p.Name)
            .Replace("{balance}", $"KSh {p.Balance / 100m:N0}");

    public async Task<Campaign> Save(Guid? id, CampaignInput input)
    {
        var c = id is { } cid ? await db.Campaigns.SingleOrDefaultAsync(x => x.Id == cid) ?? throw new AppException("Not found.", 404) : new Campaign { Name = "" };
        if (c.Status != CampaignStatus.Draft) throw new AppException("This campaign has been sent; make a new one.");
        var name = (input.Name ?? "").Trim();
        var message = (input.Message ?? "").Trim();
        if (name.Length < 2) throw new AppException("Name the campaign, e.g. “December offer”.");
        if (!Audiences.Contains(input.Audience)) throw new AppException("Choose who it goes to.");
        if (message.Length < 5) throw new AppException("Write the message.");
        if (input.Channel == CampaignChannel.Sms && message.Length > 450) throw new AppException("Keep an SMS under 450 characters (3 messages).");
        (c.Name, c.Channel, c.Audience, c.AudienceValue, c.Message) = (name, input.Channel, input.Audience, Math.Clamp(input.AudienceValue ?? 60, 1, 3650), message);
        if (id is null) db.Campaigns.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>Sends an SMS campaign now, or prepares a WhatsApp one (a ready message per person).</summary>
    public async Task<Campaign> Send(Guid id, string baseUrl = "")
    {
        var c = await db.Campaigns.Include(x => x.Recipients).SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (c.Status != CampaignStatus.Draft) throw new AppException("Already sent.");
        var (people, _, _) = await Segment(c.Audience, c.AudienceValue);
        if (people.Count == 0) throw new AppException("Nobody in that group has a phone number (or they all opted out).");
        if (people.Count > MaxRecipients) throw new AppException($"That's {people.Count:N0} people; send to at most {MaxRecipients:N0} at a time.");
        var stop = c.Channel == CampaignChannel.Sms ? await StopLine() : "";
        // {link}: a tracking link made for this campaign, so its clicks, inquiries and sales are measured.
        var message = c.Message;
        if (message.Contains("{link}"))
        {
            var link = await links.Create(new Intelligence.LinkInput($"Campaign: {c.Name}", c.Channel == CampaignChannel.Sms ? Channel.Sms : Channel.WhatsApp, null, c.Id, "shop"));
            message = message.Replace("{link}", $"{baseUrl.TrimEnd('/')}/l/{link.Code}");
        }
        foreach (var p in people)
        {
            var r = new CampaignRecipient { CustomerId = p.CustomerId, LeadId = p.LeadId, Name = p.Name, Phone = p.Phone, Message = Fill(message, p) + stop };
            if (c.Channel == CampaignChannel.Sms)
            {
                var (ok, error) = await sms.Send(p.Phone, r.Message);
                (r.Status, r.Error) = ok ? (RecipientStatus.Sent, "") : (RecipientStatus.Failed, error);
            }
            c.Recipients.Add(r);
        }
        (c.Status, c.SentAt, c.SentBy, c.TestMode) = (CampaignStatus.Sent, DateTimeOffset.UtcNow, ctx.Actor, c.Channel == CampaignChannel.Sms && !sms.Live);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task MarkSent(Guid campaignId, Guid recipientId)
    {
        var r = await db.CampaignRecipients.SingleOrDefaultAsync(x => x.Id == recipientId && x.CampaignId == campaignId) ?? throw new AppException("Not found.", 404);
        r.Status = RecipientStatus.Sent;
        await db.SaveChangesAsync();
    }

    /// <summary>What happened after: delivery, and invoices to those customers in the 14 days after sending.</summary>
    public async Task<object> Results(Campaign c)
    {
        if (c.SentAt is null) return new { sent = 0, failed = 0, ready = 0, buyers = 0, sales = 0L, windowDays = 14 };
        var from = DateOnly.FromDateTime(c.SentAt.Value.UtcDateTime.AddHours(3));
        var to = from.AddDays(14);
        var ids = c.Recipients.Where(r => r.CustomerId != null && r.Status == RecipientStatus.Sent).Select(r => r.CustomerId!.Value).ToList();
        var leadIds = c.Recipients.Where(r => r.LeadId != null).Select(r => r.LeadId!.Value).ToList();
        // Leads who became customers count too.
        ids.AddRange(await db.Leads.Where(l => leadIds.Contains(l.Id) && l.CustomerId != null).Select(l => l.CustomerId!.Value).ToListAsync());
        var bought = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void && ids.Contains(d.CustomerId) && d.IssueDate >= from && d.IssueDate <= to)
            .Select(d => new { d.CustomerId, d.Total }).ToListAsync();
        return new
        {
            sent = c.Recipients.Count(r => r.Status == RecipientStatus.Sent),
            failed = c.Recipients.Count(r => r.Status == RecipientStatus.Failed),
            ready = c.Recipients.Count(r => r.Status == RecipientStatus.Ready),
            buyers = bought.Select(b => b.CustomerId).Distinct().Count(),
            sales = bought.Sum(b => b.Total),
            windowDays = 14,
        };
    }
}
