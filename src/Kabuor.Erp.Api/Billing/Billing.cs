// Plans in shillings, paid by M-Pesa: an STK prompt on the owner's phone, into Kabuor's own till or paybill.
// A new business pays before anything else opens (Billing:PayToStart, on unless set to false): until the first
// payment arrives, only the plan and payment screens answer. Businesses from before that keep their free trial; once
// paid time (or that trial, plus 7 days' grace) runs out, a company can still see and download everything but can't
// add or change records until it pays: nobody is locked out of their own books.
// Prices are configuration (Billing:Plans:<id>:Monthly, in shillings), so they can change without a release.
// M-Pesa (Kabuor's Daraja app, from the environment or .env): BILLING__MPESA__CONSUMERKEY, __CONSUMERSECRET,
// __PASSKEY, __SHORTCODE (paybill, or a till's store number), __TILL (Buy Goods till; leave empty for a paybill),
// __ENVIRONMENT (sandbox | production) and __CALLBACKSECRET. Without keys, payments are clearly marked test payments,
// and only in Development (or with Billing:AllowTestPayments): a live server never hands out a plan for free.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Billing;

public sealed record Plan(string Id, string Name, long Monthly, int Users, int Staff, string[] Includes);
public sealed record PayInput(string Plan, int Months, string Phone);

public static class Plans
{
    public const int TrialDays = 30, GraceDays = 7;

    public static List<Plan> All(IConfiguration c)
    {
        long Price(string id, long fallback) => (c.GetValue<long?>($"Billing:Plans:{id}:Monthly") ?? fallback) * 100;
        return
        [
            new("starter", "Starter", Price("starter", 1_500), 3, 5, ["Invoices, quotes and the till (POS)", "M-Pesa matching", "Stock and suppliers", "eTIMS", "3 users"]),
            new("business", "Business", Price("business", 4_500), 15, 50, ["Everything in Starter", "Payroll and staff portal (up to 50 staff)", "CRM, fleet and dispatch", "Agents and Msaidizi", "15 users"]),
            new("growth", "Growth", Price("growth", 9_500), 1000, 1000, ["Everything in Business", "Unlimited users and staff", "Several branches and stores", "Priority support on WhatsApp"]),
            new("dine", "Kabuor Dine", Price("dine", 3_000), 10, 10, ["QR menu on every table", "Kitchen and bar screens", "M-Pesa at the table and eTIMS receipts", "Stock and sales in the books", "10 staff logins, one branch"]),
        ];
    }

    /// <summary>The plans a business chooses from: a Dine restaurant sees Kabuor Dine first, with the ERP plans to move
    /// up to; an ERP business sees the ERP plans.</summary>
    public static List<Plan> For(IConfiguration c, string product)
    {
        var all = All(c);
        return product == "dine" ? [.. all.Where(p => p.Id == "dine"), .. all.Where(p => p.Id != "dine")] : all.Where(p => p.Id != "dine").ToList();
    }

    /// <summary>New businesses pay before they start. On unless Billing:PayToStart is false.</summary>
    public static bool PayToStart(IConfiguration c) => c.GetValue("Billing:PayToStart", true);

    /// <summary>A year paid at once is charged ten months.</summary>
    public static long Amount(Plan p, int months) => months >= 12 ? p.Monthly * 10 * (months / 12) + p.Monthly * (months % 12) : p.Monthly * months;

    public static DateTimeOffset TrialEnds(Company c) => c.TrialEndsAt ?? c.CreatedAt.AddDays(TrialDays);

    /// <summary>Read-only once the trial and grace are over and nothing is paid up.</summary>
    public static bool Locked(Company c, DateTimeOffset now) =>
        !(c.PaidUntil > now) && now > TrialEnds(c).AddDays(GraceDays);
}

