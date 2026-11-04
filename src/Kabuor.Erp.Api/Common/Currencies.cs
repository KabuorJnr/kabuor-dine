// Selling in another currency: a USD invoice to a Kampala distributor, a EUR one to an exporter's buyer. The books,
// VAT and eTIMS stay in shillings (KRA reads KES); the invoice also carries what the customer sees and owes in their
// currency, at the rate fixed when it was made. When they pay, the shillings that actually land are compared with the
// shillings the invoice was booked at, and the difference is an exchange gain or loss (IAS 21).
namespace Kabuor.Erp.Common;

public static class Currencies
{
    /// <summary>The currencies a Kenyan business is invoiced in most: the dollar, euro and pound, and the EAC neighbours.</summary>
    public static readonly string[] Supported = ["USD", "EUR", "GBP", "UGX", "TZS", "RWF", "ZAR", "AED", "CNY", "INR"];

    /// <summary>"usd" → "USD"; empty or KES → "" (shillings). Anything else is refused.</summary>
    public static string Code(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        if (c is "" or "KES" or "KSH") return "";
        return Supported.Contains(c) ? c : throw new AppException($"{c} isn't a currency Kabuor invoices in yet. Choose one of {string.Join(", ", Supported)}, or KES.");
    }

    /// <summary>The currency and its rate (shillings per one unit) for a document. Shillings always have a rate of 1.</summary>
    public static (string Currency, decimal Rate) Of(string? code, decimal? rate)
    {
        var c = Code(code);
        if (c == "") return ("", 1);
        if (rate is not { } r || r <= 0) throw new AppException($"Enter the exchange rate: how many shillings one {c} is worth today.");
        if (r < 0.0001m || r > 10_000m) throw new AppException($"1 {c} = KSh {r} doesn't look right. Check the rate (CBK publishes it daily).");
        return (c, Math.Round(r, 6));
    }

    /// <summary>A foreign amount (cents) in shillings (cents) at a rate.</summary>
    public static long ToKes(long cents, decimal rate) => (long)Math.Round(cents * rate, MidpointRounding.AwayFromZero);
}
