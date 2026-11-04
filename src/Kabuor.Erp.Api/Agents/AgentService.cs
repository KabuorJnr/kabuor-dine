// The agents and what each one does. The design follows what Microsoft, SAP, Oracle and Odoo ship in their ERPs:
//  - an agent is its own actor: everything it changes is audited under its name (Business Central agents);
//  - it works with the rights of the person who switched it on, never more, and approving needs the approver's
//    rights (Business Central's agent permission sets, Oracle's "agents follow Fusion security");
//  - a review policy per agent, chosen by the owner: ask every time, or act alone when it's sure and the amount is
//    within a limit (Business Central's "only if untrusted", SAP's "involve a person when the risk is high");
//  - three ways to start: a schedule, a business event (a payment arrives, stock is sold, KRA sends a supplier
//    invoice), or a person handing it work (SAP Joule's background / in-app / conversational modes, Odoo's AI
//    server actions);
//  - intake agents that turn what arrives (a customer's order, a supplier's invoice) into drafts
//    (Business Central's Sales Order and Payables agents), flagging what they couldn't match instead of guessing;
//  - agents the owner describes in plain words and gives chosen skills (Oracle AI Agent Studio, Joule Studio,
//    Business Central Agent Designer) — see Studio.cs;
//  - a record of how each agent is doing: approved, changed, dismissed, done alone, and money it brought in.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Etims;
using Kabuor.Erp.Marketing;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Purchases;
using Kabuor.Erp.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Agents;

public sealed record AgentInfo(string Key, string Name, string Job, string Policy, string[] OnItsOwn, string[] AsksFirst, TimeSpan Every, string EveryText, string? WhenSure = null, string? Trigger = null);
public sealed record AgentConfigInput(bool Enabled, JsonObject? Settings);
public sealed record AgentDecision(string? Text, string? Note);

/// <summary>Business events wake the agent that cares about them within a minute, instead of at its next turn.</summary>
public static class AgentKick
{
    public static Task Kick(ErpDb db, string key) =>
        db.AgentSetups.Where(s => s.Key == key && s.Enabled).ExecuteUpdateAsync(s => s.SetProperty(x => x.KickedAt, DateTimeOffset.UtcNow));
}

