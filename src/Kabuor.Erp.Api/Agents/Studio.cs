// Agents the owner makes (Oracle AI Agent Studio, SAP Joule Studio, Business Central Agent Designer, Odoo's
// agents): a name, the job in plain words, the skills it may use, and how often it works. It can only look
// things up with those skills, under the role of whoever switched it on, and anything it wants done is a task a
// person approves, exactly like the built-in agents.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Agents;

public sealed record CustomAgentInput(string Name, string Instructions, List<string> Skills, string Every);

public sealed partial class AgentService
{
    static readonly Dictionary<string, (TimeSpan Every, string Text)> Cadence = new()
    {
        ["hourly"] = (TimeSpan.FromHours(1), "Every hour"),
        ["daily"] = (TimeSpan.FromHours(24), "Every morning"),
        ["weekly"] = (TimeSpan.FromDays(7), "Every Monday"),
    };

    /// <summary>Ready-made starting points, like the studios' templates. Each only uses skills that exist here.</summary>
    public static readonly object[] Templates =
    [
        new { name = "Morning briefing", every = "daily", skills = new[] { "today_summary", "list_invoices", "unmatched_payments", "stock_levels" },
            instructions = "Every morning, look at today's summary, overdue invoices, unmatched payments and low stock. Write one finding with the three things the owner should do first today, most money first." },
        new { name = "Big debtors watch", every = "weekly", skills = new[] { "list_invoices", "find_customers", "propose_reminder" },
            instructions = "Find customers who owe more than KSh 50,000 or are more than 30 days late. For each, propose a polite reminder in English. If someone owes a lot but isn't late yet, note it as a finding instead." },
        new { name = "Supplier bills due", every = "daily", skills = new[] { "list_bills", "find_suppliers" },
            instructions = "Find supplier bills due in the next 7 days or already overdue. Write one finding listing them by due date with amounts, and the total needed this week." },
        new { name = "Slow sales alert", every = "weekly", skills = new[] { "sales_report", "today_summary" },
            instructions = "Compare this week's sales with the week before. If sales fell by more than 15%, write a finding with the numbers and what changed. Otherwise write a short finding saying sales are steady." },
    ];

