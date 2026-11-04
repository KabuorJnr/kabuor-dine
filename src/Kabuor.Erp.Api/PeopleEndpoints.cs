// Employees, leave and payroll. Every endpoint here, including reads, needs the "people" policy:
// salaries are private, so a Viewer or Sales login sees none of it.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payroll;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record StartRunInput(string Month); // "2026-10"
public sealed record PreviewPayInput(long Basic, long? TaxableAllowances, long? ExtraEarnings, long? NonTaxableAllowances, long? OtherDeductions, string? Month);

public static class PeopleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var people = api.MapGroup("").RequireAuthorization(Policies.People);

        // ---------- employees ----------
        people.MapGet("/employees", async (ErpDb db, string? q, string? filter) =>
        {
            var today = Clock.Today;
            var all = await db.Employees.OrderBy(e => e.Name).ToListAsync();
            var away = await db.LeaveRequests.Where(l => l.Status == LeaveStatus.Approved && l.From <= today && l.To >= today).Select(l => l.EmployeeId).ToListAsync();
            var rows = all.Select(e => new
            {
                e.Id, e.Number, e.Name, e.JobTitle, e.Department, e.Phone, e.BasicSalary, e.TaxableAllowances, e.StartDate, e.EndDate,
                // Still with the business unless they've left: someone starting next week is a colleague, not a leaver.
                active = e.EndDate == null || e.EndDate >= today, startsLater = e.StartDate > today, onLeave = away.Contains(e.Id),
            }).ToList();
            var counts = new { all = rows.Count(r => r.active), onLeave = rows.Count(r => r.onLeave), left = rows.Count(r => !r.active) };
            var shown = rows.Where(r => filter switch { "on-leave" => r.onLeave, "left" => !r.active, _ => r.active })
                .Where(r => string.IsNullOrWhiteSpace(q) || $"{r.Name} {r.Number} {r.JobTitle} {r.Department} {r.Phone}".Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new { counts, monthlyPay = rows.Where(r => r.active).Sum(r => r.BasicSalary + r.TaxableAllowances), items = shown };
        });
        people.MapGet("/employees/{id:guid}", async (Guid id, ErpDb db, PeopleService svc) =>
        {
            var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
            var leave = await db.LeaveRequests.Where(l => l.EmployeeId == id).OrderByDescending(l => l.From).Take(50).ToListAsync();
            var payslips = await db.Payslips.Join(db.PayrollRuns, s => s.PayrollRunId, r => r.Id, (s, r) => new { s, r })
                .Where(x => x.s.EmployeeId == id).OrderByDescending(x => x.r.Period).Take(24)
                .Select(x => new { runId = x.r.Id, period = x.r.Period, status = x.r.Status, x.s.Gross, x.s.Paye, x.s.Net }).ToListAsync();
            var preview = Statutory.Calculate(new PayInput(e.BasicSalary, e.TaxableAllowances, 0, e.NonTaxableAllowances, 0), Statutory.For(Clock.Today));
            return new { employee = e, leave, leaveBalance = await svc.LeaveBalance(e), payslips, monthlyPay = preview };
        });
        people.MapPost("/employees", async (EmployeeInput input, PeopleService svc) => Results.Created("", await svc.SaveEmployee(null, input)));
        people.MapPut("/employees/{id:guid}", (Guid id, EmployeeInput input, PeopleService svc) => svc.SaveEmployee(id, input));
        // "What would this salary come to?" before saving or offering it.
        people.MapPost("/payroll/preview", (PreviewPayInput p) =>
        {
            var month = p.Month is { Length: 7 } m ? DateOnly.ParseExact($"{m}-01", "yyyy-MM-dd") : Clock.Today;
            return Statutory.Calculate(new PayInput(p.Basic, p.TaxableAllowances ?? 0, p.ExtraEarnings ?? 0, p.NonTaxableAllowances ?? 0, p.OtherDeductions ?? 0), Statutory.For(month));
        });

        // ---------- leave ----------
        people.MapGet("/leave", async (ErpDb db, string? status) =>
        {
            var q = db.LeaveRequests.AsQueryable();
            var counts = new { pending = await q.CountAsync(l => l.Status == LeaveStatus.Pending), approved = await q.CountAsync(l => l.Status == LeaveStatus.Approved), all = await q.CountAsync() };
            q = status switch { "pending" => q.Where(l => l.Status == LeaveStatus.Pending), "approved" => q.Where(l => l.Status == LeaveStatus.Approved), _ => q };
            var rows = await q.OrderByDescending(l => l.From).Take(200)
                .Select(l => new { l.Id, l.EmployeeId, employee = l.Employee!.Name, l.Type, l.From, l.To, l.Days, l.Status, l.Note, l.DecidedBy }).ToListAsync();
            return new { counts, items = rows };
        });
        people.MapPost("/leave", async (LeaveInput input, PeopleService svc) => Results.Created("", await svc.RequestLeave(input)));
        people.MapPost("/leave/{id:guid}/approve", async (Guid id, PeopleService svc) => await svc.DecideLeave(id, true));
        people.MapPost("/leave/{id:guid}/reject", async (Guid id, PeopleService svc) => await svc.DecideLeave(id, false));

        // ---------- payroll ----------
        people.MapGet("/payroll/runs", async (ErpDb db) =>
            (await db.PayrollRuns.Include(r => r.Payslips).OrderByDescending(r => r.Period).Take(36).ToListAsync())
                .Select(r => new
                {
                    r.Id, r.Period, r.Status, r.Remitted, people = r.Payslips.Count,
                    gross = r.Payslips.Sum(p => p.Gross), net = r.Payslips.Sum(p => p.Net), paye = r.Payslips.Sum(p => p.Paye),
                    employerCost = r.Payslips.Sum(p => p.Gross + p.NonTaxableAllowances + p.NssfEmployer + p.HousingLevyEmployer + p.Nita),
                }));
        people.MapGet("/payroll/runs/{id:guid}", async (Guid id, PeopleService svc) =>
        {
            var r = await svc.GetRun(id);
            var due = r.Period.AddMonths(1).AddDays(8); // the 9th of the next month
            var remitted = r.Remitted.Split(',', StringSplitOptions.RemoveEmptyEntries);
            return new
            {
                run = r,
                payslips = r.Payslips.OrderBy(p => p.EmployeeName),
                statutory = PeopleService.KindsFor(r).Select(k => new { kind = k, label = k.StartsWith("TO:") ? k[3..] : k, amount = PeopleService.Owed(r, k), paid = remitted.Contains(k), due }),
                totals = new
                {
                    gross = r.Payslips.Sum(p => p.Gross), net = r.Payslips.Sum(p => p.Net), paye = r.Payslips.Sum(p => p.Paye),
                    employerCost = r.Payslips.Sum(p => p.NssfEmployer + p.HousingLevyEmployer + p.Nita),
                },
            };
        });
        people.MapPost("/payroll/runs", async (StartRunInput input, PeopleService svc) =>
        {
            if (!DateOnly.TryParseExact($"{input.Month}-01", "yyyy-MM-dd", out var month)) throw new AppException("Choose a month.");
            return Results.Created("", await svc.StartRun(month));
        });
        people.MapPut("/payroll/runs/{id:guid}/payslips/{slip:guid}", async (Guid id, Guid slip, PayslipEdit edit, PeopleService svc) => await svc.EditPayslip(id, slip, edit));
        people.MapDelete("/payroll/runs/{id:guid}", async (Guid id, PeopleService svc) =>
        {
            await svc.DeleteDraft(id);
            return Results.NoContent();
        });
        people.MapPost("/payroll/runs/{id:guid}/approve", async (Guid id, PeopleService svc) => await svc.Approve(id)).RequireAuthorization(Policies.Finance);
        people.MapPost("/payroll/runs/{id:guid}/pay", async (Guid id, PayrollPayment pay, PeopleService svc) => await svc.MarkPaid(id, pay)).RequireAuthorization(Policies.Finance);
        people.MapPost("/payroll/runs/{id:guid}/remit", async (Guid id, RemitInput input, PeopleService svc) => await svc.Remit(id, input)).RequireAuthorization(Policies.Finance);
        people.MapGet("/payroll/runs/{id:guid}/p10.csv", async (Guid id, PeopleService svc) =>
        {
            var r = await svc.GetRun(id);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(await svc.P10Csv(id)), "text/csv", $"PAYE-{r.Period:yyyy-MM}.csv");
        });
        people.MapGet("/employees/{id:guid}/p9", (Guid id, int? year, PeopleService svc) => svc.P9(id, year ?? Clock.Today.Year));

        // ---------- agreed deductions (HELB, SACCO, loans…) ----------
        people.MapGet("/employees/{id:guid}/deductions", (Guid id, DeductionService s) => s.ForEmployee(id));
        people.MapPost("/employees/{id:guid}/deductions", async (Guid id, DeductionInput input, DeductionService s) => Results.Created("", await s.Save(id, null, input)));
        people.MapPut("/employees/{id:guid}/deductions/{did:guid}", (Guid id, Guid did, DeductionInput input, DeductionService s) => s.Save(id, did, input));
        people.MapPost("/employees/{id:guid}/deductions/{did:guid}/stop", (Guid id, Guid did, DeductionService s) => s.Stop(id, did));
    }
}
