// Agents: their setup (the owner switches them on), "run now", and the task inbox people approve from.
using Kabuor.Erp.Agents;
using Kabuor.Erp.Auth;

namespace Kabuor.Erp;

public static class AgentEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agents");
        g.MapGet("", (AgentService s) => s.List());
        g.MapPut("/{key}", (string key, AgentConfigInput input, AgentService s) => s.Configure(key, input)).RequireAuthorization(Policies.Admin);
        g.MapPost("/{key}/run", (string key, AgentService s) => s.RunNow(key));
        g.MapGet("/tasks", (string? status, AgentService s) => s.Tasks(status));
        // Handing the order taking agent a customer's message (Business Central's Sales Order agent, by paste).
        g.MapPost("/orders/intake", (OrderIntakeInput input, AgentService s) => s.TakeOrder(input));
        // The studio: agents the owner describes and equips.
        g.MapGet("/studio", (AgentService s) => s.Studio()).RequireAuthorization(Policies.Admin);
        g.MapPost("/custom", (CustomAgentInput input, AgentService s) => s.SaveCustom(null, input)).RequireAuthorization(Policies.Admin);
        g.MapPut("/custom/{key}", (string key, CustomAgentInput input, AgentService s) => s.SaveCustom(key, input)).RequireAuthorization(Policies.Admin);
        g.MapDelete("/custom/{key}", async (string key, AgentService s) => { await s.DeleteCustom(key); return Results.NoContent(); }).RequireAuthorization(Policies.Admin);
        g.MapPost("/tasks/{id:guid}/approve", (Guid id, AgentDecision? input, AgentService s) => s.Decide(id, true, input ?? new(null, null)));
        g.MapPost("/tasks/{id:guid}/dismiss", (Guid id, AgentDecision? input, AgentService s) => s.Decide(id, false, input ?? new(null, null)));
    }
}
