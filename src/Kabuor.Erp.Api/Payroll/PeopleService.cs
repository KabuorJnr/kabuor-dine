// Employees, leave and payroll.
//   Approving a payroll posts:  Dr Salaries and wages (gross)            Dr Employer NSSF/Housing Levy/NITA
//                               Cr PAYE, NSSF, SHIF, Housing Levy, NITA owed     Cr Salaries to pay (net)
//   Agreed deductions:          Dr Salaries to pay     Cr Money staff owe us (loans, advances being repaid)
//                                                      Cr Deductions to pay over (HELB, SACCO, insurance, pension)
//   Paying salaries:            Dr Salaries to pay     Cr M-Pesa / Bank / Cash
//   Paying KRA/NSSF/SHA:        Dr the amount owed     Cr M-Pesa / Bank
using System.Text;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Payroll;

public sealed record EmployeeInput(
    string Name, string? Phone, string? Email, string? KraPin, string? NationalId, string? NssfNumber, string? ShaNumber,
    string? JobTitle, string? Department, DateOnly? StartDate, DateOnly? EndDate, long BasicSalary, long? TaxableAllowances,
    long? NonTaxableAllowances, PayMethod? PayMethod, string? PayPhone, string? BankName, string? BankAccount, decimal? AnnualLeaveDays);
public sealed record LeaveInput(Guid EmployeeId, LeaveType Type, DateOnly From, DateOnly To, string? Note);
public sealed record PayslipEdit(long? ExtraEarnings, long? OtherDeductions, string? Note);
public sealed record PayrollPayment(PaymentMethod Method, string? Reference);
public sealed record RemitInput(string Kind, PaymentMethod Method, string? Reference);

public sealed class PeopleService(ErpDb db, Ledger ledger, Numbers numbers, RequestContext ctx)
{
    // ---------- employees ----------
    public async Task<Employee> SaveEmployee(Guid? id, EmployeeInput input)
    {
        var e = id is { } eid ? await db.Employees.SingleOrDefaultAsync(x => x.Id == eid) ?? throw new AppException("Not found.", 404) : new Employee { Number = await numbers.Next("E"), Name = "" };
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Enter the employee's name.");
        if (input.BasicSalary < 0 || input.TaxableAllowances < 0 || input.NonTaxableAllowances < 0) throw new AppException("Pay can't be negative.");
        var pin = (input.KraPin ?? "").Trim().ToUpperInvariant();
        if (pin.Length > 0 && !Etims.SandboxEtimsClient.IsKraPin(pin)) throw new AppException("That KRA PIN doesn't look right (e.g. A012345678Z).");
        var phone = string.IsNullOrWhiteSpace(input.Phone) ? "" : Phones.ToMsisdn(input.Phone) ?? throw new AppException("That phone number doesn't look right.");
        var payPhone = string.IsNullOrWhiteSpace(input.PayPhone) ? phone : Phones.ToMsisdn(input.PayPhone) ?? throw new AppException("The M-Pesa number for pay doesn't look right.");
        var method = input.PayMethod ?? PayMethod.Mpesa;
        if (method == PayMethod.Mpesa && payPhone.Length == 0) throw new AppException("Add the M-Pesa number their salary goes to.");
        if (method == PayMethod.Bank && string.IsNullOrWhiteSpace(input.BankAccount)) throw new AppException("Add their bank and account number.");
        var start = input.StartDate ?? e.StartDate;
        if (start == default) start = Clock.Today;
        if (input.EndDate is { } end && end < start) throw new AppException("The leaving date is before the start date.");
        (e.Name, e.Phone, e.Email, e.KraPin, e.NationalId, e.NssfNumber, e.ShaNumber, e.JobTitle, e.Department) =
            (name, phone, (input.Email ?? "").Trim(), pin, (input.NationalId ?? "").Trim(), (input.NssfNumber ?? "").Trim(), (input.ShaNumber ?? "").Trim(), (input.JobTitle ?? "").Trim(), (input.Department ?? "").Trim());
        (e.StartDate, e.EndDate, e.BasicSalary, e.TaxableAllowances, e.NonTaxableAllowances) = (start, input.EndDate, input.BasicSalary, input.TaxableAllowances ?? 0, input.NonTaxableAllowances ?? 0);
        (e.PayMethod, e.PayPhone, e.BankName, e.BankAccount) = (method, payPhone, (input.BankName ?? "").Trim(), (input.BankAccount ?? "").Trim());
        e.AnnualLeaveDays = Math.Clamp(input.AnnualLeaveDays ?? e.AnnualLeaveDays, 0, 60);
        if (id is null) db.Employees.Add(e);
        // Someone who has left loses their self-service login the same day.
        if (e.EndDate is { } left && left <= Clock.Today && await db.Users.SingleOrDefaultAsync(u => u.EmployeeId == e.Id) is { Active: true } login)
            login.Active = false;
        await db.SaveChangesAsync();
        return e;
    }

