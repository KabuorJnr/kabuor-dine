using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record PosOpenSessionInput(long OpeningFloat, string? Notes);
public sealed record PosCloseSessionInput(long ClosingCount, string? Notes);

public sealed record PosCheckoutLine(Guid ItemId, string? Description, decimal Quantity, long? UnitPrice, TaxType? TaxType);
public sealed record PosCheckoutInput(
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerPhone,
    Guid? LocationId,
    PaymentMethod PaymentMethod,
    long AmountPaid,
    long ChangeGiven,
    string? Reference,
    List<PosCheckoutLine> Lines,
    // Offline tills: a key made on the device (so a sale sent twice is kept once) and when it really happened.
    string? ClientRef = null,
    DateTimeOffset? SoldAt = null
);

public static class PosEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var pos = api.MapGroup("/pos").RequireAuthorization(Policies.Pos);

        // Core data to power the POS terminal
        pos.MapGet("/data", async (ErpDb db, RequestContext ctx, InventoryService stock, Guid? locationId) =>
        {
            var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
            var locations = await db.Locations.Where(l => l.Active).OrderByDescending(l => l.IsDefault).ThenBy(l => l.Name).ToListAsync();
            var selectedLocation = (locationId is { } lid ? locations.FirstOrDefault(l => l.Id == lid) : null)
                ?? locations.FirstOrDefault(l => l.IsDefault)
                ?? locations.FirstOrDefault()
                ?? new Location { Name = "Main Store" };

            // Fetch products that can be sold
            var items = await db.Items.Where(i => i.Active && i.Use == ItemUse.Sale).OrderBy(i => i.Name).ToListAsync();
            var stockLevels = await db.StockLevels.Where(s => s.LocationId == selectedLocation.Id).ToDictionaryAsync(s => s.ItemId, s => s.Quantity);
            var allStockLevels = await db.StockLevels.GroupBy(s => s.ItemId).Select(g => new { ItemId = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToDictionaryAsync(s => s.ItemId, s => s.Quantity);
            // Expired batches are still on the shelf but can't be sold: the till doesn't count them.
            var today = Clock.Today;
            var expired = (await db.StockBatches.Where(b => b.LocationId == selectedLocation.Id && b.Quantity > 0 && b.Expiry != null).ToListAsync())
                .Where(b => b.Expiry < today).GroupBy(b => b.ItemId).ToDictionary(g => g.Key, g => g.Sum(b => b.Quantity));

            var catalog = items.Select(i => new
            {
                i.Id,
                i.Name,
                code = i.Sku,
                barcode = i.Barcode,
                i.Unit,
                i.Price,
                i.TaxType,
                i.TrackStock,
                onHand = i.TrackStock ? Math.Max(0, stockLevels.GetValueOrDefault(i.Id, 0m) - expired.GetValueOrDefault(i.Id, 0m)) : 999m,
                expired = expired.GetValueOrDefault(i.Id, 0m),
                totalOnHand = i.TrackStock ? allStockLevels.GetValueOrDefault(i.Id, 0m) : 999m,
            }).ToList();

            var customers = await db.Customers.OrderBy(c => c.Name).Take(50)
                .Select(c => new { c.Id, c.Number, c.Name, c.Phone, c.KraPin }).ToListAsync();

            // Active session for this user or company
            var activeSession = await db.PosSessions
                .Where(s => s.Status == PosSessionStatus.Open && (s.CashierId == ctx.UserId || s.CashierId == Guid.Empty))
                .OrderByDescending(s => s.OpenedAt)
                .FirstOrDefaultAsync();

            return new
            {
                company = new { company.Name, company.KraPin, company.Phone, company.Email, company.VatRegistered, company.MpesaShortcode },
                location = selectedLocation,
                locations,
                items = catalog,
                customers,
                session = activeSession,
                cashier = new { id = ctx.UserId, name = ctx.Actor },
            };
        });

        // Open cash register session
        pos.MapPost("/session/open", async (PosOpenSessionInput input, ErpDb db, RequestContext ctx, Numbers numbers) =>
        {
            var open = await db.PosSessions.FirstOrDefaultAsync(s => s.Status == PosSessionStatus.Open && s.CashierId == ctx.UserId);
            if (open is not null) return open;

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == ctx.UserId);
            var session = new PosSession
            {
                CashierId = ctx.UserId ?? Guid.Empty,
                CashierName = user?.Name ?? ctx.Actor,
                SessionNumber = await numbers.Next("POS"),
                Status = PosSessionStatus.Open,
                OpeningFloat = Math.Max(0, input.OpeningFloat),
                Notes = (input.Notes ?? "").Trim(),
                OpenedAt = DateTimeOffset.UtcNow,
            };
            db.PosSessions.Add(session);
            await db.SaveChangesAsync();
            return session;
        });

        // Close cash register session
        pos.MapPost("/session/close", async (PosCloseSessionInput input, ErpDb db, RequestContext ctx) =>
        {
            var session = await db.PosSessions
                .Where(s => s.Status == PosSessionStatus.Open && (s.CashierId == ctx.UserId || s.CashierId == Guid.Empty))
                .OrderByDescending(s => s.OpenedAt)
                .FirstOrDefaultAsync() ?? throw new AppException("No open POS register session found.");

            session.Status = PosSessionStatus.Closed;
            session.ClosingCount = input.ClosingCount;
            session.ClosedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(input.Notes))
                session.Notes = string.IsNullOrWhiteSpace(session.Notes) ? input.Notes.Trim() : $"{session.Notes}\n{input.Notes.Trim()}";

            await db.SaveChangesAsync();
            return session;
        });

        // Current active session
        pos.MapGet("/session", async (ErpDb db, RequestContext ctx) =>
        {
            var session = await db.PosSessions
                .Where(s => s.Status == PosSessionStatus.Open && (s.CashierId == ctx.UserId || s.CashierId == Guid.Empty))
                .OrderByDescending(s => s.OpenedAt)
                .FirstOrDefaultAsync();
            return session;
        });

        // High-speed POS checkout
        pos.MapPost("/checkout", async (PosCheckoutInput input, ErpDb db, SalesService sales, PaymentService payments, Numbers numbers, RequestContext ctx) =>
        {
            if (input.Lines is not { Count: > 0 }) throw new AppException("Add at least one item to checkout.");
            var clientRef = (input.ClientRef ?? "").Trim();
            if (clientRef.Length > 64) throw new AppException("That sale reference is too long.");
            if (clientRef.Length > 0 && await db.SalesDocs.Where(d => d.ClientRef == clientRef).Select(d => new { d.Id, d.Number }).FirstOrDefaultAsync() is { } already)
                return Results.Ok(new { invoiceId = already.Id, receiptNumber = already.Number, alreadyRecorded = true });
            // A sale made offline keeps its own day (up to a week back), so the day's takings stay right.
            var soldAt = input.SoldAt is { } at && at <= DateTimeOffset.UtcNow.AddMinutes(5) && at >= DateTimeOffset.UtcNow.AddDays(-7) ? at : DateTimeOffset.UtcNow;
            var soldOn = DateOnly.FromDateTime(soldAt.ToOffset(TimeSpan.FromHours(3)).DateTime);
            // The till must be open, so every shilling lands in a drawer count at closing.
            var session = await db.PosSessions
                .Where(s => s.Status == PosSessionStatus.Open && (s.CashierId == ctx.UserId || s.CashierId == Guid.Empty))
                .OrderByDescending(s => s.OpenedAt)
                .FirstOrDefaultAsync() ?? throw new AppException("Open the till first (enter the float in the drawer).");
            // An M-Pesa payment Safaricom already told us about: use it, don't record the same money twice.
            var mpesaCode = (input.Reference ?? "").Trim().ToUpperInvariant();
            var existing = input.PaymentMethod == PaymentMethod.Mpesa && mpesaCode.Length > 0
                ? await db.Payments.SingleOrDefaultAsync(x => x.Reference == mpesaCode)
                : null;
            // Everything below is one sale: if any step fails, nothing is kept (no invoice without its payment).
            await using var tx = await db.Database.BeginTransactionAsync();

            // 1. Resolve Customer (defaults to Walk-in Cash Customer)
            Customer? customer = null;
            if (input.CustomerId is { } cid && cid != Guid.Empty)
            {
                customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == cid);
            }
            if (customer is null && !string.IsNullOrWhiteSpace(input.CustomerPhone))
            {
                var phone = Phones.ToMsisdn(input.CustomerPhone);
                if (phone is not null)
                    customer = await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone);
            }
            if (customer is null)
            {
                var walkInName = string.IsNullOrWhiteSpace(input.CustomerName) ? "Walk-in Customer" : input.CustomerName.Trim();
                customer = await db.Customers.FirstOrDefaultAsync(c => c.Name == walkInName);
                if (customer is null)
                {
                    customer = new Customer
                    {
                        Number = await numbers.Next("C"),
                        Name = walkInName,
                        Phone = Phones.ToMsisdn(input.CustomerPhone) ?? "",
                    };
                    db.Customers.Add(customer);
                    await db.SaveChangesAsync();
                }
            }

            // 2. Create Invoice
            var lines = input.Lines.Select(l => new LineInput(l.ItemId, l.Description, l.Quantity, l.UnitPrice, l.TaxType)).ToList();
            var docInput = new SalesDocInput(
                Kind: SalesDocKind.Invoice,
                CustomerId: customer.Id,
                IssueDate: soldOn,
                DueInDays: 0,
                Notes: $"POS sale by {ctx.Actor}",
                Lines: lines,
                LocationId: input.LocationId
            );
            var doc = await sales.Create(docInput);
            doc.ClientRef = clientRef;

            // 3. Issue Invoice (posts ledger journal entry, deducts store stock, signs with eTIMS)
            doc = await sales.Issue(doc.Id, overLimit: true); // paid at the till, so it adds no debt

            if (input.PaymentMethod == PaymentMethod.Cash && input.AmountPaid > 0 && input.AmountPaid < doc.Total)
                throw new AppException($"The customer gave {input.AmountPaid / 100m:N0}; the sale is {doc.Total / 100m:N0}.");

            // 4. Record Payment and allocate to the invoice
            var refCode = string.IsNullOrWhiteSpace(input.Reference) ? $"{doc.Number}-POS" : input.Reference.Trim().ToUpperInvariant();
            var paymentAmount = doc.Total; // Invoice is settled
            if (existing is not null)
            {
                if (existing.Unallocated < doc.Total)
                    throw new AppException($"M-Pesa {existing.Reference} has KSh {existing.Unallocated / 100m:N0} left to use; this sale is KSh {doc.Total / 100m:N0}.");
                await payments.Allocate(existing.Id, [new AllocationInput(doc.Id, doc.Total)], $"till: {ctx.Actor}");
            }
            else
            await payments.Record(new PaymentInput(
                Method: input.PaymentMethod,
                Amount: paymentAmount,
                ReceivedAt: DateTimeOffset.UtcNow,
                Reference: refCode,
                PayerPhone: customer.Phone,
                PayerName: customer.Name,
                AccountRef: doc.Number,
                CustomerId: customer.Id,
                InvoiceId: doc.Id
            ), ctx.Actor, PaymentSource.Till);

            // 5. Update the till's tallies
            if (input.PaymentMethod == PaymentMethod.Cash) session.CashSales += doc.Total;
            else if (input.PaymentMethod == PaymentMethod.Mpesa) session.MpesaSales += doc.Total;
            else session.BankSales += doc.Total;
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // 6. Build rich receipt object
            var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
            var fullDoc = await sales.Get(doc.Id);

            var receipt = new
            {
                invoiceId = doc.Id,
                receiptNumber = doc.Number,
                date = doc.IssueDate.ToString("yyyy-MM-dd"),
                time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm"), // Kenya time
                cashier = ctx.Actor,
                company = new
                {
                    name = company.Name,
                    kraPin = company.KraPin,
                    phone = company.Phone,
                    email = company.Email,
                    address = company.County,
                },
                customer = new
                {
                    name = customer.Name,
                    phone = customer.Phone,
                    kraPin = customer.KraPin,
                },
                lines = fullDoc.Lines.Select(l => new
                {
                    description = l.Description,
                    quantity = l.Quantity,
                    unitPrice = l.UnitPrice,
                    taxType = l.TaxType.ToString(),
                    net = l.Net,
                    tax = l.Tax,
                    total = l.Total,
                }).ToList(),
                net = doc.Net,
                tax = doc.Tax,
                total = doc.Total,
                amountPaid = input.AmountPaid > 0 ? input.AmountPaid : doc.Total,
                changeGiven = Math.Max(0, input.ChangeGiven),
                paymentMethod = input.PaymentMethod.ToString(),
                reference = refCode,
                etims = new
                {
                    status = doc.EtimsStatus.ToString(),
                    invoiceNo = doc.EtimsInvoiceNo,
                    controlCode = doc.EtimsControlCode,
                    qr = doc.EtimsQrUrl,
                },
            };

            return Results.Ok(receipt);
        });

        // M-Pesa payments received in the last hour that nothing has used yet (newest first).
        pos.MapGet("/mpesa-waiting", async (ErpDb db) =>
        {
            var since = DateTimeOffset.UtcNow.AddHours(-1);
            var rows = await db.Payments.Where(x => x.Method == PaymentMethod.Mpesa && x.Status != PaymentStatus.Matched && x.ReceivedAt >= since)
                .Select(x => new { x.Id, x.Reference, x.PayerName, x.PayerPhone, x.Amount, x.Allocated, x.ReceivedAt }).ToListAsync();
            return rows.OrderByDescending(x => x.ReceivedAt).Take(20).Select(x => new { x.Id, x.Reference, x.PayerName, x.PayerPhone, left = x.Amount - x.Allocated, x.ReceivedAt });
        });

        // This till's sales so far (for the header and the closing count).
        pos.MapGet("/shift", async (ErpDb db, RequestContext ctx) =>
        {
            var s = await db.PosSessions
                .Where(x => x.Status == PosSessionStatus.Open && (x.CashierId == ctx.UserId || x.CashierId == Guid.Empty))
                .OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync();
            if (s is null) return null;
            var note = $"POS sale by {ctx.Actor}";
            var sales = await db.SalesDocs.Where(d => d.Kind == SalesDocKind.Invoice && d.Notes == note && d.CreatedAt >= s.OpenedAt)
                .OrderByDescending(d => d.CreatedAt).Select(d => new { d.Id, d.Number, d.Total, customer = d.Customer!.Name, d.CreatedAt }).ToListAsync();
            return new { count = sales.Count, total = sales.Sum(x => x.Total), recent = sales.Take(8) };
        });

        // Receipt lookup
        pos.MapGet("/receipt/{id:guid}", async (Guid id, ErpDb db, SalesService sales) =>
        {
            var doc = await sales.Get(id);
            var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
            var payment = await db.PaymentAllocations.Where(a => a.SalesDocId == id)
                .Join(db.Payments, a => a.PaymentId, p => p.Id, (a, p) => p)
                .FirstOrDefaultAsync();

            return new
            {
                invoiceId = doc.Id,
                receiptNumber = doc.Number,
                date = doc.IssueDate.ToString("yyyy-MM-dd"),
                company = new
                {
                    name = company.Name,
                    kraPin = company.KraPin,
                    phone = company.Phone,
                    email = company.Email,
                    address = company.County,
                },
                customer = new
                {
                    name = doc.Customer?.Name ?? "Walk-in Customer",
                    phone = doc.Customer?.Phone ?? "",
                    kraPin = doc.Customer?.KraPin ?? "",
                },
                lines = doc.Lines.Select(l => new
                {
                    description = l.Description,
                    quantity = l.Quantity,
                    unitPrice = l.UnitPrice,
                    taxType = l.TaxType.ToString(),
                    net = l.Net,
                    tax = l.Tax,
                    total = l.Total,
                }).ToList(),
                net = doc.Net,
                tax = doc.Tax,
                total = doc.Total,
                amountPaid = payment?.Amount ?? doc.Total,
                paymentMethod = payment?.Method.ToString() ?? "Cash",
                reference = payment?.Reference ?? doc.Number,
                etims = new
                {
                    status = doc.EtimsStatus.ToString(),
                    invoiceNo = doc.EtimsInvoiceNo,
                    controlCode = doc.EtimsControlCode,
                    qr = doc.EtimsQrUrl,
                },
            };
        });
    }
}
