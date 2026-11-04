// Deductions an employee has agreed to: HELB, SACCO, loans and advances from the business, insurance, union dues,
// voluntary pension. HR sets them up; each one waits for the employee's agreement (in their app, or HR records a
// signed form or HELB's notice). Payroll then takes them every month, in a fixed order, always leaving the employee
// at least a third of their pay (Employment Act, s.19(3)); loans and advances stop by themselves once repaid.
//   Approving payroll:  Dr Salaries to pay   Cr Money staff owe us (loans, advances)
//                                            Cr Deductions to pay over (HELB, SACCO, insurance, union, pension)
//   Paying them over:   Dr Deductions to pay over   Cr M-Pesa / Bank
using System.Text.Json;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Payroll;

public sealed record DeductionInput(DeductionKind Kind, string? Name, string? PayTo, string? Reference, long MonthlyAmount, long? Total,
    string? StartMonth, string? EndMonth, string? Consent, string? Note);
public sealed record DeclineInput(string? Reason);

/// <summary>One deduction on a payslip.</summary>
public sealed record DeductionLine(Guid? DeductionId, DeductionKind Kind, string Name, string PayTo, long Amount, long? Wanted)
{
    /// <summary>Repaying the business (loans, advances, one-off amounts) rather than money held for someone else.</summary>
    public bool ToBusiness => Kind is DeductionKind.Loan or DeductionKind.Advance;
}

