using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Kabuor.Erp;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Assistant;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Etims;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Reports;
using Kabuor.Erp.Sales;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

DotEnv.Load(); // a local .env, if there is one; never overrides real environment variables
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Network & Web server hardening against Slowloris, buffer overflow, and banner grabbing
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10MB limit
    k.Limits.MaxRequestHeaderCount = 64;
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    k.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
    k.Limits.MinResponseDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
});

// Error alerts (Sentry), only when SENTRY__DSN is set: every crash and every logged error is reported, with the
// route and the company id, never request bodies, names or phone numbers.
if (!string.IsNullOrWhiteSpace(config["Sentry:Dsn"]))
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = config["Sentry:Dsn"];
        o.Environment = config["Sentry:Environment"] ?? builder.Environment.EnvironmentName;
        o.Release = (config["RENDER_GIT_COMMIT"] ?? config["RAILWAY_GIT_COMMIT_SHA"]) is { Length: > 7 } sha ? sha[..7] : null;
        o.SendDefaultPii = false;
        o.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None;
        o.MinimumEventLevel = LogLevel.Error;
        o.TracesSampleRate = config.GetValue("Sentry:TracesSampleRate", 0.05);
        // Expected answers (a wrong password, a rule the person can fix) aren't errors.
        o.SetBeforeSend(e => e.Exception is Kabuor.Erp.Common.AppException ? null : e);
    });

// Database: PostgreSQL in production (a "Host=…" connection string), SQLite file locally.
// Requests go through Neon's pooled address (DATABASE_URL). The direct one (DATABASE_URL_UNPOOLED) is only for the
// migrations at start: answering requests over it ran into Neon's connection limit and dropped connections, and
// pages failed now and then with "Something went wrong on our side".
var configuredDb = new[] { config.GetConnectionString("Erp"), config["DATABASE_URL"], config["DATABASE_URL_UNPOOLED"] }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
var migrationDb = config["DATABASE_URL_UNPOOLED"] is { Length: > 0 } direct && config.GetConnectionString("Erp") is null ? Program.ConnectionString(direct) : null;
var isVercel = !string.IsNullOrWhiteSpace(config["VERCEL"]) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VERCEL"));
if (configuredDb is null && builder.Environment.IsProduction() && !config.GetValue("Database:AllowLocalFile", false) && !isVercel)
    throw new InvalidOperationException("Set DATABASE_URL to the PostgreSQL (Neon) connection string. Without it each copy of the server keeps its own throwaway database file: people are signed out straight after signing in, and what they enter is lost on restart. (Set Database__AllowLocalFile=true only for a throwaway demo.)");
if (configuredDb is null && isVercel)
    Console.Error.WriteLine("[WARNING] DATABASE_URL is not set on Vercel. Falling back to temporary SQLite database (kabuor-erp.db). Set DATABASE_URL in Vercel Environment Variables for persistent PostgreSQL.");
var cs = Program.ConnectionString(configuredDb ?? "Data Source=kabuor-erp.db");
builder.Services.AddScoped<RequestContext>();
builder.Services.AddMemoryCache();