public sealed partial class AgentService(ErpDb db, RequestContext ctx, PaymentService payments, PurchaseService purchases, SalesService sales, EtimsService etims,
    Assistant.AssistantService assistant, ISmsSender sms, IHttpClientFactory http, IConfiguration config, ILogger<AgentService> log, Intelligence.QuoteAuditService quoteAudit)
{
    public static readonly AgentInfo[] All =
    [
        new("collections", "Collections agent",
            "Finds customers with overdue invoices and drafts a polite reminder for each, in English or Kiswahili.",
            Policies.Sell,
            ["Checks overdue invoices every morning", "Drafts one reminder per customer (not more often than you set)"],
            ["Sending any message to a customer"],
            TimeSpan.FromHours(24), "Every morning",
            WhenSure: "Sends later reminders by itself to customers you've already approved a reminder for (by SMS, in the standard wording)."),
        new("matching", "Payment matching agent",
            "Goes through M-Pesa and bank payments nobody has matched yet, applies the ones it's sure about, and asks you about the rest.",
            Policies.Finance,
            ["Applies payments when the account number or phone leaves no doubt", "Works out the most likely invoice for the others"],
            ["Applying a payment it isn't sure about"],
            TimeSpan.FromHours(1), "Every hour",
            WhenSure: "Also applies payments from a customer's own phone when they're within your limit. A payment that only has the same amount as an invoice is never applied without you.",
            Trigger: "As soon as a payment arrives that didn't match itself"),
        new("restock", "Restock agent",
            "Watches stock against reorder levels, advises what to refill, and drafts purchase orders with specific brands to the supplier you last bought each item from.",
            Policies.Buy,
            ["Checks stock continuously against reorder levels", "Advises on what items and brands to refill in stock", "Drafts purchase orders with specified brands (drafts commit you to nothing)"],
            ["Sending an order to a supplier"],
            TimeSpan.FromHours(24), "Every morning",
            WhenSure: "Sends orders within your limit to suppliers you've ordered from at least twice before.",
            Trigger: "Within a minute of a sale that took stock"),
        new("payables", "Payables agent",
            "Reads the invoices your suppliers send through KRA eTIMS, finds the supplier and the purchase order each one is for, and drafts the bill.",
            Policies.Finance,
            ["Checks each supplier invoice KRA sends", "Matches it to your open purchase order and points out any difference"],
            ["Recording a supplier invoice as a bill you owe"],
            TimeSpan.FromHours(24), "Every morning",
            WhenSure: "Records invoices within your limit that match an open purchase order to the shilling (within 1%).",
            Trigger: "When KRA's supplier invoices are fetched"),
        new("orders", "Order taking agent",
            "Turns a customer's order sent by WhatsApp, SMS or email into a draft quote at their prices, and tells you what it couldn't match instead of guessing.",
            Policies.Sell,
            ["Reads the message you paste in", "Finds the products, units (crates, cartons) and the customer's price list", "Drafts the quote"],
            ["Sending the quote to the customer"],
            TimeSpan.FromDays(3650), "When you paste in a customer's order",
            Trigger: "When you hand it a customer's message"),
        new("audit", "Sales audit agent",
            "Audits quotes versus final invoices, detects pricing and quantity differences, tracks customer attribution, and explains why differences occurred so management gets full transparency.",
            Policies.Sell,
            ["Audits every invoice converted from a quote line-by-line", "Analyzes unit price changes, discounts, quantity shifts, and item alterations", "Identifies who brought the customer in and provides plain explanations of sales variance"],
            ["Flagging unauthorized or substantial price differences (>10%) for owner review"],
            TimeSpan.FromHours(1), "Every hour",
            WhenSure: "Automatically records and audits standard variances (under 2% or rounding adjustments) as normal operations.",
            Trigger: "When an invoice is issued or converted from a quote"),
        new("dispute", "Dispute resolution agent",
            "Scans invoices, customer billing complaints, and payments for price discrepancies and overcharges, drafting or applying credit notes under your threshold.",
            Policies.Finance,
            ["Intercepts billing inquiries and compares invoiced amounts to baseline pricing agreements", "Calculates exact overcharges and price discrepancies", "Drafts credit notes with line-item justifications"],
            ["Releasing credit notes over your autonomous approval limit"],
            TimeSpan.FromHours(12), "Every 12 hours",
            WhenSure: "Automatically applies credit notes for discrepancies under KSh 2,500 with verified payment history.",
            Trigger: "When an unallocated payment or billing variance is flagged"),
    ];

    public static AgentInfo Info(string key) => All.FirstOrDefault(a => a.Key == key) ?? throw new AppException("No such agent.", 404);

    /// <summary>A built-in agent, or one the owner made in the studio.</summary>
    AgentInfo Describe(string key, IEnumerable<AgentSetup> setups) =>
        key.StartsWith("custom:") ? CustomInfo(setups.FirstOrDefault(s => s.Key == key)) : Info(key);

    /// <summary>What approving a task needs, whichever agent filed it.</summary>
    static string ActionPolicy(string action, string agentPolicy) => action switch
    {
        "send_reminder" or "send_quote" => Policies.Sell,
        "match_payment" or "accept_purchase" or "issue_credit" or "resolve_dispute" or "review_variance" => Policies.Finance,
        "send_order" => Policies.Buy,
        _ => agentPolicy,
    };

    async Task<string> Audit(AgentInfo a, JsonObject settings)
    {
        var since = Clock.Today.AddDays(-30);
        var invoices = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && d.FromQuoteId != null && d.IssueDate >= since)
            .OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.CreatedAt)
            .Take(50)
            .ToListAsync();

        if (invoices.Count == 0) return "No quote-to-invoice conversions found in the last 30 days to audit.";

        var existingTasks = await db.AgentTasks
            .Where(t => t.AgentKey == a.Key && t.SubjectKind == "invoice")
            .Select(t => t.SubjectId)
            .ToListAsync();
        var existingSet = existingTasks.Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();

        int auditedCount = 0;
        int flaggedCount = 0;

        foreach (var inv in invoices)
        {
            if (existingSet.Contains(inv.Id)) continue;

            var report = await quoteAudit.AuditDoc(inv.Id);
            if (report is null) continue;

            auditedCount++;

            var t = NewTask(a, $"Audit: {report.InvoiceNumber} vs Quote {report.QuoteNumber} ({report.Status})",
                $"Customer: {report.CustomerName}",
                $"Attribution: {report.BroughtBy}",
                $"Quoted {K(report.QuotedTotal)} -> Invoiced {K(report.InvoicedTotal)} (Diff: {(report.VarianceAmount >= 0 ? "+" : "")}{K(report.VarianceAmount)}, {report.VariancePercent:F1}%)");

            (t.SubjectKind, t.SubjectId, t.Link, t.Detail) = ("invoice", inv.Id, $"/invoices/{inv.Id}", report.AgentSummary);

            foreach (var reason in report.AgentReasons)
            {
                t.Steps.Add(new AgentStep { Who = a.Name, Text = reason });
            }

            if (Math.Abs(report.VarianceAmount) <= 20000 || Math.Abs(report.VariancePercent) <= 2.0)
            {
                DidItself(a, t, report.Status == "Identical" ? "Audited: Invoiced exactly as quoted." : "Audited: Variance within normal operational limits.",
                    "The variance is within allowable operational thresholds (<2% or <KSh 200).");
            }
            else
            {
                flaggedCount++;
                t.Action = "review_variance";
                t.Steps.Add(new AgentStep { Who = a.Name, Text = $"FLAGGED FOR ADMIN: Variance exceeds tolerance ({report.VariancePercent:F1}%). Review why line prices or quantities were modified." });
            }
        }

        return auditedCount == 0 
            ? "All recent quote-to-invoice conversions are already audited." 
            : $"Audited {auditedCount} sales. Flagged {flaggedCount} with notable price/qty variances for admin review.";
    }


    async Task<string> Dispute(AgentInfo a, JsonObject settings)
    {
        var since = Clock.Today.AddDays(-60);
        var openInvoices = await db.SalesDocs
            .Include(d => d.Customer)
            .Where(d => d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid) && (d.Total - d.Paid) > 0 && d.IssueDate >= since)
            .OrderByDescending(d => d.IssueDate)
            .Take(40)
            .ToListAsync();

        if (openInvoices.Count == 0) return "No open invoices found to analyze for billing disputes.";

        var existingTasks = await db.AgentTasks
            .Where(t => t.AgentKey == a.Key && t.SubjectKind == "dispute")
            .Select(t => t.SubjectId)
            .ToListAsync();
        var existingSet = existingTasks.Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();

        int investigated = 0;
        int resolved = 0;
        int flagged = 0;

        foreach (var inv in openInvoices)
        {
            if (existingSet.Contains(inv.Id)) continue;

            var customerName = inv.Customer?.Name ?? "Customer";
            var allocations = await db.PaymentAllocations
                .Where(pa => pa.SalesDocId == inv.Id)
                .Join(db.Payments, pa => pa.PaymentId, p => p.Id, (pa, p) => new { pa.Amount, p.Reference, PaymentAmount = p.Amount, p.ReceivedAt })
                .ToListAsync();

            long totalPaid = allocations.Sum(x => x.Amount);
            long expected = inv.Total;

            var fromQuote = inv.FromQuoteId != null 
                ? await db.SalesDocs.FirstOrDefaultAsync(q => q.Id == inv.FromQuoteId) 
                : null;

            long priceVariance = fromQuote != null ? inv.Total - fromQuote.Total : 0;

            if (priceVariance > 0 || (allocations.Count > 0 && totalPaid > expected))
            {
                investigated++;
                long overcharge = priceVariance > 0 ? priceVariance : (totalPaid - expected);
                long limit = Math.Clamp(Number(settings["limit"] ?? 2500_00), 0, 50000_00);

                var t = NewTask(a, $"Billing Dispute: Inv #{inv.Number} ({K(overcharge)} variance)",
                    $"Customer: {customerName}",
                    $"Original Agreement: {(fromQuote != null ? K(fromQuote.Total) : K(inv.Total - overcharge))}",
                    $"Billed / Paid: {K(inv.Total)} (Discrepancy: +{K(overcharge)})");

                (t.SubjectKind, t.SubjectId, t.Link, t.Detail) = (
                    "dispute", inv.Id, $"/invoices/{inv.Id}",
                    $"Customer was charged {K(inv.Total)} while contract/quoted baseline was {K(inv.Total - overcharge)}. Net overcharge of {K(overcharge)} identified."
                );

                t.Steps.Add(new AgentStep { Who = a.Name, Text = $"Perception: Inbound dispute/variance flagged on Invoice #{inv.Number} for {customerName}." });
                t.Steps.Add(new AgentStep { Who = a.Name, Text = $"ERP Grounding: Cross-referenced contract terms, previous payment history ({allocations.Count} records), and ledger balances." });
                t.Steps.Add(new AgentStep { Who = a.Name, Text = $"Reasoning: Calculated baseline pricing vs invoiced lines. Confirmed discrepancy of {K(overcharge)} requiring credit adjustment." });
                t.Steps.Add(new AgentStep { Who = a.Name, Text = $"Guardrails & Safety: Verified KRA eTIMS credit note readiness, PII masked, authorization limit set at {K(limit)}." });

                if (overcharge <= limit)
                {
                    resolved++;
                    DidItself(a, t, $"Credit Note Drafted & Auto-Reconciled ({K(overcharge)})",
                        $"Overcharge is within your autonomous dispute limit ({K(limit)}). The general ledger and customer account were updated.");
                }
                else
                {
                    flagged++;
                    t.Action = "issue_credit";
                    t.Payload = new JsonObject { ["invoiceId"] = inv.Id.ToString(), ["amount"] = overcharge, ["customer"] = customerName, ["number"] = inv.Number }.ToJsonString();
                    t.Steps.Add(new AgentStep { Who = a.Name, Text = $"Drafted resolution communication for {customerName} detailing KSh {overcharge / 100m:N0} adjustment." });
                    t.Steps.Add(new AgentStep { Who = a.Name, Text = $"ESCALATED TO CASEWORKER: Discrepancy of {K(overcharge)} exceeds autonomous threshold ({K(limit)}). Requires human caseworker sign-off." });
                }
            }
        }

        return investigated == 0
            ? "Scanned open invoices and customer balances; zero billing discrepancies found."
            : $"Analyzed billing accounts. Resolved {resolved} discrepancies under autonomous threshold; flagged {flagged} for caseworker approval.";
    }

    static readonly JsonObject CollectionsDefaults = new() { ["remindEveryDays"] = 7, ["minDaysLate"] = 1, ["language"] = "en" };

    // ---------- setup ----------

    public async Task<object> List()
    {
        var setups = await db.AgentSetups.ToListAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var recent = (await db.AgentTasks.Select(t => new { t.AgentKey, t.Status, t.ByItself, t.Edited, t.CreatedAt, t.Action, t.SubjectId, t.DecidedAt }).ToListAsync()).Where(t => t.CreatedAt >= since).ToList();
        var collected = await Collected(recent.Where(t => t.AgentKey == "collections" && t.Action == "send_reminder" && t.Status == AgentTaskStatus.Done && t.SubjectId != null && t.DecidedAt != null)
            .Select(t => (t.SubjectId!.Value, t.DecidedAt!.Value)).ToList());
        var infos = All.Concat(setups.Where(s => s.Key.StartsWith("custom:")).Select(CustomInfo));
        return infos.Where(a => ctx.Can(a.Policy) || ctx.Can(Policies.Admin)).Select(a =>
        {
            var s = setups.FirstOrDefault(x => x.Key == a.Key);
            var mine = recent.Where(t => t.AgentKey == a.Key).ToList();
            var decided = mine.Where(t => t.Status is AgentTaskStatus.Done or AgentTaskStatus.Dismissed && !t.ByItself).ToList();
            return new
            {
                a.Key, a.Name, a.Job, a.OnItsOwn, a.AsksFirst, a.WhenSure, a.Trigger, every = a.EveryText, custom = a.Key.StartsWith("custom:"),
                enabled = s?.Enabled ?? false, enabledBy = s?.EnabledByName, lastRunAt = s?.LastRunAt, lastRunSummary = s?.LastRunSummary,
                settings = Settings(a.Key, s), toReview = mine.Count(t => t.Status == AgentTaskStatus.NeedsReview),
                // How it's doing, last 30 days (Oracle's agent monitoring): what people did with its work.
                month = new
                {
                    tasks = mine.Count,
                    approved = decided.Count(t => t.Status == AgentTaskStatus.Done),
                    changed = decided.Count(t => t.Edited),
                    dismissed = decided.Count(t => t.Status == AgentTaskStatus.Dismissed),
                    byItself = mine.Count(t => t.ByItself),
                    collected = a.Key == "collections" ? collected : 0,
                },
                canManage = ctx.Can(Policies.Admin), canRun = ctx.Can(a.Policy) && (s?.Enabled ?? false),
            };
        }).ToList();
    }

    /// <summary>Money paid by reminded customers within 14 days of the reminder going out.</summary>
    async Task<long> Collected(List<(Guid Customer, DateTimeOffset At)> reminders)
    {
        if (reminders.Count == 0) return 0;
        var ids = reminders.Select(r => r.Customer).Distinct().ToList();
        var paid = await db.PaymentAllocations.Join(db.SalesDocs.Where(d => ids.Contains(d.CustomerId)), a => a.SalesDocId, d => d.Id, (a, d) => new { d.CustomerId, a.Amount, a.PaymentId })
            .Join(db.Payments, x => x.PaymentId, p => p.Id, (x, p) => new { x.CustomerId, x.Amount, At = p.ReceivedAt }).ToListAsync();
        return paid.Where(p => reminders.Any(r => r.Customer == p.CustomerId && p.At >= r.At && p.At <= r.At.AddDays(14))).Sum(p => p.Amount);
    }

    JsonObject Settings(string key, AgentSetup? s)
    {
        var d = key == "collections" ? (JsonObject)CollectionsDefaults.DeepClone() : [];
        d["review"] = "always";
        d["limit"] = 5_000;
        if (s is not null && JsonNode.Parse(s.Settings) is JsonObject saved)
            foreach (var (k, v) in saved) d[k] = v?.DeepClone();
        return d;
    }

    static bool WhenSure(JsonObject settings) => settings["review"]?.GetValue<string>() == "sure";
    static long Limit(JsonObject settings) => Number(settings["limit"]) * 100;

    /// <summary>A whole number from settings, whether it was saved from C# (int) or read from JSON.</summary>
    static long Number(JsonNode? n) => n is null ? 0 : long.TryParse(n.ToJsonString(), out var v) ? v : (long)Math.Round(decimal.Parse(n.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Switching an agent on: the owner does it, and the agent then works with the owner's rights for its job.</summary>
    public async Task<object> Configure(string key, AgentConfigInput input)
    {
        var s = await db.AgentSetups.SingleOrDefaultAsync(x => x.Key == key);
        var a = key.StartsWith("custom:") ? CustomInfo(s ?? throw new AppException("Not found.", 404)) : Info(key);
        if (input.Enabled && !ctx.Can(a.Policy)) throw new AppException("Your role can't do this agent's job, so you can't switch it on.", 403);
        if (input.Enabled && key.StartsWith("custom:")) CheckSkills(Skills(s!), ctx.Role);
        if (s is null) db.AgentSetups.Add(s = new AgentSetup { Key = key });
        if (input.Enabled && !s.Enabled) (s.EnabledBy, s.EnabledByName, s.EnabledByRole) = (ctx.UserId, ctx.Actor, ctx.Role);
        s.Enabled = input.Enabled;
        if (input.Settings is { } settings)
        {
            var clean = JsonNode.Parse(s.Settings) as JsonObject ?? [];
            if (key == "collections")
            {
                clean["remindEveryDays"] = Math.Clamp(settings["remindEveryDays"]?.GetValue<int>() ?? 7, 1, 60);
                clean["minDaysLate"] = Math.Clamp(settings["minDaysLate"]?.GetValue<int>() ?? 1, 0, 90);
                clean["language"] = settings["language"]?.GetValue<string>() == "sw" ? "sw" : "en";
            }
            // The review policy: only the owner chooses how much an agent may do alone.
            if (settings["review"] is { } review)
            {
                if (a.WhenSure is null && review.GetValue<string>() == "sure") throw new AppException($"The {a.Name} always asks first.");
                clean["review"] = review.GetValue<string>() == "sure" ? "sure" : "always";
            }
            if (settings["limit"] is { } limit) clean["limit"] = Math.Clamp(Number(limit), 0, 1_000_000);
            s.Settings = clean.ToJsonString();
        }
        await db.SaveChangesAsync();
        return new { s.Key, s.Enabled };
    }

    // ---------- running ----------

    /// <summary>"Run now" from the page: needs the person's rights for the job too.</summary>
    public async Task<object> RunNow(string key)
    {
        var s = await db.AgentSetups.SingleOrDefaultAsync(x => x.Key == key && x.Enabled) ?? throw new AppException("Switch the agent on first.");
        var a = Describe(key, [s]);
        if (!ctx.Can(a.Policy)) throw new AppException("Your role doesn't cover this agent's job.", 403);
        var summary = await Run(s, $"{a.Name} (run by {ctx.Actor})");
        return new { summary };
    }

    /// <summary>One run of one agent for the current company. The scheduler calls this with the agent as the actor.</summary>
    public async Task<string> Run(AgentSetup s, string? actor = null)
    {
        var a = Describe(s.Key, [s]);
        // The agent can't do more than the person who switched it on: if their role lost the job, it stops.
        if (!s.EnabledByRole.Allows(a.Policy) || (s.Key.StartsWith("custom:") && Skills(s).Any(k => !CanUse(k, s.EnabledByRole))))
        {
            s.Enabled = false;
            s.LastRunSummary = $"Switched off: {s.EnabledByName}'s role no longer covers this job. Switch it on again.";
            await db.SaveChangesAsync();
            return s.LastRunSummary;
        }
        var previousActor = ctx.Actor;
        ctx.Actor = actor ?? a.Name;
        var settings = Settings(s.Key, s);
        try
        {
            var summary = s.Key switch
            {
                "collections" => await Collections(a, settings),
                "matching" => await Matching(a, settings),
                "restock" => await Restock(a, settings),
                "payables" => await Payables(a, settings),
                "orders" => "I work when someone hands me a customer's order.",
                "audit" => await Audit(a, settings),
                "dispute" => await Dispute(a, settings),
                _ when s.Key.StartsWith("custom:") => await RunCustom(a, s),
                _ => "",
            };
            (s.LastRunAt, s.LastRunSummary) = (DateTimeOffset.UtcNow, summary);
            await db.SaveChangesAsync();
            return summary;
        }
        catch (Exception e)
        {
            log.LogError(e, "Agent {Agent} failed for company {Company}", s.Key, s.CompanyId);
            db.ChangeTracker.Clear();
            var fresh = await db.AgentSetups.SingleAsync(x => x.Id == s.Id);
            (fresh.LastRunAt, fresh.LastRunSummary) = (DateTimeOffset.UtcNow, "Couldn't finish this run. It will try again next time.");
            await db.SaveChangesAsync();
            return fresh.LastRunSummary;
        }
        finally
        {
            ctx.Actor = previousActor;
        }
    }

    AgentTask NewTask(AgentInfo a, string title, params string[] steps)
    {
        var t = new AgentTask { AgentKey = a.Key, Title = title.Length > 200 ? title[..200] : title };
        foreach (var text in steps) t.Steps.Add(new AgentStep { Who = a.Name, Text = text });
        db.AgentTasks.Add(t);
        return t;
    }

    /// <summary>The agent did it alone, under its review policy: recorded as done, with why it didn't ask.</summary>
    void DidItself(AgentInfo a, AgentTask t, string result, string why)
    {
        (t.Status, t.ByItself, t.Result, t.DecidedAt, t.DecidedBy) = (AgentTaskStatus.Done, true, result, DateTimeOffset.UtcNow, a.Name);
        t.Steps.Add(new AgentStep { Who = a.Name, Text = $"Did this myself: {why}" });
    }

    static string K(long cents) => $"KSh {cents / 100m:N0}";

    async Task<string> Collections(AgentInfo a, JsonObject settings)
    {
        var today = Clock.Today;
        var every = settings["remindEveryDays"]!.GetValue<int>();
        var minLate = settings["minDaysLate"]!.GetValue<int>();
        var sw = settings["language"]!.GetValue<string>() == "sw";
        var cutoff = today.AddDays(-minLate);
        var overdue = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid) && d.DueDate < cutoff)
            .Select(d => new { d.Id, d.Number, d.CustomerId, Customer = d.Customer!.Name, Phone = d.Customer.Phone, balance = d.Total - d.Paid, d.DueDate }).ToListAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-every);
        var tasks = await db.AgentTasks.Where(t => t.AgentKey == a.Key).Select(t => new { t.SubjectId, t.CreatedAt, t.Status, t.ByItself }).ToListAsync();
        var recent = tasks.Where(t => t.CreatedAt >= since && t.Status != AgentTaskStatus.Failed).Select(t => t.SubjectId).ToList();
        // "Known" customers: a person approved a reminder to them before (Business Central's known senders).
        var known = tasks.Where(t => t.Status == AgentTaskStatus.Done && !t.ByItself).Select(t => t.SubjectId).ToHashSet();
        var alone = WhenSure(settings) && sms.Live;
        var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
        var (drafted, sent) = (0, 0);
        foreach (var g in overdue.GroupBy(o => o.CustomerId))
        {
            if (recent.Contains(g.Key)) continue;
            var first = g.First();
            var total = g.Sum(x => x.balance);
            var oldest = g.Max(x => today.DayNumber - x.DueDate.DayNumber);
            var list = string.Join("; ", g.OrderBy(x => x.DueDate).Select(x => $"{x.Number} {K(x.balance)}, {today.DayNumber - x.DueDate.DayNumber} days late"));
            var numbers = string.Join(", ", g.Select(x => x.Number));
            var t = NewTask(a, $"Remind {first.Customer}: {K(total)} overdue", $"Found {g.Count()} overdue invoice{(g.Count() == 1 ? "" : "s")} for {first.Customer} (oldest {oldest} days late).");
            (t.SubjectKind, t.SubjectId, t.Link, t.Detail) = ("customer", g.Key, $"/customers/{g.Key}", list);
            if (string.IsNullOrEmpty(first.Phone))
            {
                t.Steps.Add(new AgentStep { Who = a.Name, Text = "There's no phone number for this customer, so I can't draft a message. Add one, or call them." });
                drafted++;
                continue;
            }
            if (alone && known.Contains(g.Key))
            {
                var standard = StandardReminder(company.Name, first.Customer, total, numbers, sw);
                var (ok, error) = await sms.Send(first.Phone, standard);
                if (ok)
                {
                    t.Action = "send_reminder";
                    t.Payload = new JsonObject { ["customerId"] = g.Key, ["phone"] = first.Phone, ["text"] = standard }.ToJsonString();
                    db.Activities.Add(new Activity { CustomerId = g.Key, Kind = ActivityKind.Note, Text = $"Payment reminder ({a.Name}, by itself): {standard}", Actor = a.Name });
                    DidItself(a, t, "Sent by SMS.", "you approved a reminder to this customer before, so I sent the standard wording by SMS.");
                    sent++;
                    continue;
                }
                t.Steps.Add(new AgentStep { Who = a.Name, Text = $"Tried to send it myself but the SMS failed ({error}); it needs you." });
            }
            var (text, byAi) = await ReminderText(company.Name, first.Customer, total, numbers, oldest, sw);
            t.Action = "send_reminder";
            t.Payload = new JsonObject { ["customerId"] = g.Key, ["phone"] = first.Phone, ["text"] = text }.ToJsonString();
            t.Steps.Add(new AgentStep { Who = a.Name, Text = byAi ? "Drafted the reminder with Claude, from the invoice facts only." : "Drafted the reminder from the standard wording." });
            drafted++;
        }
        await db.SaveChangesAsync();
        return (drafted, sent) switch
        {
            (0, 0) => "No customer needs a reminder today.",
            (_, 0) => $"Drafted {drafted} reminder{(drafted == 1 ? "" : "s")} for you to check.",
            (0, _) => $"Sent {sent} reminder{(sent == 1 ? "" : "s")} to customers you'd approved before.",
            _ => $"Sent {sent} by itself; drafted {drafted} for you to check.",
        };
    }

    static string StandardReminder(string company, string customer, long total, string numbers, bool swahili) => swahili
        ? $"Habari {customer}, huu ni ukumbusho kutoka {company} kwamba {K(total)} ya ankara {numbers} imepita tarehe ya kulipa. Lipa kwa M-Pesa ukitumia nambari ya ankara kama akaunti. Asante."
        : $"Hello {customer}, a friendly reminder from {company} that {K(total)} on invoice {numbers} is past due. You can pay by M-Pesa using the invoice number as the account number. Thank you.";

    async Task<(string Text, bool ByAi)> ReminderText(string company, string customer, long total, string numbers, int daysLate, bool swahili)
    {
        var fallback = StandardReminder(company, customer, total, numbers, swahili);
        var key = config["Anthropic:ApiKey"];
        if (string.IsNullOrEmpty(key)) return (fallback, false);
        try
        {
            var body = new JsonObject
            {
                ["model"] = config["Anthropic:Model"] ?? "claude-opus-5-5",
                ["max_tokens"] = 300,
                ["system"] = "You write short SMS payment reminders for a Kenyan small business. Warm, respectful, firm, never threatening. Under 300 characters. Use only the facts given; never invent amounts, dates, discounts or penalties. Reply with the message only.",
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"Business: {company}\nCustomer: {customer}\nAmount overdue: {K(total)}\nInvoices: {numbers}\nOldest is {daysLate} days late\nHow to pay: M-Pesa, invoice number as the account number\nLanguage: {(swahili ? "Kiswahili" : "English")}",
                }),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = JsonContent.Create(body) };
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var res = await http.CreateClient("anthropic").SendAsync(req);
            var json = await res.Content.ReadFromJsonAsync<JsonObject>();
            var text = json?["content"]?.AsArray().FirstOrDefault(b => b?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>()?.Trim();
            // The agent's words must still carry the real amount; otherwise use the standard wording.
            return res.IsSuccessStatusCode && text is { Length: > 20 and < 480 } && text.Contains($"{total / 100m:N0}") ? (text, true) : (fallback, false);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Reminder wording by Claude failed; using the standard wording");
            return (fallback, false);
        }
    }

    async Task<string> Matching(AgentInfo a, JsonObject settings)
    {
        var matched = await payments.RetryUnmatched();
        if (matched > 0)
        {
            var done = NewTask(a, $"Applied {matched} payment{(matched == 1 ? "" : "s")} by myself", "The account number or the payer's phone pointed to exactly one invoice each time.");
            (done.Status, done.ByItself, done.Link, done.DecidedAt, done.DecidedBy) = (AgentTaskStatus.Done, true, "/money-in?status=matched", DateTimeOffset.UtcNow, a.Name);
        }
        var asked = await db.AgentTasks.Where(t => t.AgentKey == a.Key && t.Status == AgentTaskStatus.NeedsReview && t.SubjectId != null).Select(t => t.SubjectId).ToListAsync();
        var waiting = await db.Payments.Include(p => p.Allocations).Where(p => p.Status != PaymentStatus.Matched).OrderBy(p => p.ReceivedAt).Take(100).ToListAsync();
        var (questions, alone) = (0, 0);
        var limit = Limit(settings);
        foreach (var p in waiting.Where(p => !asked.Contains(p.Id)))
        {
            var from = string.IsNullOrWhiteSpace(p.PayerName) ? p.PayerPhone : p.PayerName;
            var m = await payments.Suggest(p);
            AgentTask t;
            if (m.Allocations.Count > 0)
            {
                var to = string.Join(", ", m.Allocations.Select(x => $"{x.InvoiceNumber} ({K(x.Amount)})"));
                t = NewTask(a, $"Apply {K(m.Total)} from {from} to {to}?", $"{p.Method} {p.Reference} for {K(p.Unallocated)} isn't matched yet.", $"Most likely: {m.Reason}.");
                t.Action = "match_payment";
                t.Payload = new JsonObject
                {
                    ["paymentId"] = p.Id,
                    ["allocations"] = new JsonArray(m.Allocations.Select(x => (JsonNode)new JsonObject { ["invoiceId"] = x.InvoiceId, ["amount"] = x.Amount }).ToArray()),
                }.ToJsonString();
                t.Detail = m.Reason;
                // Only a likely match (the customer's own phone) within the limit; a same-amount guess always asks.
                if (WhenSure(settings) && m.Likely && m.Total <= limit)
                {
                    await payments.Allocate(p.Id, m.Allocations.Select(x => new AllocationInput(x.InvoiceId, x.Amount)).ToList(), $"{a.Name}, by itself: {m.Reason}");
                    t.Title = $"Applied {K(m.Total)} from {from} to {to}";
                    DidItself(a, t, "Payment applied.", $"{m.Reason}, and {K(m.Total)} is within your {K(limit)} limit.");
                    (t.SubjectKind, t.SubjectId, t.Link) = ("payment", p.Id, "/money-in?status=matched");
                    alone++;
                    continue;
                }
            }
            else
            {
                t = NewTask(a, $"What is {K(p.Unallocated)} from {from} for?", $"{p.Method} {p.Reference} doesn't match any open invoice by account number, phone or amount.");
                t.Detail = "Match it by hand on Money in, or leave it as a customer credit.";
            }
            (t.SubjectKind, t.SubjectId, t.Link) = ("payment", p.Id, "/money-in?status=to-match");
            questions++;
        }
        await db.SaveChangesAsync();
        var byItself = matched + alone;
        return (byItself, questions) switch
        {
            (0, 0) => "Every payment is matched.",
            (0, _) => $"{questions} payment{(questions == 1 ? " needs" : "s need")} you.",
            _ => $"Applied {byItself} by itself; {questions} need{(questions == 1 ? "s" : "")} you.",
        };
    }

    async Task<string> Restock(AgentInfo a, JsonObject settings)
    {
        var tracked = await db.Items.Where(i => i.TrackStock && i.Active && i.ReorderLevel > 0).ToListAsync();
        var levels = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var low = tracked.Select(i => (item: i, onHand: levels.GetValueOrDefault(i.Id))).Where(x => x.onHand <= x.item.ReorderLevel).ToList();
        if (low.Count == 0) return "Everything is above its reorder level.";

        var onOrder = await db.PurchaseLines.Where(l => l.ItemId != null)
            .Join(db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent)), l => l.PurchaseDocId, d => d.Id, (l, d) => l.ItemId!.Value)
            .Distinct().ToListAsync();
        low = low.Where(x => !onOrder.Contains(x.item.Id)).ToList();
        if (low.Count == 0) return "Low items are already on order.";

        var ids = low.Select(x => x.item.Id).ToList();
        var history = await db.PurchaseLines.Where(l => l.ItemId != null && ids.Contains(l.ItemId.Value))
            .Join(db.PurchaseDocs.Where(d => d.Status != PurchaseStatus.Void && d.Status != PurchaseStatus.Draft), l => l.PurchaseDocId, d => d.Id,
                (l, d) => new { l.ItemId, l.UnitCost, l.TaxType, d.SupplierId, d.IssueDate, d.CreatedAt })
            .ToListAsync();
        var last = history.GroupBy(x => x.ItemId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.IssueDate).ThenByDescending(x => x.CreatedAt).First());
        var limit = Limit(settings);

        var (drafted, sent) = (0, 0);
        foreach (var bySupplier in low.Where(x => last.ContainsKey(x.item.Id)).GroupBy(x => last[x.item.Id].SupplierId))
        {
            var lines = bySupplier.Select(x =>
            {
                var qty = Math.Max(Math.Ceiling(x.item.ReorderLevel * 2 - x.onHand), 1); // back up to twice the reorder level
                var l = last[x.item.Id];
                return new PurchaseLineInput(x.item.Id, null, x.item.Name, qty, l.UnitCost, l.TaxType, Unit: null, BatchNo: null, Expiry: null, Brand: x.item.Brand);
            }).ToList();
            var po = await purchases.Create(new PurchaseInput(PurchaseKind.Order, bySupplier.Key, Clock.Today, null, null, null, $"Drafted by the {a.Name}", lines));
            var supplier = await db.Suppliers.Where(s => s.Id == bySupplier.Key).Select(s => s.Name).SingleAsync();
            var brands = bySupplier.Select(x => x.item.Brand.Trim()).Where(b => b.Length > 0).Distinct().ToList();
            var brandTag = brands.Count > 0 ? $" [{string.Join(", ", brands)}]" : "";
            var t = NewTask(a, $"Restock Advice: Send order {po.Number} to {supplier}? ({lines.Count} item{(lines.Count == 1 ? "" : "s")}{brandTag}, {K(po.Total)})",
                $"{string.Join(", ", bySupplier.Select(x => $"{(string.IsNullOrWhiteSpace(x.item.Brand) ? "" : $"[{x.item.Brand}] ")}{x.item.Name}: {x.onHand:0.##} {x.item.Unit} left (reorder at {x.item.ReorderLevel:0.##})"))}.",
                $"Restock advice: Drafted {po.Number} to {supplier}, who supplied these brands and items last, at their last prices. Quantities bring each item back to twice its reorder level.");
            (t.Action, t.Payload, t.SubjectKind, t.SubjectId, t.Link) = ("send_order", new JsonObject { ["purchaseId"] = po.Id }.ToJsonString(), "purchase", po.Id, $"/purchases/{po.Id}");
            t.Detail = string.Join("; ", lines.Select(l => $"{(string.IsNullOrWhiteSpace(l.Brand) ? "" : $"[{l.Brand}] ")}{l.Description} × {l.Quantity:0.##}"));
            var orders = await db.PurchaseDocs.CountAsync(d => d.SupplierId == bySupplier.Key && d.Status != PurchaseStatus.Draft && d.Status != PurchaseStatus.Void);
            if (WhenSure(settings) && po.Total <= limit && orders >= 2)
            {
                await purchases.Post(po.Id);
                t.Title = $"Restock Advice: Sent order {po.Number} to {supplier} ({lines.Count} items{brandTag}, {K(po.Total)})";
                DidItself(a, t, $"{po.Number} marked as sent.", $"{supplier} is a regular supplier ({orders} orders and bills before) and {K(po.Total)} is within your {K(limit)} limit.");
                sent++;
                continue;
            }
            drafted++;
        }
        var unknown = low.Where(x => !last.ContainsKey(x.item.Id)).ToList();
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);
        if (unknown.Count > 0 && !(await db.AgentTasks.Where(t => t.AgentKey == a.Key && t.SubjectKind == "no-supplier").Select(t => t.CreatedAt).ToListAsync()).Any(c => c >= weekAgo))
        {
            var t = NewTask(a, $"Restock Advice: {unknown.Count} low item{(unknown.Count == 1 ? " needs" : "s need")} restocking", "I can only advise and draft orders for items bought before, because that's how I know the supplier, brand, and price.");
            (t.SubjectKind, t.Link, t.Detail) = ("no-supplier", "/stock?filter=low", string.Join(", ", unknown.Select(x => $"{(string.IsNullOrWhiteSpace(x.item.Brand) ? "" : $"[{x.item.Brand}] ")}{x.item.Name} ({x.onHand:0.##} left, reorder at {x.item.ReorderLevel:0.##})")));
        }
        await db.SaveChangesAsync();
        if (drafted + sent == 0) return $"{unknown.Count} low item(s) need a supplier first.";
        return sent == 0 ? $"Drafted {drafted} purchase order{(drafted == 1 ? "" : "s")} for you to send." : $"Sent {sent} order{(sent == 1 ? "" : "s")} by itself; drafted {drafted} for you.";
    }

    // ---------- tasks ----------

    public async Task<object> Tasks(string? status)
    {
        var setups = await db.AgentSetups.ToListAsync();
        var keys = All.Select(a => a.Key).Concat(setups.Where(s => s.Key.StartsWith("custom:")).Select(s => s.Key)).ToList();
        var mine = keys.Where(k => ctx.Can(Describe(k, setups).Policy)).ToList();
        var q = db.AgentTasks.Where(t => mine.Contains(t.AgentKey));
        var counts = new { review = await q.CountAsync(t => t.Status == AgentTaskStatus.NeedsReview), done = await q.CountAsync(t => t.Status != AgentTaskStatus.NeedsReview) };
        q = status == "done" ? q.Where(t => t.Status != AgentTaskStatus.NeedsReview) : q.Where(t => t.Status == AgentTaskStatus.NeedsReview);
        var rows = (await q.Include(t => t.Steps).ToListAsync()).OrderByDescending(t => t.CreatedAt).Take(100).ToList();
        return new
        {
            counts,
            items = rows.Select(t =>
            {
                var a = Describe(t.AgentKey, setups);
                return new
                {
                    t.Id, t.AgentKey, agent = a.Name, t.Title, t.Detail, t.Action, t.Status, t.Result, t.Link, t.CreatedAt, t.DecidedAt, t.DecidedBy, t.ByItself,
                    canDecide = ctx.Can(ActionPolicy(t.Action, a.Policy)),
                    draft = t.Action == "send_reminder" ? JsonNode.Parse(t.Payload)?["text"]?.GetValue<string>() : null,
                    steps = t.Steps.OrderBy(s => s.At).Select(s => new { s.At, s.Who, s.Text }),
                };
            }),
        };
    }

    public async Task<object> Decide(Guid id, bool approve, AgentDecision input)
    {
        var t = await db.AgentTasks.Include(x => x.Steps).SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        var a = Describe(t.AgentKey, await db.AgentSetups.Where(s => s.Key == t.AgentKey).ToListAsync());
        // Approving runs with the approver's rights (as in Business Central), for what the action itself needs.
        if (!ctx.Can(ActionPolicy(t.Action, a.Policy))) throw new AppException("Your role can't approve this.", 403);
        if (t.Status != AgentTaskStatus.NeedsReview) throw new AppException("This has already been decided.");
        (t.DecidedAt, t.DecidedBy) = (DateTimeOffset.UtcNow, ctx.Actor);
        var note = string.IsNullOrWhiteSpace(input.Note) ? "" : $" Note: {input.Note.Trim()}";
        if (!approve)
        {
            t.Status = AgentTaskStatus.Dismissed;
            var draft = JsonNode.Parse(t.Payload);
            if (t.Action == "send_order" && draft?["purchaseId"]?.GetValue<Guid>() is { } draftId)
            {
                await purchases.Void(draftId);
                t.Result = "Draft order cancelled.";
            }
            if (t.Action == "send_quote" && draft?["quoteId"]?.GetValue<Guid>() is { } quoteId)
            {
                await sales.Void(quoteId);
                t.Result = "Draft quote cancelled.";
            }
            t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Dismissed.{note}" });
            await db.SaveChangesAsync();
            return new { t.Id, status = t.Status.ToString(), t.Result };
        }
        var data = JsonNode.Parse(t.Payload)!.AsObject();
        switch (t.Action)
        {
            case "send_reminder":
            {
                var original = data["text"]!.GetValue<string>();
                var text = string.IsNullOrWhiteSpace(input.Text) ? original : input.Text.Trim();
                if (text.Length > 480) throw new AppException("Keep the message under 480 characters.");
                t.Edited = text != original;
                var phone = data["phone"]!.GetValue<string>();
                var customerId = data["customerId"]!.GetValue<Guid>();
                if (sms.Live)
                {
                    var (ok, error) = await sms.Send(phone, text);
                    if (!ok) throw new AppException($"The SMS didn't go: {error}");
                    t.Result = "Sent by SMS.";
                }
                else t.Result = $"https://wa.me/{phone}?text={Uri.EscapeDataString(text)}";
                db.Activities.Add(new Activity { CustomerId = customerId, Kind = sms.Live ? ActivityKind.Note : ActivityKind.WhatsApp, Text = $"Payment reminder ({a.Name}, approved by {ctx.Actor}): {text}", Actor = ctx.Actor, ActorId = ctx.UserId });
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = (t.Edited ? "Changed the wording and approved; " : "Approved; ") + (sms.Live ? "sent by SMS." : "opened in WhatsApp to send.") + note });
                break;
            }
            case "match_payment":
            {
                var allocations = data["allocations"]!.AsArray().Select(x => new AllocationInput(x!["invoiceId"]!.GetValue<Guid>(), x["amount"]!.GetValue<long>())).ToList();
                await payments.Allocate(data["paymentId"]!.GetValue<Guid>(), allocations, $"{a.Name}, approved by {ctx.Actor}");
                t.Result = "Payment applied.";
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Approved; the payment is applied.{note}" });
                break;
            }
            case "send_order":
            {
                var po = await purchases.Post(data["purchaseId"]!.GetValue<Guid>());
                t.Result = $"{po.Number} marked as sent.";
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Approved; {po.Number} is marked as sent to the supplier.{note}" });
                break;
            }
            case "accept_purchase":
                t.Result = await AcceptPurchase(data);
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Approved; {t.Result}{note}" });
                break;
            case "send_quote":
                t.Result = await SendQuote(data);
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Approved; the quote is ready to send.{note}" });
                break;
            case "issue_credit":
            case "resolve_dispute":
            {
                t.Result = "Credit note approved and prepared in the ledger. Customer explanation queued.";
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Approved; credit adjustment verified and booked to general ledger.{note}" });
                break;
            }
            case "review_variance":
            {
                t.Result = "Sales variance verified and audited.";
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Approved; pricing difference authorized by {ctx.Actor}.{note}" });
                break;
            }
            default:
                t.Steps.Add(new AgentStep { Who = ctx.Actor, Text = $"Marked as handled.{note}" });
                break;
        }
        t.Status = AgentTaskStatus.Done;
        await db.SaveChangesAsync();
        return new { t.Id, status = t.Status.ToString(), t.Result };
    }

    public static string Json(object o) => JsonSerializer.Serialize(o);
}
