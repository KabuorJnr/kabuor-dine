// Employee self-service ("me"): their own leave, payslips and P9, nothing else. Every query is pinned to the
// signed-in person's employee record; a login without one gets nothing. Until someone sets their own password
// (after HR created or reset it), only the password change works.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payroll;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record MyLeaveInput(LeaveType Type, DateOnly From, DateOnly To, string? Note);
/// <summary>A fault from the portal: the asset by its tag (typed or scanned) or the place.</summary>
public sealed record MyFaultInput(string? AssetTag, Guid? LocationId, string Title, string? Description, string? Symptom, MaintenancePriority? Priority, bool? AssetDown);

public static class SelfEndpoints
{
    /// <summary>The caller's employee record, after checking they're allowed to use self-service right now.</summary>
    static async Task<Employee> Me(ErpDb db, RequestContext ctx)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == ctx.UserId) ?? throw new AppException("Please sign in again.", 401);
        if (user.MustChangePassword) throw new AppException("Set your own password first.", 403);
        if (user.EmployeeId is not { } eid) throw new AppException("Your login isn't linked to an employee record. Ask HR.", 404);
        return await db.Employees.SingleOrDefaultAsync(e => e.Id == eid) ?? throw new AppException("Your employee record wasn't found. Ask HR.", 404);
    }

    static bool Visible(PayrollStatus s) => s != PayrollStatus.Draft; // drafts can still change

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/auth/password", (PasswordInput input, AuthService auth) => auth.ChangePassword(input)).RequireAuthorization();

        var me = app.MapGroup("/api/me").RequireAuthorization();
        me.MapGet("", async (ErpDb db, RequestContext ctx, PeopleService people) =>
        {
            var e = await Me(db, ctx);
            var company = await db.Companies.SingleAsync(c => c.Id == db.CompanyId);
            var leave = await db.LeaveRequests.Where(l => l.EmployeeId == e.Id).OrderByDescending(l => l.From).Take(50).ToListAsync();
            var payslips = await db.Payslips.Join(db.PayrollRuns, s => s.PayrollRunId, r => r.Id, (s, r) => new { s, r })
                .Where(x => x.s.EmployeeId == e.Id && x.r.Status != PayrollStatus.Draft)
                .OrderByDescending(x => x.r.Period).Take(24)
                .Select(x => new { runId = x.r.Id, period = x.r.Period, x.s.Gross, x.s.Net, paid = x.r.Status == PayrollStatus.Paid })
                .ToListAsync();
            var today = Clock.Today;
            return new
            {
                company = company.Name,
                employee = new { e.Name, e.Number, e.JobTitle, e.Department, e.StartDate, e.Phone, e.KraPin, e.NssfNumber, e.ShaNumber, e.PayMethod, e.AnnualLeaveDays },
                leaveBalance = await people.LeaveBalance(e),
                leave,
                upcoming = leave.Where(l => l.Status is LeaveStatus.Approved or LeaveStatus.Pending && l.To >= today).OrderBy(l => l.From),
                payslips,
                deductions = await db.EmployeeDeductions.Where(d => d.EmployeeId == e.Id && d.Status != DeductionStatus.Declined).OrderBy(d => d.Status).ThenBy(d => d.CreatedAt).ToListAsync(),
            };
        });
        me.MapPost("/deductions/{id:guid}/agree", async (Guid id, ErpDb db, RequestContext ctx, DeductionService s) => await s.Decide((await Me(db, ctx)).Id, id, true, null));
        me.MapPost("/deductions/{id:guid}/decline", async (Guid id, DeclineInput input, ErpDb db, RequestContext ctx, DeductionService s) => await s.Decide((await Me(db, ctx)).Id, id, false, input.Reason));
        me.MapPost("/leave", async (MyLeaveInput input, ErpDb db, RequestContext ctx, PeopleService people) =>
        {
            var e = await Me(db, ctx);
            if (input.From < Clock.Today.AddDays(-30)) throw new AppException("That's too far in the past; ask HR to record it.");
            return Results.Created("", await people.RequestLeave(new LeaveInput(e.Id, input.Type, input.From, input.To, input.Note)));
        });
        me.MapPost("/leave/{id:guid}/cancel", async (Guid id, ErpDb db, RequestContext ctx) =>
        {
            var e = await Me(db, ctx);
            var l = await db.LeaveRequests.SingleOrDefaultAsync(x => x.Id == id && x.EmployeeId == e.Id) ?? throw new AppException("Not found.", 404);
            if (l.Status != LeaveStatus.Pending) throw new AppException("Only a request that hasn't been decided can be withdrawn. Ask HR to change approved leave.");
            l.Status = LeaveStatus.Cancelled;
            await db.SaveChangesAsync();
            return l;
        });
        me.MapGet("/payslips/{runId:guid}", async (Guid runId, ErpDb db, RequestContext ctx) =>
        {
            var e = await Me(db, ctx);
            var run = await db.PayrollRuns.SingleOrDefaultAsync(r => r.Id == runId) ?? throw new AppException("Not found.", 404);
            if (!Visible(run.Status)) throw new AppException("Not found.", 404);
            var slip = await db.Payslips.SingleOrDefaultAsync(s => s.PayrollRunId == runId && s.EmployeeId == e.Id) ?? throw new AppException("Not found.", 404);
            return new { period = run.Period, paid = run.Status == PayrollStatus.Paid, slip };
        });
        // Cash advances: ask, follow, account for with receipts; a head of department answers their department's.
        me.MapGet("/advances", async (ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.CashAdvanceService s) => await s.ForEmployee(await Me(db, ctx)));
        me.MapPost("/advances", async (Kabuor.Erp.Operations.AdvanceInput input, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.CashAdvanceService s) => await s.Request(input, (await Me(db, ctx)).Id));
        me.MapPost("/advances/{id:guid}/account", async (Guid id, Kabuor.Erp.Operations.AdvanceAccountInput input, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.CashAdvanceService s) => await s.Account(id, input, (await Me(db, ctx)).Id));
        me.MapPost("/advances/{id:guid}/cancel", async (Guid id, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.CashAdvanceService s) => await s.Cancel(id, (await Me(db, ctx)).Id));
        me.MapPost("/advances/{id:guid}/approve", async (Guid id, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.CashAdvanceService s) => await s.HeadDecision(id, (await Me(db, ctx)).Id, true, null));
        me.MapPost("/advances/{id:guid}/reject", async (Guid id, Kabuor.Erp.Operations.ReasonInput input, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.CashAdvanceService s) => await s.HeadDecision(id, (await Me(db, ctx)).Id, false, input.Reason));
        me.MapGet("/expense-accounts", async (ErpDb db) => await db.Accounts.Where(a => a.Type == AccountType.Expense && a.SystemKey == null || a.SystemKey == "GENERAL_EXP").OrderBy(a => a.Code).Select(a => new { a.Id, a.Name }).ToListAsync());
        me.MapGet("/p9", async (int? year, ErpDb db, RequestContext ctx, PeopleService people) => await people.P9((await Me(db, ctx)).Id, year ?? Clock.Today.Year));

        // Operations from the portal: report what's broken, ask for what the department needs, and (for a head of
        // department) answer the department's requests.
        me.MapGet("/operations", async (ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.RequisitionService reqs, Kabuor.Erp.Operations.DepartmentService departments) =>
        {
            var e = await Me(db, ctx);
            await departments.SeedFromEmployees();
            var depts = await db.Departments.Where(d => d.Active).OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync();
            var places = await db.Locations.Where(l => l.Active).OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync();
            var mine = await db.Assets.Where(a => a.EmployeeId == e.Id && a.Status != AssetStatus.Disposed).OrderBy(a => a.Tag)
                .Select(a => new { a.Id, a.Tag, a.Name, status = a.Status.ToString() }).ToListAsync();
            var faults = await db.FaultReports.Where(f => f.ReportedByEmployeeId == e.Id).OrderByDescending(f => f.CreatedAt).Take(30)
                .Select(f => new { f.Id, f.Number, f.Title, status = f.Status.ToString(), f.RejectReason, f.CreatedAt, f.ResolvedAt }).ToListAsync();
            return new
            {
                departments = depts, myDepartmentId = depts.FirstOrDefault(d => string.Equals(d.Name, e.Department, StringComparison.OrdinalIgnoreCase))?.Id,
                locations = places, assets = mine, faults, requests = await reqs.ForEmployee(e),
                codes = Kabuor.Erp.Operations.MaintenanceService.Modes,
            };
        });
        // What can be asked for: the business's items, with what one costs it (the request's estimate).
        me.MapGet("/catalogue", async (string? q, ErpDb db, RequestContext ctx) =>
        {
            await Me(db, ctx);
            var t = (q ?? "").Trim();
            return await db.Items.Where(i => i.Active && (t == "" || i.Name.Contains(t))).OrderBy(i => i.Name).Take(30)
                .Select(i => new { i.Id, i.Name, i.Unit, estimate = (long)Math.Round(i.AverageCost) }).ToListAsync();
        });
        me.MapPost("/requests", async (Kabuor.Erp.Operations.RequisitionInput input, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.RequisitionService reqs) =>
        {
            var e = await Me(db, ctx);
            var r = await reqs.Create(input with { Submit = input.Submit ?? true, WorkOrderId = null }, e.Id);
            return Results.Created("", new { r.Id, r.Number, status = r.Status.ToString() });
        });
        me.MapPost("/requests/{id:guid}/cancel", async (Guid id, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.RequisitionService reqs) =>
        {
            await Me(db, ctx);
            var r = await reqs.Cancel(id);
            return new { r.Id, status = r.Status.ToString() };
        });
        me.MapPost("/requests/{id:guid}/approve", async (Guid id, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.RequisitionService reqs) =>
        {
            var r = await reqs.HeadDecision(id, (await Me(db, ctx)).Id, true, null);
            return new { r.Id, status = r.Status.ToString() };
        });
        me.MapPost("/requests/{id:guid}/reject", async (Guid id, Kabuor.Erp.Operations.ReasonInput input, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.RequisitionService reqs) =>
        {
            var r = await reqs.HeadDecision(id, (await Me(db, ctx)).Id, false, input.Reason);
            return new { r.Id, status = r.Status.ToString() };
        });
        me.MapPost("/faults", async (MyFaultInput input, ErpDb db, RequestContext ctx, Kabuor.Erp.Operations.MaintenanceService maintenance) =>
        {
            var e = await Me(db, ctx);
            Guid? assetId = null;
            var tag = (input.AssetTag ?? "").Trim().ToUpperInvariant();
            if (tag.Length > 0)
                assetId = await db.Assets.Where(a => a.Tag == tag || a.SerialNumber == tag).Select(a => (Guid?)a.Id).FirstOrDefaultAsync()
                    ?? throw new AppException($"No asset has the tag {tag}. Check the label, or choose where it is instead.");
            var f = await maintenance.Report(new Kabuor.Erp.Operations.FaultInput(assetId, input.LocationId, input.Title, input.Description, input.Symptom, input.Priority, input.AssetDown), e.Id);
            return Results.Created("", new { f.Id, f.Number });
        });

        // HR side: give or take away an employee's self-service login.
        var hr = app.MapGroup("/api/employees/{id:guid}/access").RequireAuthorization(Policies.People);
        hr.MapGet("", async (Guid id, ErpDb db) =>
        {
            var u = await db.Users.SingleOrDefaultAsync(x => x.EmployeeId == id);
            return new { hasAccess = u is { Active: true }, mustChangePassword = u?.MustChangePassword ?? false, phone = u?.Phone };
        });
        hr.MapPost("", (Guid id, AuthService auth) => auth.GrantAccess(id));
        hr.MapDelete("", async (Guid id, AuthService auth) =>
        {
            await auth.RevokeAccess(id);
            return Results.NoContent();
        });
    }
}