// Distributed caching: Redis in production when REDIS_URL or ConnectionStrings:Redis is set, otherwise memory fallback.
var redisCs = config["REDIS_URL"] ?? config["VALKEY_URL"] ?? config.GetConnectionString("Redis") ?? config.GetConnectionString("Valkey") ?? config["REDIS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(redisCs))
{
    var redisOptions = Program.ParseRedisConnectionString(redisCs);
    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.ConfigurationOptions = redisOptions;
        o.InstanceName = "kerp:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddSingleton<AppCache>();
// Live sync between the module workspaces: every save is announced to the open screens of that business (Live/Live.cs).
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Kabuor.Erp.Live.LiveFeed>();
builder.Services.AddSingleton<Kabuor.Erp.Live.LiveSaveInterceptor>();
builder.Services.AddHostedService<Kabuor.Erp.Live.LivePostgresBridge>();
builder.Services.AddSingleton<Kabuor.Erp.Platform.QueryCounter>(); // database round trips per request (Platform/QueryBudget.cs)
builder.Services.AddDbContext<ErpDb>((sp, o) =>
{
    o.AddInterceptors(sp.GetRequiredService<Kabuor.Erp.Live.LiveSaveInterceptor>(), sp.GetRequiredService<Kabuor.Erp.Platform.QueryCounter>());
    if (cs.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
    {
        o.UseNpgsql(cs, npgsql =>
        {
            // Brief network drops are retried, except inside a transaction the code opened itself (see the class).
            npgsql.ExecutionStrategy(d => new RetryOutsideTransactions(d));
            npgsql.CommandTimeout(30);
        });
    }
    else o.UseSqlite(cs);
});

builder.Services.AddScoped<Ledger>();
builder.Services.AddScoped<Numbers>();
builder.Services.AddScoped<SalesService>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<SideBySide>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<Kabuor.Erp.Verified.VerifiedService>();
builder.Services.AddScoped<ReportExports>();
builder.Services.AddScoped<Kabuor.Erp.Finance.FinanceService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<Kabuor.Erp.Auth.OnboardingService>();
builder.Services.AddHealthChecks().AddCheck<ErpDbHealthCheck>("database");
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddScoped<AssistantService>();
builder.Services.AddScoped<Kabuor.Erp.Inventory.InventoryService>();
builder.Services.AddScoped<Kabuor.Erp.Purchases.PurchaseService>();
builder.Services.AddScoped<Kabuor.Erp.Payroll.PeopleService>();
builder.Services.AddScoped<Kabuor.Erp.Payroll.DeductionService>();
builder.Services.AddScoped<WithholdingService>();
builder.Services.AddScoped<StatementService>();
builder.Services.AddScoped<Kabuor.Erp.Import.ImportService>();
builder.Services.AddScoped<Kabuor.Erp.Billing.BillingService>();
builder.Services.AddSingleton<Kabuor.Erp.Billing.BillingMpesa>();
builder.Services.AddScoped<Kabuor.Erp.Payments.StkService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.LinkService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.InquiryService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.InsightsService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.KpiService>();
builder.Services.AddScoped<Kabuor.Erp.Privacy.DataRightsService>();
builder.Services.AddScoped<Kabuor.Erp.Assets.AssetService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.DepartmentService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.MaintenanceService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.RequisitionService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.TenderService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.StockTakeService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.OperationsFinance>();
builder.Services.AddScoped<Kabuor.Erp.Operations.ApprovalRegister>();
builder.Services.AddScoped<Kabuor.Erp.Operations.BudgetService>();
builder.Services.AddScoped<Kabuor.Erp.Operations.CashAdvanceService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.ForesightService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.PlanningService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.TraceService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.QuoteAuditService>();
builder.Services.AddScoped<Kabuor.Erp.Intelligence.PortalService>();
builder.Services.AddSingleton<Kabuor.Erp.Intelligence.PortalTokens>();
builder.Services.AddSingleton<Kabuor.Erp.Dine.DineTokens>();
builder.Services.AddScoped<Kabuor.Erp.Dine.DineService>();
builder.Services.AddScoped<Kabuor.Erp.Integrations.WebhookService>();
builder.Services.AddHostedService<Kabuor.Erp.Integrations.WebhookWorker>();
builder.Services.AddHostedService<Kabuor.Erp.Dispatch.DriverTrailRetention>(); // drivers' locations kept 90 days (privacy notice)
builder.Services.AddHostedService<Kabuor.Erp.Payments.PaymentSourceBackfill>(); // labels payments recorded before they carried a source
// Webhook addresses come from businesses: never into this server's own network (see Common/OutboundGuard.cs).
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(OutboundGuard.Handler);
builder.Services.AddHttpClient("daraja", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<Kabuor.Erp.Crm.CrmService>();
builder.Services.AddScoped<Kabuor.Erp.Production.ProductionService>();
builder.Services.AddScoped<Kabuor.Erp.Dispatch.DispatchService>();
builder.Services.AddScoped<Kabuor.Erp.Dispatch.DriverLinks>();
builder.Services.AddScoped<Kabuor.Erp.Recruitment.RecruitmentService>();
builder.Services.AddScoped<Kabuor.Erp.Marketing.MarketingService>();
builder.Services.AddScoped<Kabuor.Erp.Agents.AgentService>();
builder.Services.AddSingleton<Kabuor.Erp.Agents.AgentScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Kabuor.Erp.Agents.AgentScheduler>());
builder.Services.AddSingleton<Kabuor.Erp.Marketing.ISmsSender, Kabuor.Erp.Marketing.AfricasTalkingSms>();
builder.Services.AddScoped<IEtimsClient, OscuEtimsClient>();
builder.Services.AddSingleton<OscuApi>();
builder.Services.AddScoped<EtimsService>();
builder.Services.AddHttpClient("kra-etims", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddHttpClient("anthropic", c =>
{
    c.BaseAddress = new Uri("https://api.anthropic.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddSingleton<Kabuor.Erp.Platform.RequestMetrics>();
builder.Services.AddSingleton<Kabuor.Erp.Platform.Heartbeat>();
builder.Services.AddScoped<Kabuor.Erp.Platform.PlatformService>();
builder.Services.AddHostedService<Kabuor.Erp.Platform.EnterpriseTrafficWorker>();

builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Sign-in tokens, checked on every request.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenIssuer>((o, tokens) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = "kabuor-erp",
        ValidAudience = "kabuor-erp",
        IssuerSigningKey = tokens.Key,
        ClockSkew = TimeSpan.FromMinutes(1),
        RoleClaimType = "role",
        NameClaimType = "name",
    };
});
builder.Services.AddAuthorization(o =>
{
    foreach (var (name, roles) in Policies.Roles)
        o.AddPolicy(name, p => p.RequireAuthenticatedUser().RequireClaim("cid").RequireRole(roles.Select(r => r.ToString())));
});

// Limits per visitor IP on what's worth abusing.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(15) }));
    o.AddPolicy("assistant", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirst("sub")?.Value ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
    // M-Pesa prompts land on someone's phone: a few an hour per business is plenty for paying, too few to pester anyone.
    o.AddPolicy("prompts", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirst("cid")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) }));

    // A driver's phone sends its location every 15 seconds or so for a whole shift: counted per link, with room to spare.
    o.AddPolicy("drive", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Request.Path.Value?.Split('/').ElementAtOrDefault(3) ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(15) }));

    // Public lender verified report links: 120 requests per 15 minutes per token.
    // Kabuor Dine guests: a phone at a table checks on its order every few seconds, so a looser limit per address.
    o.AddPolicy("dine", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 900, Window = TimeSpan.FromMinutes(15) }));
    o.AddPolicy("verified_share", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Request.Path.Value?.Split('/').ElementAtOrDefault(4) ?? "?", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(15) }));

    // Global flood protection across all API routes
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var path = http.Request.Path.Value ?? "";
        if (path.StartsWith("/health") || !path.StartsWith("/api"))
            return RateLimitPartition.GetNoLimiter("unlimited");

        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetTokenBucketLimiter(ip, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 250,
            QueueLimit = 25,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = 50,
            AutoReplenishment = true
        });
    });
});

