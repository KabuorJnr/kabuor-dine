// Kabuor Verified: the figures a lender can trust, worked out from what Safaricom and KRA told us, never from what
// someone typed. Facts only (money in, how many different payers, how much KRA signed); no scores or grades, which
// belong to lenders and licensed credit bureaus. Totals and counts only: no names, phone numbers or invoice lines
// ever leave this service, so the same summary can later be shown to a lender the owner chooses.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Verified;

public sealed record VerifiedMonth(string Month, bool Partial, long Confirmed, long AllMoneyIn, double ConfirmedShare, int Payers, double TopFiveShare);
public sealed record VerifiedSource(PaymentSource Source, long Amount, int Count, long ConfirmedAmount);
public sealed record VerifiedEtims(bool Required, int Issued, int Signed, int TestSigned, int FailedOrPending, double SignedShare);
public sealed record VerifiedFix(string Code, string Text, string? Link, string? Action);
public sealed record VerifiedSummary(
    int Months, string From, string To,
    long AverageMonthlyConfirmed, int Payers, double TopFiveShare, long MonthToDateConfirmed,
    long OwnNumbers, long NotFromSafaricom,
    IReadOnlyList<VerifiedMonth> MonthList, IReadOnlyList<VerifiedSource> BySource, VerifiedEtims Etims,
    IReadOnlyList<VerifiedFix> Fixes, DateTimeOffset CheckedAt);

public sealed class VerifiedService(ErpDb db)
{
    public static readonly int[] Periods = [3, 6, 12];
    static readonly TimeSpan Eat = TimeSpan.FromHours(3); // months are Kenyan months

    sealed record Row(long Amount, DateTimeOffset ReceivedAt, PaymentSource Source, bool FromSafaricom, PaymentMethod Method, string PayerPhone);

