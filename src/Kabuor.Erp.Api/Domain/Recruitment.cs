// Hiring: the business's own job openings and the people who applied, from first contact to hired or turned down.
namespace Kabuor.Erp.Domain;

public sealed class JobOpening : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Title { get; set; }
    public string Department { get; set; } = "";
    public string Location { get; set; } = "";
    /// <summary>Full-time, Part-time, Contract, Casual or Internship.</summary>
    public string Type { get; set; } = "Full-time";
    public string Description { get; set; } = "";
    /// <summary>How many people this opening is for.</summary>
    public int Positions { get; set; } = 1;
    public bool Open { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
}

public enum CandidateStage
{
    Applied,
    Shortlisted,
    Interview,
    Offer,
    Hired,
    Rejected,
}

public sealed class Candidate : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid OpeningId { get; set; }
    public required string Name { get; set; }
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Where they heard about it: referral, walk-in, WhatsApp, a job board.</summary>
    public string Source { get; set; } = "";
    public CandidateStage Stage { get; set; } = CandidateStage.Applied;
    public DateOnly? InterviewOn { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>Why they were turned down (kept so the reasons can be counted).</summary>
    public string RejectReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StageChangedAt { get; set; } = DateTimeOffset.UtcNow;
}