var originsConfig = (config["Cors:AllowedOrigins"] ?? Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddPolicy("dev", p => p.WithOrigins(originsConfig).AllowAnyHeader().AllowAnyMethod()));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 2; // Prevent X-Forwarded-For header chain spoofing
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("127.0.0.1/8"));
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();
if (app.Environment.IsProduction() && PublicUrl.Configured(app.Configuration) is null)
    app.Logger.LogWarning("Set App:PublicUrl (APP__PUBLICURL) to this server's public https address: M-Pesa callbacks and shared links fall back to the request's Host header without it.");

// Fail at start, with the reason, if there's no signing key: never run a server that can't sign anyone in.
app.Services.GetRequiredService<TokenIssuer>();

// Bring the database to the model's shape on start (see Data/SchemaSync.cs).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
    if (migrationDb is not null && db.Database.IsNpgsql())
    {
        db.Database.SetConnectionString(migrationDb);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
    }
    await SchemaSync.Prepare(db, app.Configuration); // migrations on PostgreSQL; EnsureCreated + new tables/columns on SQLite
}

// The Kabuor Dine demo restaurant, when the host sets Demo:Password (DEMO__PASSWORD). Never blocks the start.
try { await Kabuor.Erp.Dine.DineDemo.Ensure(app.Services, app.Configuration); }
catch (Exception e) { app.Logger.LogWarning(e, "The Kabuor Dine demo restaurant couldn't be prepared."); }


