// Live sync between the module workspaces. Each module (Operations, Procurement & supply, Sales & marketing, Human
// resources, Finance, Intelligence) has its own workspace, and they all read and write the same records. When anyone
// saves anything, the audit rows ErpDb writes in the same transaction are turned into one small event per save and
// pushed to every open screen of that business over Server-Sent Events, so the other workspaces refresh by themselves
// instead of showing yesterday's numbers. Nothing in the event is new data: it says what changed, and the screens
// ask the API again, through the same permission checks as always.
//
// One server: events go straight from the save to the open streams. Several copies of the server: set
// Live__Postgres=true and they pass events to each other with PostgreSQL LISTEN/NOTIFY over the direct connection
// (DATABASE_URL_UNPOOLED). It is off by default because a connection held open keeps a Neon database awake.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Kabuor.Erp.Live;

/// <summary>One thing that changed in a save: which record, and a short label people can read.</summary>
public sealed record LiveChange(string Entity, string Action, string Id, string Module, string What);

/// <summary>One save, as the open screens hear about it.</summary>
public sealed record LiveEvent(Guid Company, DateTimeOffset At, string Actor, Guid? UserId, string? Client, string[] Modules, LiveChange[] Changes, int Total);

/// <summary>Which module owns each kind of record, what the modules hand each other, and who may see each module.</summary>
public static class ModuleMap
{
    public static readonly string[] Order = ["operations", "procurement", "sales", "people", "finance", "intelligence"];

    static readonly Dictionary<string, string> Owner = Build(new()
    {
        ["operations"] = ["Asset", "AssetEvent", "AssetType", "FaultReport", "WorkOrder", "WorkOrderPart", "WorkOrderLabour", "WorkOrderCost", "WorkOrderTool", "MaintenancePlan", "MeterReading", "DispatchTrip", "DeliveryItem", "Recipe", "RecipeLine", "ProductionRun"],
        ["procurement"] = ["Supplier", "PurchaseDoc", "PurchaseLine", "Requisition", "RequisitionLine", "Tender", "TenderLine", "Bid", "BidLine", "StockTake", "StockTakeLine", "StockCountLine", "StockLevel", "StockMove", "StockBatch", "Location", "Item", "ItemUnit", "EtimsPurchase"],
        ["sales"] = ["Customer", "SalesDoc", "SalesLine", "PriceList", "PriceListItem", "Lead", "Activity", "Campaign", "CampaignRecipient", "PosSession", "TrackingLink", "LinkVisit", "Inquiry", "InquiryMessage"],
        ["people"] = ["Employee", "LeaveRequest", "PayrollRun", "Payslip", "EmployeeDeduction", "Department", "JobOpening", "Candidate"],
        ["finance"] = ["Payment", "PaymentAllocation", "Account", "JournalEntry", "JournalLine", "SupplierPayment", "WithholdingCertificate", "Budget", "BudgetTransfer", "CashAdvance", "CashAdvanceExpense", "ApprovalRecord", "StatementImport", "StatementLine", "VerifiedShare", "MpesaRequest"],
        ["intelligence"] = ["PlanningMeeting", "AgentSetup", "AgentTask", "AgentStep", "Proposal"],
    });

    /// <summary>What each workspace needs to hear from the others: the handoffs along the chain.</summary>
    public static readonly Dictionary<string, string[]> Inbound = new()
    {
        ["operations"] = ["Requisition", "StockMove", "PurchaseDoc", "Employee", "LeaveRequest", "Budget"],
        ["procurement"] = ["WorkOrderPart", "WorkOrder", "ProductionRun", "SalesDoc", "Budget", "ApprovalRecord"],
        ["sales"] = ["Item", "PriceList", "Payment", "DispatchTrip", "DeliveryItem", "StockTake"],
        ["people"] = ["CashAdvance", "WorkOrderLabour", "Budget", "ApprovalRecord", "Department"],
        ["finance"] = ["Requisition", "PurchaseDoc", "SalesDoc", "PayrollRun", "WorkOrderCost", "Tender", "Payslip", "StockTake"],
        ["intelligence"] = ["SalesDoc", "Payment", "WorkOrder", "Budget", "Lead", "PurchaseDoc"],
    };

