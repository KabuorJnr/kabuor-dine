// Kenyan statutory pay rules, as dated rate sets: a payroll month uses the set in force on its first day.
// When KRA, NSSF or the SHA change a rate, add a new set with its start date; past months keep their rules.
// Pure logic, no database, so every rule is unit tested.
//
// Order (in force since 27 Dec 2024): taxable pay = gross − NSSF (employee) − SHIF − Housing Levy (employee);
// PAYE = tax on taxable pay by band − personal relief, never below zero.
// Rates below were checked against public guidance in October 2026; confirm with KRA/NSSF/SHA before relying on
// them for a real payroll, and update this table when they change.
namespace Kabuor.Erp.Payroll;

public sealed record TaxBand(long UpTo, int RateBps); // monthly, cents; UpTo = long.MaxValue for the top band

public sealed record StatutoryRates(
    DateOnly From,
    TaxBand[] PayeBands,
    long PersonalRelief,
    int NssfRateBps,
    long NssfLowerLimit,   // Tier I ceiling
    long NssfUpperLimit,   // Tier II ceiling
    int ShifRateBps,
    long ShifMinimum,
    int HousingLevyRateBps,
    long NitaPerEmployee);

/// <summary>OtherDeductions are taken after tax; Pension (voluntary contributions) before tax, within KRA's limit.</summary>
public sealed record PayInput(long Basic, long TaxableAllowances, long ExtraEarnings, long NonTaxableAllowances, long OtherDeductions, long Pension = 0);

public sealed record PayResult(
    long Gross, long NssfEmployee, long NssfEmployer, long Shif, long HousingLevyEmployee, long HousingLevyEmployer,
    long Taxable, long PayeBeforeRelief, long PersonalRelief, long Paye, long Nita, long OtherDeductions, long NonTaxable, long Net, long Pension = 0)
{
    /// <summary>What the employer pays on top of gross pay.</summary>
    public long EmployerCost => NssfEmployer + HousingLevyEmployer + Nita;
}

public static class Statutory
{
    static readonly TaxBand[] Bands2023 =
    [
        new(24_000_00, 1000),        // first KSh 24,000 at 10%
        new(32_333_00, 2500),        // next KSh 8,333 at 25%
        new(500_000_00, 3000),       // up to KSh 500,000 at 30%
        new(800_000_00, 3250),       // up to KSh 800,000 at 32.5%
        new(long.MaxValue, 3500),    // above at 35%
    ];

    /// <summary>Oldest first.</summary>
    public static readonly StatutoryRates[] Sets =
    [
        // NSSF year 3 limits (Feb 2025): Tier I to KSh 8,000, Tier II to KSh 72,000.
        new(new DateOnly(2025, 2, 1), Bands2023, 2_400_00, 600, 8_000_00, 72_000_00, 275, 300_00, 150, 50_00),
        // NSSF year 4 limits (Feb 2026): Tier I to KSh 9,000, Tier II to KSh 108,000.
        new(new DateOnly(2026, 2, 1), Bands2023, 2_400_00, 600, 9_000_00, 108_000_00, 275, 300_00, 150, 50_00),
    ];

    public static StatutoryRates For(DateOnly period) =>
        Sets.LastOrDefault(s => s.From <= period) ?? throw new Common.AppException("Payroll before February 2025 isn't supported.");

    static long Pct(long amount, int bps) => (long)Math.Round(amount * (decimal)bps / 10000, MidpointRounding.AwayFromZero);

    public static long Nssf(long pensionable, StatutoryRates r)
    {
        var tier1 = Pct(Math.Min(pensionable, r.NssfLowerLimit), r.NssfRateBps);
        var tier2 = pensionable > r.NssfLowerLimit ? Pct(Math.Min(pensionable, r.NssfUpperLimit) - r.NssfLowerLimit, r.NssfRateBps) : 0;
        return tier1 + tier2;
    }

    public static long TaxByBand(long taxable, TaxBand[] bands)
    {
        long tax = 0, floor = 0;
        foreach (var b in bands)
        {
            if (taxable <= floor) break;
            var top = Math.Min(taxable, b.UpTo);
            tax += Pct(top - floor, b.RateBps);
            floor = b.UpTo;
        }
        return tax;
    }

    public const long PensionReliefLimit = 30_000_00;

    public static PayResult Calculate(PayInput p, StatutoryRates r)
    {
        var gross = p.Basic + p.TaxableAllowances + p.ExtraEarnings;
        if (gross <= 0) return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, p.OtherDeductions, p.NonTaxableAllowances, p.NonTaxableAllowances - p.OtherDeductions - p.Pension, p.Pension);
        var nssf = Nssf(gross, r);
        var shif = Math.Max(Pct(gross, r.ShifRateBps), r.ShifMinimum);
        var ahl = Pct(gross, r.HousingLevyRateBps);
        // Pension contributions (NSSF included) reduce taxable pay up to KSh 30,000 a month (Tax Laws (Amendment) Act 2024).
        var pensionRelief = Math.Min(p.Pension, Math.Max(0, PensionReliefLimit - nssf));
        var taxable = Math.Max(0, gross - nssf - shif - ahl - pensionRelief);
        var before = TaxByBand(taxable, r.PayeBands);
        var relief = Math.Min(before, r.PersonalRelief);
        var paye = before - relief;
        var net = gross - nssf - shif - ahl - paye - p.OtherDeductions - p.Pension + p.NonTaxableAllowances;
        return new(gross, nssf, nssf, shif, ahl, ahl, taxable, before, relief, paye, r.NitaPerEmployee, p.OtherDeductions, p.NonTaxableAllowances, net, p.Pension);
    }
}