app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    // Block dangerous methods (Cross-Site Tracing / XST attacks)
    var method = ctx.Request.Method;
    if (method is "TRACE" or "TRACK" or "DEBUG")
    {
        ctx.Response.StatusCode = 405;
        return;
    }

    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h.XFrameOptions = "DENY";
    h["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(self)"; // the driver's trip link shares its location
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    h["X-Permitted-Cross-Domain-Policies"] = "none";
    // Only this site's own scripts, styles, fonts and API: injected scripts and other sites' code can't run.
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https://tile.openstreetmap.org; font-src 'self'; connect-src 'self' https://*.ingest.sentry.io https://*.ingest.us.sentry.io https://*.ingest.de.sentry.io; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'; upgrade-insecure-requests;";
    if (ctx.Request.IsHttps) h.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
    await next();
});

// Problems the user can fix come back as { error }, with the right status. Anything else is logged, not leaked.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Expected answers (wrong password, a rule the person can fix) aren't errors: keep them out of the error log.
    SuppressDiagnosticsCallback = c => c.Exception is AppException or BadHttpRequestException,
    ExceptionHandler = async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        AppException a => (a.Status, a.Message),
        DbUpdateConcurrencyException => (409, "Someone else changed this at the same time. Refresh and try again."),
        BadHttpRequestException b => (b.StatusCode, "The request wasn't in the expected format."),
        UnbalancedEntryException => (500, "Something went wrong on our side."),
        _ => (500, "Something went wrong on our side."),
    };
    if (status >= 500) app.Logger.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
    ctx.Response.StatusCode = status;
    // In development, 500s carry the exception so problems are quick to find; never in production.
    if (ex is not AppException && app.Environment.IsDevelopment()) await ctx.Response.WriteAsJsonAsync(new { error = message, detail = ex?.ToString() });
    else await ctx.Response.WriteAsJsonAsync(new { error = message });
},
});

app.UseCors("dev");
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(Kabuor.Erp.Platform.RequestMetrics.Middleware(app.Services.GetRequiredService<Kabuor.Erp.Platform.RequestMetrics>()));
app.Use(Kabuor.Erp.Platform.QueryCounter.Middleware(app.Configuration, app.Environment, app.Logger));

// Limits come after reading the sign-in token, so the per-person and per-business ones (the assistant, M-Pesa
// prompts) count each person or business on its own instead of everyone together; floods are still cut before
// any database work.
app.UseAuthentication();
app.UseRateLimiter();

