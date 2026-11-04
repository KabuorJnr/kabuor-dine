// Marketing: messages to a chosen group of customers or leads, by SMS or WhatsApp, and what sold afterwards.
namespace Kabuor.Erp.Domain;

public enum CampaignChannel
{
    Sms,
    WhatsApp,
}

public enum CampaignStatus
{
    Draft,
    Sent,
}

public sealed class Campaign : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public CampaignChannel Channel { get; set; } = CampaignChannel.Sms;
    /// <summary>Who it goes to: all, recent, lapsed, top, owing, never-bought, leads.</summary>
    public string Audience { get; set; } = "all";
    /// <summary>For "recent"/"lapsed": the number of days; for "top": how many customers.</summary>
    public int AudienceValue { get; set; } = 60;
    /// <summary>The message, with {name}, {first_name} and {balance} filled in per person.</summary>
    public string Message { get; set; } = "";
    public CampaignStatus Status { get; set; } = CampaignStatus.Draft;
    public bool TestMode { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string SentBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<CampaignRecipient> Recipients { get; set; } = [];
}

public enum RecipientStatus
{
    Ready,   // WhatsApp: link ready, not yet opened by a person
    Sent,
    Failed,
}

public sealed class CampaignRecipient : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid CampaignId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Message { get; set; } = "";
    public RecipientStatus Status { get; set; } = RecipientStatus.Ready;
    public string Error { get; set; } = "";
}