    static JsonObject Custom(AgentSetup s) => JsonNode.Parse(s.Settings) as JsonObject ?? [];
    static List<string> Skills(AgentSetup s) => Custom(s)["skills"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    AgentInfo CustomInfo(AgentSetup? s)
    {
        if (s is null) return new("custom:gone", "Removed agent", "", Policies.Admin, [], [], TimeSpan.FromDays(3650), "");
        var c = Custom(s);
        var every = Cadence.GetValueOrDefault(c["every"]?.GetValue<string>() ?? "daily", Cadence["daily"]);
        var skills = Skills(s);
        var policies = assistant.SkillPolicies();
        var acts = skills.Where(k => k.StartsWith("propose_")).ToList();
        return new(s.Key, c["name"]?.GetValue<string>() ?? "Agent", c["instructions"]?.GetValue<string>() ?? "", Policies.Read,
            [$"Looks things up: {string.Join(", ", skills.Where(k => !k.StartsWith("propose_")).Select(Plain))}", "Writes findings for you"],
            acts.Count == 0 ? ["It can't change anything; it only reports"] : acts.Select(k => $"{Plain(k)} (always asks)").ToArray(),
            every.Every, every.Text);
    }

    static string Plain(string skill) => skill.Replace("propose_", "").Replace('_', ' ');

    bool CanUse(string skill, Role role) => assistant.SkillPolicies().TryGetValue(skill, out var p) && role.Allows(p);

    void CheckSkills(IEnumerable<string> skills, Role role)
    {
        var missing = skills.Where(k => !CanUse(k, role)).ToList();
        if (missing.Count > 0) throw new AppException($"Your role can't use {string.Join(", ", missing.Select(Plain))}, so an agent you switch on can't either. Remove {(missing.Count == 1 ? "it" : "them")} or ask the owner.", 403);
    }

    public object Studio() => new { skills = assistant.Skills(), templates = Templates, cadence = Cadence.Select(c => new { id = c.Key, text = c.Value.Text }), aiReady = !string.IsNullOrEmpty(config["Anthropic:ApiKey"]) };

    public async Task<object> SaveCustom(string? key, CustomAgentInput input)
    {
        var name = (input.Name ?? "").Trim();
        var instructions = (input.Instructions ?? "").Trim();
        if (name.Length is < 3 or > 60) throw new AppException("Give the agent a short name (3–60 characters).");
        if (instructions.Length is < 20 or > 2000) throw new AppException("Describe its job in a few sentences (20–2,000 characters): what to look at, and what to tell you or propose.");
        var policies = assistant.SkillPolicies();
        var skills = (input.Skills ?? []).Distinct().ToList();
        if (skills.Count == 0) throw new AppException("Give it at least one skill.");
        if (skills.Any(k => !policies.ContainsKey(k))) throw new AppException("One of those skills doesn't exist.");
        if (!Cadence.ContainsKey(input.Every ?? "")) throw new AppException("Choose how often it works.");
        CheckSkills(skills, ctx.Role);
        AgentSetup s;
        if (key is null)
        {
            if (await db.AgentSetups.CountAsync(x => x.Key.StartsWith("custom:")) >= 20) throw new AppException("You can have up to 20 agents of your own.");
            db.AgentSetups.Add(s = new AgentSetup { Key = $"custom:{Guid.NewGuid():N}" });
        }
        else s = await db.AgentSetups.SingleOrDefaultAsync(x => x.Key == key && x.Key.StartsWith("custom:")) ?? throw new AppException("Not found.", 404);
        var c = Custom(s);
        (c["name"], c["instructions"], c["every"]) = (name, instructions, input.Every);
        c["skills"] = new JsonArray(skills.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
        s.Settings = c.ToJsonString();
        // A changed job goes back to off: the owner switches it on again knowing what it now does.
        if (key is not null && s.Enabled) (s.Enabled, s.LastRunSummary) = (false, "Switched off after its job changed. Switch it on to start.");
        await db.SaveChangesAsync();
        return new { s.Key, name };
    }

    public async Task DeleteCustom(string key)
    {
        var s = await db.AgentSetups.SingleOrDefaultAsync(x => x.Key == key && x.Key.StartsWith("custom:")) ?? throw new AppException("Not found.", 404);
        db.AgentSetups.Remove(s);
        await db.SaveChangesAsync();
    }

    async Task<string> RunCustom(AgentInfo a, AgentSetup s)
    {
        var c = Custom(s);
        var (proposals, findings) = (0, 0);
        var summary = await assistant.Work(a.Name, c["instructions"]!.GetValue<string>(), Skills(s), s.EnabledByRole,
            propose: async (kind, title, payload) =>
            {
                var p = JsonSerializer.SerializeToNode(payload)!.AsObject();
                var t = NewTask(a, title, "Proposed this while doing its job.");
                switch (kind)
                {
                    case "match_payment":
                        t.Action = "match_payment";
                        t.Payload = new JsonObject
                        {
                            ["paymentId"] = p["paymentId"]!.GetValue<Guid>(),
                            ["allocations"] = new JsonArray(new JsonObject { ["invoiceId"] = p["invoiceId"]!.GetValue<Guid>(), ["amount"] = p["amount"]!.GetValue<long>() }),
                        }.ToJsonString();
                        (t.SubjectKind, t.SubjectId, t.Link) = ("payment", p["paymentId"]!.GetValue<Guid>(), "/money-in?status=to-match");
                        break;
                    case "send_reminder":
                        var invoice = await db.SalesDocs.SingleAsync(d => d.Id == p["invoiceId"]!.GetValue<Guid>());
                        t.Action = "send_reminder";
                        t.Payload = new JsonObject { ["customerId"] = invoice.CustomerId, ["phone"] = p["phone"]!.GetValue<string>(), ["text"] = p["text"]!.GetValue<string>() }.ToJsonString();
                        (t.SubjectKind, t.SubjectId, t.Link) = ("customer", invoice.CustomerId, $"/sales/{invoice.Id}");
                        break;
                }
                await db.SaveChangesAsync();
                proposals++;
                return new { task_id = t.Id, status = "waiting for a person to approve" };
            },
            finding: async (title, detail) =>
            {
                var t = NewTask(a, title.Length > 0 ? title : "Finding", detail);
                t.Detail = detail;
                await db.SaveChangesAsync();
                findings++;
                return new { task_id = t.Id, status = "shown to the owner" };
            });
        return proposals + findings == 0 ? summary : $"{summary} ({findings} finding{(findings == 1 ? "" : "s")}, {proposals} proposal{(proposals == 1 ? "" : "s")})";
    }
}