    /// <summary>The last <paramref name="months"/> whole months plus this month so far.</summary>
    public async Task<VerifiedSummary> Summary(int months, Guid? companyId = null)
    {
        if (!Periods.Contains(months)) throw new AppException("Choose 3, 6 or 12 months.");
        var targetCompanyId = companyId ?? db.CompanyId;
        var today = Clock.Today;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var first = thisMonth.AddMonths(-months);
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), Eat);

        var query = companyId.HasValue
            ? db.Payments.IgnoreQueryFilters().Where(p => p.CompanyId == targetCompanyId).AsNoTracking()
            : db.Payments.AsNoTracking();
        if (db.Database.IsNpgsql()) query = query.Where(p => p.ReceivedAt >= from);
        var rows = (await query.Select(p => new Row(p.Amount, p.ReceivedAt, p.Source, p.FromSafaricom, p.Method, p.PayerPhone)).ToListAsync())
            .Where(p => p.ReceivedAt >= from).ToList(); // SQLite can't compare DateTimeOffset in a query

        static DateOnly MonthOf(DateTimeOffset at) { var d = at.ToOffset(Eat); return new DateOnly(d.Year, d.Month, 1); }
        var confirmed = rows.Where(r => r.FromSafaricom).ToList();
        var full = confirmed.Where(r => MonthOf(r.ReceivedAt) < thisMonth).ToList();

        var monthList = Enumerable.Range(0, months + 1).Select(i => first.AddMonths(i)).Select(m =>
        {
            var all = rows.Where(r => MonthOf(r.ReceivedAt) == m).ToList();
            var ok = all.Where(r => r.FromSafaricom).ToList();
            var sum = ok.Sum(r => r.Amount);
            var allSum = all.Sum(r => r.Amount);
            return new VerifiedMonth(m.ToString("yyyy-MM"), m == thisMonth, sum, allSum, Share(sum, allSum), Payers(ok), TopFive(ok));
        }).ToList();

        var companyQuery = companyId.HasValue
            ? db.Companies.IgnoreQueryFilters().AsNoTracking()
            : db.Companies.AsNoTracking();
        var company = await companyQuery.SingleAsync(c => c.Id == targetCompanyId);

        var usersQuery = companyId.HasValue
            ? db.Users.IgnoreQueryFilters().Where(u => u.CompanyId == targetCompanyId).AsNoTracking()
            : db.Users.AsNoTracking();
        var own = (await usersQuery.Select(u => u.Phone).ToListAsync()).Append(company.Phone)
            .Select(Phones.ToMsisdn).OfType<string>().ToHashSet();
        var ownNumbers = full.Where(r => Phones.ToMsisdn(r.PayerPhone) is { } m && own.Contains(m)).Sum(r => r.Amount);
        var notFromSafaricom = rows.Where(r => r.Source is PaymentSource.MpesaConfirmation or PaymentSource.MpesaPrompt && !r.FromSafaricom).Sum(r => r.Amount);

        var bySource = rows.GroupBy(r => r.Source).OrderByDescending(g => g.Sum(r => r.Amount))
            .Select(g => new VerifiedSource(g.Key, g.Sum(r => r.Amount), g.Count(), g.Where(r => r.FromSafaricom).Sum(r => r.Amount))).ToList();

        var etims = await Etims(company, first, today, targetCompanyId, companyId.HasValue);
        var summary = new VerifiedSummary(
            months, first.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"),
            full.Sum(r => r.Amount) / months, Payers(full), TopFive(full), monthList[^1].Confirmed,
            ownNumbers, notFromSafaricom, monthList, bySource, etims, [], DateTimeOffset.UtcNow);
        return summary with { Fixes = Fixes(summary, rows, confirmed) };
    }

    async Task<VerifiedEtims> Etims(Company company, DateOnly from, DateOnly to, Guid targetCompanyId, bool ignoreFilters)
    {
        if (!company.VatRegistered) return new VerifiedEtims(false, 0, 0, 0, 0, 0);
        var query = ignoreFilters
            ? db.SalesDocs.IgnoreQueryFilters().Where(d => d.CompanyId == targetCompanyId).AsNoTracking()
            : db.SalesDocs.AsNoTracking();
        var docs = await query
            .Where(d => d.Kind == SalesDocKind.Invoice && d.Status != SalesDocStatus.Draft && d.Status != SalesDocStatus.Void && d.IssueDate >= from && d.IssueDate <= to)
            .Select(d => new { d.EtimsStatus, d.EtimsControlCode }).ToListAsync();
        var issued = docs.Count(d => d.EtimsStatus != EtimsStatus.NotRequired);
        var signed = docs.Count(d => d.EtimsStatus == EtimsStatus.Signed && !d.EtimsControlCode.StartsWith("TEST-"));
        var test = docs.Count(d => d.EtimsStatus == EtimsStatus.Signed && d.EtimsControlCode.StartsWith("TEST-"));
        var waiting = docs.Count(d => d.EtimsStatus is EtimsStatus.Pending or EtimsStatus.Failed);
        return new VerifiedEtims(true, issued, signed, test, waiting, Share(signed, issued));
    }

    /// <summary>What would make the proof stronger, in the owner's words, each with where to fix it.</summary>
    static List<VerifiedFix> Fixes(VerifiedSummary s, List<Row> rows, List<Row> confirmed)
    {
        var fixes = new List<VerifiedFix>();
        var all = rows.Sum(r => r.Amount);
        var monthAgo = DateTimeOffset.UtcNow.AddDays(-30);
        if (!confirmed.Any(r => r.ReceivedAt >= monthAgo) && rows.Any(r => r.Method == PaymentMethod.Mpesa))
            fixes.Add(new("connect-mpesa", "You take M-Pesa, but no payment in the last 30 days came from Safaricom. Connect your Paybill or Till confirmations so they count.", "/settings", "Connect M-Pesa"));
        var typed = rows.Where(r => r.Source is PaymentSource.Manual or PaymentSource.StatementImport or PaymentSource.Unknown).Sum(r => r.Amount);
        if (all > 0 && typed * 5 > all)
            fixes.Add(new("typed-share", $"{Percent(typed, all)} of money in was entered by hand or uploaded, so it doesn't count as proof.", "/money-in", "See money in"));
        if (s.NotFromSafaricom > 0)
            fixes.Add(new("not-from-safaricom", $"{Money(s.NotFromSafaricom)} arrived as M-Pesa messages that didn't come from Safaricom's servers, so it doesn't count.", "/money-in", "See money in"));
        if (s.Payers >= 5 && s.TopFiveShare > 0.5)
            fixes.Add(new("concentration", $"{Percent(s.TopFiveShare)} of confirmed money comes from five numbers. Lenders may ask about this.", null, null));
        if (s.OwnNumbers > 0)
            fixes.Add(new("own-numbers", $"{Money(s.OwnNumbers)} came from your own or your staff's phone numbers. Lenders may not count it as customer money.", null, null));
        if (s.Etims.TestSigned > 0)
            fixes.Add(new("etims-test", $"{s.Etims.TestSigned} invoices were signed in test mode. They'll be signed for real once KRA certifies Kabuor ERP.", null, null));
        if (s.Etims.FailedOrPending > 0)
            fixes.Add(new("etims-unsigned", $"{s.Etims.FailedOrPending} invoices aren't signed by KRA yet.", "/invoices", "See invoices"));
        return fixes;
    }

    static int Payers(List<Row> rows) => rows.Select(r => Phones.ToMsisdn(r.PayerPhone)).OfType<string>().Distinct().Count();

    /// <summary>Share of the money that came from the five biggest payers (payments without a number count as nobody's).</summary>
    static double TopFive(List<Row> rows)
    {
        var total = rows.Sum(r => r.Amount);
        var top = rows.Where(r => Phones.ToMsisdn(r.PayerPhone) is not null).GroupBy(r => Phones.ToMsisdn(r.PayerPhone)).Select(g => g.Sum(r => r.Amount)).OrderDescending().Take(5).Sum();
        return Share(top, total);
    }

    static double Share(long part, long whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 4);
    static string Percent(long part, long whole) => Percent(Share(part, whole));
    static string Percent(double share) => $"{Math.Round(share * 100):0}%";
    static string Money(long cents) => $"KSh {cents / 100:N0}";
}
