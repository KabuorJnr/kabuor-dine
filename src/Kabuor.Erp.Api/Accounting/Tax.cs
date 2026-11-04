using Kabuor.Erp.Domain;

namespace Kabuor.Erp.Accounting;

/// <summary>VAT, worked out from VAT-inclusive prices (how prices are quoted in Kenya).</summary>
public static class Tax
{
    /// <summary>Rate in basis points: 16% is 1600.</summary>
    public static int RateBps(TaxType t) => t switch
    {
        TaxType.B_Standard16 => 1600,
        TaxType.E_Reduced8 => 800,
        _ => 0,
    };

    public static string Label(TaxType t) => t switch
    {
        TaxType.A_Exempt => "Exempt",
        TaxType.B_Standard16 => "VAT 16%",
        TaxType.C_ZeroRated => "Zero-rated",
        TaxType.D_NonVat => "Non-VAT",
        TaxType.E_Reduced8 => "VAT 8%",
        _ => t.ToString(),
    };

    /// <summary>Splits a VAT-inclusive total (cents) into net and tax. Tax is rounded to the cent; net takes the rest,
    /// so net + tax always equals the total exactly.</summary>
    public static (long Net, long Tax) SplitInclusive(long total, TaxType type, bool vatRegistered = true)
    {
        var bps = vatRegistered ? RateBps(type) : 0;
        if (bps == 0) return (total, 0);
        var tax = (long)Math.Round(total * (decimal)bps / (10000 + bps), MidpointRounding.AwayFromZero);
        return (total - tax, tax);
    }

    /// <summary>A line's totals: quantity × unit price (rounded to the cent), then split into net and VAT.</summary>
    public static (long Net, long Tax, long Total) Line(decimal quantity, long unitPrice, TaxType type, bool vatRegistered = true)
    {
        var total = (long)Math.Round(quantity * unitPrice, MidpointRounding.AwayFromZero);
        var (net, tax) = SplitInclusive(total, type, vatRegistered);
        return (net, tax, total);
    }
}
