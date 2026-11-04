// Payroll rules that must hold for every salary, checked across the whole range in KSh 250 steps rather
// than at a few hand-picked figures: no negative deduction, every shilling accounted for, and a raise
// never lowers take-home pay.
using Kabuor.Erp.Payroll;

namespace Kabuor.Erp.Tests;

public class PayrollPropertyTests
{
    [Theory]
    [InlineData("2025-03-01")] // before the February 2026 NSSF change
    [InlineData("2026-10-01")]
    public void Every_salary_adds_up_and_a_raise_never_lowers_take_home(string period)
    {
        var rates = Statutory.For(DateOnly.Parse(period));
        long lastNet = long.MinValue;
        for (long basic = 0; basic <= 1_000_000_00; basic += 250_00)
        {
            var r = Statutory.Calculate(new PayInput(basic, 0, 0, 0, 0), rates);
            Assert.True(r.Paye >= 0 && r.Shif >= 0 && r.NssfEmployee >= 0 && r.HousingLevyEmployee >= 0, $"negative deduction at {basic}");
            Assert.Equal(r.Gross, basic);
            Assert.Equal(r.Gross - r.NssfEmployee - r.Shif - r.HousingLevyEmployee - r.Paye - r.OtherDeductions + r.NonTaxable, r.Net);
            Assert.True(r.Paye <= r.Taxable, $"PAYE above taxable pay at {basic}");
            // Below about KSh 10,000 the SHIF minimum (KSh 300) can exceed what's left: the law, not a bug, and
            // the payroll screen warns about it. From there up, more pay must always mean more take-home.
            if (basic >= 10_000_00)
            {
                Assert.True(r.Net >= lastNet, $"take-home fell when pay rose to {basic}");
                Assert.True(r.Net > 0, $"take-home not positive at {basic}");
                lastNet = r.Net;
            }
        }
    }
}