    static readonly Dictionary<string, string> Names = new()
    {
        ["SalesDoc"] = "sales document", ["SalesLine"] = "sales line", ["PurchaseDoc"] = "purchase document", ["PurchaseLine"] = "purchase line",
        ["WorkOrder"] = "work order", ["WorkOrderPart"] = "part on a job", ["WorkOrderLabour"] = "labour on a job", ["WorkOrderCost"] = "cost on a job",
        ["WorkOrderTool"] = "tool on a job", ["FaultReport"] = "fault report", ["MaintenancePlan"] = "maintenance plan", ["MeterReading"] = "meter reading",
        ["AssetEvent"] = "asset history", ["AssetType"] = "asset type", ["DispatchTrip"] = "trip", ["DeliveryItem"] = "delivery", ["ProductionRun"] = "production run",
        ["RequisitionLine"] = "request line", ["Requisition"] = "request", ["TenderLine"] = "tender line", ["BidLine"] = "bid line", ["StockTake"] = "stock take",
        ["StockTakeLine"] = "stock count", ["StockCountLine"] = "stock count", ["StockLevel"] = "stock level", ["StockMove"] = "stock movement", ["StockBatch"] = "stock batch",
        ["ItemUnit"] = "item unit", ["EtimsPurchase"] = "eTIMS purchase", ["PriceList"] = "price list", ["PriceListItem"] = "price", ["CampaignRecipient"] = "campaign recipient",
        ["PosSession"] = "till session", ["TrackingLink"] = "tracking link", ["LinkVisit"] = "link visit", ["InquiryMessage"] = "inquiry reply", ["LeaveRequest"] = "leave request",
        ["PayrollRun"] = "payroll run", ["EmployeeDeduction"] = "deduction", ["JobOpening"] = "job opening", ["PaymentAllocation"] = "payment allocation",
        ["JournalEntry"] = "journal entry", ["JournalLine"] = "journal line", ["SupplierPayment"] = "supplier payment", ["WithholdingCertificate"] = "withholding certificate",
        ["BudgetTransfer"] = "budget transfer", ["CashAdvance"] = "cash advance", ["CashAdvanceExpense"] = "advance expense", ["ApprovalRecord"] = "approval",
        ["StatementImport"] = "bank statement", ["StatementLine"] = "statement line", ["VerifiedShare"] = "Verified share", ["MpesaRequest"] = "M-Pesa prompt",
        ["PlanningMeeting"] = "monthly plan", ["AgentSetup"] = "agent", ["AgentTask"] = "agent task", ["AgentStep"] = "agent step",
    };

    // Lines and postings say nothing a person needs that their document doesn't already: kept out of the feeds people read.
    public static readonly HashSet<string> Quiet = ["SalesLine", "PurchaseLine", "JournalLine", "PaymentAllocation", "RequisitionLine", "TenderLine", "BidLine", "StockLevel", "StockBatch", "LinkVisit", "AgentStep", "StatementLine", "CampaignRecipient", "Sequence"];

    static Dictionary<string, string> Build(Dictionary<string, string[]> by) => by.SelectMany(kv => kv.Value.Select(e => (e, kv.Key))).ToDictionary(x => x.e, x => x.Key);

    public static string? Of(string entity) => Owner.GetValueOrDefault(entity);
    public static IEnumerable<string> EntitiesOf(string module) => Owner.Where(kv => kv.Value == module).Select(kv => kv.Key);
    public static string Name(string entity) => Names.GetValueOrDefault(entity) ?? System.Text.RegularExpressions.Regex.Replace(entity, "(?<!^)([A-Z])", " $1").ToLowerInvariant();

