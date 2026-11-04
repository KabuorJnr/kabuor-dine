// Connecting other systems: an online shop sending its orders in, an accounting firm reading the books, a
// delivery app told when an invoice is paid.
//  - API keys: made by the owner, shown once, stored only as a hash. Sent as the X-Api-Key header.
//  - /api/v1: a small, stable surface (catalogue, orders, order status). An order with the same externalRef
//    is kept once, so a shop can safely retry.
//  - Webhooks: we POST events (payment.received, invoice.paid) to the owner's URL, signed with HMAC-SHA256 of
//    the body in X-Kabuor-Signature, retried with growing gaps for about a day.
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Integrations;

public sealed class ApiKey : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    /// <summary>The first characters, so the owner can tell keys apart ("kerp_3f9a…").</summary>
    public string Prefix { get; set; } = "";
    public string Hash { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class WebhookEndpoint : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Url { get; set; }
    /// <summary>Comma-separated event names, or "*".</summary>
    public string Events { get; set; } = "*";
    public string Secret { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum DeliveryStatus { Pending, Delivered, Failed }

public sealed class WebhookDelivery : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid EndpointId { get; set; }
    public required string Event { get; set; }
    public required string Body { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAt { get; set; } = DateTimeOffset.UtcNow;
    public int LastStatusCode { get; set; }
    public string LastError { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ApiKeyInput(string Name);
public sealed record WebhookInput(string Url, string? Events);
public sealed record OrderLineInput(Guid? ItemId, string? Sku, string? Description, decimal Quantity, long? UnitPrice);
public sealed record OrderInput(string ExternalRef, string? CustomerName, string? CustomerPhone, string? CustomerEmail, List<OrderLineInput> Lines, string? MpesaReceipt, long? AmountPaid, string? Notes);

public static class ApiKeys
{
    public static string HashOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

public sealed class WebhookService(ErpDb db)
{
    public static readonly string[] EventNames = ["payment.received", "invoice.paid"];

    /// <summary>Queues an event for every endpoint that wants it; the worker sends it.</summary>
    public async Task Raise(string name, object data)
    {
        var endpoints = await db.WebhookEndpoints.Where(e => e.Active).ToListAsync();
        var wanted = endpoints.Where(e => e.Events == "*" || e.Events.Split(',').Contains(name)).ToList();
        if (wanted.Count == 0) return;
        var body = JsonSerializer.Serialize(new { id = Guid.NewGuid(), type = name, createdAt = DateTimeOffset.UtcNow, data }, JsonSerializerOptions.Web);
        foreach (var e in wanted) db.WebhookDeliveries.Add(new WebhookDelivery { EndpointId = e.Id, Event = name, Body = body });
        await db.SaveChangesAsync();
    }

    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
}

/// <summary>Sends queued webhook deliveries; a failure waits 1, 5, 30 minutes, 2, 6 then 12 hours before giving up.</summary>
public sealed class WebhookWorker(IServiceScopeFactory scopes, IHttpClientFactory http, IConfiguration config, ILogger<WebhookWorker> log) : BackgroundService
{
    static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (config.GetValue("Webhooks:Disabled", false)) return;
        while (!stop.IsCancellationRequested)
        {
            try { await SendDue(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Webhook tick failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stop);
        }
    }

    public async Task SendDue(CancellationToken stop = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDb>();
        var now = DateTimeOffset.UtcNow;
        var pending = (await db.WebhookDeliveries.IgnoreQueryFilters().Where(d => d.Status == DeliveryStatus.Pending).Take(200).ToListAsync(stop))
            .Where(d => d.NextAt <= now).Take(50).ToList();
        if (pending.Count == 0) return;
        var ids = pending.Select(d => d.EndpointId).Distinct().ToList();
        var endpoints = await db.WebhookEndpoints.IgnoreQueryFilters().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, stop);
        var client = http.CreateClient("webhooks");
        foreach (var d in pending)
        {
            if (!endpoints.TryGetValue(d.EndpointId, out var ep) || !ep.Active) { d.Status = DeliveryStatus.Failed; d.LastError = "Endpoint removed"; continue; }
            d.Attempts++;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ep.Url) { Content = new StringContent(d.Body, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Kabuor-Event", d.Event);
                req.Headers.Add("X-Kabuor-Delivery", d.Id.ToString());
                req.Headers.Add("X-Kabuor-Signature", WebhookService.Sign(ep.Secret, d.Body));
                using var res = await client.SendAsync(req, stop);
                d.LastStatusCode = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) { d.Status = DeliveryStatus.Delivered; d.LastError = ""; continue; }
                d.LastError = $"HTTP {(int)res.StatusCode}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                d.LastError = e.Message.Length > 200 ? e.Message[..200] : e.Message;
            }
            if (d.Attempts > Backoff.Length) d.Status = DeliveryStatus.Failed;
            else d.NextAt = now + Backoff[d.Attempts - 1];
        }
        await db.SaveChangesAsync(stop);
    }
}

public static class IntegrationEndpoints
{
    public static void Map(RouteGroupBuilder api, RouteGroupBuilder app)
    {
        // ---------- owner settings ----------
        var keys = app.MapGroup("/settings/api-keys").RequireAuthorization(Policies.Admin);
        keys.MapGet("", async (ErpDb db) => await db.ApiKeys.Where(k => k.RevokedAt == null).OrderBy(k => k.Name).Select(k => new { k.Id, k.Name, k.Prefix, k.CreatedAt, k.CreatedBy, k.LastUsedAt }).ToListAsync());
        keys.MapPost("", async (ApiKeyInput input, ErpDb db, RequestContext ctx) =>
        {
            var name = (input.Name ?? "").Trim();
            if (name.Length < 2) throw new AppException("Name the key after what will use it (e.g. Online shop).");
            var key = "kerp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var k = new ApiKey { Name = name, Prefix = key[..9], Hash = ApiKeys.HashOf(key), CreatedBy = ctx.Actor };
            db.ApiKeys.Add(k);
            await db.SaveChangesAsync();
            return Results.Created("", new { k.Id, k.Name, k.Prefix, key }); // the only time the full key is shown
        });
        keys.MapDelete("/{id:guid}", async (Guid id, ErpDb db) =>
        {
            var k = await db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            k.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var hooks = app.MapGroup("/settings/webhooks").RequireAuthorization(Policies.Admin);
        hooks.MapGet("", async (ErpDb db) =>
        {
            var list = await db.WebhookEndpoints.Where(e => e.Active).ToListAsync();
            var recent = await db.WebhookDeliveries.Where(d => list.Select(e => e.Id).Contains(d.EndpointId)).ToListAsync();
            return list.Select(e => new
            {
                e.Id, e.Url, e.Events, e.Secret,
                delivered = recent.Count(d => d.EndpointId == e.Id && d.Status == DeliveryStatus.Delivered),
                failing = recent.Count(d => d.EndpointId == e.Id && d.Status != DeliveryStatus.Delivered && d.Attempts > 0),
                lastError = recent.Where(d => d.EndpointId == e.Id && d.LastError != "").OrderByDescending(d => d.CreatedAt).Select(d => d.LastError).FirstOrDefault(),
            });
        });
        hooks.MapPost("", async (WebhookInput input, ErpDb db) =>
        {
            if (!Uri.TryCreate((input.Url ?? "").Trim(), UriKind.Absolute, out var uri)) throw new AppException("Enter the full https:// address that should receive events.");
            if (OutboundGuard.Problem(uri) is { } problem) throw new AppException(problem);
            var events = string.IsNullOrWhiteSpace(input.Events) ? "*" : string.Join(',', input.Events.Split(',').Select(x => x.Trim()).Where(WebhookService.EventNames.Contains));
            if (events.Length == 0) throw new AppException($"Choose events from: {string.Join(", ", WebhookService.EventNames)}.");
            var e = new WebhookEndpoint { Url = uri.ToString(), Events = events, Secret = "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() };
            db.WebhookEndpoints.Add(e);
            await db.SaveChangesAsync();
            return Results.Created("", new { e.Id, e.Url, e.Events, e.Secret });
        });
        hooks.MapDelete("/{id:guid}", async (Guid id, ErpDb db) =>
        {
            var e = await db.WebhookEndpoints.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            e.Active = false;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- the public API ----------
        var v1 = api.MapGroup("/v1").AddEndpointFilter(async (inv, next) =>
        {
            var http = inv.HttpContext;
            var key = http.Request.Headers["X-Api-Key"].ToString();
            var db = http.RequestServices.GetRequiredService<ErpDb>();
            var hash = ApiKeys.HashOf(key);
            var k = key.StartsWith("kerp_") ? await db.ApiKeys.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Hash == hash && x.RevokedAt == null) : null;
            if (k is null) return Results.Json(new { error = "Send a valid API key in the X-Api-Key header." }, statusCode: 401);
            var ctx = http.RequestServices.GetRequiredService<RequestContext>();
            (ctx.CompanyId, ctx.Actor, ctx.Role) = (k.CompanyId, $"API · {k.Name}", Role.Sales);
            if (http.Request.Method != "GET")
            {
                var c = await db.Companies.SingleAsync(x => x.Id == k.CompanyId);
                if (Billing.Plans.Locked(c, DateTimeOffset.UtcNow)) return Results.Json(new { error = "This business's Kabuor ERP trial has ended; orders can't be added until it chooses a plan." }, statusCode: 402);
            }
            if (k.LastUsedAt is null || DateTimeOffset.UtcNow - k.LastUsedAt > TimeSpan.FromMinutes(5))
            {
                k.LastUsedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            return await next(inv);
        });

        v1.MapGet("/items", async (ErpDb db) =>
        {
            var items = await db.Items.Where(i => i.Active).OrderBy(i => i.Name).ToListAsync();
            var levels = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
            return items.Select(i => new { i.Id, i.Name, sku = i.Sku, i.Unit, price = i.Price, taxType = i.TaxType.ToString(), inStock = i.TrackStock ? levels.GetValueOrDefault(i.Id) : (decimal?)null });
        });

        v1.MapPost("/orders", async (OrderInput input, ErpDb db, SalesService sales, PaymentService payments, Numbers numbers) =>
        {
            var reference = (input.ExternalRef ?? "").Trim();
            if (reference.Length is 0 or > 64) throw new AppException("Give each order your own reference (externalRef, up to 64 characters).");
            var clientRef = $"api:{reference}";
            if (await db.SalesDocs.Where(d => d.ClientRef == clientRef).Select(d => d.Id).FirstOrDefaultAsync() is var existing && existing != Guid.Empty)
                return Results.Ok(await Status(db, existing, true));
            if (input.Lines is not { Count: > 0 }) throw new AppException("An order needs at least one line.");

            var phone = Phones.ToMsisdn(input.CustomerPhone);
            var customer = phone is null ? null : await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone);
            if (customer is null)
            {
                var name = (input.CustomerName ?? "").Trim();
                customer = new Customer { Number = await numbers.Next("C"), Name = name.Length >= 2 ? name : "Online customer", Phone = phone ?? "", Email = (input.CustomerEmail ?? "").Trim() };
                db.Customers.Add(customer);
                await db.SaveChangesAsync();
            }
            var skus = input.Lines.Where(l => l.ItemId is null && !string.IsNullOrWhiteSpace(l.Sku)).Select(l => l.Sku!.Trim()).ToList();
            var bySku = await db.Items.Where(i => skus.Contains(i.Sku)).ToDictionaryAsync(i => i.Sku, i => i.Id);
            var lines = input.Lines.Select(l => new LineInput(l.ItemId ?? (l.Sku is { } s && bySku.TryGetValue(s.Trim(), out var id) ? id : null), l.Description, l.Quantity, l.UnitPrice, null)).ToList();

            await using var tx = await db.Database.BeginTransactionAsync();
            var doc = await sales.Create(new SalesDocInput(SalesDocKind.Invoice, customer.Id, null, 0, (input.Notes ?? "").Trim(), lines));
            doc.ClientRef = clientRef;
            await sales.Issue(doc.Id, overLimit: input.MpesaReceipt is not null);
            var receipt = (input.MpesaReceipt ?? "").Trim().ToUpperInvariant();
            if (receipt.Length > 0)
            {
                // The shop already took the money by M-Pesa: use the payment Safaricom told us about, or record it.
                var paid = await db.Payments.SingleOrDefaultAsync(p => p.Reference == receipt);
                if (paid is not null) await payments.Allocate(paid.Id, [new AllocationInput(doc.Id, Math.Min(paid.Unallocated, doc.Total))], "API");
                else await payments.Record(new PaymentInput(PaymentMethod.Mpesa, input.AmountPaid ?? doc.Total, DateTimeOffset.UtcNow, receipt, phone, customer.Name, doc.Number, customer.Id, doc.Id), "API", PaymentSource.ConnectedSystem);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created($"/api/v1/orders/{Uri.EscapeDataString(reference)}", await Status(db, doc.Id, false));
        });

        v1.MapGet("/orders/{externalRef}", async (string externalRef, ErpDb db) =>
        {
            var id = await db.SalesDocs.Where(d => d.ClientRef == $"api:{externalRef}").Select(d => d.Id).FirstOrDefaultAsync();
            return id == Guid.Empty ? Results.NotFound(new { error = "No order with that reference." }) : Results.Ok(await Status(db, id, false));
        });
    }

    static async Task<object> Status(ErpDb db, Guid id, bool already)
    {
        var d = await db.SalesDocs.Include(x => x.Customer).SingleAsync(x => x.Id == id);
        return new { externalRef = d.ClientRef.StartsWith("api:") ? d.ClientRef[4..] : d.ClientRef, invoice = d.Number, status = d.Status.ToString(), total = d.Total, paid = d.Paid, balance = d.Total - d.Paid, etims = d.EtimsStatus.ToString(), customer = d.Customer!.Name, alreadyRecorded = already };
    }
}
