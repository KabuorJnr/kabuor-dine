// The customer's side, on the open internet (/s/{slug}): browse the products, ask a question, and, after signing
// in with their phone and a one-time code, see what they owe, pay by M-Pesa, order again, follow up their
// questions, and keep their details and marketing consent up to date. Everything they do lands in the same
// books, stock and CRM the staff use, credited to the link that brought them.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Marketing;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Intelligence;

public sealed record PortalCodeInput(string Phone);
public sealed record VerifyInput(string Phone, string Code, string? Name, string? Ref);
public sealed record PortalOrderLine(Guid ItemId, decimal Quantity, string? Unit);
public sealed record PortalOrderInput(List<PortalOrderLine> Lines, string? Note, string? Name = null, string? Phone = null, string? DeliveryAddress = null, string? Ref = null);
public sealed record PortalProfileInput(string? Name, string? Email, bool? MarketingOptOut);
public sealed record PortalPayInput(string? Phone);

/// <summary>Signed customer tokens: company, customer and expiry, sealed with a key derived from the server's signing key.</summary>
public sealed class PortalTokens(TokenIssuer tokens)
{
    byte[] Key => SHA256.HashData([.. tokens.Key.Key, .. "kabuor-erp/portal"u8]);

    public string Issue(Guid company, Guid customer, TimeSpan life)
    {
        var body = $"{company:N}.{customer:N}.{DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds()}";
        var sig = Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(body))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{body}.{sig}";
    }

    public (Guid Company, Guid Customer)? Read(string? token)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var company) || !Guid.TryParseExact(parts[1], "N", out var customer) || !long.TryParse(parts[2], out var exp)) return null;
        var body = string.Join('.', parts[..3]);
        var want = Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(body))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(parts[3]))) return null;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp ? null : (company, customer);
    }
}

