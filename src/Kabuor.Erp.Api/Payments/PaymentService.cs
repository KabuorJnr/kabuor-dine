using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Payments;

/// <summary>Amount is always the shillings that came in. FxAmount: for a foreign-currency invoice, how much of it
/// (in its currency) the money settles.</summary>
public sealed record PaymentInput(PaymentMethod Method, long Amount, DateTimeOffset? ReceivedAt, string? Reference, string? PayerPhone, string? PayerName, string? AccountRef, Guid? CustomerId, Guid? InvoiceId, long? FxAmount = null);
public sealed record AllocationInput(Guid InvoiceId, long Amount, long? FxAmount = null);

/// <summary>Safaricom's C2B confirmation body (what a Paybill/Till payment looks like when it arrives).</summary>
public sealed record MpesaC2B(string TransID, string TransAmount, string? TransTime, string BusinessShortCode, string? BillRefNumber, string? MSISDN, string? FirstName, string? MiddleName, string? LastName);

public sealed class PaymentService(ErpDb db, Ledger ledger, Kabuor.Erp.Integrations.WebhookService hooks, IConfiguration config)
{
    /// <summary>Records money in and posts it. Then pays the chosen invoice, or tries to match it automatically.
    /// <paramref name="source"/> says how it arrived (the caller knows; a request body never decides it), and for an
    /// M-Pesa callback <paramref name="callbackIp"/> is the address that delivered it.</summary>
    public async Task<Payment> Record(PaymentInput input, string actor, PaymentSource source = PaymentSource.Manual, System.Net.IPAddress? callbackIp = null)
    {
        if (input.Amount <= 0) throw new AppException("Enter an amount above 0.");
        var reference = (input.Reference ?? "").Trim().ToUpperInvariant();
        if (reference.Length > 0 && await db.Payments.AnyAsync(p => p.Reference == reference))
            throw new AppException($"A payment with reference {reference} is already recorded.", 409);
        // A foreign-currency invoice is checked before the money is recorded, so a refusal leaves nothing half-done.
        var target = input.InvoiceId is { } tid ? await db.SalesDocs.SingleOrDefaultAsync(d => d.Id == tid) ?? throw new AppException("That invoice no longer exists.") : null;
        if (target is { Currency.Length: > 0 }) CheckFx(target, input.FxAmount);
        var p = new Payment
        {
            Method = input.Method,
            Amount = input.Amount,
            ReceivedAt = input.ReceivedAt ?? DateTimeOffset.UtcNow,
            Reference = reference,
            PayerPhone = Phones.ToMsisdn(input.PayerPhone) ?? (input.PayerPhone ?? "").Trim(),
            PayerName = (input.PayerName ?? "").Trim(),
            AccountRef = (input.AccountRef ?? "").Trim(),
            CustomerId = input.CustomerId,
            Source = source,
            CallbackIp = callbackIp?.ToString() ?? "",
            FromSafaricom = source is PaymentSource.MpesaConfirmation or PaymentSource.MpesaPrompt && SafaricomCallers.IsSafaricom(callbackIp, config),
        };
        db.Payments.Add(p);
        await ledger.PostReceipt(p);
        await db.SaveChangesAsync();
        await hooks.Raise("payment.received", new { p.Id, method = p.Method.ToString(), p.Amount, p.Reference, p.PayerPhone, p.PayerName, p.AccountRef, p.ReceivedAt });

        if (target is not null)
            await Allocate(p.Id, [target.Currency.Length > 0 ? new AllocationInput(target.Id, p.Amount, input.FxAmount) : new AllocationInput(target.Id, Math.Min(p.Amount, target.Balance))], $"user: {actor}");
        else await AutoMatch(p);
        // Money nobody could place: the matching agent looks at it within a minute instead of at its next hour.
        if (p.Status != PaymentStatus.Matched) await Agents.AgentKick.Kick(db, "matching");
        return p;
    }