// Who is asking: the company and person from the sign-in token, used by the database's company filter.
app.Use(async (http, next) =>
{
    var u = http.User;
    if (u.Identity?.IsAuthenticated == true && Guid.TryParse(u.FindFirst("cid")?.Value, out var cid))
    {
        var ctx = http.RequestServices.GetRequiredService<RequestContext>();
        ctx.CompanyId = cid;
        ctx.UserId = Guid.TryParse(u.FindFirst("sub")?.Value, out var uid) ? uid : null;
        ctx.Actor = u.FindFirst("name")?.Value ?? "user";
        ctx.Role = Enum.TryParse<Role>(u.FindFirst("role")?.Value, out var r) ? r : Role.Viewer;
        ctx.EmployeeId = Guid.TryParse(u.FindFirst("eid")?.Value, out var eid) ? eid : null;
        Sentry.SentrySdk.ConfigureScope(scope => scope.SetTag("company", cid.ToString()));
        // A session ends when the person is switched off, their sessions were ended (password, sign out everywhere), or
        // the login no longer belongs to the business the token names.
        var tv = int.TryParse(u.FindFirst("tv")?.Value, out var v) ? v : 0;
        var emailClaim = u.FindFirst(ClaimTypes.Email)?.Value ?? u.FindFirst("email")?.Value;
        var db = http.RequestServices.GetRequiredService<ErpDb>();
        // The person and their business's plan in one round trip: every request pays for this before any work.
        var current = await (
            from x in db.Users.IgnoreQueryFilters()
            join c in db.Companies.IgnoreQueryFilters() on x.CompanyId equals c.Id
            where x.Id == ctx.UserId || (!string.IsNullOrEmpty(emailClaim) && x.Email == emailClaim && x.CompanyId == cid)
            select new { x.Id, x.Active, x.TokenVersion, x.Role, x.MustChangePassword, x.CompanyId, c.CreatedAt, c.TrialEndsAt, c.PaidUntil, c.AwaitingFirstPayment, c.OwnerOversight }
        ).FirstOrDefaultAsync();

        // A throwaway local database file (a serverless copy without DATABASE_URL) may not have this business yet: rebuild
        // it from the signed token so the person isn't signed out on every cold start. Never on a real database, where a
        // missing business or login means it was erased or removed, and must stay that way.
        if (current is null && !db.Database.IsSqlite())
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsJsonAsync(new { error = "Your session has ended. Please sign in again." });
            return;
        }
        if (current is null)
        {
            // Stateless resilience (local file only, see above): recover from the cryptographically verified JWT claims.
            var company = await db.Companies.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == cid);
            if (company is null)
            {
                company = new Company
                {
                    Id = cid,
                    Name = u.FindFirst("cname")?.Value ?? (ctx.Role == Role.Owner ? $"{ctx.Actor}'s Business" : "Kabuor Business"),
                    Email = emailClaim ?? "",
                    PaidUntil = DateTimeOffset.UtcNow.AddYears(1),
                    Plan = "growth",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-30)
                };
                db.Companies.Add(company);
                await db.SaveChangesAsync();
            }

            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == ctx.UserId || (!string.IsNullOrEmpty(emailClaim) && x.Email == emailClaim && x.CompanyId == cid));
            if (user is null)
            {
                user = new User
                {
                    Id = ctx.UserId ?? Guid.NewGuid(),
                    CompanyId = cid,
                    Name = ctx.Actor,
                    Email = emailClaim ?? $"{ctx.Actor.Replace(" ", "").ToLowerInvariant()}@kabuor.test",
                    Role = ctx.Role,
                    Active = true,
                    TokenVersion = tv,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastSignInAt = DateTimeOffset.UtcNow
                };
                db.Users.Add(user);
                await db.SaveChangesAsync();
            }

            ctx.UserId = user.Id;
            current = new
            {
                user.Id,
                user.Active,
                user.TokenVersion,
                user.Role,
                user.MustChangePassword,
                user.CompanyId,
                company.CreatedAt,
                company.TrialEndsAt,
                company.PaidUntil,
                company.AwaitingFirstPayment,
                company.OwnerOversight
            };
        }
        else
        {
            ctx.UserId = current.Id;
        }

        if (!current.Active || current.TokenVersion != tv || current.Role != ctx.Role || current.CompanyId != cid)
        {
            http.Response.StatusCode = 401;
            await http.Response.WriteAsJsonAsync(new { error = "Your session has ended. Please sign in again." });
            return;
        }
        // A password someone else chose (a new login, a reset, staff portal access) opens nothing but choosing your own.
        if (current.MustChangePassword && !(http.Request.Path.Value ?? "").StartsWith("/api/auth/"))
        {
            http.Response.StatusCode = 403;
            await http.Response.WriteAsJsonAsync(new { error = "Choose your own password first.", code = "set-password" });
            return;
        }
        var method = http.Request.Method;
        var path = http.Request.Path.Value ?? "";
        if (path.StartsWith("/api/") && !path.StartsWith("/api/billing") && !path.StartsWith("/api/auth") && !path.StartsWith("/api/platform"))
        {
            // Signed up, not paid yet: only the plan and payment screens answer until the first payment arrives.
            if (current.AwaitingFirstPayment)
            {
                http.Response.StatusCode = 402;
                await http.Response.WriteAsJsonAsync(new { error = "Pay for a plan by M-Pesa to open your account.", code = "pay-to-start" });
                return;
            }
            // The owner oversees (Company.OwnerOversight): every page reads, and they approve, sign off, set targets and run
            // the business's settings; the daily work is the teams'. Kept here, ahead of every endpoint, so none can forget it.
            if (ctx.Role == Role.Owner && current.OwnerOversight && method is not ("GET" or "HEAD" or "OPTIONS") && !Kabuor.Erp.Auth.Oversight.Allows(path))
            {
                http.Response.StatusCode = 403;
                await http.Response.WriteAsJsonAsync(new { error = "As the owner you oversee this: the team does the daily work, and you approve and sign off. To do it yourself, turn off owner oversight in Settings → Business.", code = "oversight" });
                return;
            }
            // Paid time (or the old free trial) over: everything stays readable and downloadable; adding and changing waits for a plan.
            var probe = new Company { Name = "", CreatedAt = current.CreatedAt, TrialEndsAt = current.TrialEndsAt, PaidUntil = current.PaidUntil };
            if (method is not ("GET" or "HEAD" or "OPTIONS") && Kabuor.Erp.Billing.Plans.Locked(probe, DateTimeOffset.UtcNow))
            {
                http.Response.StatusCode = 402;
                await http.Response.WriteAsJsonAsync(new { error = "Your plan has run out. Renew it (Settings → Plan & billing) to keep adding and changing records. Everything you have is still here.", code = "renew" });
                return;
            }
        }
    }
    await next();
});
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Name == "database"
});