    /// <summary>Who may read a module's records, by the same policies the module's pages use.</summary>
    public static bool CanSee(Role role, string module) => module switch
    {
        "operations" => role.Allows(Policies.MaintainView) || role.Allows(Policies.FleetView) || role.Allows(Policies.Assets) || role.Allows(Policies.Make),
        "procurement" => role.Allows(Policies.BuyView) || role.Allows(Policies.StockView),
        "sales" => role.Allows(Policies.SalesView) || role.Allows(Policies.Crm) || role.Allows(Policies.Pos),
        "people" => role.Allows(Policies.People),
        "finance" => role.Allows(Policies.Finance) || role.Allows(Policies.Reports),
        "intelligence" => role.Allows(Policies.Reports),
        _ => false,
    };

    /// <summary>A short label from an audit row: its number or name, and a status change when there is one. Never amounts.</summary>
    public static string What(string entity, string action, string changes)
    {
        var name = Name(entity);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(changes) ? "{}" : changes);
            var r = doc.RootElement;
            string? Val(string p)
            {
                if (!r.TryGetProperty(p, out var v)) return null;
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("to", out var to)) v = to;
                return v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
            }
            var label = Val("Name") ?? Val("Title") ?? Val("Number") ?? Val("Code") ?? Val("Tag");
            if (label is { Length: > 60 }) label = label[..60] + "…";
            var status = action == "Modified" && r.TryGetProperty("Status", out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("to", out var st) ? st.ToString() : null;
            var verb = action switch { "Added" => "added", "Deleted" => "removed", _ => status is null ? "changed" : $"moved to {Spaced(status)}" };
            return label is null ? $"{Cap(name)} {verb}" : $"{Cap(name)} {label} {verb}";
        }
        catch (JsonException)
        {
            return $"{Cap(name)} {action.ToLowerInvariant()}";
        }
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string Spaced(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<!^)([A-Z])", " $1").ToLowerInvariant();
}

/// <summary>The open streams, per business, and the hand-off between copies of the server when there are several.</summary>
public sealed class LiveFeed(ILogger<LiveFeed> log)
{
    readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, System.Threading.Channels.Channel<LiveEvent>>> subs = new();
    internal Func<LiveEvent, bool>? Relay; // set by the PostgreSQL bridge while it is listening

    public int Open => subs.Values.Sum(s => s.Count);

    public Subscription Subscribe(Guid company)
    {
        var ch = System.Threading.Channels.Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        subs.GetOrAdd(company, _ => new()).TryAdd(id, ch);
        return new Subscription(ch.Reader, () =>
        {
            if (subs.TryGetValue(company, out var set)) set.TryRemove(id, out _);
        });
    }

    /// <summary>Called after a save: through the bridge when one is listening (so every copy hears it once), else here.</summary>
    public void Publish(LiveEvent e)
    {
        if (Relay is { } relay && relay(e)) return;
        Deliver(e);
    }

    internal void Deliver(LiveEvent e)
    {
        if (!subs.TryGetValue(e.Company, out var set)) return;
        foreach (var ch in set.Values) ch.Writer.TryWrite(e);
        log.LogDebug("Live: {Count} change(s) to {Streams} stream(s)", e.Total, set.Count);
    }

    public sealed class Subscription(ChannelReader<LiveEvent> reader, Action dispose) : IDisposable
    {
        public ChannelReader<LiveEvent> Reader { get; } = reader;
        public void Dispose() => dispose();
    }
}

/// <summary>Turns each successful save into one live event, from the audit rows ErpDb added to it.</summary>
public sealed class LiveSaveInterceptor(LiveFeed feed, IHttpContextAccessor http) : SaveChangesInterceptor
{
    static readonly ConditionalWeakTable<DbContext, List<AuditEntry>> pending = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (data.Context is { } c)
        {
            var rows = c.ChangeTracker.Entries<AuditEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
            pending.AddOrUpdate(c, rows);
        }
        return base.SavingChangesAsync(data, result, ct);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
    {
        if (data.Context is { } c && pending.TryGetValue(c, out var rows))
        {
            pending.Remove(c);
            try { Announce(rows); } catch (Exception) { /* live updates are a convenience: never fail a save over them */ }
        }
        return await base.SavedChangesAsync(data, result, ct);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData data, CancellationToken ct = default)
    {
        if (data.Context is { } c) pending.Remove(c);
        return base.SaveChangesFailedAsync(data, ct);
    }

