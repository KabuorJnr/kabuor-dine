// Hiring: openings and applicants, for the people who look after staff (HR, the accountant, the owner).
using Kabuor.Erp.Auth;
using Kabuor.Erp.Recruitment;

namespace Kabuor.Erp;

public static class RecruitmentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var hiring = api.MapGroup("/recruitment").RequireAuthorization(Policies.People);
        hiring.MapGet("", (RecruitmentService s) => s.Overview());
        hiring.MapPost("/openings", async (OpeningInput input, RecruitmentService s) => Results.Created("", await s.AddOpening(input)));
        hiring.MapPut("/openings/{id:guid}", (Guid id, OpeningInput input, RecruitmentService s) => s.UpdateOpening(id, input));
        hiring.MapGet("/candidates", (RecruitmentService s, Guid? openingId, string? stage, string? q) => s.Candidates(openingId, stage, q));
        hiring.MapPost("/candidates", async (CandidateInput input, RecruitmentService s) => Results.Created("", await s.AddCandidate(input)));
        hiring.MapPost("/candidates/{id:guid}/stage", (Guid id, StageInput input, RecruitmentService s) => s.Move(id, input));
    }
}
