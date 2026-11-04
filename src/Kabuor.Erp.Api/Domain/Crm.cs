// Customer management: people who might buy (leads), every conversation with a lead or customer, and the
// follow-ups that keep them from going cold.
namespace Kabuor.Erp.Domain;

public enum LeadStage
{
    New,
    Contacted,
    Quoted,
    Won,
    Lost,
}

public enum LeadSource
{
    WhatsApp,
    Instagram,
    Facebook,
    Referral,
    WalkIn,
    Website,
    Call,
    Event,
    Other,
}

public sealed class Lead : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // L-0001
    public required string Name { get; set; }   // the person
    public string Business { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public LeadSource Source { get; set; } = LeadSource.Other;
    public LeadStage Stage { get; set; } = LeadStage.New;
    /// <summary>What it could be worth, in cents.</summary>
    public long EstimatedValue { get; set; }
    public string Interest { get; set; } = ""; // what they asked about
    public Guid? OwnerUserId { get; set; }
    public string OwnerName { get; set; } = "";
    public string LostReason { get; set; } = "";
    public Guid? CustomerId { get; set; } // once won
    /// <summary>Attribution: the tracking link, channel, or inquiry that brought this lead in.</summary>
    public Guid? LinkId { get; set; }
    public Channel? Channel { get; set; }
    public Guid? InquiryId { get; set; }
    public DateTimeOffset StageChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ActivityKind
{
    Call,
    WhatsApp,
    Visit,
    Meeting,
    Email,
    Note,
}

/// <summary>Something that happened with a lead or customer, optionally with a follow-up to do.</summary>
public sealed class Activity : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid? LeadId { get; set; }
    public Guid? CustomerId { get; set; }
    public ActivityKind Kind { get; set; } = ActivityKind.Note;
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = "";
    public Guid? ActorId { get; set; }
    /// <summary>When to get back to them (a to-do), if any.</summary>
    public DateOnly? FollowUpOn { get; set; }
    public bool FollowUpDone { get; set; }
}