public sealed class PortalService(ErpDb db, RequestContext ctx, Numbers numbers, ISmsSender sms, PortalTokens tokens, InquiryService inquiries,
    SalesService sales, StkService stk, IHostEnvironment env)
{
    static string Hash(Guid company, string phone, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{company:N}|{phone}|{code}")));

    public async Task<object> Shop(Guid? customerId)
    {
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        var customer = customerId is { } cid ? await db.Customers.SingleOrDefaultAsync(x => x.Id == cid) : null;
        var listed = customer?.PriceListId is { } pl ? await db.PriceListItems.Where(p => p.PriceListId == pl).ToDictionaryAsync(p => p.ItemId, p => p.Price) : [];
        var items = await db.Items.Where(i => i.Active && i.Price > 0 && i.Use == ItemUse.Sale).OrderBy(i => i.Name).ToListAsync();
        var onHand = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var units = await db.ItemUnits.ToListAsync();
        // Expired batches are still counted in stock but can't be sold: they don't make a product available.
        var today = Clock.Today;
        var expired = (await db.StockBatches.Where(x => x.Quantity > 0 && x.Expiry != null).ToListAsync()).Where(x => x.Expiry < today)
            .GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        return new
        {
            business = new { c.Name, c.Phone, c.Email, c.County, paybill = c.MpesaShortcode },
            signedIn = customer is not null, customer = customer is null ? null : new { customer.Name, customer.Phone },
            items = items.Select(i => new
            {
                i.Id, i.Name, i.Unit, price = listed.TryGetValue(i.Id, out var lp) ? lp : i.Price, kind = i.Kind.ToString(),
                available = !i.TrackStock || onHand.GetValueOrDefault(i.Id) - expired.GetValueOrDefault(i.Id) > 0,
                units = units.Where(u => u.ItemId == i.Id).Select(u => new { u.Name, u.Factor }),
            }),
        };
    }

    // ---------- signing in with a one-time code ----------

    public async Task<object> SendCode(PortalCodeInput input)
    {
        var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter your phone number (e.g. 0712 345 678).");
        var since = DateTimeOffset.UtcNow.AddMinutes(-15);
        var recent = (await db.PortalCodes.Where(p => p.Phone == phone).Select(p => p.CreatedAt).ToListAsync()).Count(t => t > since);
        if (recent >= 3) throw new AppException("Too many codes asked for. Wait 15 minutes and try again.", 429);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        db.PortalCodes.Add(new PortalCode { Phone = phone, CodeHash = Hash(db.CompanyId, phone, code), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        var company = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => c.Name).SingleAsync();
        if (sms.Live)
        {
            var (ok, error) = await sms.Send(phone, $"{code} is your {company} sign-in code. It works for 10 minutes. Don't share it.");
            if (!ok) throw new AppException($"The code didn't go: {error}", 502);
            await db.SaveChangesAsync();
            return new { sent = true };
        }
        // No SMS account on this server: only a development server shows the code, so testing works.
        if (!env.IsDevelopment()) throw new AppException("Sign-in codes need SMS, which isn't set up for this shop yet. Call or WhatsApp the business instead.", 503);
        await db.SaveChangesAsync();
        return new { sent = true, devCode = code };
    }

    public async Task<object> Verify(VerifyInput input)
    {
        var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter your phone number.");
        var now = DateTimeOffset.UtcNow;
        var pending = (await db.PortalCodes.Where(p => p.Phone == phone && !p.Used).ToListAsync()).Where(p => p.ExpiresAt > now).OrderByDescending(p => p.CreatedAt).FirstOrDefault()
            ?? throw new AppException("Ask for a new code: that one has expired.");
        if (pending.Attempts >= 5) throw new AppException("Too many wrong tries. Ask for a new code.", 429);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pending.CodeHash), Encoding.UTF8.GetBytes(Hash(db.CompanyId, phone, (input.Code ?? "").Trim()))))
        {
            pending.Attempts++;
            await db.SaveChangesAsync();
            throw new AppException("That code isn't right.");
        }
        pending.Used = true;
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone);
        if (customer is null)
        {
            var name = (input.Name ?? "").Trim();
            if (name.Length < 2) throw new AppException("Tell us your name or your business's name to open your account.");
            var link = string.IsNullOrWhiteSpace(input.Ref) ? null : await db.TrackingLinks.SingleOrDefaultAsync(l => l.Code == input.Ref.Trim().ToLowerInvariant());
            customer = new Customer { Number = await numbers.Next("C"), Name = name, Phone = phone, FirstLinkId = link?.Id, AcquisitionChannel = link?.Channel ?? Channel.Website, AcquiredByUserId = link?.OwnerUserId };
            db.Customers.Add(customer);
        }
        await db.SaveChangesAsync();
        return new { token = tokens.Issue(db.CompanyId, customer.Id, TimeSpan.FromDays(30)), customer = new { customer.Name, customer.Phone } };
    }

    // ---------- the signed-in customer's account ----------

    async Task<Customer> Me(Guid id) => await db.Customers.SingleOrDefaultAsync(c => c.Id == id) ?? throw new AppException("Sign in again.", 401);

    public async Task<object> Account(Guid customerId)
    {
        var c = await Me(customerId);
        c.PortalLastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var docs = (await db.SalesDocs.Where(d => d.CustomerId == c.Id && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void).ToListAsync()).OrderByDescending(d => d.IssueDate).ThenByDescending(d => d.CreatedAt).ToList();
        var asks = (await db.Inquiries.Include(i => i.Messages).Where(i => i.CustomerId == c.Id).ToListAsync()).OrderByDescending(i => i.CreatedAt).Take(30).ToList();
        var paid = await db.Payments.Where(p => p.CustomerId == c.Id).OrderByDescending(p => p.ReceivedAt).Take(20).Select(p => new { p.Reference, p.Amount, p.ReceivedAt, method = p.Method.ToString() }).ToListAsync();
        var company = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        var today = Clock.Today;
        var open = docs.Where(d => d.Kind == SalesDocKind.Invoice && d.Balance > 0).ToList();
        return new
        {
            customer = new { c.Name, c.Phone, c.Email, c.MarketingOptOut, c.Number },
            owes = open.Sum(d => d.Balance), overdue = open.Where(d => d.DueDate < today).Sum(d => d.Balance),
            payTo = new { paybill = company.MpesaShortcode, prompt = company.StkShortcode.Length > 0 && company.StkConsumerKey.Length > 0 },
            invoices = docs.Where(d => d.Kind == SalesDocKind.Invoice).Take(50).Select(d => new { d.Id, d.Number, d.IssueDate, d.DueDate, d.Total, d.Paid, balance = d.Balance, status = d.Status.ToString(), overdue = d.Balance > 0 && d.DueDate < today }),
            quotes = docs.Where(d => d.Kind == SalesDocKind.Quote).Take(20).Select(d => new { d.Id, d.Number, d.IssueDate, d.Total, status = d.Status.ToString() }),
            payments = paid,
            inquiries = asks.Select(i => new { i.Id, i.Number, i.Subject, status = i.Status.ToString(), i.CreatedAt, messages = i.Messages.OrderBy(m => m.At).Select(m => new { m.From, m.Author, m.Text, m.At }) }),
        };
    }

    public async Task<object> Invoice(Guid customerId, Guid id)
    {
        var d = await db.SalesDocs.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId && x.Status != SalesDocStatus.Draft) ?? throw new AppException("Not found.", 404);
        return new
        {
            d.Id, d.Number, kind = d.Kind.ToString(), d.IssueDate, d.DueDate, d.Net, d.Tax, d.Total, d.Paid, balance = d.Balance, status = d.Status.ToString(),
            etims = new { receipt = d.EtimsInvoiceNo, control = d.EtimsControlCode, qr = d.EtimsQrUrl },
            lines = d.Lines.Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitPrice, l.Total }),
        };
    }

    public async Task<object> UpdateProfile(Guid customerId, PortalProfileInput input)
    {
        var c = await Me(customerId);
        var name = (input.Name ?? c.Name).Trim();
        if (name.Length < 2) throw new AppException("Enter your name.");
        var email = (input.Email ?? c.Email).Trim();
        if (email.Length > 0 && !email.Contains('@')) throw new AppException("That email doesn't look right.");
        (c.Name, c.Email) = (name, email);
        if (input.MarketingOptOut is { } opt && opt != c.MarketingOptOut)
        {
            c.MarketingOptOut = opt;
            db.Activities.Add(new Activity { CustomerId = c.Id, Kind = ActivityKind.Note, Text = opt ? "Asked (on the customer portal) not to get offers." : "Agreed (on the customer portal) to get offers.", Actor = "Customer portal" });
        }
        await db.SaveChangesAsync();
        return new { c.Name, c.Email, c.MarketingOptOut };
    }

    /// <summary>An order from the portal becomes a draft quote at the customer's prices, with an inquiry for staff to confirm it.</summary>
    public async Task<object> Order(Guid? customerId, PortalOrderInput input)
    {
        Customer c;
        string? issuedToken = null;
        if (customerId is { } cid)
        {
            c = await Me(cid);
        }
        else
        {
            var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter your phone number (e.g. 0712 345 678).");
            var name = (input.Name ?? "").Trim();
            if (name.Length < 2) throw new AppException("Tell us your name or business name to place the order.");
            var existing = await db.Customers.FirstOrDefaultAsync(x => x.Phone == phone);
            if (existing is not null)
            {
                c = existing;
                if (!string.IsNullOrWhiteSpace(input.DeliveryAddress) && string.IsNullOrWhiteSpace(c.Address))
                {
                    c.Address = input.DeliveryAddress.Trim();
                }
            }
            else
            {
                var link = string.IsNullOrWhiteSpace(input.Ref) ? null : await db.TrackingLinks.SingleOrDefaultAsync(l => l.Code == input.Ref.Trim().ToLowerInvariant());
                c = new Customer
                {
                    Number = await numbers.Next("C"),
                    Name = name,
                    Phone = phone,
                    Address = (input.DeliveryAddress ?? "").Trim(),
                    FirstLinkId = link?.Id,
                    AcquisitionChannel = link?.Channel ?? Channel.Website,
                    AcquiredByUserId = link?.OwnerUserId
                };
                db.Customers.Add(c);
                await db.SaveChangesAsync();
            }
            issuedToken = tokens.Issue(db.CompanyId, c.Id, TimeSpan.FromDays(30));
        }

        if (input.Lines is not { Count: > 0 } || input.Lines.Count > 50) throw new AppException("Add between 1 and 50 products.");
        if (input.Lines.Any(l => l.Quantity <= 0 || l.Quantity > 100_000)) throw new AppException("Quantities must be more than 0.");
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        if ((await db.Inquiries.Where(i => i.CustomerId == c.Id).Select(i => i.CreatedAt).ToListAsync()).Count(t => t > since) >= 20) throw new AppException("That's a lot of orders today. Call the business to add more.", 429);
        var ids = input.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id) && i.Active && i.Price > 0 && i.Use == ItemUse.Sale).ToDictionaryAsync(i => i.Id);
        if (items.Count != ids.Distinct().Count()) throw new AppException("One of those products isn't sold any more.");

        var noteDetails = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.DeliveryAddress)) noteDetails.Add($"Deliver to: {input.DeliveryAddress.Trim()}");
        if (!string.IsNullOrWhiteSpace(input.Note)) noteDetails.Add(input.Note.Trim());
        var noteText = noteDetails.Count > 0 ? $": {string.Join(" · ", noteDetails)}" : "";

        var quote = await sales.Create(new SalesDocInput(SalesDocKind.Quote, c.Id, null, 7, $"Ordered online on the shop{noteText}",
            input.Lines.Select(l => new LineInput(l.ItemId, null, l.Quantity, null, null, l.Unit)).ToList()));
        var summary = string.Join(", ", quote.Lines.Select(l => $"{l.Description} × {l.Quantity:0.##}{(l.Unit.Length > 0 ? " " + l.Unit : "")}"));
        var inq = await inquiries.Create(new InquiryInput(c.Name, c.Phone, c.Email, $"Order: {summary}.{noteText}", null, c.Id, c.AcquisitionChannel ?? Channel.Website, null), "customer");
        (inq.QuoteId, inq.LinkId) = (quote.Id, c.FirstLinkId);
        await db.SaveChangesAsync();
        return new
        {
            quote = new { quote.Id, quote.Number, quote.Total },
            inquiry = new { inq.Id, inq.Number },
            token = issuedToken,
            customer = new { c.Name, c.Phone }
        };
    }

    public async Task<object> Ask(Guid customerId, InquiryInput input)
    {
        var c = await Me(customerId);
        var inq = await inquiries.Create(input with { CustomerId = c.Id, Phone = c.Phone, Name = c.Name, Channel = c.AcquisitionChannel ?? Channel.Website }, "customer");
        return new { inq.Id, inq.Number };
    }

    public async Task<object> Reply(Guid customerId, Guid inquiryId, ReplyInput input)
    {
        if (!await db.Inquiries.AnyAsync(i => i.Id == inquiryId && i.CustomerId == customerId)) throw new AppException("Not found.", 404);
        var inq = await inquiries.Reply(inquiryId, input, "customer");
        return new { inq.Id, status = inq.Status.ToString() };
    }

    public async Task<object> Pay(Guid customerId, Guid invoiceId, PortalPayInput input, string baseUrl)
    {
        var c = await Me(customerId);
        if (!await db.SalesDocs.AnyAsync(d => d.Id == invoiceId && d.CustomerId == c.Id)) throw new AppException("Not found.", 404);
        var r = await stk.Request(invoiceId, new StkRequestInput(string.IsNullOrWhiteSpace(input.Phone) ? c.Phone : input.Phone, null), baseUrl);
        return new { r.Id, status = r.Status.ToString(), r.Amount };
    }
}
