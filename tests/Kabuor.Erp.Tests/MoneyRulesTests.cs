using Kabuor.Erp.Accounting;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;

namespace Kabuor.Erp.Tests;

public class TaxTests
{
    [Theory]
    [InlineData(116000, TaxType.B_Standard16, 100000, 16000)] // KSh 1,160 incl. 16% VAT
    [InlineData(10800, TaxType.E_Reduced8, 10000, 800)]
    [InlineData(5000, TaxType.C_ZeroRated, 5000, 0)]
    [InlineData(5000, TaxType.A_Exempt, 5000, 0)]
    [InlineData(1, TaxType.B_Standard16, 1, 0)] // a cent can't carry VAT
    [InlineData(99999, TaxType.B_Standard16, 86206, 13793)]
    public void Splits_vat_inclusive_prices_exactly(long total, TaxType type, long net, long tax)
    {
        var (n, t) = Tax.SplitInclusive(total, type);
        Assert.Equal(net, n);
        Assert.Equal(tax, t);
        Assert.Equal(total, n + t);
    }

    [Fact]
    public void Not_vat_registered_means_no_vat()
    {
        Assert.Equal((116000L, 0L), Tax.SplitInclusive(116000, TaxType.B_Standard16, vatRegistered: false));
    }

    [Fact]
    public void Line_rounds_quantity_times_price_to_the_cent()
    {
        var (net, tax, total) = Tax.Line(2.5m, 33333, TaxType.B_Standard16);
        Assert.Equal(83333, total);
        Assert.Equal(total, net + tax);
    }
}

public class MatchingTests
{
    static readonly Guid Wanjiku = Guid.NewGuid(), Otieno = Guid.NewGuid();

    static readonly List<OpenInvoice> Open =
    [
        new(Guid.NewGuid(), "INV-0012", Wanjiku, "C-0003", "Wanjiku Stores", "0712345678", 500000, new DateOnly(2026, 10, 1)),
        new(Guid.NewGuid(), "INV-0015", Wanjiku, "C-0003", "Wanjiku Stores", "0712345678", 250000, new DateOnly(2026, 10, 9)),
        new(Guid.NewGuid(), "INV-0020", Otieno, "C-0007", "Otieno Hardware", "0722000111", 120000, new DateOnly(2026, 10, 5)),
    ];

    [Theory]
    [InlineData("INV-0012")]
    [InlineData("inv12")]
    [InlineData(" Inv 0012 ")]
    [InlineData("12")]
    public void Invoice_number_in_the_account_field_auto_matches(string accountRef)
    {
        var m = Matching.Find(500000, accountRef, "", Open);
        Assert.True(m.Auto);
        Assert.Equal("INV-0012", Assert.Single(m.Allocations).InvoiceNumber);
    }

    [Fact]
    public void Overpaying_an_invoice_leaves_the_rest_unallocated()
    {
        var m = Matching.Find(600000, "INV-0020", "", Open);
        Assert.Equal(120000, Assert.Single(m.Allocations).Amount);
    }

    [Fact]
    public void Customer_number_pays_their_invoices_oldest_first()
    {
        var m = Matching.Find(600000, "c3", "", Open);
        Assert.True(m.Auto);
        Assert.Equal(["INV-0012", "INV-0015"], m.Allocations.Select(a => a.InvoiceNumber));
        Assert.Equal([500000L, 100000L], m.Allocations.Select(a => a.Amount));
    }

    [Fact]
    public void Customers_phone_and_exact_balance_auto_matches()
    {
        var m = Matching.Find(250000, "", "254712345678", Open);
        Assert.True(m.Auto);
        Assert.Equal("INV-0015", Assert.Single(m.Allocations).InvoiceNumber);
    }

    [Fact]
    public void Customers_phone_without_exact_amount_is_only_a_suggestion()
    {
        var m = Matching.Find(300000, "", "254712345678", Open);
        Assert.False(m.Auto);
        Assert.Equal("INV-0012", m.Allocations[0].InvoiceNumber);
    }

    [Fact]
    public void Same_amount_from_a_stranger_is_only_a_suggestion()
    {
        var m = Matching.Find(120000, "", "254799999999", Open);
        Assert.False(m.Auto);
        Assert.Equal("INV-0020", Assert.Single(m.Allocations).InvoiceNumber);
    }

    [Theory]
    [InlineData("rent")]
    [InlineData("99")]
    [InlineData("QT-0012")]
    public void Nothing_to_go_on_means_no_match(string accountRef)
    {
        Assert.Empty(Matching.Find(77700, accountRef, "254799999999", Open).Allocations);
    }

    [Fact]
    public void Bare_digits_never_mean_a_customer()
    {
        Assert.False(Matching.SameNumber("3", "C-0003", "C"));
        Assert.True(Matching.SameNumber("C3", "C-0003", "C"));
    }
}
