// Kabuor Intelligence: where customers come from and what happens to them. A tracking link (one per salesperson,
// per channel, per campaign) is the first touch; its visits, the inquiries they make, the customers they become
// and the sales those customers bring are all tied back to it, so the owner can follow any customer from the
// first click to the stock that left the shelf, and see which person and which channel brings the business.
namespace Kabuor.Erp.Domain;

public enum Channel
{
    WhatsApp,
    Facebook,
    Instagram,
    TikTok,
    LinkedIn,
    X,
    YouTube,
    Google,
    Email,
    Sms,
    Website,
    Referral,
    Event,
    WalkIn,
    Other,
}

/// <summary>A link shared by a salesperson or posted on a channel. Visiting it is counted; what follows is credited to it.</summary>
public sealed class TrackingLink : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    /// <summary>The short code in the address (/l/{code}); unique across all businesses.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Channel Channel { get; set; } = Channel.Other;
    /// <summary>The salesperson it belongs to, if any: their sales and the customers it brings count for them.</summary>
    public Guid? OwnerUserId { get; set; }
    public string OwnerName { get; set; } = "";
    public Guid? CampaignId { get; set; }
    /// <summary>Where it opens: the shop ("shop"), the inquiry form ("ask"), or one product (an item id).</summary>
    public string Target { get; set; } = "shop";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LinkVisit : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid LinkId { get; set; }
    /// <summary>A random id kept in the visitor's browser, to count people rather than clicks.</summary>
    public string VisitorId { get; set; } = "";
    public string Referrer { get; set; } = "";
    public bool Mobile { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public enum InquiryStatus
{
    Open,      // waiting for the business
    Answered,  // the business replied; waiting for the customer
    Quoted,    // a quote went out
    Won,       // it became a sale
    Lost,      // closed without a sale, with the reason
}

/// <summary>A question or request from a customer or a would-be customer, from the web, WhatsApp, a call or a visit.</summary>
public sealed class Inquiry : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // ENQ-0001
    public Guid? CustomerId { get; set; }
    public Guid? LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>The product they asked about, if they picked one.</summary>
    public Guid? ItemId { get; set; }
    public Channel Channel { get; set; } = Channel.Website;
    public Guid? LinkId { get; set; }
    public InquiryStatus Status { get; set; } = InquiryStatus.Open;
    public Guid? AssignedUserId { get; set; }
    public string AssignedName { get; set; } = "";
    /// <summary>Who or what brought this inquiry in (salesperson name, link, campaign, or channel).</summary>
    public string BroughtByName { get; set; } = "";
    public DateTimeOffset? FirstResponseAt { get; set; }
    public string LostReason { get; set; } = "";
    public Guid? QuoteId { get; set; }
    public Guid? InvoiceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public List<InquiryMessage> Messages { get; set; } = [];
}

public sealed class InquiryMessage : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid InquiryId { get; set; }
    /// <summary>customer | staff | agent</summary>
    public string From { get; set; } = "customer";
    public string Author { get; set; } = "";
    public required string Text { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A one-time sign-in code sent to a customer's phone for the self-service portal.</summary>
public sealed class PortalCode : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Phone { get; set; }
    public required string CodeHash { get; set; }
    public int Attempts { get; set; }
    public bool Used { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One line of a stock count: what the system expected and what was on the shelf, kept even when they
/// agree, so stock accuracy can be measured (system stock must match the warehouse).</summary>
public sealed class StockCountLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid CountId { get; set; }
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public decimal Expected { get; set; }
    public decimal Counted { get; set; }
    /// <summary>Cost of one unit at the time, in cents.</summary>
    public decimal UnitCost { get; set; }
    public string CountedBy { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