public sealed class DeductionService(ErpDb db, RequestContext ctx)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    public static List<DeductionLine> Lines(Payslip s) => JsonSerializer.Deserialize<List<DeductionLine>>(s.DeductionLines, Json) ?? [];
    public static string Write(List<DeductionLine> lines) => JsonSerializer.Serialize(lines, Json);

    /// <summary>The order payroll takes them in when pay is short: what the law or a lender requires first.</summary>
    static int Priority(DeductionKind k) => k switch
    {
        DeductionKind.Pension => 0, DeductionKind.Helb => 1, DeductionKind.Loan => 2, DeductionKind.Advance => 3,
        DeductionKind.Sacco => 4, DeductionKind.Insurance => 5, DeductionKind.Union => 6, _ => 7,
    };

    static readonly Dictionary<DeductionKind, (string Name, string PayTo)> Defaults = new()
    {
        [DeductionKind.Helb] = ("HELB loan repayment", "HELB"),
        [DeductionKind.Sacco] = ("SACCO contribution", ""),
        [DeductionKind.Loan] = ("Loan from the business", ""),
        [DeductionKind.Advance] = ("Salary advance", ""),
        [DeductionKind.Insurance] = ("Insurance", ""),
        [DeductionKind.Union] = ("Union dues", ""),
        [DeductionKind.Pension] = ("Pension contribution", ""),
        [DeductionKind.Other] = ("Deduction", ""),
    };

    static DateOnly Month(string? m, DateOnly fallback) =>
        m is { Length: 7 } && DateOnly.TryParseExact($"{m}-01", "yyyy-MM-dd", out var d) ? d : fallback;

    // ---------- HR ----------

    public async Task<List<EmployeeDeduction>> ForEmployee(Guid employeeId) =>
        await db.EmployeeDeductions.Where(d => d.EmployeeId == employeeId).OrderBy(d => d.Status).ThenBy(d => d.CreatedAt).ToListAsync();

    public async Task<EmployeeDeduction> Save(Guid employeeId, Guid? id, DeductionInput input)
    {
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == employeeId) ?? throw new AppException("Not found.", 404);
        var d = id is { } did
            ? await db.EmployeeDeductions.SingleOrDefaultAsync(x => x.Id == did && x.EmployeeId == employeeId) ?? throw new AppException("Not found.", 404)
            : null;
        if (d is { Status: DeductionStatus.Stopped or DeductionStatus.Completed or DeductionStatus.Declined }) throw new AppException("This deduction has ended. Add a new one instead.");
        if (input.MonthlyAmount <= 0) throw new AppException("Enter the amount to take each month.");
        var isRepayment = input.Kind is DeductionKind.Loan or DeductionKind.Advance;
        if (isRepayment && (input.Total is not > 0)) throw new AppException("Enter the total to recover, so the deduction stops once it's repaid.");
        if (input.Total is { } t && t < input.MonthlyAmount) throw new AppException("The monthly amount is more than the total.");
        var start = Month(input.StartMonth, new DateOnly(Clock.Today.Year, Clock.Today.Month, 1));
        DateOnly? end = input.EndMonth is { Length: 7 } ? Month(input.EndMonth, start) : null;
        if (end < start) throw new AppException("The last month is before the first.");
        var consent = (input.Consent ?? "app").Trim().ToLowerInvariant();
        if (consent == "helb" && input.Kind != DeductionKind.Helb) throw new AppException("A HELB notice only covers HELB deductions.");
        if (consent == "app" && !await db.Users.AnyAsync(u => u.EmployeeId == e.Id && u.Active))
            throw new AppException($"{e.Name} doesn't have the app. Give them access on their page, or record their signed form instead.");

        var (defName, defPayTo) = Defaults[input.Kind];
        var raised = d is not null && input.MonthlyAmount > d.MonthlyAmount;
        d ??= db.EmployeeDeductions.Add(new EmployeeDeduction { EmployeeId = e.Id, Name = "", CreatedBy = ctx.Actor }).Entity;
        d.Kind = input.Kind;
        d.Name = string.IsNullOrWhiteSpace(input.Name) ? defName : input.Name.Trim();
        d.PayTo = isRepayment ? "" : string.IsNullOrWhiteSpace(input.PayTo) ? defPayTo : input.PayTo.Trim();
        if (!isRepayment && d.PayTo.Length == 0) throw new AppException("Who is this paid to? (e.g. the SACCO or insurer's name)");
        d.Reference = (input.Reference ?? "").Trim();
        d.MonthlyAmount = input.MonthlyAmount;
        d.Total = input.Total;
        (d.StartMonth, d.EndMonth) = (start, end);
        d.Note = (input.Note ?? "").Trim();

        // New, or more than they agreed to: needs their agreement again. Less, or a name change: no need.
        if (d.Status == DeductionStatus.AwaitingEmployee || raised || d.ConsentAt is null)
        {
            if (consent is "form" or "helb")
                (d.Status, d.Consent, d.ConsentAt) = (DeductionStatus.Active, consent == "helb" ? $"HELB notice, recorded by {ctx.Actor}" : $"Signed form, recorded by {ctx.Actor}", DateTimeOffset.UtcNow);
            else
                (d.Status, d.Consent, d.ConsentAt) = (DeductionStatus.AwaitingEmployee, "", null);
        }
        await db.SaveChangesAsync();
        return d;
    }

    public async Task<EmployeeDeduction> Stop(Guid employeeId, Guid id)
    {
        var d = await db.EmployeeDeductions.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId) ?? throw new AppException("Not found.", 404);
        if (d.Status is DeductionStatus.Active or DeductionStatus.AwaitingEmployee) d.Status = DeductionStatus.Stopped;
        await db.SaveChangesAsync();
        return d;
    }

    // ---------- the employee ----------

    public async Task<EmployeeDeduction> Decide(Guid employeeId, Guid id, bool agree, string? reason)
    {
        var d = await db.EmployeeDeductions.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == employeeId) ?? throw new AppException("Not found.", 404);
        if (d.Status != DeductionStatus.AwaitingEmployee) throw new AppException("This has already been decided. Talk to HR to change it.");
        if (agree) (d.Status, d.Consent, d.ConsentAt) = (DeductionStatus.Active, "In the app", DateTimeOffset.UtcNow);
        else (d.Status, d.Note) = (DeductionStatus.Declined, string.IsNullOrWhiteSpace(reason) ? d.Note : $"Declined: {reason.Trim()}");
        await db.SaveChangesAsync();
        return d;
    }

    // ---------- payroll ----------

    /// <summary>The deductions to take this month and how much of each: never leaving less than a third of pay.</summary>
    public static List<DeductionLine> Plan(IEnumerable<EmployeeDeduction> agreed, DateOnly period, long netBefore, long gross)
    {
        var keep = (gross + 2) / 3; // at least a third of wages stays with the employee
        var room = Math.Max(0, netBefore - keep);
        var lines = new List<DeductionLine>();
        foreach (var d in agreed.Where(d => d.Status == DeductionStatus.Active && d.StartMonth <= period && (d.EndMonth is null || d.EndMonth >= period) && d.Remaining > 0)
                     .OrderBy(d => Priority(d.Kind)).ThenBy(d => d.CreatedAt))
        {
            var want = Math.Min(d.MonthlyAmount, d.Remaining);
            var take = Math.Min(want, room);
            room -= take;
            lines.Add(new DeductionLine(d.Id, d.Kind, d.Name, d.PayTo, take, take < want ? want : null));
        }
        return lines;
    }
}