Endpoints.Map(app);
IntelligenceEndpoints.Map(app);
Kabuor.Erp.Live.LiveEndpoints.Map(app);
AssetEndpoints.Map(app);
OperationsEndpoints.Map(app);
VerifiedEndpoints.Map(app);
PlatformEndpoints.Map(app);
// An API address that matches nothing says so in JSON, instead of falling through to the web app's page below and
// handing a screen HTML where it expected data. Reads only, like the page fallback: a catch-all that took any method
// would win over real endpoints for bodiless POSTs (approve, dismiss), since routing sends a request with no
// Content-Type only to endpoints that accept any.
app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "That isn’t part of this server. Reload the page; it may have been updated." }))
    .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
// The web app (built into wwwroot): any other path is a page in it.
app.MapFallbackToFile("index.html");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var msg = $"[{DateTime.UtcNow:O}] AppDomain.UnhandledException: {e.ExceptionObject}";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + Environment.NewLine); } catch { }
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    var msg = $"[{DateTime.UtcNow:O}] TaskScheduler.UnobservedTaskException: {e.Exception}";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + Environment.NewLine); } catch { }
    e.SetObserved();
};

app.Lifetime.ApplicationStopping.Register(() =>
{
    var msg = $"[{DateTime.UtcNow:O}] Application stopping requested.";
    Console.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + Environment.NewLine); } catch { }
});
app.Lifetime.ApplicationStopped.Register(() =>
{
    var msg = $"[{DateTime.UtcNow:O}] Application stopped.";
    Console.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + Environment.NewLine); } catch { }
});

try
{
    app.Run();
    var msg = $"[{DateTime.UtcNow:O}] app.Run() completed normally.";
    Console.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + Environment.NewLine); } catch { }
}
catch (Exception ex)
{
    var msg = $"[{DateTime.UtcNow:O}] Fatal host crash: {ex}";
    Console.Error.WriteLine(msg);
    try { File.AppendAllText("crash.log", msg + Environment.NewLine); } catch { }
    throw;
}