    // ---------- leave ----------
    /// <summary>Monday to Friday days in a range (public holidays are left to the person approving).</summary>
    public static decimal WorkingDays(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1)) if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        return n;
    }

    /// <summary>Annual leave earned this calendar year (1/12 of the entitlement per month worked) less what's been taken.</summary>
    public async Task<object> LeaveBalance(Employee e)
    {
        var today = Clock.Today;
        var yearStart = new DateOnly(today.Year, 1, 1);
        var from = e.StartDate > yearStart ? e.StartDate : yearStart;
        var months = Math.Max(0, (today.Year - from.Year) * 12 + today.Month - from.Month + (today.Day >= from.Day ? 1 : 0));
        var earned = Math.Round(e.AnnualLeaveDays / 12 * Math.Min(12, months), 1);
        var taken = (await db.LeaveRequests.Where(l => l.EmployeeId == e.Id && l.Type == LeaveType.Annual && l.Status == LeaveStatus.Approved && l.From >= yearStart).Select(l => l.Days).ToListAsync()).Sum();
        return new { entitlement = e.AnnualLeaveDays, earned, taken, left = earned - taken };
    }

    public async Task<LeaveRequest> RequestLeave(LeaveInput input)
    {
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == input.EmployeeId) ?? throw new AppException("Choose an employee.");
        if (input.To < input.From) throw new AppException("The leave ends before it starts.");
        var days = WorkingDays(input.From, input.To);
        if (days == 0) throw new AppException("Those dates are all weekend days.");
        if (await db.LeaveRequests.AnyAsync(l => l.EmployeeId == e.Id && (l.Status == LeaveStatus.Pending || l.Status == LeaveStatus.Approved) && l.From <= input.To && l.To >= input.From))
            throw new AppException($"{e.Name} already has leave on some of those days.");
        var l = new LeaveRequest { EmployeeId = e.Id, Type = input.Type, From = input.From, To = input.To, Days = days, Note = (input.Note ?? "").Trim() };
        db.LeaveRequests.Add(l);
        await db.SaveChangesAsync();
        return l;
    }

    public async Task<LeaveRequest> DecideLeave(Guid id, bool approve)
    {
        var l = await db.LeaveRequests.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (l.Status != LeaveStatus.Pending) throw new AppException("This request has already been decided.");
        (l.Status, l.DecidedBy) = (approve ? LeaveStatus.Approved : LeaveStatus.Rejected, ctx.Actor);
        await db.SaveChangesAsync();
        return l;
    }

    // ---------- payroll ----------
    static Payslip Fill(Payslip s, StatutoryRates rates)
    {
        var r = Statutory.Calculate(new PayInput(s.Basic, s.TaxableAllowances, s.ExtraEarnings, s.NonTaxableAllowances, s.OtherDeductions, s.Pension), rates);
        (s.Gross, s.NssfEmployee, s.NssfEmployer, s.Shif, s.HousingLevyEmployee, s.HousingLevyEmployer) = (r.Gross, r.NssfEmployee, r.NssfEmployer, r.Shif, r.HousingLevyEmployee, r.HousingLevyEmployer);
        (s.Taxable, s.PayeBeforeRelief, s.PersonalRelief, s.Paye, s.Nita, s.Net) = (r.Taxable, r.PayeBeforeRelief, r.PersonalRelief, r.Paye, r.Nita, r.Net);
        return s;
    }

    /// <summary>Puts the month's deductions on the payslip: pension before tax, the rest after.</summary>
    static void SetLines(Payslip s, List<DeductionLine> lines, StatutoryRates rates)
    {
        s.DeductionLines = DeductionService.Write(lines);
        s.Pension = lines.Where(l => l.Kind == DeductionKind.Pension).Sum(l => l.Amount);
        s.OtherDeductions = lines.Where(l => l.Kind != DeductionKind.Pension).Sum(l => l.Amount);
        Fill(s, rates);
    }

    public async Task<PayrollRun> GetRun(Guid id) =>
        await db.PayrollRuns.Include(r => r.Payslips).SingleOrDefaultAsync(r => r.Id == id) ?? throw new AppException("Not found.", 404);

    /// <summary>Starts a month's payroll with everyone employed in that month, from their saved pay.</summary>
    public async Task<PayrollRun> StartRun(DateOnly month)
    {
        var period = new DateOnly(month.Year, month.Month, 1);
        var end = period.AddMonths(1).AddDays(-1);
        if (period > Clock.Today.AddMonths(1)) throw new AppException("That month is too far ahead.");
        if (await db.PayrollRuns.AnyAsync(r => r.Period == period)) throw new AppException($"There's already a payroll for {period:MMMM yyyy}.", 409);
        var rates = Statutory.For(period);
        var staff = await db.Employees.Where(e => e.StartDate <= end && (e.EndDate == null || e.EndDate >= period)).OrderBy(e => e.Name).ToListAsync();
        if (staff.Count == 0) throw new AppException("Add your employees first.");
        var run = new PayrollRun { Period = period };
        var ids = staff.Select(e => e.Id).ToList();
        var agreed = (await db.EmployeeDeductions.Where(d => ids.Contains(d.EmployeeId) && d.Status == DeductionStatus.Active).ToListAsync()).ToLookup(d => d.EmployeeId);
        foreach (var e in staff)
        {
            var s = Fill(new Payslip { EmployeeId = e.Id, EmployeeName = e.Name, Basic = e.BasicSalary, TaxableAllowances = e.TaxableAllowances, NonTaxableAllowances = e.NonTaxableAllowances }, rates);
            SetLines(s, DeductionService.Plan(agreed[e.Id], period, s.Net, s.Gross), rates);
            run.Payslips.Add(s);
        }
        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    public async Task<PayrollRun> EditPayslip(Guid runId, Guid payslipId, PayslipEdit edit)
    {
        var run = await GetRun(runId);
        if (run.Status != PayrollStatus.Draft) throw new AppException("This payroll has been approved; it can't change now.");
        var s = run.Payslips.SingleOrDefault(p => p.Id == payslipId) ?? throw new AppException("Not found.", 404);
        if (edit.ExtraEarnings < 0 || edit.OtherDeductions < 0) throw new AppException("Amounts can't be negative.");
        s.ExtraEarnings = edit.ExtraEarnings ?? s.ExtraEarnings;
        s.Note = (edit.Note ?? s.Note).Trim();
        var rates = Statutory.For(run.Period);
        var lines = DeductionService.Lines(s);
        var oneOff = edit.OtherDeductions ?? lines.Where(l => l.DeductionId is null).Sum(l => l.Amount);
        lines.RemoveAll(l => l.DeductionId is null);
        if (oneOff > 0) lines.Add(new DeductionLine(null, DeductionKind.Advance, "One-off deduction (advance repaid)", "", oneOff, null));
        SetLines(s, lines, rates);
        if (s.Net < 0) throw new AppException($"Deductions are more than {s.EmployeeName}'s pay this month.");
        if (oneOff > 0 && s.Net < (s.Gross + 2) / 3) throw new AppException($"That would leave {s.EmployeeName} less than a third of their pay, which the Employment Act (s.19) doesn't allow. Take less this month.");
        await db.SaveChangesAsync();
        return run;
    }

    public async Task DeleteDraft(Guid runId)
    {
        var run = await GetRun(runId);
        if (run.Status != PayrollStatus.Draft) throw new AppException("Only a draft payroll can be deleted.");
        db.PayrollRuns.Remove(run);
        await db.SaveChangesAsync();
    }

    /// <summary>Approves the month: posts pay, deductions and employer costs to the books.</summary>
    public async Task<PayrollRun> Approve(Guid runId)
    {
        var run = await GetRun(runId);
        if (run.Status != PayrollStatus.Draft) throw new AppException("Already approved.");
        var p = run.Payslips;
        // A very small salary can be less than the SHIF minimum: a person must look at that before anyone is paid.
        if (p.FirstOrDefault(x => x.Net < 0) is { } short_)
            throw new AppException($"{short_.EmployeeName}'s deductions are more than their pay this month. Check their pay before approving.");
        var date = run.Period.AddMonths(1).AddDays(-1);
        await ledger.Post(date, $"Payroll {run.Period:MMMM yyyy}", "Payroll", run.Id,
            (SystemAccounts.Salaries, p.Sum(x => x.Gross + x.NonTaxableAllowances), 0),
            (SystemAccounts.EmployerStatutory, p.Sum(x => x.NssfEmployer + x.HousingLevyEmployer + x.Nita), 0),
            (SystemAccounts.PayePayable, 0, p.Sum(x => x.Paye)),
            (SystemAccounts.NssfPayable, 0, p.Sum(x => x.NssfEmployee + x.NssfEmployer)),
            (SystemAccounts.ShifPayable, 0, p.Sum(x => x.Shif)),
            (SystemAccounts.HousingLevyPayable, 0, p.Sum(x => x.HousingLevyEmployee + x.HousingLevyEmployer)),
            (SystemAccounts.NitaPayable, 0, p.Sum(x => x.Nita)),
            (SystemAccounts.NetPayPayable, 0, p.Sum(x => x.Net + x.OtherDeductions + x.Pension)));
        // Deductions: loans and advances staff already received are repaid to the business; the rest (HELB, SACCO,
        // insurance, union, pension) is held for whoever it's owed to until it's paid over.
        var lines = p.SelectMany(DeductionService.Lines).ToList();
        long toBusiness = lines.Where(l => l.ToBusiness).Sum(l => l.Amount), toOthers = lines.Where(l => !l.ToBusiness).Sum(l => l.Amount);
        if (toBusiness + toOthers > 0)
            await ledger.Post(date, $"Deductions from pay {run.Period:MMMM yyyy}", "PayrollDeductions", run.Id,
                (SystemAccounts.NetPayPayable, toBusiness + toOthers, 0), (SystemAccounts.StaffAdvances, 0, toBusiness), (SystemAccounts.DeductionsPayable, 0, toOthers));
        // Count what was repaid; a loan or advance fully repaid stops by itself.
        var taken = lines.Where(l => l.DeductionId is not null && l.Amount > 0).GroupBy(l => l.DeductionId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        foreach (var d in await db.EmployeeDeductions.Where(d => taken.Keys.Contains(d.Id)).ToListAsync())
        {
            d.Recovered += taken[d.Id];
            if (d.Total is { } total && d.Recovered >= total) d.Status = DeductionStatus.Completed;
        }
        (run.Status, run.ApprovedBy) = (PayrollStatus.Approved, ctx.Actor);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Net pay sent to everyone (by M-Pesa bulk payment, bank or cash): recorded here.</summary>
    public async Task<PayrollRun> MarkPaid(Guid runId, PayrollPayment pay)
    {
        var run = await GetRun(runId);
        if (run.Status != PayrollStatus.Approved) throw new AppException(run.Status == PayrollStatus.Paid ? "Already paid." : "Approve the payroll first.");
        var total = run.Payslips.Sum(x => x.Net);
        if (total > 0)
            await ledger.Post(Clock.Today, $"Salaries paid {run.Period:MMMM yyyy} {pay.Reference}".Trim(), "PayrollPaid", run.Id,
                (SystemAccounts.NetPayPayable, total, 0), (SystemAccounts.ForMethod(pay.Method), 0, total));
        (run.Status, run.PaidAt, run.PaidReference) = (PayrollStatus.Paid, DateTimeOffset.UtcNow, (pay.Reference ?? "").Trim().ToUpperInvariant());
        await db.SaveChangesAsync();
        return run;
    }

    public static readonly string[] RemitKinds = ["PAYE", "NSSF", "SHIF", "AHL", "NITA"];
    const string To = "TO:";

    /// <summary>Everything to pay over for the month: the statutory five, then each payee of agreed deductions ("TO:HELB").</summary>
    public static List<string> KindsFor(PayrollRun run) =>
        [.. RemitKinds, .. run.Payslips.SelectMany(DeductionService.Lines).Where(l => !l.ToBusiness && l.Amount > 0).Select(l => To + l.PayTo).Distinct().OrderBy(k => k)];

    public static long Owed(PayrollRun run, string kind) => kind.StartsWith(To)
        ? run.Payslips.SelectMany(DeductionService.Lines).Where(l => !l.ToBusiness && To + l.PayTo == kind).Sum(l => l.Amount)
        : kind switch
    {
        "PAYE" => run.Payslips.Sum(x => x.Paye),
        "NSSF" => run.Payslips.Sum(x => x.NssfEmployee + x.NssfEmployer),
        "SHIF" => run.Payslips.Sum(x => x.Shif),
        "AHL" => run.Payslips.Sum(x => x.HousingLevyEmployee + x.HousingLevyEmployer),
        "NITA" => run.Payslips.Sum(x => x.Nita),
        _ => throw new AppException("Unknown payment."),
    };

    /// <summary>Records paying one statutory amount for the month (due by the 9th of the next month).</summary>
    public async Task<PayrollRun> Remit(Guid runId, RemitInput input)
    {
        var run = await GetRun(runId);
        var kind = (input.Kind ?? "").ToUpperInvariant();
        if (!KindsFor(run).Contains(kind)) throw new AppException("Choose PAYE, NSSF, SHIF, Housing Levy, NITA or one of this month's deduction payees.");
        if (run.Status == PayrollStatus.Draft) throw new AppException("Approve the payroll first.");
        var done = run.Remitted.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (done.Contains(kind)) throw new AppException($"{kind} for {run.Period:MMMM yyyy} is already recorded as paid.");
        var amount = Owed(run, kind);
        var key = kind.StartsWith(To) ? SystemAccounts.DeductionsPayable : kind switch { "PAYE" => SystemAccounts.PayePayable, "NSSF" => SystemAccounts.NssfPayable, "SHIF" => SystemAccounts.ShifPayable, "AHL" => SystemAccounts.HousingLevyPayable, _ => SystemAccounts.NitaPayable };
        if (amount > 0)
            await ledger.Post(Clock.Today, $"{kind} paid for {run.Period:MMMM yyyy} {input.Reference}".Trim(), "Remittance", run.Id, (key, amount, 0), (SystemAccounts.ForMethod(input.Method), 0, amount));
        done.Add(kind);
        run.Remitted = string.Join(',', KindsFor(run).Where(done.Contains));
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>The P9: an employee's year, month by month, for their tax return.</summary>
    public async Task<object> P9(Guid employeeId, int year)
    {
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == employeeId) ?? throw new AppException("Not found.", 404);
        var months = await db.Payslips.Join(db.PayrollRuns, s => s.PayrollRunId, r => r.Id, (s, r) => new { s, r })
            .Where(x => x.s.EmployeeId == employeeId && x.r.Status != PayrollStatus.Draft && x.r.Period >= new DateOnly(year, 1, 1) && x.r.Period <= new DateOnly(year, 12, 1))
            .OrderBy(x => x.r.Period)
            .Select(x => new { month = x.r.Period, basic = x.s.Basic, benefits = x.s.TaxableAllowances + x.s.ExtraEarnings, gross = x.s.Gross, nssf = x.s.NssfEmployee, shif = x.s.Shif, housingLevy = x.s.HousingLevyEmployee, taxable = x.s.Taxable, taxCharged = x.s.PayeBeforeRelief, relief = x.s.PersonalRelief, paye = x.s.Paye })
            .ToListAsync();
        return new
        {
            employee = new { e.Name, e.Number, e.KraPin },
            year,
            months,
            totals = new { gross = months.Sum(m => m.gross), nssf = months.Sum(m => m.nssf), shif = months.Sum(m => m.shif), housingLevy = months.Sum(m => m.housingLevy), taxable = months.Sum(m => m.taxable), paye = months.Sum(m => m.paye) },
        };
    }

    /// <summary>A month's PAYE in rows for the iTax P10 return (copy into KRA's template).</summary>
    public async Task<string> P10Csv(Guid runId)
    {
        var run = await GetRun(runId);
        var staff = await db.Employees.Where(e => run.Payslips.Select(p => p.EmployeeId).Contains(e.Id)).ToDictionaryAsync(e => e.Id);
        static string K(long c) => (c / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        static string Q(string s) => CsvCell.Of(s);
        var sb = new StringBuilder("KRA PIN,Employee name,Basic salary,Allowances and other pay,Gross pay,NSSF,SHIF,Housing Levy,Taxable pay,Tax charged,Personal relief,PAYE\n");
        foreach (var s in run.Payslips.OrderBy(p => p.EmployeeName))
            sb.AppendLine(string.Join(',', Q(staff.GetValueOrDefault(s.EmployeeId)?.KraPin ?? ""), Q(s.EmployeeName), K(s.Basic), K(s.TaxableAllowances + s.ExtraEarnings), K(s.Gross), K(s.NssfEmployee), K(s.Shif), K(s.HousingLevyEmployee), K(s.Taxable), K(s.PayeBeforeRelief), K(s.PersonalRelief), K(s.Paye)));
        return sb.ToString();
    }
}
