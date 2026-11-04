// Hiring for the business: openings with how many people applied and where each stands, and a candidate's journey
// Applied → Shortlisted → Interview → Offer → Hired (or Rejected, with a reason). HR, the accountant and the owner
// (the people policy) run it; candidates' details are personal data and stay inside the business.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Recruitment;

public sealed record OpeningInput(string Title, string? Department, string? Location, string? Type, string? Description, int? Positions, bool? Open);
public sealed record CandidateInput(Guid OpeningId, string Name, string? Phone, string? Email, string? Source, string? Notes);
public sealed record StageInput(CandidateStage Stage, DateOnly? InterviewOn, string? Reason, string? Notes);

public sealed class RecruitmentService(ErpDb db)
{
    static readonly string[] Types = ["Full-time", "Part-time", "Contract", "Casual", "Internship"];
    static bool Active(Candidate c) => c.Stage is not (CandidateStage.Hired or CandidateStage.Rejected);

    public async Task<object> Overview()
    {
        var openings = await db.JobOpenings.ToListAsync();
        var candidates = await db.Candidates.ToListAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        return new
        {
            counts = new
            {
                open = openings.Count(o => o.Open),
                closed = openings.Count(o => !o.Open),
                candidates = candidates.Count(Active),
                interviewsThisWeek = candidates.Count(c => c.Stage == CandidateStage.Interview && c.InterviewOn is { } d && d >= Clock.Today && d <= Clock.Today.AddDays(7)),
                hiredLast30 = candidates.Count(c => c.Stage == CandidateStage.Hired && c.StageChangedAt >= since),
            },
            openings = openings.OrderByDescending(o => o.Open).ThenByDescending(o => o.CreatedAt).Select(o =>
            {
                var mine = candidates.Where(c => c.OpeningId == o.Id).ToList();
                return new
                {
                    o.Id, o.Title, o.Department, o.Location, o.Type, o.Description, o.Positions, o.Open, o.CreatedAt, o.ClosedAt,
                    applied = mine.Count,
                    active = mine.Count(Active),
                    hired = mine.Count(c => c.Stage == CandidateStage.Hired),
                    stages = Enum.GetValues<CandidateStage>().ToDictionary(s => s.ToString(), s => mine.Count(c => c.Stage == s)),
                };
            }),
            types = Types,
        };
    }