/// <summary>Kabuor's own Daraja app, the one subscriptions are paid into (never a customer's).</summary>
public sealed class BillingMpesa(IConfiguration config)
{
    string Get(string key) => (config[$"Billing:Mpesa:{key}"] ?? "").Trim();
    static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    public string ConsumerKey => Get("ConsumerKey");
    public string ConsumerSecret => Get("ConsumerSecret");
    public string Passkey => Get("Passkey");
    public string Environment => Get("Environment") == "production" ? "production" : "sandbox";
    /// <summary>A Buy Goods till number. Older settings said the same with Type=CustomerBuyGoodsOnline and PartyB.</summary>
    public string Till => Digits(Get("Till")) is { Length: > 0 } t ? t : Get("Type") == "CustomerBuyGoodsOnline" ? Digits(Get("PartyB")) : "";
    /// <summary>The paybill, or for a till its store (head office) number; a till with no store number given uses itself.</summary>
    public string Shortcode => Digits(Get("Shortcode")) is { Length: > 0 } s ? s : Till;
    public string PartyB => Till.Length > 0 ? Till : Digits(Get("PartyB")) is { Length: > 0 } b ? b : Shortcode;
    public string CallbackSecret => Get("CallbackSecret");

    public bool Live => ConsumerKey.Length > 0 && ConsumerSecret.Length > 0 && Passkey.Length > 0 && Shortcode.Length > 0;

    /// <summary>The address Safaricom calls with the result: Billing:Mpesa:CallbackUrl if set (it must be public HTTPS),
    /// otherwise this server's own address.</summary>
    public string CallbackUrl(string requestBase) =>
        $"{(Get("CallbackUrl") is { Length: > 0 } b ? b : requestBase).TrimEnd('/')}/api/billing/mpesa/{CallbackSecret}";

    public bool SecretMatches(string? secret) =>
        CallbackSecret.Length >= 16 && secret is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(CallbackSecret));
}

public sealed class BillingService(ErpDb db, IConfiguration config, BillingMpesa mpesa, IHostEnvironment env, IHttpClientFactory http, RequestContext ctx, ILogger<BillingService> log)
{
    async Task<Company> Company() => await db.Companies.SingleAsync(c => c.Id == db.CompanyId);

    /// <summary>Test payments (no money moves) only where nobody can mistake them for real ones.</summary>
    bool TestPaymentsAllowed => !mpesa.Live && (env.IsDevelopment() || config.GetValue("Billing:AllowTestPayments", false));

    public async Task<object> Status()
    {
        var c = await Company();
        var now = DateTimeOffset.UtcNow;
        var plans = Plans.For(config, c.Product);
        var trialEnds = Plans.TrialEnds(c);
        var users = await db.Users.CountAsync(u => u.Active && u.Role != Role.Employee);
        var staff = await db.Employees.CountAsync(e => e.EndDate == null || e.EndDate >= Clock.Today);
        var current = plans.FirstOrDefault(p => p.Id == c.Plan);
        var paid = c.PaidUntil > now;
        return new
        {
            plan = c.Plan, planName = current?.Name ?? (c.AwaitingFirstPayment ? "Not started" : "Free trial"), paidUntil = c.PaidUntil, trialEnds,
            daysLeft = c.AwaitingFirstPayment ? 0 : (int)Math.Ceiling(((paid ? c.PaidUntil!.Value : trialEnds) - now).TotalDays),
            onTrial = !paid && !c.AwaitingFirstPayment, locked = Plans.Locked(c, now), graceDays = Plans.GraceDays,
            awaitingFirstPayment = c.AwaitingFirstPayment, phone = c.Phone,
            users, userLimit = current?.Users ?? 1000, staff, staffLimit = current?.Staff ?? 1000,
            plans = plans.Select(p => new { p.Id, p.Name, p.Monthly, yearly = Plans.Amount(p, 12), p.Users, p.Staff, p.Includes }),
            payments = (await db.BillingPayments.ToListAsync()).OrderByDescending(p => p.CreatedAt).Take(12).Select(View),
            testMode = !mpesa.Live, canPay = mpesa.Live || TestPaymentsAllowed,
            payTo = mpesa.Live ? (mpesa.Till.Length > 0 ? $"Till {mpesa.Till}" : $"Paybill {mpesa.Shortcode}") : "",
        };
    }