    /// <summary>A Paybill/Till payment from Safaricom. Safe to receive twice: the M-Pesa receipt is unique.</summary>
    public async Task<Payment?> FromMpesa(MpesaC2B c, System.Net.IPAddress? callbackIp = null)
    {
        var reference = c.TransID.Trim().ToUpperInvariant();
        if (await db.Payments.AnyAsync(p => p.Reference == reference)) return null; // Safaricom retried
        if (!decimal.TryParse(c.TransAmount, System.Globalization.CultureInfo.InvariantCulture, out var shillings) || shillings <= 0)
            throw new AppException("Bad amount.");
        var when = DateTime.TryParseExact(c.TransTime, "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.None, out var t)
            ? new DateTimeOffset(t, TimeSpan.FromHours(3))
            : DateTimeOffset.UtcNow;
        var name = string.Join(' ', new[] { c.FirstName, c.MiddleName, c.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var phone = Phones.ToMsisdn(c.MSISDN) ?? (c.MSISDN ?? ""); // Safaricom may send a hashed MSISDN; kept as is
        var customer = phone.Length > 0 ? await db.Customers.Where(x => x.Phone == phone).Select(x => (Guid?)x.Id).FirstOrDefaultAsync() : null;
        return await Record(new PaymentInput(PaymentMethod.Mpesa, (long)Math.Round(shillings * 100), when, reference, phone, name, c.BillRefNumber, customer, null), "M-Pesa", PaymentSource.MpesaConfirmation, callbackIp);
    }

    async Task<List<OpenInvoice>> OpenInvoices() =>
        (await db.SalesDocs
            // Foreign-currency invoices are left out: the shillings that arrive never equal the balance, so only a
            // person who knows how much of the invoice came can apply them.
            .Where(d => d.Kind == SalesDocKind.Invoice && (d.Status == SalesDocStatus.Sent || d.Status == SalesDocStatus.PartPaid) && d.Currency == "")
            .Select(d => new { d.Id, d.Number, d.CustomerId, CNo = d.Customer!.Number, CName = d.Customer.Name, CPhone = d.Customer.Phone, d.Total, d.Paid, d.DueDate })
            .ToListAsync())
        .Select(d => new OpenInvoice(d.Id, d.Number, d.CustomerId, d.CNo, d.CName, d.CPhone, d.Total - d.Paid, d.DueDate))
        .ToList();

    /// <summary>What the matcher thinks this payment pays (for the "To match" screen and the assistant).</summary>
    public async Task<MatchResult> Suggest(Payment p)
    {
        if (p.Unallocated <= 0) return MatchResult.None;
        var open = await OpenInvoices();
        if (p.CustomerId is { } cid && open.Any(i => i.CustomerId == cid))
            open = open.Where(i => i.CustomerId == cid).ToList();
        return Matching.Find(p.Unallocated, p.AccountRef, p.PayerPhone, open);
    }

    public async Task<bool> AutoMatch(Payment p)
    {
        var m = await Suggest(p);
        if (!m.Auto || m.Allocations.Count == 0) return false;
        await Allocate(p.Id, m.Allocations.Select(a => new AllocationInput(a.InvoiceId, a.Amount)).ToList(), $"auto: {m.Reason}");
        return true;
    }

    /// <summary>Tries every unmatched payment again (e.g. after a new invoice is issued for someone who paid early).</summary>
    public async Task<int> RetryUnmatched()
    {
        var waiting = await db.Payments.Include(p => p.Allocations).Where(p => p.Status != PaymentStatus.Matched).OrderBy(p => p.ReceivedAt).Take(500).ToListAsync();
        var n = 0;
        foreach (var p in waiting) if (await AutoMatch(p)) n++;
        return n;
    }

    /// <summary>Applies a payment to invoices. Checks the money exists on both sides; posts it; updates statuses.</summary>
    public async Task<Payment> Allocate(Guid paymentId, List<AllocationInput> allocations, string matchedBy)
    {
        var p = await db.Payments.Include(x => x.Allocations).SingleOrDefaultAsync(x => x.Id == paymentId) ?? throw new AppException("Payment not found.", 404);
        if (allocations.Count == 0) throw new AppException("Choose at least one invoice.");
        if (allocations.Count > 100) throw new AppException("Cannot allocate to more than 100 invoices at once.");
        if (allocations.Any(a => a.Amount <= 0)) throw new AppException("Amounts must be above 0.");
        if (allocations.Sum(a => a.Amount) > p.Unallocated) throw new AppException("That's more than is left on this payment.");
        var ids = allocations.Select(a => a.InvoiceId).ToList();
        var invoices = await db.SalesDocs.Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id);

        foreach (var a in allocations)
        {
            var inv = invoices.GetValueOrDefault(a.InvoiceId) ?? throw new AppException("One of those invoices no longer exists.");
            if (inv.Kind != SalesDocKind.Invoice || inv.Status is SalesDocStatus.Draft or SalesDocStatus.Void) throw new AppException($"{inv.Number} isn't an issued invoice.");
            if (inv.Currency.Length > 0)
            {
                // The part of the invoice settled is cleared at the shillings it was booked at; the last payment
                // clears whatever is left, so nothing is stranded by rounding.
                var fx = CheckFx(inv, a.FxAmount);
                var booked = fx == inv.FxBalance ? inv.Balance : Math.Min(inv.Balance, Currencies.ToKes(fx, inv.Rate));
                inv.Paid += booked;
                inv.FxPaid += fx;
                inv.Status = inv.FxBalance == 0 ? SalesDocStatus.Paid : SalesDocStatus.PartPaid;
                p.Allocations.Add(new PaymentAllocation { SalesDocId = inv.Id, Amount = a.Amount, FxAmount = fx, MatchedBy = matchedBy });
                await ledger.PostFxAllocation(p, inv, a.Amount, booked, fx);
            }
            else
            {
                if (a.Amount > inv.Balance) throw new AppException($"{inv.Number} only has {inv.Balance / 100m:N2} left to pay.");
                inv.Paid += a.Amount;
                inv.Status = inv.Balance == 0 ? SalesDocStatus.Paid : SalesDocStatus.PartPaid;
                p.Allocations.Add(new PaymentAllocation { SalesDocId = inv.Id, Amount = a.Amount, MatchedBy = matchedBy });
                await ledger.PostAllocation(p, inv, a.Amount);
            }
            p.Allocated += a.Amount;
            p.CustomerId ??= inv.CustomerId;
        }
        p.Status = p.Unallocated == 0 ? PaymentStatus.Matched : PaymentStatus.PartlyMatched;
        await db.SaveChangesAsync();
        foreach (var inv in invoices.Values.Where(i => i.Status == SalesDocStatus.Paid))
            await hooks.Raise("invoice.paid", new { inv.Id, inv.Number, inv.Total, inv.Paid, externalRef = inv.ClientRef.StartsWith("api:") ? inv.ClientRef[4..] : null, payment = p.Reference });
        return p;
    }

    /// <summary>How much of a foreign-currency invoice a payment settles: required, above 0, and no more than is owed.</summary>
    static long CheckFx(SalesDoc inv, long? fxAmount)
    {
        if (fxAmount is not { } fx || fx <= 0) throw new AppException($"{inv.Number} is in {inv.Currency}. Say how many {inv.Currency} this payment covers.");
        if (fx > inv.FxBalance) throw new AppException($"{inv.Number} only has {inv.Currency} {inv.FxBalance / 100m:N2} left to pay.");
        return fx;
    }
}
