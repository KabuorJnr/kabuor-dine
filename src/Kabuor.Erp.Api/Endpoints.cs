// Every HTTP endpoint. Each group asks for a policy (see Auth/Policies); the database filters every query by
// the caller's company, so handlers never pass company ids around.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Etims;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Reports;
using Kabuor.Erp.Sales;
using Kabuor.Erp.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record CustomerInput(string Name, string? Phone, string? Email, string? KraPin, string? Address, long? CreditLimit, string? Notes, bool? MarketingOptOut = null, Guid? PriceListId = null, string? Currency = null);
public sealed record ItemInput(string Name, string? Sku, ItemKind? Kind, string? Unit, long Price, TaxType? TaxType, bool? Active, bool? TrackStock, decimal? ReorderLevel, string? EtimsClassCode = null, bool? TrackBatches = null, string? Brand = null, string? Barcode = null,
    ItemUse? Use = null, string? StoresCategory = null);
public sealed record CompanyInput(string Name, string? KraPin, string? Phone, string? Email, string? County, bool VatRegistered, string? MpesaShortcode, bool? PreventNegativeStock = null, string? Industry = null, DateOnly? AccountingLockDate = null, string? Address = null, bool? OwnerOversight = null);
public sealed record LogoInput(string? DataUrl);
public sealed record AcceptPurchaseInput(Guid? AccountId);
public sealed record RejectPurchaseInput(string? Reason);
public sealed record UserInput(string Name, string Email, string? Phone, Role Role, string Password);
public sealed record RoleInput(Role Role);
public sealed record AllocateInput(List<AllocationInput> Allocations);

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/health", () => Results.Ok(new { ok = true }));
        // Ready = the database answers too (the host waits for this before sending traffic to a new version).
        api.MapGet("/health/ready", async (ErpDb db) =>
            await db.Database.CanConnectAsync() ? Results.Ok(new { ok = true, database = "ok" }) : Results.Json(new { ok = false, database = "unreachable" }, statusCode: 503));

        // ---------- sign up / sign in ----------
        var auth = api.MapGroup("/auth").RequireRateLimiting("auth");
        auth.MapPost("/register", (RegisterInput input, AuthService s) => s.Register(input));
        auth.MapPost("/login", (LoginInput input, AuthService s) => s.Login(input));
        auth.MapPost("/login/code", (CodeInput input, AuthService s) => s.LoginCode(input));
        var me = api.MapGroup("/auth/security").RequireAuthorization();
        me.MapGet("", (AuthService s) => s.Security());
        me.MapPost("/2fa/start", (AuthService s) => s.StartTwoFactor());
        me.MapPost("/2fa/enable", (TwoFactorInput input, AuthService s) => s.EnableTwoFactor(input));
        me.MapPost("/2fa/disable", async (TwoFactorInput input, AuthService s) => { await s.DisableTwoFactor(input); return Results.NoContent(); });
        me.MapPost("/sign-out-everywhere", async (AuthService s) => { await s.SignOutEverywhere(); return Results.NoContent(); });

        // Safaricom's answer to a subscription prompt (the secret in the URL is ours, from configuration).
        api.MapPost("/billing/mpesa/{secret}", async (string secret, System.Text.Json.Nodes.JsonObject body, Kabuor.Erp.Billing.BillingMpesa mpesa, Kabuor.Erp.Billing.BillingService s) =>
        {
            if (!mpesa.SecretMatches(secret)) return Results.NotFound();
            await s.Callback(body);
            return MpesaAck();
        });

        // Public: who to contact about privacy, and where the data lives (for the privacy notice and terms).
        // Public settings the web app needs before sign-in: where to report browser errors (empty = off).
        // And what opening an account costs, so sign-up can say so before anyone starts.
        api.MapGet("/client-config", (IConfiguration config) => new
        {
            sentryDsn = config["Sentry:BrowserDsn"] ?? config["Sentry:Dsn"] ?? "", environment = config["Sentry:Environment"] ?? "production",
            payToStart = Kabuor.Erp.Billing.Plans.PayToStart(config),
            plans = Kabuor.Erp.Billing.Plans.All(config).Select(p => new { p.Id, p.Name, p.Monthly }),
        });
        api.MapGet("/legal", (IConfiguration config) => new { email = config["Legal:Email"] ?? "", hosting = config["Legal:Hosting"] ?? "Vercel (United States)", hostingRegion = config["Legal:HostingRegion"] ?? "", updated = config["Legal:Updated"] ?? "30 October 2026" });

        // ---------- M-Pesa (Safaricom calls these; no login, the URL carries the company's secret key) ----------
        api.MapPost("/mpesa/stk/{key}", async (string key, System.Text.Json.Nodes.JsonObject body, ErpDb db, RequestContext ctx, StkService stk, HttpContext http) =>
        {
            var company = await db.Companies.SingleOrDefaultAsync(c => c.MpesaCallbackKey == key);
            if (company is null) return Results.NotFound();
            using var activity = ErpTelemetry.StartPaymentActivity("StkCallback", "mpesa", company.Id);
            (ctx.CompanyId, ctx.Actor) = (company.Id, "M-Pesa");
            await stk.Callback(company, body, http.Connection.RemoteIpAddress);
            return MpesaAck();
        });
        var mpesa = api.MapGroup("/mpesa/c2b/{key}");
        mpesa.MapPost("/validation", () => MpesaAck());
        mpesa.MapPost("/confirmation", async (string key, MpesaC2B body, ErpDb db, RequestContext ctx, PaymentService payments, ILogger<PaymentService> log, HttpContext http, IConfiguration config) =>
        {
            var company = await db.Companies.SingleOrDefaultAsync(c => c.MpesaCallbackKey == key);
            if (company is null) return Results.NotFound();
            if (company.MpesaShortcode.Length > 0 && body.BusinessShortCode != company.MpesaShortcode)
            {
                log.LogWarning("M-Pesa confirmation for shortcode {Got} reached company {Company} (expects {Want})", body.BusinessShortCode, company.Id, company.MpesaShortcode);
                return MpesaAck();
            }
            using var activity = ErpTelemetry.StartPaymentActivity("C2BConfirmation", "mpesa", company.Id, body.TransID);
            (ctx.CompanyId, ctx.Actor) = (company.Id, "M-Pesa");
            var from = http.Connection.RemoteIpAddress;
            // Recorded either way (money mustn't go missing), but only counts as confirmed when Safaricom sent it.
            if (!SafaricomCallers.IsSafaricom(from, config)) log.LogWarning("M-Pesa confirmation for company {Company} came from {Ip}, not a Safaricom callback address", company.Id, from);
            var payment = await payments.FromMpesa(body, from);
            if (payment is not null)
            {
                ErpTelemetry.RecordPayment("mpesa", payment.Amount, true, company.Id);
            }
            return MpesaAck();
        });

        var app_ = api.MapGroup("").RequireAuthorization(Policies.Read);

        // ---------- onboarding & quickstart self-service ----------
        var onboarding = app_.MapGroup("/onboarding").RequireAuthorization(Policies.Admin);
        onboarding.MapGet("/status", (OnboardingService s) => s.GetStatus());
        onboarding.MapPost("/quickstart", (QuickstartSetupInput input, OnboardingService s) => s.Quickstart(input));

        // ---------- home ----------
        app_.MapGet("/today", (ReportService r) => r.Today()).CachedPerCompany();

        // ---------- customers ----------
        var customers = app_.MapGroup("/customers").RequireAuthorization(Policies.SalesView);
        customers.MapGet("", async (ErpDb db, string? q, string? filter, int? skip, int? take) =>
        {
            var today = Clock.Today;
            var (s, t) = Paging.Of(skip, take);
            var query = db.Customers.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(c => EF.Functions.Like(c.Name, like) || EF.Functions.Like(c.Phone, like) || EF.Functions.Like(c.Number, like) || EF.Functions.Like(c.KraPin, like));
            }
            var rows = await query.Select(c => new
            {
                c.Id, c.Number, c.Name, c.Phone, c.Email, c.KraPin, c.PriceListId, c.CreditLimit, c.Currency,
                owes = db.SalesDocs.Where(d => d.CustomerId == c.Id && d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid)).Sum(d => d.Total - d.Paid),
                overdue = db.SalesDocs.Where(d => d.CustomerId == c.Id && d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid) && d.DueDate < today).Sum(d => d.Total - d.Paid),
            }).ToListAsync();
            var counts = new { all = rows.Count, owing = rows.Count(r => r.owes > 0), overdue = rows.Count(r => r.overdue > 0) };
            var filtered = (filter switch { "owing" => rows.Where(r => r.owes > 0), "overdue" => rows.Where(r => r.overdue > 0), _ => rows.AsEnumerable() })
                .OrderByDescending(r => r.overdue).ThenByDescending(r => r.owes).ThenBy(r => r.Name).ToList();
            return new { counts, page = new Page<object>(filtered.Skip(s).Take(t).Cast<object>().ToList(), filtered.Count, s, t) };
        });
        customers.MapGet("/{id:guid}", async (Guid id, ErpDb db) =>
        {
            var c = await db.Customers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            var docs = await db.SalesDocs.Where(d => d.CustomerId == id).OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number)
                .Select(d => new { d.Id, d.Kind, d.Number, d.Status, d.IssueDate, d.DueDate, d.Total, d.Paid }).Take(200).ToListAsync();
            var payments = await db.Payments.Where(p => p.CustomerId == id).OrderByDescending(p => p.ReceivedAt)
                .Select(p => new { p.Id, p.Method, p.Amount, p.Allocated, p.Reference, p.ReceivedAt }).Take(200).ToListAsync();
            var open = docs.Where(d => d.Kind == SalesDocKind.Invoice && d.Status is SalesDocStatus.Sent or SalesDocStatus.PartPaid).ToList();
            return new
            {
                customer = c, documents = docs, payments,
                owes = open.Sum(d => d.Total - d.Paid),
                overdue = open.Where(d => d.DueDate < Clock.Today).Sum(d => d.Total - d.Paid),
                credit = payments.Sum(p => p.Amount - p.Allocated),
                lifetime = docs.Where(d => d.Kind == SalesDocKind.Invoice && d.Status != SalesDocStatus.Void && d.Status != SalesDocStatus.Draft).Sum(d => d.Total),
            };
        });
        customers.MapPost("", async (CustomerInput input, ErpDb db, Numbers numbers, RequestContext ctx) =>
        {
            var c = new Customer { Number = await numbers.Next("C"), Name = "" };
            await ApplyCustomer(db, c, input, ctx);
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return Results.Created($"/api/customers/{c.Id}", c);
        }).RequireAuthorization(Policies.Sell);
        // The customer's rights under the Data Protection Act: a full copy of their data, and erasure. Owner and admins only.
        customers.MapGet("/{id:guid}/data-export", (Guid id, Kabuor.Erp.Privacy.DataRightsService s) => s.Export(id)).RequireAuthorization(Policies.Admin);
        customers.MapPost("/{id:guid}/erase", (Guid id, Kabuor.Erp.Privacy.EraseInput input, Kabuor.Erp.Privacy.DataRightsService s) => s.Erase(id, input)).RequireAuthorization(Policies.Admin);
        customers.MapPut("/{id:guid}", async (Guid id, CustomerInput input, ErpDb db, RequestContext ctx) =>
        {
            var c = await db.Customers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            await ApplyCustomer(db, c, input, ctx);
            await db.SaveChangesAsync();
            return c;
        }).RequireAuthorization(Policies.Sell);

        // ---------- items ----------
        var items = app_.MapGroup("/items");
        items.MapGet("", async (ErpDb db, string? q, bool? all, ItemUse? use) =>
        {
            var query = db.Items.AsQueryable();
            if (all != true) query = query.Where(i => i.Active);
            if (use is { } u) query = query.Where(i => i.Use == u);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(i => EF.Functions.Like(i.Name, $"%{q.Trim()}%") || EF.Functions.Like(i.Sku, $"%{q.Trim()}%") || EF.Functions.Like(i.Brand, $"%{q.Trim()}%") || i.Barcode == q.Trim());
            return await query.OrderBy(i => i.Name).Take(500).ToListAsync();
        });
        items.MapPost("", async (ItemInput input, ErpDb db) =>
        {
            var i = new Item { Name = "" };
            ApplyItem(i, input);
            await UniqueBarcode(db, i);
            db.Items.Add(i);
            await db.SaveChangesAsync();
            return Results.Created($"/api/items/{i.Id}", i);
        }).RequireAuthorization(Policies.Sell);
        items.MapPut("/{id:guid}", async (Guid id, ItemInput input, ErpDb db, Kabuor.Erp.Accounting.Ledger ledger) =>
        {
            var i = await db.Items.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            var wasUse = i.Use;
            ApplyItem(i, input);
            // Moving an item between stock for sale and stores moves what's on hand between the two accounts.
            if (i.Use != wasUse && i.TrackStock)
            {
                var onHand = await db.StockLevels.Where(l => l.ItemId == i.Id).SumAsync(l => (decimal?)l.Quantity) ?? 0;
                await ledger.PostStockReclass(i, (long)Math.Round(onHand * i.AverageCost), i.Use == ItemUse.Stores);
            }
            await UniqueBarcode(db, i);
            await db.SaveChangesAsync();
            return i;
        }).RequireAuthorization(Policies.Sell);

        // ---------- quotes and invoices ----------
        var docs = app_.MapGroup("/sales").RequireAuthorization(Policies.SalesView);
        docs.MapGet("", async (ErpDb db, SalesDocKind? kind, string? status, string? q, int? skip, int? take) =>
        {
            var today = Clock.Today;
            var (s, t) = Paging.Of(skip, take);
            var query = db.SalesDocs.Where(d => d.Kind == (kind ?? SalesDocKind.Invoice));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(d => EF.Functions.Like(d.Number, like) || EF.Functions.Like(d.Customer!.Name, like) || EF.Functions.Like(d.Customer.Phone, like));
            }
            var open = new[] { SalesDocStatus.Sent, SalesDocStatus.PartPaid };
            // Counts for the filter chips, so each one shows what it will return before it's tapped.
            var counts = new
            {
                all = await query.CountAsync(),
                draft = await query.CountAsync(d => d.Status == SalesDocStatus.Draft),
                unpaid = await query.CountAsync(d => open.Contains(d.Status)),
                overdue = await query.CountAsync(d => open.Contains(d.Status) && d.DueDate < today),
                paid = await query.CountAsync(d => d.Status == SalesDocStatus.Paid),
            };
            query = status switch
            {
                "draft" => query.Where(d => d.Status == SalesDocStatus.Draft),
                "unpaid" => query.Where(d => open.Contains(d.Status)),
                "overdue" => query.Where(d => open.Contains(d.Status) && d.DueDate < today),
                "paid" => query.Where(d => d.Status == SalesDocStatus.Paid),
                _ => query,
            };
            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.Number).Skip(s).Take(t)
                .Select(d => new { d.Id, d.Kind, d.Number, d.Status, d.IssueDate, d.DueDate, d.Total, d.Tax, d.Paid, d.Currency, d.FxTotal, d.FxPaid, d.EtimsStatus, customer = d.Customer!.Name, customerId = d.CustomerId })
                .ToListAsync();
            return new { counts, page = new Page<object>(rows.Cast<object>().ToList(), total, s, t) };
        });
        docs.MapGet("/{id:guid}", async (Guid id, SalesService s, ErpDb db) =>
        {
            var d = await s.Get(id);
            var paidBy = await db.PaymentAllocations.Where(a => a.SalesDocId == id)
                .Join(db.Payments, a => a.PaymentId, p => p.Id, (a, p) => new { a.Amount, a.FxAmount, a.MatchedBy, a.At, p.Method, p.Reference, p.ReceivedAt }).ToListAsync();
            var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
            var prompt = company.StkShortcode.Length > 0 && company.StkConsumerKey.Length > 0 && company.StkPasskey.Length > 0;
            return new { document = d, payments = paidBy, payTo = new { paybill = company.MpesaShortcode, account = d.Number, prompt } };
        });
        // The rate each currency was last invoiced at, to start the next invoice from (the person checks it against CBK).
        docs.MapGet("/rates", async (ErpDb db) =>
            (await db.SalesDocs.Where(d => d.Currency != "").OrderByDescending(d => d.CreatedAt).Select(d => new { d.Currency, d.Rate, d.IssueDate }).Take(500).ToListAsync())
                .GroupBy(d => d.Currency).Select(g => new { currency = g.Key, rate = g.First().Rate, on = g.First().IssueDate }).ToList());
        docs.MapPost("", async (SalesDocInput input, SalesService s) => Results.Created("", await s.Create(input))).RequireAuthorization(Policies.Sell);
        docs.MapPut("/{id:guid}", (Guid id, SalesDocInput input, SalesService s) => s.Update(id, input)).RequireAuthorization(Policies.Sell);
        docs.MapPost("/{id:guid}/issue", async (Guid id, SalesService s, PaymentService payments, RequestContext ctx, bool? overLimit) =>
        {
            // Going over a customer's credit limit is the owner's or accountant's call.
            if (overLimit == true && !ctx.Role.Allows(Policies.Finance)) throw new AppException("Only the owner or the accountant can allow an invoice over the customer's credit limit.", 403);
            var d = await s.Issue(id, overLimit == true);
            if (d.Kind == SalesDocKind.Invoice) await payments.RetryUnmatched(); // money that arrived before the invoice
            return await s.Get(id);
        }).RequireAuthorization(Policies.Sell);
        docs.MapPost("/{id:guid}/void", (Guid id, SalesService s) => s.Void(id)).RequireAuthorization(Policies.Finance);
        docs.MapPost("/{id:guid}/mpesa-request", (Guid id, StkRequestInput input, StkService s, HttpRequest req) => s.Request(id, input, PublicUrl.Of(req))).RequireAuthorization(Policies.Sell);
        docs.MapGet("/{id:guid}/mpesa-requests", (Guid id, StkService s) => s.ForInvoice(id));
        docs.MapPost("/{id:guid}/to-invoice", (Guid id, SalesService s) => s.ToInvoice(id)).RequireAuthorization(Policies.Sell);
        docs.MapPost("/{id:guid}/etims", async (Guid id, SalesService s) =>
        {
            var d = await s.Get(id);
            if (d.Kind != SalesDocKind.Invoice || d.Status is SalesDocStatus.Draft or SalesDocStatus.Void) throw new AppException("Only issued invoices are signed.");
            if (d.EtimsStatus == EtimsStatus.Signed) return d;
            await s.Sign(d);
            return d;
        }).RequireAuthorization(Policies.Sell);

        // ---------- money in ----------
        var pay = app_.MapGroup("/payments").RequireAuthorization(Policies.SalesView);
        pay.MapGet("", async (ErpDb db, string? status, string? q, int? skip, int? take) =>
        {
            var (s, t) = Paging.Of(skip, take);
            var query = db.Payments.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = $"%{q.Trim()}%";
                query = query.Where(p => EF.Functions.Like(p.Reference, like) || EF.Functions.Like(p.PayerName, like) || EF.Functions.Like(p.PayerPhone, like) || EF.Functions.Like(p.AccountRef, like));
            }
            var counts = new { all = await query.CountAsync(), toMatch = await query.CountAsync(p => p.Status != PaymentStatus.Matched), matched = await query.CountAsync(p => p.Status == PaymentStatus.Matched), confirmed = await query.CountAsync(p => p.FromSafaricom) };
            if (status == "to-match") query = query.Where(p => p.Status != PaymentStatus.Matched);
            if (status == "matched") query = query.Where(p => p.Status == PaymentStatus.Matched);
            if (status == "confirmed") query = query.Where(p => p.FromSafaricom);
            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(p => p.ReceivedAt).Skip(s).Take(t).ToListAsync();
            return new { counts, page = new Page<Payment>(rows, total, s, t) };
        });
        pay.MapGet("/{id:guid}", async (Guid id, ErpDb db, PaymentService payments) =>
        {
            var p = await db.Payments.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            var suggestion = await payments.Suggest(p);
            return new { payment = p, suggestion = suggestion.Allocations.Count > 0 ? new { suggestion.Reason, suggestion.Auto, allocations = suggestion.Allocations } : null };
        });
        pay.MapPost("", async (PaymentInput input, PaymentService payments, RequestContext ctx) => Results.Created("", await payments.Record(input, ctx.Actor))).RequireAuthorization(Policies.Sell);
        pay.MapPost("/{id:guid}/allocate", (Guid id, AllocateInput input, PaymentService payments, RequestContext ctx) => payments.Allocate(id, input.Allocations ?? [], $"user: {ctx.Actor}")).RequireAuthorization(Policies.Finance);
        pay.MapPost("/retry-matching", async (PaymentService payments) => new { matched = await payments.RetryUnmatched() }).RequireAuthorization(Policies.Finance);

        // ---------- reports ----------
        var reports = app_.MapGroup("/reports").RequireAuthorization(Policies.Reports);
        reports.MapGet("/aging", (ReportService r) => r.Aging());
        reports.MapGet("/profit-and-loss", (ReportService r, DateOnly? from, DateOnly? to) =>
            r.ProfitAndLoss(from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today));
        reports.MapGet("/vat", (ReportService r, DateOnly? from, DateOnly? to) =>
            r.Vat(from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today));
        reports.MapGet("/trial-balance", (ReportService r, DateOnly? to) => r.TrialBalance(to));
        reports.MapGet("/accounts", async (ReportService r) => await r.Balances());
        reports.MapGet("/balance-sheet", (ReportService r, DateOnly? at) => r.BalanceSheet(at ?? Clock.Today));
        reports.MapGet("/cash-flow", (ReportService r, DateOnly? from, DateOnly? to) =>
            r.CashFlow(from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today));
        reports.MapGet("/withheld", (ReportService r, DateOnly? from, DateOnly? to) =>
            r.Withheld(from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today));
        // Files: any report as a spreadsheet or CSV (or as tables for the printable view), and a month's numbers as one
        // workbook to send on. Balance sheet, trial balance and debtors are "as at" the end date.
        const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        reports.MapGet("/export/{report}", async (string report, string? format, DateOnly? from, DateOnly? to, ReportExports x) =>
        {
            var (f, t) = (from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today);
            var sheet = await x.One(report, f, t);
            if (format == "json") return Results.Ok(new { sheets = new[] { ReportExports.ToJson(sheet) } });
            var period = report is "balance-sheet" or "trial-balance" or "aging" ? $"at {t:yyyy-MM-dd}" : $"{f:yyyy-MM-dd} to {t:yyyy-MM-dd}";
            var company = await x.CompanyName();
            return format == "csv"
                ? Results.File([.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(Xlsx.Csv(sheet))], "text/csv", ReportExports.FileName(company, sheet.Name, period, "csv"))
                : Results.File(Xlsx.Write([sheet]), XlsxType, ReportExports.FileName(company, sheet.Name, period, "xlsx"));
        });
        reports.MapGet("/monthly", async (string? month, string? format, ReportExports x) =>
        {
            var m = DateOnly.TryParseExact($"{month}-01", "yyyy-MM-dd", out var d) ? d : new DateOnly(Clock.Today.Year, Clock.Today.Month, 1);
            var sheets = await x.Monthly(m);
            var company = await x.CompanyName();
            return format == "json"
                ? Results.Ok(new { company, month = m, sheets = sheets.Select(ReportExports.ToJson) })
                : Results.File(Xlsx.Write(sheets), XlsxType, ReportExports.FileName(company, "Monthly report", m.ToString("yyyy-MM"), "xlsx"));
        }).CachedPerCompany(600);

        // KRA VAT3 registers, to paste into the iTax return template.
        reports.MapGet("/itax/sales.csv", async (ReportService r, DateOnly? from, DateOnly? to, string? rate) =>
        {
            var (f, t) = (from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today);
            var csv = await r.ItaxSales(f, t, rate == "8" ? TaxType.E_Reduced8 : TaxType.B_Standard16);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"VAT3-sales-{(rate == "8" ? "8pc" : "16pc")}-{f:yyyy-MM}.csv");
        });
        reports.MapGet("/itax/purchases.csv", async (ReportService r, DateOnly? from, DateOnly? to) =>
        {
            var (f, t) = (from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1), to ?? Clock.Today);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(await r.ItaxPurchases(f, t)), "text/csv", $"VAT3-purchases-{f:yyyy-MM}.csv");
        });

        // ---------- KRA eTIMS ----------
        var etims = app_.MapGroup("/etims");
        etims.MapGet("", (EtimsService s) => s.Status()).RequireAuthorization(Policies.Finance);
        etims.MapPut("", (EtimsSetupInput input, EtimsService s) => s.Setup(input)).RequireAuthorization(Policies.Admin);
        etims.MapPost("/retry", async (ErpDb db, SalesService s) =>
        {
            var waiting = await db.SalesDocs.Where(d => d.EtimsStatus == EtimsStatus.Failed || d.EtimsStatus == EtimsStatus.Pending).Select(d => d.Id).Take(100).ToListAsync();
            var signed = 0;
            foreach (var id in waiting) { var d = await s.Get(id); await s.Sign(d); if (d.EtimsStatus == EtimsStatus.Signed) signed++; }
            return new { tried = waiting.Count, signed };
        }).RequireAuthorization(Policies.Finance);
        etims.MapGet("/purchases", (string? status, EtimsService s) => s.Purchases(status)).RequireAuthorization(Policies.Finance);
        etims.MapPost("/purchases/sync", async (EtimsService s) => new { received = await s.SyncPurchases() }).RequireAuthorization(Policies.Finance);
        etims.MapPost("/purchases/{id:guid}/accept", (Guid id, AcceptPurchaseInput? input, EtimsService s) => s.Accept(id, input?.AccountId)).RequireAuthorization(Policies.Finance);
        etims.MapPost("/purchases/{id:guid}/reject", (Guid id, RejectPurchaseInput input, EtimsService s) => s.Reject(id, input.Reason)).RequireAuthorization(Policies.Finance);

        // ---------- tax withheld by customers ----------
        docs.MapPost("/{id:guid}/withheld", (Guid id, WithholdingInput input, WithholdingService s) => s.Record(id, input)).RequireAuthorization(Policies.Finance);

        // ---------- subscription ----------
        var billing = app_.MapGroup("/billing").RequireAuthorization(Policies.Admin);
        billing.MapGet("", (Kabuor.Erp.Billing.BillingService s) => s.Status());
        billing.MapPost("/pay", (Kabuor.Erp.Billing.PayInput input, Kabuor.Erp.Billing.BillingService s, HttpRequest req) => s.Pay(input, PublicUrl.Of(req))).RequireRateLimiting("prompts");
        billing.MapPost("/payments/{id:guid}/check", (Guid id, Kabuor.Erp.Billing.BillingService s) => s.Check(id));

        // ---------- moving in: CSV from Excel, QuickBooks, Sage, Zoho ----------
        var import = app_.MapGroup("/import").RequireAuthorization(Policies.Admin);
        import.MapPost("/customers", (Kabuor.Erp.Import.ImportRequest r, Kabuor.Erp.Import.ImportService s) => s.Parties(false, r));
        import.MapPost("/suppliers", (Kabuor.Erp.Import.ImportRequest r, Kabuor.Erp.Import.ImportService s) => s.Parties(true, r));
        import.MapPost("/items", (Kabuor.Erp.Import.ImportRequest r, Kabuor.Erp.Import.ImportService s) => s.Items(r));

        // ---------- M-Pesa and bank statements ----------
        var statements = app_.MapGroup("/statements").RequireAuthorization(Policies.Finance);
        statements.MapGet("/{account}", (string account, StatementService s) => s.Overview(account));
        statements.MapPost("/import", (ImportInput input, StatementService s) => s.Import(input));
        statements.MapPost("/{account}/match", async (string account, StatementService s) => new { matched = await s.Match(account.ToUpperInvariant()) });
        statements.MapPost("/lines/{id:guid}/payment", (Guid id, RecordPaymentInput input, StatementService s) => s.RecordPayment(id, input));
        statements.MapPost("/lines/{id:guid}/expense", (Guid id, RecordExpenseInput input, StatementService s) => s.RecordExpense(id, input));
        statements.MapPost("/lines/{id:guid}/ignore", (Guid id, IgnoreInput input, StatementService s) => s.Ignore(id, input));

        // ---------- company, people, audit ----------
        var settings = app_.MapGroup("/settings");
        settings.MapGet("/company", async (ErpDb db, HttpRequest req) =>
        {
            var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
            return new { c.Id, c.Name, c.KraPin, c.Phone, c.Email, c.County, c.Address, c.Logo, c.VatRegistered, c.MpesaShortcode, c.PreventNegativeStock, c.Industry, c.AccountingLockDate, c.OwnerOversight, mpesaConfirmationUrl = $"{PublicUrl.Of(req)}/api/mpesa/c2b/{c.MpesaCallbackKey}/confirmation" };
        }).RequireAuthorization(Policies.Admin);
        settings.MapGet("/mpesa-prompt", (StkService s) => s.Setup()).RequireAuthorization(Policies.Admin);
        settings.MapPut("/mpesa-prompt", (StkSetupInput input, StkService s) => s.Save(input)).RequireAuthorization(Policies.Admin);
        settings.MapPut("/company", async (CompanyInput input, ErpDb db) =>
        {
            var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
            var pin = (input.KraPin ?? "").Trim().ToUpperInvariant();
            if (pin.Length > 0 && !Etims.SandboxEtimsClient.IsKraPin(pin)) throw new AppException("That KRA PIN doesn't look right (e.g. P051234567X).");
            if ((input.Name ?? "").Trim().Length < 2) throw new AppException("Enter the business name.");
            (c.Name, c.KraPin, c.Phone, c.Email, c.County, c.VatRegistered) = (input.Name!.Trim(), pin, Phones.ToMsisdn(input.Phone) ?? c.Phone, (input.Email ?? "").Trim(), (input.County ?? "").Trim(), input.VatRegistered);
            c.MpesaShortcode = new string((input.MpesaShortcode ?? "").Where(char.IsDigit).ToArray());
            c.PreventNegativeStock = input.PreventNegativeStock ?? c.PreventNegativeStock;
            if (!string.IsNullOrWhiteSpace(input.Industry)) c.Industry = input.Industry.Trim().ToLowerInvariant();
            c.AccountingLockDate = input.AccountingLockDate;
            if (input.Address is not null) c.Address = input.Address.Trim();
            c.OwnerOversight = input.OwnerOversight ?? c.OwnerOversight;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);
        settings.MapPut("/company/logo", async (LogoInput input, ErpDb db) =>
        {
            var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
            c.Logo = Letterhead.CheckLogo(input.DataUrl);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);
        // What every printed invoice, waybill and report carries at the top, for anyone who can print one.
        app_.MapGet("/company/letterhead", async (ErpDb db) =>
        {
            var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
            return new { c.Name, c.KraPin, c.Phone, c.Email, c.County, c.Address, c.Logo, c.VatRegistered };
        });
        settings.MapGet("/users", async (ErpDb db) => await db.Users.OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name, u.Email, u.Phone, Role = u.Role.ToString(), u.Active, u.TwoFactorEnabled, u.MustChangePassword, u.LastSignInAt, u.LockedUntil, u.CreatedAt }).ToListAsync()).RequireAuthorization(Policies.Admin);
        settings.MapPut("/users/{id:guid}/role", async (Guid id, RoleInput input, AuthService s) => { var u = await s.ChangeRole(id, input.Role); return new { u.Id, Role = u.Role.ToString() }; }).RequireAuthorization(Policies.Admin);
        settings.MapPost("/users/{id:guid}/deactivate", async (Guid id, AuthService s) => { var u = await s.SetActive(id, false); return new { u.Id, u.Active }; }).RequireAuthorization(Policies.Admin);
        settings.MapPost("/users/{id:guid}/activate", async (Guid id, AuthService s) => { var u = await s.SetActive(id, true); return new { u.Id, u.Active }; }).RequireAuthorization(Policies.Admin);
        settings.MapPost("/users/{id:guid}/reset-password", (Guid id, AuthService s) => s.ResetPassword(id)).RequireAuthorization(Policies.Admin);
        settings.MapPost("/users", async (UserInput input, AuthService s) =>
        {
            var u = await s.AddUser(input.Name, input.Email, input.Phone ?? "", input.Role, input.Password);
            return Results.Created("", new { u.Id, u.Name, u.Email, Role = u.Role.ToString() });
        }).RequireAuthorization(Policies.Admin);
        app_.MapGet("/audit", async (ErpDb db, string? entity, int? take) =>
        {
            var query = db.AuditEntries.AsQueryable();
            if (!string.IsNullOrEmpty(entity)) query = query.Where(a => a.Entity == entity);
            return await query.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync();
        }).RequireAuthorization(Policies.Finance);

        BuyingEndpoints.Map(app_);
        TradeEndpoints.Map(app_);
        Integrations.IntegrationEndpoints.Map(api, app_);
        PosEndpoints.Map(app_);
        // Kabuor Dine: its guest side is public (a table's QR), so it hangs off /api itself, not the signed-in group.
        Dine.DineEndpoints.Map(api);
        PeopleEndpoints.Map(app_);
        GrowthEndpoints.Map(app_);
        AgentEndpoints.Map(app_);
        SelfEndpoints.Map(app);
        SearchEndpoints.Map(app_);
        ProductionEndpoints.Map(app_);
        DispatchEndpoints.Map(app_);
        DispatchEndpoints.MapDriverLinks(api);
        RecruitmentEndpoints.Map(app_);
        Kabuor.Erp.Finance.FinanceEndpoints.Map(app_);
        Assistant.AssistantEndpoints.Map(app_);
    }

    // Safaricom's exact reply format (PascalCase, unlike the rest of the API).
    static readonly System.Text.Json.JsonSerializerOptions Exact = new();
    static IResult MpesaAck() => Results.Json(new { ResultCode = 0, ResultDesc = "Accepted" }, Exact);

    static async Task ApplyCustomer(ErpDb db, Customer c, CustomerInput input, RequestContext ctx)
    {
        // Credit terms are the owner's or accountant's call, not the salesperson's.
        var termsChange = (input.CreditLimit is { } cl && Math.Max(0, cl) != c.CreditLimit) || (input.PriceListId is { } pid && (pid == Guid.Empty ? null : (Guid?)pid) != c.PriceListId);
        if (termsChange && !ctx.Role.Allows(Policies.Finance)) throw new AppException("Only the owner or the accountant can change a customer's credit limit or price list.", 403);
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter the customer's name.");
        var phone = string.IsNullOrWhiteSpace(input.Phone) ? "" : Phones.ToMsisdn(input.Phone) ?? throw new AppException("That phone number doesn't look right (e.g. 0712 345 678).");
        var pin = (input.KraPin ?? "").Trim().ToUpperInvariant();
        if (pin.Length > 0 && !Etims.SandboxEtimsClient.IsKraPin(pin)) throw new AppException("That KRA PIN doesn't look right (e.g. P051234567X).");
        if (phone.Length > 0 && await db.Customers.AnyAsync(x => x.Phone == phone && x.Id != c.Id))
            throw new AppException("Another customer already has that phone number.", 409);
        (c.Name, c.Phone, c.Email, c.KraPin, c.Address, c.Notes) = (name, phone, (input.Email ?? "").Trim(), pin, (input.Address ?? "").Trim(), (input.Notes ?? "").Trim());
        if (input.Currency is not null) c.Currency = Currencies.Code(input.Currency);
        c.CreditLimit = Math.Max(0, input.CreditLimit ?? c.CreditLimit);
        c.MarketingOptOut = input.MarketingOptOut ?? c.MarketingOptOut;
        // Guid.Empty takes the customer off their price list.
        if (input.PriceListId is { } pl && pl != Guid.Empty && !await db.PriceLists.AnyAsync(x => x.Id == pl)) throw new AppException("That price list no longer exists.");
        if (input.PriceListId is { } chosen) c.PriceListId = chosen == Guid.Empty ? null : chosen;
    }

    /// <summary>A scan must find exactly one item.</summary>
    static async Task UniqueBarcode(ErpDb db, Item i)
    {
        if (i.Barcode.Length > 0 && await db.Items.AnyAsync(x => x.Barcode == i.Barcode && x.Id != i.Id))
            throw new AppException($"Another item already has barcode {i.Barcode}.");
    }

    static void ApplyItem(Item i, ItemInput input)
    {
        if (input.Barcode is { } code) i.Barcode = new string(code.Where(char.IsLetterOrDigit).ToArray());
        if (input.EtimsClassCode is { } cls) i.EtimsClassCode = new string(cls.Where(char.IsDigit).ToArray());
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter the item's name.");
        if (input.Price < 0) throw new AppException("Prices can't be negative.");
        (i.Name, i.Sku, i.Kind, i.Unit, i.Price, i.TaxType, i.Active) = (name, (input.Sku ?? "").Trim(), input.Kind ?? i.Kind, string.IsNullOrWhiteSpace(input.Unit) ? i.Unit : input.Unit.Trim(), input.Price, input.TaxType ?? i.TaxType, input.Active ?? i.Active);
        i.Brand = (input.Brand ?? i.Brand ?? "").Trim();
        if (input.ReorderLevel < 0) throw new AppException("The reorder level can't be negative.");
        i.TrackStock = (input.TrackStock ?? i.TrackStock) && i.Kind == ItemKind.Product; // services aren't counted
        i.ReorderLevel = input.ReorderLevel ?? i.ReorderLevel;
        i.TrackBatches = (input.TrackBatches ?? i.TrackBatches) && i.TrackStock;
        i.Use = input.Use ?? i.Use;
        i.StoresCategory = i.Use == ItemUse.Stores ? (input.StoresCategory ?? i.StoresCategory ?? "").Trim() : "";
    }
}