    public async Task<object> Candidates(Guid? openingId, string? stage, string? q)
    {
        var query = db.Candidates.AsQueryable();
        if (openingId is { } oid) query = query.Where(c => c.OpeningId == oid);
        var all = await query.ToListAsync();
        var titles = await db.JobOpenings.ToDictionaryAsync(o => o.Id, o => o.Title);
        var term = (q ?? "").Trim();
        var shown = all
            .Where(c => term.Length == 0 || c.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || c.Phone.Contains(term) || c.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Where(c => string.IsNullOrEmpty(stage) || stage == "all" || (stage == "active" ? Active(c) : c.Stage.ToString().Equals(stage, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Stage switch { CandidateStage.Offer => 0, CandidateStage.Interview => 1, CandidateStage.Shortlisted => 2, CandidateStage.Applied => 3, CandidateStage.Hired => 4, _ => 5 })
            .ThenBy(c => c.InterviewOn ?? DateOnly.MaxValue).ThenByDescending(c => c.CreatedAt);
        return new
        {
            counts = Enum.GetValues<CandidateStage>().ToDictionary(s => s.ToString(), s => all.Count(c => c.Stage == s)),
            active = all.Count(Active),
            items = shown.Select(c => new
            {
                c.Id, c.OpeningId, opening = titles.GetValueOrDefault(c.OpeningId, ""), c.Name, c.Phone, c.Email, c.Source,
                stage = c.Stage.ToString(), c.InterviewOn, c.Notes, c.RejectReason, c.CreatedAt, c.StageChangedAt,
            }),
        };
    }

    static void Apply(JobOpening o, OpeningInput input)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length < 2) throw new AppException("Give the job a title, e.g. \"Delivery driver\".");
        if (title.Length > 120) throw new AppException("Keep the title under 120 characters.");
        var type = string.IsNullOrWhiteSpace(input.Type) ? o.Type
            : Types.FirstOrDefault(t => t.Equals(input.Type.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new AppException($"Choose one of: {string.Join(", ", Types)}.");
        var positions = input.Positions ?? o.Positions;
        if (positions is < 1 or > 500) throw new AppException("Hire for between 1 and 500 people.");
        var description = (input.Description ?? "").Trim();
        if (description.Length > 4000) throw new AppException("Keep the description under 4,000 characters.");
        (o.Title, o.Department, o.Location, o.Type, o.Positions, o.Description) = (title, (input.Department ?? "").Trim(), (input.Location ?? "").Trim(), type, positions, description);
    }

    public async Task<JobOpening> AddOpening(OpeningInput input)
    {
        var o = new JobOpening { Title = "" };
        Apply(o, input);
        db.JobOpenings.Add(o);
        await db.SaveChangesAsync();
        return o;
    }

    public async Task<JobOpening> UpdateOpening(Guid id, OpeningInput input)
    {
        var o = await db.JobOpenings.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        Apply(o, input);
        if (input.Open is { } open && open != o.Open) (o.Open, o.ClosedAt) = (open, open ? null : DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return o;
    }

    public async Task<Candidate> AddCandidate(CandidateInput input)
    {
        var opening = await db.JobOpenings.SingleOrDefaultAsync(o => o.Id == input.OpeningId) ?? throw new AppException("Choose the job they applied for.");
        if (!opening.Open) throw new AppException($"{opening.Title} is closed. Open it again to add applicants.");
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter the applicant's name.");
        var phone = string.IsNullOrWhiteSpace(input.Phone) ? "" : Phones.ToMsisdn(input.Phone) ?? throw new AppException("That phone number doesn't look right (e.g. 0712 345 678).");
        var email = (input.Email ?? "").Trim().ToLowerInvariant();
        if (email.Length > 0 && (!email.Contains('@') || email.Length > 200)) throw new AppException("That email doesn't look right.");
        if (phone.Length == 0 && email.Length == 0) throw new AppException("Add a phone number or email, so you can reach them.");
        if (phone.Length > 0 && await db.Candidates.AnyAsync(c => c.OpeningId == opening.Id && c.Phone == phone)) throw new AppException("They've already applied for this job.", 409);
        var c = new Candidate { OpeningId = opening.Id, Name = name, Phone = phone, Email = email, Source = (input.Source ?? "").Trim(), Notes = (input.Notes ?? "").Trim() };
        db.Candidates.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    /// <summary>Moves a candidate on (or back). An interview needs a date; turning someone down needs a reason; an
    /// opening can't hire more people than it was for.</summary>
    public async Task<Candidate> Move(Guid id, StageInput input)
    {
        var c = await db.Candidates.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (!Enum.IsDefined(input.Stage)) throw new AppException("Choose a stage.");
        if (input.Stage == CandidateStage.Interview && input.InterviewOn is null && c.InterviewOn is null) throw new AppException("Pick the interview date.");
        if (input.Stage == CandidateStage.Rejected && (input.Reason ?? "").Trim().Length < 3) throw new AppException("Say why (it helps you see why people don't make it).");
        if (input.Stage == CandidateStage.Hired && c.Stage != CandidateStage.Hired)
        {
            var opening = await db.JobOpenings.SingleAsync(o => o.Id == c.OpeningId);
            var hired = await db.Candidates.CountAsync(x => x.OpeningId == opening.Id && x.Stage == CandidateStage.Hired);
            if (hired >= opening.Positions) throw new AppException($"{opening.Title} was for {opening.Positions} {(opening.Positions == 1 ? "person" : "people")} and is filled. Add a position to the opening first.");
        }
        if (input.InterviewOn is { } on) c.InterviewOn = on;
        c.RejectReason = input.Stage == CandidateStage.Rejected ? input.Reason!.Trim() : "";
        if (!string.IsNullOrWhiteSpace(input.Notes)) c.Notes = string.IsNullOrEmpty(c.Notes) ? input.Notes.Trim() : $"{c.Notes}\n{input.Notes.Trim()}";
        if (c.Stage != input.Stage) (c.Stage, c.StageChangedAt) = (input.Stage, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return c;
    }
}
