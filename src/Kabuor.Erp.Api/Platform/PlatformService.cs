// The platform console for Kabuor Builds: is the system healthy, and how is the business of Kabuor itself doing?
// It reads across every company: counts and totals per business, plus the inquiries desk and a business's latest
// invoices for support. Only the emails in Platform:Admins get in (with two-step sign-in on when Platform:Require2FA).
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Kabuor.Erp.Billing;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Kabuor.Erp.Platform;

public sealed class PlatformService(ErpDb db, RequestContext ctx, IConfiguration config, IWebHostEnvironment env, IDistributedCache cache, RequestMetrics metrics, Heartbeat heartbeat)
{
    static readonly SalesDocStatus[] Issued = [SalesDocStatus.Sent, SalesDocStatus.PartPaid, SalesDocStatus.Paid];

    /// <summary>Is this email listed in Platform:Admins? The one list, for the console and for the sign-in.</summary>
    public static bool IsAdmin(IConfiguration config, string email) =>
        (config["Platform:Admins"] ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Any(e => e.Trim().Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Platform admins: listed by email in Platform:Admins, and (outside Development) using two-step sign-in.</summary>
    public async Task<(bool Allowed, string Why)> Access()
    {
        if (ctx.UserId is not { } id) return (false, "Sign in first.");
        var u = await db.Users.IgnoreQueryFilters().Where(x => x.Id == id).Select(x => new { x.Email, x.TwoFactorEnabled, x.Active }).SingleOrDefaultAsync();
        if (u is null || !u.Active || !IsAdmin(config, u.Email)) return (false, "This login isn't a platform admin.");
        if (!u.TwoFactorEnabled && !env.IsDevelopment() && config.GetValue("Platform:Require2FA", false)) return (false, "Turn on two-step sign-in (Security) to use the platform console.");
        return (true, "");
    }

    /// <summary>Demo businesses (ids b0000000-0000-0000-0000-…) are kept out of the platform's figures.</summary>
    public static bool IsDemo(Guid id) => id.ToString().StartsWith("b0000000-0000-0000-0000-", StringComparison.Ordinal);

    // Paying while paid up; then the trial; then a grace week; then read-only. AccessEnds is when the current state
    // ends (for a locked business, when it locked).
    static string StateOf(DateTimeOffset? paidUntil, DateTimeOffset? trialEnds, DateTimeOffset created, DateTimeOffset now)
    {
        var trial = trialEnds ?? created.AddDays(Plans.TrialDays);
        return paidUntil > now ? "paying" : now <= trial ? "trial" : now <= trial.AddDays(Plans.GraceDays) ? "grace" : "locked";
    }

    static DateTimeOffset AccessEnds(DateTimeOffset? paidUntil, DateTimeOffset? trialEnds, DateTimeOffset created, DateTimeOffset now)
    {
        var trial = trialEnds ?? created.AddDays(Plans.TrialDays);
        return StateOf(paidUntil, trialEnds, created, now) switch
        {
            "paying" => paidUntil!.Value,
            "trial" => trial,
            _ => paidUntil > trial.AddDays(Plans.GraceDays) ? paidUntil.Value : trial.AddDays(Plans.GraceDays),
        };
    }

    /// <summary>Works on another business as that business, so the tenant guard allows it and its own audit log
    /// shows the change, signed by the admin as Kabuor support.</summary>
    async Task<T> AsBusiness<T>(Guid companyId, Func<Task<T>> work)
    {
        var (company, actor) = (ctx.CompanyId, ctx.Actor);
        (ctx.CompanyId, ctx.Actor) = (companyId, $"{actor} (Kabuor support)");
        try { return await work(); }
        finally { (ctx.CompanyId, ctx.Actor) = (company, actor); }
    }

    // ------------------------------------------------------------------ health
    sealed record Check(string Key, string Name, string Status, string Summary, object? Detail = null);

    /// <summary>The checks always judge real requests; with demo = true the traffic charts add the simulated demo load.</summary>
    public async Task<object> Health(bool demo = false)
    {
        var checks = new List<Check>();

        // Database
        try
        {
            var clock = Stopwatch.StartNew();
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            var ms = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
            // PostgreSQL (live) is kept by migrations; SQLite (local, tests) is built from the model directly.
            var pending = db.Database.IsNpgsql() ? (await db.Database.GetPendingMigrationsAsync()).ToList() : [];
            var provider = db.Database.IsNpgsql() ? "PostgreSQL" : "SQLite";
            long? size = null;
            try
            {
                if (db.Database.IsNpgsql()) size = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync();
                else if (db.Database.GetDbConnection().DataSource is { Length: > 0 } file && File.Exists(file)) size = new FileInfo(file).Length;
            }
            catch { /* size is a nice-to-have */ }
            var status = pending.Count > 0 ? "bad" : ms > 250 ? "warn" : "good";
            checks.Add(new("database", "Database", status,
                pending.Count > 0 ? $"{pending.Count} migration(s) not applied" : $"{provider} answering in {ms} ms",
                new { provider, latencyMs = ms, pendingMigrations = pending, sizeMb = size is { } s ? Math.Round(s / 1048576.0, 1) : (double?)null }));
        }
        catch (Exception e)
        {
            checks.Add(new("database", "Database", "bad", "Not answering: " + e.Message));
        }

        // Cache
        try
        {
            var clock = Stopwatch.StartNew();
            var key = $"platform:health:{Guid.NewGuid():N}";
            await cache.SetStringAsync(key, "ok", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });
            var back = await cache.GetStringAsync(key);
            await cache.RemoveAsync(key);
            var kind = cache.GetType().Name.Contains("Redis", StringComparison.OrdinalIgnoreCase) ? "Redis / Valkey" : "Memory (one server only)";
            var ms = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
            checks.Add(new("cache", "Cache", back == "ok" ? (ms > 100 ? "warn" : "good") : "bad", back == "ok" ? $"{kind}, round trip {ms} ms" : $"{kind} didn't return what was stored", new { kind, latencyMs = ms }));
        }
        catch (Exception e)
        {
            checks.Add(new("cache", "Cache", "warn", "Cache unavailable, the app falls back to the database: " + e.Message));
        }

        // Agent scheduler
        var tickMinutes = config.GetValue("Agents:TickMinutes", 1);
        if (config.GetValue("Agents:Disabled", false)) checks.Add(new("agents", "Agent scheduler", "warn", "Switched off by configuration"));
        else if (heartbeat.AgentsTick is not { } tick) checks.Add(new("agents", "Agent scheduler", (DateTimeOffset.UtcNow - metrics.StartedAt).TotalMinutes < 2 ? "good" : "bad", "Hasn't run since the server started"));
        else
        {
            var age = DateTimeOffset.UtcNow - tick;
            var failedRecently = heartbeat.AgentsError is { } err && DateTimeOffset.UtcNow - err < TimeSpan.FromMinutes(10 * tickMinutes);
            checks.Add(new("agents", "Agent scheduler", age.TotalMinutes > 3 * tickMinutes ? "bad" : failedRecently ? "warn" : "good",
                failedRecently ? $"Last run failed: {heartbeat.AgentsErrorMessage}" : $"Last run {Math.Round(age.TotalSeconds)} s ago",
                new { lastTick = tick, lastError = heartbeat.AgentsError, heartbeat.AgentsErrorMessage }));
        }

        var day = DateTimeOffset.UtcNow.AddDays(-1);
        // M-Pesa prompts in the last day
        var stk = await db.MpesaRequests.IgnoreQueryFilters().Where(r => r.CreatedAt >= day).GroupBy(r => r.Status).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
        var stkFailed = stk.Where(s => s.Key == MpesaRequestStatus.Failed).Sum(s => s.n);
        var stkAll = stk.Sum(s => s.n);
        var lastMpesa = await db.Payments.IgnoreQueryFilters().Where(p => p.Method == PaymentMethod.Mpesa).OrderByDescending(p => p.ReceivedAt).Select(p => (DateTimeOffset?)p.ReceivedAt).FirstOrDefaultAsync();
        checks.Add(new("mpesa", "M-Pesa", stkAll >= 5 && stkFailed * 2 > stkAll ? "warn" : "good",
            $"{stkAll} prompt(s) in 24 h, {stkFailed} failed" + (lastMpesa is { } lm ? $"; last payment {lm:dd MMM HH:mm}" : ""),
            new { prompts24h = stkAll, failed24h = stkFailed, lastPayment = lastMpesa }));

        // eTIMS
        var week = Clock.Today.AddDays(-7);
        var etims = await db.SalesDocs.IgnoreQueryFilters().Where(d => d.Kind == SalesDocKind.Invoice && d.IssueDate >= week && d.EtimsStatus != EtimsStatus.NotRequired)
            .GroupBy(d => d.EtimsStatus).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
        var etimsFailed = etims.Where(e => e.Key == EtimsStatus.Failed).Sum(e => e.n);
        var etimsPending = etims.Where(e => e.Key == EtimsStatus.Pending).Sum(e => e.n);
        checks.Add(new("etims", "KRA eTIMS", etimsFailed > 0 ? "warn" : "good",
            $"{etims.Where(e => e.Key == EtimsStatus.Signed).Sum(e => e.n)} signed in 7 days, {etimsPending} waiting, {etimsFailed} failed",
            new { signed7d = etims.Where(e => e.Key == EtimsStatus.Signed).Sum(e => e.n), pending = etimsPending, failed = etimsFailed }));

        // Webhooks
        var hooks = await db.WebhookDeliveries.IgnoreQueryFilters().Where(d => d.CreatedAt >= day).GroupBy(d => d.Status).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
        var hooksFailed = hooks.Where(h => h.Key == Kabuor.Erp.Integrations.DeliveryStatus.Failed).Sum(h => h.n);
        checks.Add(new("webhooks", "Webhooks", hooksFailed > 0 ? "warn" : "good", $"{hooks.Sum(h => h.n)} sent in 24 h, {hooksFailed} gave up",
            new { sent24h = hooks.Sum(h => h.n), failed24h = hooksFailed }));

        // Subscription payments stuck waiting
        var stuckSince = DateTimeOffset.UtcNow.AddMinutes(-15);
        var stuck = await db.BillingPayments.IgnoreQueryFilters().CountAsync(p => p.Status == BillingStatus.Pending && p.CreatedAt < stuckSince && p.CreatedAt > day);
        checks.Add(new("billing", "Subscription payments", stuck > 0 ? "warn" : "good", stuck > 0 ? $"{stuck} payment(s) waiting over 15 minutes" : "None stuck"));

        // Requests
        dynamic real = metrics.Snapshot();
        double errorRate = real.errorRate;
        checks.Add(new("requests", "Requests (last hour)", errorRate >= 5 ? "bad" : errorRate >= 1 || (double)real.p95 > 1000 ? "warn" : "good",
            $"{real.requests} requests, {errorRate}% server errors, 95% under {real.p95} ms"));
        var traffic = demo ? metrics.Snapshot(includeDemo: true) : (object)real;

        var process = Process.GetCurrentProcess();
        var configured = new
        {
            sentry = !string.IsNullOrWhiteSpace(config["Sentry:Dsn"]), ai = !string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]),
            sms = !string.IsNullOrWhiteSpace(config["AfricasTalking:ApiKey"]), billing = !string.IsNullOrWhiteSpace(config["Billing:Mpesa:ConsumerKey"]),
            redis = !string.IsNullOrWhiteSpace(config["REDIS_URL"] ?? config["VALKEY_URL"] ?? config.GetConnectionString("Redis")),
            legalEmail = !string.IsNullOrWhiteSpace(config["Legal:Email"]),
        };
        var order = new[] { "good", "warn", "bad" };
        return new
        {
            status = checks.Select(c => Array.IndexOf(order, c.Status)).DefaultIfEmpty(0).Max() switch { 2 => "bad", 1 => "warn", _ => "good" },
            checkedAt = DateTimeOffset.UtcNow,
            server = new
            {
                environment = env.EnvironmentName, version = (config["RENDER_GIT_COMMIT"] ?? config["RAILWAY_GIT_COMMIT_SHA"] ?? "local") is var v && v.Length > 7 ? v[..7] : config["RENDER_GIT_COMMIT"] ?? "local",
                startedAt = metrics.StartedAt, uptimeHours = Math.Round((DateTimeOffset.UtcNow - metrics.StartedAt).TotalHours, 1),
                memoryMb = Math.Round(process.WorkingSet64 / 1048576.0), heapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0), threads = process.Threads.Count,
                dotnet = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            },
            configured, checks, traffic, demoTraffic = demo,
        };
    }

    // ------------------------------------------------------------------ analytics
    /// <summary>Growth across businesses. With demo = false the seeded demo businesses are left out of every figure,
    /// so the numbers are Kabuor's real ones.</summary>
    public async Task<object> Analytics(int days, bool demo = true)
    {
        days = Math.Clamp(days, 7, 365);
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-days);
        var prevFrom = from.AddDays(-days);
        var fromDate = Clock.Today.AddDays(1 - days);
        var plans = Plans.All(config).ToDictionary(p => p.Id, p => p);

        var companies = (await db.Companies.Select(c => new { c.Id, c.Name, c.Plan, c.TrialEndsAt, c.PaidUntil, c.CreatedAt, c.County }).ToListAsync())
            .Where(c => demo || !IsDemo(c.Id)).ToList();
        var counted = companies.Select(c => c.Id).ToHashSet();
        string State(DateTimeOffset? paidUntil, DateTimeOffset? trialEnds, DateTimeOffset created) => StateOf(paidUntil, trialEnds, created, now);
        var states = companies.Select(c => new { c.Id, state = State(c.PaidUntil, c.TrialEndsAt, c.CreatedAt), c.Plan }).ToList();
        var paying = states.Where(s => s.state == "paying").ToList();
        var mrr = paying.Sum(s => plans.TryGetValue(s.Plan, out var p) ? p.Monthly : 0);

        var users = (await db.Users.IgnoreQueryFilters().Where(u => u.Active).Select(u => new { u.CompanyId, u.LastSignInAt, u.Role }).ToListAsync()).Where(u => counted.Contains(u.CompanyId)).ToList();
        var docs = (await db.SalesDocs.IgnoreQueryFilters().Where(d => d.Kind == SalesDocKind.Invoice && !d.Opening && Issued.Contains(d.Status) && d.IssueDate >= DateOnly.FromDateTime(prevFrom.UtcDateTime))
            .Select(d => new { d.CompanyId, d.IssueDate, d.Total }).ToListAsync()).Where(d => counted.Contains(d.CompanyId)).ToList();
        var payments = (await db.Payments.IgnoreQueryFilters().Select(p => new { p.CompanyId, p.ReceivedAt, p.Amount }).ToListAsync()).Where(p => p.ReceivedAt >= prevFrom && counted.Contains(p.CompanyId)).ToList();
        var revenue = (await db.BillingPayments.IgnoreQueryFilters().Where(p => p.Status == BillingStatus.Paid && !p.Test).Select(p => new { p.CompanyId, p.Amount, p.PaidAt }).ToListAsync())
            .Where(p => p.PaidAt >= from && counted.Contains(p.CompanyId)).Sum(p => p.Amount);
        var inquiries = (await db.Inquiries.IgnoreQueryFilters().Select(i => new { i.CompanyId, i.CreatedAt }).ToListAsync()).Count(i => i.CreatedAt >= from && counted.Contains(i.CompanyId));
        var assets = (await db.Assets.IgnoreQueryFilters().Select(a => a.CompanyId).ToListAsync()).Count(counted.Contains);
        var agentTasks = (await db.AgentTasks.IgnoreQueryFilters().Select(t => new { t.CompanyId, t.CreatedAt }).ToListAsync()).Count(t => t.CreatedAt >= from && counted.Contains(t.CompanyId));

        var periodDocs = docs.Where(d => d.IssueDate >= fromDate).ToList();
        var prevDocs = docs.Where(d => d.IssueDate < fromDate).ToList();
        var periodPayments = payments.Where(p => p.ReceivedAt >= from).ToList();
        var prevPayments = payments.Where(p => p.ReceivedAt < from).ToList();
        var activeSince7 = now.AddDays(-7);
        var active7 = docs.Where(d => d.IssueDate >= Clock.Today.AddDays(-6)).Select(d => d.CompanyId)
            .Concat(payments.Where(p => p.ReceivedAt >= activeSince7).Select(p => p.CompanyId))
            .Concat(users.Where(u => u.LastSignInAt >= activeSince7).Select(u => u.CompanyId)).ToHashSet();
        var active30 = periodDocs.Select(d => d.CompanyId).Concat(periodPayments.Select(p => p.CompanyId)).Concat(users.Where(u => u.LastSignInAt >= from).Select(u => u.CompanyId)).ToHashSet();
        static decimal? Change(decimal now, decimal before) => before == 0 ? null : Math.Round(100m * (now - before) / before, 1);

        var dates = Enumerable.Range(0, days).Select(i => fromDate.AddDays(i)).ToList();
        var signupsByDay = companies.GroupBy(c => DateOnly.FromDateTime(c.CreatedAt.ToOffset(TimeSpan.FromHours(3)).DateTime)).ToDictionary(g => g.Key, g => g.Count());
        var invoicesByDay = periodDocs.GroupBy(d => d.IssueDate).ToDictionary(g => g.Key, g => g.Sum(d => d.Total));
        var paymentsByDay = periodPayments.GroupBy(p => DateOnly.FromDateTime(p.ReceivedAt.ToOffset(TimeSpan.FromHours(3)).DateTime)).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var lastActivity = docs.GroupBy(d => d.CompanyId).ToDictionary(g => g.Key, g => g.Max(d => d.IssueDate));
        var signIns = users.GroupBy(u => u.CompanyId).ToDictionary(g => g.Key, g => g.Max(u => u.LastSignInAt));

        return new
        {
            days, from, to = now,
            businesses = new
            {
                total = companies.Count, newInPeriod = companies.Count(c => c.CreatedAt >= from), newBefore = companies.Count(c => c.CreatedAt >= prevFrom && c.CreatedAt < from),
                active7d = active7.Count, activeInPeriod = active30.Count,
                trial = states.Count(s => s.state == "trial"), paying = paying.Count, grace = states.Count(s => s.state == "grace"), locked = states.Count(s => s.state == "locked"),
                byPlan = plans.Values.Select(p => new { p.Id, p.Name, businesses = paying.Count(s => s.Plan == p.Id), monthly = p.Monthly, mrr = paying.Count(s => s.Plan == p.Id) * p.Monthly }),
            },
            revenue = new { mrr, arr = mrr * 12, collectedInPeriod = revenue },
            users = new { total = users.Count, signedIn7d = users.Count(u => u.LastSignInAt >= activeSince7), signedInPeriod = users.Count(u => u.LastSignInAt >= from) },
            volume = new
            {
                invoices = periodDocs.Count, invoiceValue = periodDocs.Sum(d => d.Total), invoiceValueChange = Change(periodDocs.Sum(d => d.Total), prevDocs.Sum(d => d.Total)),
                payments = periodPayments.Count, paymentValue = periodPayments.Sum(p => p.Amount), paymentValueChange = Change(periodPayments.Sum(p => p.Amount), prevPayments.Sum(p => p.Amount)),
                inquiries, agentTasks, assets,
            },
            series = dates.Select(d => new { day = d, signups = signupsByDay.GetValueOrDefault(d), invoiceValue = invoicesByDay.GetValueOrDefault(d), paymentValue = paymentsByDay.GetValueOrDefault(d) }),
            top = companies.Select(c => new
            {
                c.Id, c.Name, c.County, c.CreatedAt, state = State(c.PaidUntil, c.TrialEndsAt, c.CreatedAt), plan = c.Plan,
                users = users.Count(u => u.CompanyId == c.Id),
                invoices = periodDocs.Count(d => d.CompanyId == c.Id), invoiceValue = periodDocs.Where(d => d.CompanyId == c.Id).Sum(d => d.Total),
                lastInvoice = lastActivity.TryGetValue(c.Id, out var li) ? li : (DateOnly?)null, lastSignIn = signIns.GetValueOrDefault(c.Id),
            }).OrderByDescending(c => c.invoiceValue).ThenByDescending(c => c.lastSignIn).Take(50),
        };
    }

    public sealed record UpdatePlanInput(string? Plan, int? ExtendDays, bool? Lock);
    public sealed record WipeBusinessInput(string? Reason, string? Confirm);
    public sealed record ReplyIssueInput(string? Message);

    // ------------------------------------------------------------------ resources & scaling
    public async Task<object> Resources()
    {
        var proc = Process.GetCurrentProcess();
        var workingSetMb = Math.Round(proc.WorkingSet64 / 1048576.0, 1);
        var heapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1);
        // What the runtime may use: the container's memory limit when there is one, else the machine's memory.
        var limitMb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0);
        ThreadPool.GetAvailableThreads(out int availWorkers, out int availIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        var busyWorkers = maxWorkers - availWorkers;

        long? dbSizeBytes = null;
        int? dbConnections = null;
        try
        {
            if (db.Database.IsNpgsql())
            {
                dbSizeBytes = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync();
                dbConnections = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity").SingleAsync();
            }
        }
        catch { }

        dynamic traffic = metrics.Snapshot();
        double p95 = (double)traffic.p95;
        double errorRate = (double)traffic.errorRate;

        // Automated scaling recommendation
        string scalingStatus = "optimal";
        var reasons = new List<string>();

        if (limitMb > 0 && workingSetMb > 0.85 * limitMb)
        {
            scalingStatus = "scale_up_recommended";
            reasons.Add($"Memory is at {Math.Round(100 * workingSetMb / limitMb)}% of the {limitMb:0} MB limit. Give the server more memory.");
        }
        if (p95 > 800)
        {
            scalingStatus = "scale_up_recommended";
            reasons.Add($"95th percentile latency is elevated ({p95} ms). Recommend scaling to 2+ CPU cores.");
        }
        if (errorRate > 2.0)
        {
            scalingStatus = "attention_needed";
            reasons.Add($"Server error rate is {errorRate}%. Check application logs.");
        }
        if (reasons.Count == 0)
        {
            reasons.Add("Memory, response times and errors are all well within limits.");
        }

        return new
        {
            scaling = new
            {
                status = scalingStatus,
                summary = scalingStatus switch { "optimal" => "Enough capacity", "attention_needed" => "Needs attention", _ => "Scale up recommended" },
                recommendations = reasons,
            },
            memory = new
            {
                workingSetMb,
                heapMb,
                limitMb,
                gen0 = GC.CollectionCount(0),
                gen1 = GC.CollectionCount(1),
                gen2 = GC.CollectionCount(2),
            },
            cpu = new
            {
                threads = proc.Threads.Count,
                busyWorkerThreads = busyWorkers,
                maxWorkerThreads = maxWorkers,
            },
            database = new
            {
                sizeMb = dbSizeBytes.HasValue ? Math.Round(dbSizeBytes.Value / 1048576.0, 1) : (double?)null,
                connections = dbConnections,
            },
            uptime = new
            {
                startedAt = metrics.StartedAt,
                uptimeHours = Math.Round((DateTimeOffset.UtcNow - metrics.StartedAt).TotalHours, 1),
            }
        };
    }

    // ------------------------------------------------------------------ businesses & governance
    public object PlanList() => Plans.All(config).Select(p => new { p.Id, p.Name, p.Monthly, p.Users, p.Staff });

    public async Task<object> Businesses()
    {
        var now = DateTimeOffset.UtcNow;
        var companies = await db.Companies.OrderByDescending(c => c.CreatedAt).ToListAsync();
        var users = (await db.Users.IgnoreQueryFilters().Select(u => new { u.CompanyId, u.LastSignInAt }).ToListAsync()).ToLookup(u => u.CompanyId);
        var docs = (await db.SalesDocs.IgnoreQueryFilters().Where(d => d.Kind == SalesDocKind.Invoice && Issued.Contains(d.Status)).Select(d => new { d.CompanyId, d.Total, d.IssueDate }).ToListAsync()).ToLookup(d => d.CompanyId);

        return companies.Select(c => new
        {
            c.Id,
            c.Name,
            c.Email,
            c.Phone,
            c.County,
            c.Plan,
            c.CreatedAt,
            c.TrialEndsAt,
            c.PaidUntil,
            state = StateOf(c.PaidUntil, c.TrialEndsAt, c.CreatedAt, now),
            accessEndsAt = AccessEnds(c.PaidUntil, c.TrialEndsAt, c.CreatedAt, now),
            isDemo = IsDemo(c.Id),
            usersCount = users[c.Id].Count(),
            invoicesCount = docs[c.Id].Count(),
            invoiceValue = docs[c.Id].Sum(d => d.Total),
            lastInvoiceOn = docs[c.Id].Select(d => (DateOnly?)d.IssueDate).Max(),
            lastActive = users[c.Id].Max(u => u.LastSignInAt),
        }).ToList();
    }

    public async Task<object> BusinessDetail(Guid id)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id) ?? throw new AppException("Business not found.", 404);
        var users = await db.Users.IgnoreQueryFilters().Where(u => u.CompanyId == id).OrderByDescending(u => u.LastSignInAt).Select(u => new
        {
            u.Id,
            u.Name,
            u.Email,
            u.Phone,
            role = u.Role.ToString(),
            u.Active,
            u.CreatedAt,
            u.LastSignInAt
        }).ToListAsync();

        var docs = await db.SalesDocs.IgnoreQueryFilters()
            .Include(d => d.Customer)
            .Where(d => d.CompanyId == id && d.Kind == SalesDocKind.Invoice)
            .OrderByDescending(d => d.CreatedAt).Take(20).Select(d => new
            {
                d.Id,
                d.Number,
                customerName = d.Customer != null ? d.Customer.Name : "Walk-in Customer",
                total = d.Total,
                balance = d.Total - d.Paid,
                status = d.Status.ToString(),
                d.IssueDate,
                d.DueDate
            }).ToListAsync();

        var footprint = new
        {
            users = users.Count,
            customers = await db.Customers.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
            items = await db.Items.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
            invoices = await db.SalesDocs.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id && c.Kind == SalesDocKind.Invoice),
            payments = await db.Payments.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
            assets = await db.Assets.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
            inquiries = await db.Inquiries.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
            employees = await db.Employees.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
            journals = await db.JournalEntries.IgnoreQueryFilters().CountAsync(c => c.CompanyId == id),
        };

        var now = DateTimeOffset.UtcNow;
        return new
        {
            company.Id,
            company.Name,
            company.Email,
            company.Phone,
            company.County,
            company.KraPin,
            company.VatRegistered,
            company.Plan,
            company.Industry,
            company.MpesaShortcode,
            company.CreatedAt,
            company.TrialEndsAt,
            company.PaidUntil,
            state = StateOf(company.PaidUntil, company.TrialEndsAt, company.CreatedAt, now),
            accessEndsAt = AccessEnds(company.PaidUntil, company.TrialEndsAt, company.CreatedAt, now),
            isDemo = IsDemo(company.Id),
            users,
            invoices = docs,
            invoicedTotal = docs.Sum(d => d.total),
            footprint,
            canWipe = company.Name != "Kabuor Builds"
        };
    }

    public async Task<object> Issues()
    {
        var inquiries = await db.Inquiries.IgnoreQueryFilters()
            .Include(i => i.Messages)
            .OrderByDescending(i => i.CreatedAt)
            .Take(50)
            .ToListAsync();

        var companies = await db.Companies.ToDictionaryAsync(c => c.Id, c => c.Name);

        return inquiries.Select(i => new
        {
            i.Id,
            i.CompanyId,
            companyName = companies.GetValueOrDefault(i.CompanyId, "Unknown Business"),
            i.Number,
            i.Name,
            i.Phone,
            i.Email,
            i.Subject,
            channel = i.Channel.ToString(),
            status = i.Status.ToString(),
            i.CreatedAt,
            i.FirstResponseAt,
            messages = i.Messages.OrderBy(m => m.At).Select(m => new
            {
                m.Id,
                m.From,
                m.Author,
                m.Text,
                m.At
            }).ToList()
        }).ToList();
    }

    /// <summary>Answers in the business's own thread (the customer sees it in the shop portal), as the business's
    /// staff would, signed by the admin as Kabuor support.</summary>
    public async Task<object> ReplyIssue(Guid id, ReplyIssueInput input)
    {
        var msg = (input.Message ?? "").Trim();
        if (msg.Length is < 2 or > 4000) throw new AppException("Write the reply (up to 4,000 characters).");

        var inq = await db.Inquiries.IgnoreQueryFilters().Include(i => i.Messages).FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new AppException("Issue not found.", 404);

        return await AsBusiness(inq.CompanyId, async () =>
        {
            inq.Messages.Add(new InquiryMessage { InquiryId = inq.Id, CompanyId = inq.CompanyId, From = "staff", Author = ctx.Actor, Text = msg });
            inq.FirstResponseAt ??= DateTimeOffset.UtcNow;
            if (inq.Status == InquiryStatus.Open) inq.Status = InquiryStatus.Answered;
            await db.SaveChangesAsync();
            return new { inq.Id, status = inq.Status.ToString() };
        });
    }

    /// <summary>Change the plan; give access for some days (from today, or from the paid-up date if later); or lock
    /// the business now (read-only until access is given again).</summary>
    public async Task<object> UpdatePlan(Guid id, UpdatePlanInput input)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id) ?? throw new AppException("Business not found.", 404);
        var plans = Plans.All(config);
        if (input.Plan is { Length: > 0 } wanted)
            company.Plan = plans.FirstOrDefault(p => p.Id == wanted.Trim().ToLowerInvariant())?.Id
                ?? throw new AppException($"There's no '{wanted}' plan. Choose {string.Join(", ", plans.Select(p => p.Id))}.");
        if (input.ExtendDays is < 0 or > 366) throw new AppException("Give access for 1 to 366 days.");

        var now = DateTimeOffset.UtcNow;
        if (input.Lock == true) (company.PaidUntil, company.TrialEndsAt) = (now.AddDays(-1), now.AddDays(-Plans.GraceDays - 1));
        else if (input.ExtendDays is > 0 and var days) company.PaidUntil = (company.PaidUntil > now ? company.PaidUntil.Value : now).AddDays(days);
        await AsBusiness(id, () => db.SaveChangesAsync());

        return new
        {
            company.Id, company.Name, company.Plan, company.PaidUntil,
            state = StateOf(company.PaidUntil, company.TrialEndsAt, company.CreatedAt, now),
            accessEndsAt = AccessEnds(company.PaidUntil, company.TrialEndsAt, company.CreatedAt, now),
        };
    }

    static readonly MethodInfo RemoveOwnedMethod = typeof(PlatformService).GetMethod(nameof(RemoveOwned), BindingFlags.NonPublic | BindingFlags.Instance)!;

    async Task<int> RemoveOwned<T>(Guid companyId) where T : class, ICompanyOwned
    {
        var rows = await db.Set<T>().IgnoreQueryFilters().Where(x => x.CompanyId == companyId).ToListAsync();
        db.Set<T>().RemoveRange(rows);
        return rows.Count;
    }

    /// <summary>Erases a business: every row it owns in every table the model knows (so a table added later is
    /// covered too), its logins, and its audit log, which holds copies of what was deleted. What stays is one line
    /// in the platform's own audit log: who erased which business, when, and why.</summary>
    public async Task<object> WipeBusiness(Guid id, WipeBusinessInput input)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == id) ?? throw new AppException("Business not found.", 404);
        if (company.Name == "Kabuor Builds") throw new AppException("Cannot wipe the platform admin company.", 400);

        var confirm = (input.Confirm ?? "").Trim();
        if (!string.Equals(confirm, company.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(confirm, "WIPE", StringComparison.OrdinalIgnoreCase))
            throw new AppException($"Confirmation mismatch. Type '{company.Name}' or 'WIPE' to confirm permanent erasure.", 400);

        var reason = (input.Reason ?? "").Trim();
        if (reason.Length < 3) throw new AppException("A reason for data erasure must be provided for compliance audit logging.", 400);
        reason = reason[..Math.Min(reason.Length, 500)];

        var owned = db.Model.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned() && t.FindPrimaryKey() is not null && typeof(ICompanyOwned).IsAssignableFrom(t.ClrType) && t.ClrType != typeof(AuditEntry))
            .Select(t => t.ClrType).Distinct().ToList();
        var removed = new SortedDictionary<string, int>();
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            removed.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();
            await AsBusiness(id, async () =>
            {
                foreach (var type in owned)
                    if (await (Task<int>)RemoveOwnedMethod.MakeGenericMethod(type).Invoke(this, [id])! is > 0 and var n) removed[type.Name] = n;
                db.Companies.Remove(company);
                await db.SaveChangesAsync();
                return await db.AuditEntries.IgnoreQueryFilters().Where(a => a.CompanyId == id).ExecuteDeleteAsync();
            });
            db.AuditEntries.Add(new AuditEntry
            {
                CompanyId = ctx.CompanyId, UserId = ctx.UserId, Actor = ctx.Actor, Action = "Erased", Entity = nameof(Company), EntityId = id.ToString(),
                Changes = JsonSerializer.Serialize(new { name = company.Name, reason, removed = removed.Values.Sum(), tables = removed }),
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        return new { success = true, wiped = company.Name, reason, removed = removed.Values.Sum(), tables = removed, wipedAt = DateTimeOffset.UtcNow };
    }

    /// <summary>The record each erasure leaves behind, newest first.</summary>
    public async Task<object> Erasures()
    {
        var rows = await db.AuditEntries.IgnoreQueryFilters().Where(a => a.Entity == nameof(Company) && a.Action == "Erased")
            .OrderByDescending(a => a.Id).Take(100).ToListAsync();
        return rows.Select(a =>
        {
            using var doc = JsonDocument.Parse(a.Changes is { Length: > 0 } c ? c : "{}");
            var root = doc.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            return new
            {
                companyId = Guid.TryParse(a.EntityId, out var g) ? g : Guid.Empty,
                name = Text("name"), reason = Text("reason"),
                removed = root.TryGetProperty("removed", out var r) && r.TryGetInt32(out var n) ? n : 0,
                a.Actor, a.At,
            };
        }).ToList();
    }

    public async Task<object> ExecuteAction(string action)
    {
        action = action.ToLowerInvariant().Trim();
        switch (action)
        {
            case "gc-collect":
                // The managed heap shows what a collection freed; the process's total lags while the OS takes pages back.
                var before = GC.GetTotalMemory(false) / 1048576.0;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                var after = GC.GetTotalMemory(false) / 1048576.0;
                return new { action, status = "ok", message = $"Freed {Math.Max(0, before - after):0.#} MB: managed memory {before:0} MB → {after:0} MB." };
            default:
                throw new AppException($"Unknown action '{action}'.", 400);
        }
    }
}