    void Announce(List<AuditEntry> rows)
    {
        foreach (var group in rows.Where(r => r.CompanyId != Guid.Empty && ModuleMap.Of(r.Entity) is not null).GroupBy(r => r.CompanyId))
        {
            var all = group.ToList();
            var shown = all.Where(r => !ModuleMap.Quiet.Contains(r.Entity)).ToList();
            if (shown.Count == 0) shown = all; // only lines changed: still say so, so screens showing them refresh
            var changes = shown.Take(12).Select(r => new LiveChange(r.Entity, r.Action, r.EntityId, ModuleMap.Of(r.Entity)!, ModuleMap.What(r.Entity, r.Action, r.Changes))).ToArray();
            var modules = all.Select(r => ModuleMap.Of(r.Entity)!).Distinct().ToArray();
            var client = http.HttpContext?.Request.Headers["X-Client"].ToString();
            var first = all[0];
            feed.Publish(new LiveEvent(group.Key, DateTimeOffset.UtcNow, first.Actor, first.UserId, string.IsNullOrWhiteSpace(client) ? null : client[..Math.Min(client.Length, 40)], modules, changes, shown.Count));
        }
    }
}

/// <summary>Several copies of the server: each save is announced with NOTIFY and every copy, this one too, delivers what it hears.</summary>
public sealed class LivePostgresBridge(LiveFeed feed, IConfiguration config, ILogger<LivePostgresBridge> log) : BackgroundService
{
    const string Channel = "kerp_live";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!config.GetValue("Live:Postgres", false)) return;
        var raw = config["DATABASE_URL_UNPOOLED"] ?? config["DATABASE_URL"] ?? config.GetConnectionString("Erp");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var cs = Program.ConnectionString(raw);
        if (!cs.StartsWith("Host=", StringComparison.OrdinalIgnoreCase)) return;
        var outbox = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        await using var sender = NpgsqlDataSource.Create(cs);
        var sending = Send(sender, outbox.Reader, stop);
        var delay = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // One connection only listens (a connection waiting for notifications can't run commands); sends use the pool.
                await using var conn = new NpgsqlConnection(cs + ";Keepalive=30");
                await conn.OpenAsync(stop);
                conn.Notification += (_, n) =>
                {
                    try { if (JsonSerializer.Deserialize<LiveEvent>(n.Payload, Json) is { } e) feed.Deliver(e); } catch (JsonException) { }
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", conn)) await listen.ExecuteNonQueryAsync(stop);
                feed.Relay = e =>
                {
                    var payload = JsonSerializer.Serialize(e, Json);
                    if (payload.Length > 7500) payload = JsonSerializer.Serialize(e with { Changes = e.Changes.Take(2).ToArray() }, Json);
                    return outbox.Writer.TryWrite(payload);
                };
                log.LogInformation("Live updates: listening on PostgreSQL for the other copies of the server.");
                delay = TimeSpan.FromSeconds(1);
                while (!stop.IsCancellationRequested) await conn.WaitAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                feed.Relay = null; // deliver locally while the bridge is down
                log.LogWarning(ex, "Live updates: PostgreSQL bridge dropped; retrying in {Delay}.", delay);
                try { await Task.Delay(delay, stop); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
        feed.Relay = null;
        outbox.Writer.TryComplete();
        try { await sending; } catch (OperationCanceledException) { }
    }

    async Task Send(NpgsqlDataSource source, ChannelReader<string> outbox, CancellationToken stop)
    {
        await foreach (var payload in outbox.ReadAllAsync(stop))
        {
            try
            {
                await using var notify = source.CreateCommand("SELECT pg_notify(@c, @p)");
                notify.Parameters.AddWithValue("c", Channel);
                notify.Parameters.AddWithValue("p", payload);
                await notify.ExecuteNonQueryAsync(stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not sent: deliver it here at least, so the people on this copy still see it.
                try { if (JsonSerializer.Deserialize<LiveEvent>(payload, Json) is { } e) feed.Deliver(e); } catch (JsonException) { }
                log.LogWarning(ex, "Live updates: couldn't pass a change to the other copies.");
            }
        }
    }
}