    static object View(BillingPayment p) => new { p.Id, p.Plan, p.Months, p.Amount, p.Phone, Status = p.Status.ToString(), p.Receipt, p.Test, p.Note, p.CreatedAt, p.PaidAt };

    public async Task<object> Pay(PayInput input, string requestBase)
    {
        var plan = Plans.All(config).FirstOrDefault(p => p.Id == input.Plan) ?? throw new AppException("Choose a plan.");
        if (input.Months is not (1 or 3 or 6 or 12)) throw new AppException("Pay for 1, 3, 6 or 12 months.");
        var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter the Safaricom number to send the M-Pesa prompt to (e.g. 0712 345 678).");
        if (!mpesa.Live && !TestPaymentsAllowed)
            throw new AppException("M-Pesa payments aren't switched on for this server yet. Call or WhatsApp Kabuor and we'll start your account.", 503);
        var users = await db.Users.CountAsync(u => u.Active && u.Role != Role.Employee);
        if (users > plan.Users) throw new AppException($"You have {users} users; {plan.Name} allows {plan.Users}. Choose a bigger plan or remove users first.");
        // One prompt at a time: a second one while the first is still on the phone only confuses (and can't be used to
        // flood someone's phone with prompts).
        var since = DateTimeOffset.UtcNow.AddSeconds(-60);
        var recent = (await db.BillingPayments.Where(p => p.Status == BillingStatus.Pending).Select(p => p.CreatedAt).ToListAsync()).Any(t => t > since);
        if (recent) throw new AppException("A prompt was just sent. Enter your M-Pesa PIN on that phone, or wait a minute to send another.", 429);

        var amount = Plans.Amount(plan, input.Months);
        var p = new BillingPayment { Plan = plan.Id, Months = input.Months, Amount = amount, Phone = phone, Test = !mpesa.Live, RequestedBy = ctx.Actor };
        db.BillingPayments.Add(p);
        if (!mpesa.Live)
        {
            // Development without M-Pesa keys: a test payment, marked as such, so the flow can be tried end to end.
            await Settle(p, $"TEST{DateTime.UtcNow:HHmmss}");
            return View(p);
        }
        p.CheckoutId = await Daraja.Send(http.CreateClient("daraja"), new Daraja.Push(mpesa.Environment, mpesa.ConsumerKey, mpesa.ConsumerSecret, mpesa.Shortcode, mpesa.Passkey,
            mpesa.Till.Length > 0, mpesa.PartyB, phone, amount / 100, mpesa.CallbackUrl(requestBase), "Kabuor ERP", $"{plan.Name} {input.Months}m"), log);
        await db.SaveChangesAsync();
        return View(p);
    }

    /// <summary>Where a payment stands. While it waits, Safaricom is asked directly (every 10 seconds at most), so the
    /// account opens even when its callback can't reach this server (a local machine, a firewall).</summary>
    public async Task<object> Check(Guid id)
    {
        var p = await db.BillingPayments.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        var age = DateTimeOffset.UtcNow - p.CreatedAt;
        if (p.Status == BillingStatus.Pending && mpesa.Live && p.CheckoutId.Length > 0 && age > TimeSpan.FromSeconds(10)
            && !(p.CheckedAt > DateTimeOffset.UtcNow.AddSeconds(-10)))
        {
            p.CheckedAt = DateTimeOffset.UtcNow;
            var (done, paid, note) = await Query(p.CheckoutId);
            if (done && paid) await Settle(p, p.Receipt);
            else if (done) (p.Status, p.Note) = (BillingStatus.Failed, note);
            await db.SaveChangesAsync();
        }
        // Nobody answered for three minutes: the prompt has expired on the phone.
        if (p.Status == BillingStatus.Pending && age > TimeSpan.FromMinutes(3))
        {
            (p.Status, p.Note) = (BillingStatus.Failed, "The prompt expired without a PIN.");
            await db.SaveChangesAsync();
        }
        var c = await Company();
        return new { payment = View(p), awaitingFirstPayment = c.AwaitingFirstPayment, plan = c.Plan, paidUntil = c.PaidUntil };
    }