public partial class Program
{
    // Railway and most hosts give PostgreSQL as a URL (postgresql://user:pass@host:port/db); Npgsql wants key=value.
    internal static string ConnectionString(string raw)
    {
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) return raw;
        var u = new Uri(raw);
        var (user, pass) = (u.UserInfo.Split(':', 2) is [var a, var b] ? (Uri.UnescapeDataString(a), Uri.UnescapeDataString(b)) : (Uri.UnescapeDataString(u.UserInfo), ""));
        var query = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToLowerInvariant(), p => Uri.UnescapeDataString(p[1]).ToLowerInvariant());
        // Neon (and any URL asking for verify-full) gets an encrypted, certificate-checked connection; sslmode=require
        // is encrypted; otherwise (a private network or a proxy with its own certificate) it prefers encryption.
        var ssl = u.Host.EndsWith(".neon.tech", StringComparison.OrdinalIgnoreCase) || query.GetValueOrDefault("sslmode") == "verify-full" ? "SSL Mode=VerifyFull"
            : query.GetValueOrDefault("sslmode") is "require" or "verify-ca" ? "SSL Mode=Require;Trust Server Certificate=true"
            : "SSL Mode=Prefer;Trust Server Certificate=true";
        var binding = query.GetValueOrDefault("channel_binding") == "require" ? ";Channel Binding=Require" : "";
        var maxPool = query.GetValueOrDefault("max_pool_size") ?? query.GetValueOrDefault("maximum pool size") ?? Environment.GetEnvironmentVariable("DB_MAX_POOL_SIZE") ?? "100";
        var minPool = query.GetValueOrDefault("min_pool_size") ?? query.GetValueOrDefault("minimum pool size") ?? Environment.GetEnvironmentVariable("DB_MIN_POOL_SIZE") ?? "5";
        var idleLife = query.GetValueOrDefault("connection_idle_lifetime") ?? "300";
        var timeout = query.GetValueOrDefault("timeout") ?? "15";
        var pool = $";Pooling=true;Minimum Pool Size={minPool};Maximum Pool Size={maxPool};Connection Idle Lifetime={idleLife};Timeout={timeout}";
        return $"Host={u.Host};Port={(u.Port > 0 ? u.Port : 5432)};Database={u.AbsolutePath.TrimStart('/')};Username={user};Password={pass};{ssl}{binding}{pool}";
    }

    internal static StackExchange.Redis.ConfigurationOptions ParseRedisConnectionString(string raw)
    {
        if (Uri.TryCreate(raw, UriKind.Absolute, out var u) && 
           (u.Scheme.Equals("redis", StringComparison.OrdinalIgnoreCase) || 
            u.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase) ||
            u.Scheme.Equals("valkey", StringComparison.OrdinalIgnoreCase) ||
            u.Scheme.Equals("valkeys", StringComparison.OrdinalIgnoreCase)))
        {
            var isSsl = u.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase) || u.Scheme.Equals("valkeys", StringComparison.OrdinalIgnoreCase);
            var parts = u.UserInfo.Split(':', 2);
            var pass = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : (parts.Length == 1 ? Uri.UnescapeDataString(parts[0]) : null);
            var user = parts.Length == 2 && parts[0].Length > 0 && parts[0] != "default" ? Uri.UnescapeDataString(parts[0]) : null;
            var port = u.Port > 0 ? u.Port : (isSsl ? 6380 : 6379);
            var opts = new StackExchange.Redis.ConfigurationOptions
            {
                EndPoints = { { u.Host, port } },
                Ssl = isSsl,
                AbortOnConnectFail = false,
                ConnectTimeout = 5000,
                SyncTimeout = 5000
            };
            if (!string.IsNullOrEmpty(pass)) opts.Password = pass;
            if (!string.IsNullOrEmpty(user)) opts.User = user;
            return opts;
        }

        var parsed = StackExchange.Redis.ConfigurationOptions.Parse(raw);
        parsed.AbortOnConnectFail = false;
        parsed.ConnectTimeout = 5000;
        parsed.SyncTimeout = 5000;
        return parsed;
    }
}

