// Agents: background workers that do a job for the business on a schedule, the way Dynamics 365 agents do.
// Each agent is its own named actor in the audit log, runs with no more rights than the person who switched it on,
// does safe internal work by itself, and turns anything that leaves the business or moves money into a task a
// person approves. Every task keeps a timeline of what the agent and people did.
namespace Kabuor.Erp.Domain;

/// <summary>One agent switched on (or off) for one company.</summary>
public sealed class AgentSetup : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Key { get; set; } // collections | matching | restock
    public bool Enabled { get; set; }
    /// <summary>Who switched it on, and their role then: the agent never does more than that role allows.</summary>
    public Guid? EnabledBy { get; set; }
    public string EnabledByName { get; set; } = "";
    public Role EnabledByRole { get; set; } = Role.Viewer;
    /// <summary>The agent's own settings, as JSON (e.g. how often to remind a customer).</summary>
    public string Settings { get; set; } = "{}";
    public DateTimeOffset? LastRunAt { get; set; }
    public string LastRunSummary { get; set; } = "";
    /// <summary>A business event asked for a run (a payment arrived, stock was sold); the scheduler picks it up within a minute.</summary>
    public DateTimeOffset? KickedAt { get; set; }
}

public enum AgentTaskStatus
{
    NeedsReview, // waiting for a person
    Done,        // approved and carried out, or finished by the agent itself
    Dismissed,   // a person said no
    Failed,
}

/// <summary>One piece of work an agent did or wants to do, with its timeline.</summary>
public sealed class AgentTask : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string AgentKey { get; set; }
    public required string Title { get; set; }
    /// <summary>What the person reviewing should know, in plain words.</summary>
    public string Detail { get; set; } = "";
    /// <summary>What approving does (e.g. "send_reminder"); empty when there's nothing to approve.</summary>
    public string Action { get; set; } = "";
    public string Payload { get; set; } = "{}";
    /// <summary>What it's about, so the agent doesn't repeat itself and the page can link to it.</summary>
    public string SubjectKind { get; set; } = ""; // customer | payment | purchase
    public Guid? SubjectId { get; set; }
    public string Link { get; set; } = "";
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.NeedsReview;
    public string Result { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public string DecidedBy { get; set; } = "";
    /// <summary>Done by the agent alone, under its review policy (not approved by a person).</summary>
    public bool ByItself { get; set; }
    /// <summary>A person changed the agent's draft before approving it.</summary>
    public bool Edited { get; set; }
    public List<AgentStep> Steps { get; set; } = [];
}

/// <summary>A line on a task's timeline: who did what, when.</summary>
public sealed class AgentStep : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid AgentTaskId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public required string Who { get; set; } // "Collections agent" or a person's name
    public required string Text { get; set; }
}