    /// <summary>Daraja's STK query: (finished?, paid?, what Safaricom said). Still on the phone = not finished.</summary>
    async Task<(bool Done, bool Paid, string Note)> Query(string checkoutId)
    {
        try
        {
            var client = http.CreateClient("daraja");
            var token = await Token(client);
            var stamp = DateTime.UtcNow.AddHours(3).ToString("yyyyMMddHHmmss");
            var body = new JsonObject
            {
                ["BusinessShortCode"] = mpesa.Shortcode,
                ["Password"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(mpesa.Shortcode + mpesa.Passkey + stamp)),
                ["Timestamp"] = stamp,
                ["CheckoutRequestID"] = checkoutId,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/mpesa/stkpushquery/v1/query") { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await client.SendAsync(req);
            var json = await res.Content.ReadFromJsonAsync<JsonObject>();
            var code = json?["ResultCode"]?.ToString();
            if (code is null) return (false, false, ""); // "being processed": the person hasn't answered yet
            return (true, code == "0", json?["ResultDesc"]?.ToString() ?? "Not paid");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning(e, "STK query failed");
            return (false, false, "");
        }
    }

    string Base => mpesa.Environment == "production" ? "https://api.safaricom.co.ke" : "https://sandbox.safaricom.co.ke";

    async Task<string> Token(HttpClient client)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mpesa.ConsumerKey}:{mpesa.ConsumerSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/oauth/v1/generate?grant_type=client_credentials");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var res = await client.SendAsync(req);
        var json = await res.Content.ReadFromJsonAsync<JsonObject>();
        return json?["access_token"]?.GetValue<string>() ?? throw new AppException("M-Pesa sign-in failed.", 502);
    }

    /// <summary>Paid: the plan runs from today, or from the end of what's already paid, and the account opens.</summary>
    async Task Settle(BillingPayment p, string receipt)
    {
        var c = await db.Companies.SingleAsync(x => x.Id == (p.CompanyId == Guid.Empty ? db.CompanyId : p.CompanyId));
        var from = c.PaidUntil > DateTimeOffset.UtcNow ? c.PaidUntil!.Value : DateTimeOffset.UtcNow;
        (c.Plan, c.PaidUntil, c.AwaitingFirstPayment) = (p.Plan, from.AddMonths(p.Months), false);
        (p.Status, p.Receipt, p.PaidAt, p.Note) = (BillingStatus.Paid, receipt, DateTimeOffset.UtcNow, "");
        await db.SaveChangesAsync();
    }

    /// <summary>Safaricom's answer to the prompt (no sign-in; the URL carries the secret). Paid in full, or not at all.</summary>
    public async Task Callback(JsonObject body)
    {
        var (checkout, paid, receipt, amount, _, note) = Daraja.Read(body);
        if (checkout.Length == 0) return;
        var p = await db.BillingPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.CheckoutId == checkout);
        if (p is null) return;
        ctx.CompanyId = p.CompanyId;
        // Settled already by a status check: keep the receipt Safaricom sends now.
        if (p.Status == BillingStatus.Paid && paid && p.Receipt.Length == 0)
        {
            p.Receipt = receipt;
            await db.SaveChangesAsync();
            return;
        }
        if (p.Status != BillingStatus.Pending) return;
        if (paid && amount >= p.Amount) await Settle(p, receipt);
        else
        {
            (p.Status, p.Note) = (BillingStatus.Failed, paid ? $"Received {Money.Ksh(amount)}, not {Money.Ksh(p.Amount)}: call Kabuor to sort it out ({receipt})." : note.Length > 0 ? note : "Not paid");
            if (paid) log.LogWarning("Subscription payment {Receipt} for {Company} was {Got}, expected {Want}", receipt, p.CompanyId, amount, p.Amount);
            await db.SaveChangesAsync();
        }
    }
}
