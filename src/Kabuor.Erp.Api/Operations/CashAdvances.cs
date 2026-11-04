// Cash advances (imprest): money given to an employee before they spend it for the business (a trip upcountry, a site
// visit, petty cash for a branch), which they then account for with receipts. The controls are the same as any other
// spending, every step written to the approvals register:
//   the employee asks (portal or app) → their head of department → finance signs off against the department's budget
//   (over it, with a written reason) → finance pays it out (the employee now owes it) → the employee accounts for it:
//   receipts become the department's expenses; what's left is handed back or taken from their next pay; anything they
//   spent of their own money is paid back to them.
// The classic imprest rule applies: nobody gets a new advance while an earlier one is overdue.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record AdvanceInput(Guid? EmployeeId, Guid? DepartmentId, string Purpose, long Amount, DateOnly AccountBy);
public sealed record AdvancePayInput(PaymentMethod Method, string? Reference);
public sealed record AdvanceExpenseInput(string Description, Guid? AccountId, long Amount, string? Receipt, DateOnly? Date);
/// <summary>How the leftover is settled: "returned" (cash handed back, by Method) or "payroll" (taken from the next pay).</summary>
public sealed record AdvanceAccountInput(List<AdvanceExpenseInput> Expenses, string? Leftover, PaymentMethod? Method, string? Reference);

public sealed class CashAdvanceService(ErpDb db, Numbers numbers, Ledger ledger, DepartmentService departments, ApprovalRegister register, RequestContext ctx)
{
    static string Money(long cents) => $"KSh {cents / 100m:N0}";

    async Task<CashAdvance> Load(Guid id) => await db.CashAdvances.Include(a => a.Expenses).SingleOrDefaultAsync(a => a.Id == id) ?? throw new AppException("Not found.", 404);

    /// <summary>A new request: from the employee's portal (for themselves), or by finance/HR for an employee.</summary>
    public async Task<object> Request(AdvanceInput input, Guid? portalEmployeeId = null)
    {
        var employeeId = portalEmployeeId ?? input.EmployeeId ?? throw new AppException("Choose who the advance is for.");
        if (portalEmployeeId is null && !ctx.Can(Policies.Finance) && !ctx.Can(Policies.People)) throw new AppException("Employees ask for advances from their portal.", 403);
        var e = await db.Employees.SingleOrDefaultAsync(x => x.Id == employeeId) ?? throw new AppException("That employee isn't on the list.");
        var purpose = (input.Purpose ?? "").Trim();
        if (purpose.Length < 5) throw new AppException("Say what the money is for, e.g. “Fuel and lodging, Kisumu site visit, 3 days”.");
        if (input.Amount <= 0) throw new AppException("Enter how much you need.");
        if (input.AccountBy < Clock.Today) throw new AppException("The date to account by has passed.");
        if (input.AccountBy > Clock.Today.AddDays(60)) throw new AppException("Advances are accounted for within 60 days.");
        var dept = input.DepartmentId is { } did
            ? await db.Departments.SingleOrDefaultAsync(d => d.Id == did && d.Active) ?? throw new AppException("Choose the department it's for.")
            : await db.Departments.FirstOrDefaultAsync(d => d.Name == e.Department && d.Active) ?? throw new AppException("Choose the department it's charged to.");
        // The imprest rule: account for the last one before taking another.
        var overdue = await db.CashAdvances.Where(a => a.EmployeeId == e.Id && a.Status == AdvanceStatus.PaidOut && a.AccountBy < Clock.Today).Select(a => a.Number).FirstOrDefaultAsync();
        if (overdue is not null) throw new AppException($"{e.Name} hasn't accounted for {overdue}, which is overdue. That comes first.", 409);
        var head = dept.HeadEmployeeId;
        var a = new CashAdvance
        {
            Number = await numbers.Next("ADV"), EmployeeId = e.Id, DepartmentId = dept.Id, Purpose = purpose, Amount = input.Amount, AccountBy = input.AccountBy,
            Status = head is { } h && h != e.Id ? AdvanceStatus.WithHead : AdvanceStatus.WithFinance, RequestedBy = ctx.Actor, RequestedByUserId = ctx.UserId,
        };
        db.CashAdvances.Add(a);
        await register.Record("Advance", a.Id, a.Number, "Asked for", a.Amount, $"{e.Name}: {purpose}");
        await db.SaveChangesAsync();
        return await Get(a.Id);
    }

    public async Task<object> HeadDecision(Guid id, Guid headEmployeeId, bool approve, string? reason)
    {
        var a = await Load(id);
        var head = await db.Departments.Where(d => d.Id == a.DepartmentId).Select(d => d.HeadEmployeeId).SingleAsync();
        if (head != headEmployeeId) throw new AppException("Only the head of the department can answer this.", 403);
        if (a.Status != AdvanceStatus.WithHead) throw new AppException("It isn't waiting for you.");
        var why = (reason ?? "").Trim();
        if (!approve && why.Length < 3) throw new AppException("Say why, so they know.");
        if (approve) (a.Status, a.HeadApprovedBy) = (AdvanceStatus.WithFinance, ctx.Actor);
        else (a.Status, a.RejectReason) = (AdvanceStatus.Rejected, why);
        await register.Record("Advance", a.Id, a.Number, approve ? "Approved by head of department" : "Turned down by head of department", a.Amount, why);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Finance signs off against what's left of the department's budget this month (approved − spent − committed).</summary>
    public async Task<object> SignOff(Guid id, string? note)
    {
        if (!ctx.Can(Policies.Finance)) throw new AppException("Only finance signs off money.", 403);
        var a = await Load(id);
        if (a.Status != AdvanceStatus.WithFinance) throw new AppException(a.Status == AdvanceStatus.WithHead ? "It's waiting for the head of the department." : "It isn't waiting for finance.");
        var position = await departments.Position(a.DepartmentId);
        if (!position.HasBudget)
        {
            var name = await db.Departments.Where(d => d.Id == a.DepartmentId).Select(d => d.Name).SingleAsync();
            throw new AppException($"{name} has no budget approved by finance for {BudgetService.MonthName(Clock.Today)}: no budget, no spending.", 409);
        }
        var over = a.Amount > position.Available;
        var why = (note ?? "").Trim();
        if (over && why.Length < 10) throw new AppException($"This is {Money(a.Amount - Math.Max(0, position.Available))} over what's left of the budget ({Money(Math.Max(0, position.Available))}). Write why you're signing it off anyway.", 409);
        (a.Status, a.SignedOffBy, a.SignedOffAt, a.FinanceNote, a.OverBudget) = (AdvanceStatus.SignedOff, ctx.Actor, DateTimeOffset.UtcNow, why, over);
        await register.Record("Advance", a.Id, a.Number, over ? "Signed off over budget" : "Signed off by finance", a.Amount, why);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> Reject(Guid id, string? reason)
    {
        if (!ctx.Can(Policies.Finance)) throw new AppException("Only finance can turn it down at this stage.", 403);
        var a = await Load(id);
        if (a.Status is not (AdvanceStatus.WithFinance or AdvanceStatus.SignedOff or AdvanceStatus.WithHead)) throw new AppException("It's past the point of turning down.");
        var why = (reason ?? "").Trim();
        if (why.Length < 3) throw new AppException("Say why, so they know.");
        (a.Status, a.RejectReason) = (AdvanceStatus.Rejected, why);
        await register.Record("Advance", a.Id, a.Number, "Turned down by finance", a.Amount, why);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Records that the money was handed over (cash, M-Pesa or bank, done outside the system): the employee now owes it.
    /// Nothing is sent from here.</summary>
    public async Task<object> Pay(Guid id, AdvancePayInput input)
    {
        if (!ctx.Can(Policies.Finance)) throw new AppException("Only finance pays out advances.", 403);
        var a = await Load(id);
        if (a.Status != AdvanceStatus.SignedOff) throw new AppException("Only a signed-off advance is paid out.");
        (a.Status, a.PaidBy, a.PaidReference, a.PaidAt) = (AdvanceStatus.PaidOut, input.Method, (input.Reference ?? "").Trim().ToUpperInvariant(), DateTimeOffset.UtcNow);
        var name = await db.Employees.Where(e => e.Id == a.EmployeeId).Select(e => e.Name).SingleAsync();
        await ledger.PostFor(new Dims(a.DepartmentId), Clock.Today, $"{a.Number}: advance to {name}", "Advance", a.Id,
            (SystemAccounts.StaffAdvances, a.Amount, 0), (SystemAccounts.ForMethod(input.Method), 0, a.Amount));
        await register.Record("Advance", a.Id, a.Number, "Paid out", a.Amount, $"{input.Method} {a.PaidReference}".Trim());
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>The employee accounts for it: each receipt becomes the department's expense; the leftover is handed back or
    /// taken from pay; an overspend is paid back to them.</summary>
    public async Task<object> Account(Guid id, AdvanceAccountInput input, Guid? portalEmployeeId = null)
    {
        var a = await Load(id);
        if (portalEmployeeId is { } me && a.EmployeeId != me) throw new AppException("Not found.", 404);
        if (portalEmployeeId is null && !ctx.Can(Policies.Finance)) throw new AppException("Only finance (or the employee, from their portal) accounts for an advance.", 403);
        if (a.Status != AdvanceStatus.PaidOut) throw new AppException("Only an advance that's been paid out is accounted for.");
        if (input.Expenses is not { Count: > 0 } && input.Leftover is null) throw new AppException("Add the receipts, or say the money is being handed back.");
        var other = await ledger.Id(SystemAccounts.OtherExpenses);
        var allowed = await db.Accounts.Where(x => x.Type == AccountType.Expense).Select(x => x.Id).ToListAsync();
        foreach (var x in input.Expenses ?? [])
        {
            var d = (x.Description ?? "").Trim();
            if (d.Length < 3) throw new AppException("Say what each receipt was for.");
            if (x.Amount <= 0) throw new AppException($"Enter the amount for “{d}”.");
            var account = x.AccountId ?? other;
            if (!allowed.Contains(account)) throw new AppException("Choose an expense account for each receipt.");
            a.Expenses.Add(new CashAdvanceExpense { CashAdvanceId = a.Id, Description = d, AccountId = account, Amount = x.Amount, Receipt = (x.Receipt ?? "").Trim().ToUpperInvariant(), Date = x.Date ?? Clock.Today });
        }
        a.Spent = a.Expenses.Sum(x => x.Amount);
        var leftover = a.Amount - a.Spent;
        var name = await db.Employees.Where(e => e.Id == a.EmployeeId).Select(e => e.Name).SingleAsync();
        var lines = a.Expenses.GroupBy(x => x.AccountId).Select(g => (g.Key, g.Sum(x => x.Amount), 0L)).ToList();
        var staffAdv = await ledger.Id(SystemAccounts.StaffAdvances);
        if (leftover > 0)
        {
            if (input.Leftover == "payroll")
            {
                // Taken from the next pay, as a business recovery the payroll already knows how to post.
                db.EmployeeDeductions.Add(new EmployeeDeduction
                {
                    EmployeeId = a.EmployeeId, Kind = DeductionKind.Advance, Name = $"Unspent advance {a.Number}", MonthlyAmount = leftover, Total = leftover,
                    StartMonth = new DateOnly(Clock.Today.Year, Clock.Today.Month, 1).AddMonths(1), Status = DeductionStatus.Active,
                    Consent = $"Accounted for {a.Number} and agreed to repay the balance from pay, recorded by {ctx.Actor}", ConsentAt = DateTimeOffset.UtcNow, CreatedBy = ctx.Actor,
                });
                a.RecoveredFromPay = leftover;
                lines.Add((staffAdv, 0, a.Spent));
            }
            else
            {
                if (input.Method is not { } m) throw new AppException($"{Money(leftover)} is left over: say how it was handed back (cash, M-Pesa or bank), or take it from pay.");
                a.Returned = leftover;
                lines.Add((await ledger.Id(SystemAccounts.ForMethod(m)), leftover, 0));
                lines.Add((staffAdv, 0, a.Amount));
            }
        }
        else
        {
            lines.Add((staffAdv, 0, a.Amount));
            if (leftover < 0)
            {
                // They spent their own money too: it's paid back to them.
                if (input.Method is not { } m) throw new AppException($"{name} spent {Money(-leftover)} more than the advance: say how it's paid back to them.");
                a.Reimbursed = -leftover;
                lines.Add((await ledger.Id(SystemAccounts.ForMethod(m)), 0, -leftover));
            }
        }
        // Nothing spent and all of it taken from pay: the employee still owes it, so the books don't move until payroll.
        if (lines.Any(l => l.Item2 != 0 || l.Item3 != 0) && !(a.Spent == 0 && a.RecoveredFromPay > 0))
        {
            var entry = await ledger.PostIds(Clock.Today, $"{a.Number}: {name} accounted for the advance", "AdvanceAccounted", a.Id, lines);
            new Dims(a.DepartmentId).Stamp(entry);
        }
        (a.Status, a.AccountedAt) = (AdvanceStatus.Accounted, DateTimeOffset.UtcNow);
        await register.Record("Advance", a.Id, a.Number, "Accounted for", a.Spent,
            leftover > 0 ? $"{Money(leftover)} {(a.RecoveredFromPay > 0 ? "to be taken from pay" : "handed back")}" : leftover < 0 ? $"{Money(-leftover)} paid back to {name}" : "Spent in full");
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> Cancel(Guid id, Guid? portalEmployeeId = null)
    {
        var a = await Load(id);
        if (portalEmployeeId is { } me && a.EmployeeId != me) throw new AppException("Not found.", 404);
        if (a.Status is not (AdvanceStatus.WithHead or AdvanceStatus.WithFinance or AdvanceStatus.SignedOff)) throw new AppException("It can't be cancelled now.");
        a.Status = AdvanceStatus.Cancelled;
        await register.Record("Advance", a.Id, a.Number, "Cancelled", a.Amount);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    object Row(CashAdvance a, Dictionary<Guid, string> people, Dictionary<Guid, string> depts) => new
    {
        a.Id, a.Number, a.EmployeeId, employee = people.GetValueOrDefault(a.EmployeeId, ""), a.DepartmentId, department = depts.GetValueOrDefault(a.DepartmentId, ""),
        a.Purpose, a.Amount, a.AccountBy, status = a.Status.ToString(), a.OverBudget, a.SignedOffBy, a.FinanceNote, paidBy = a.PaidBy?.ToString(), a.PaidReference, a.PaidAt,
        a.Spent, a.Returned, a.RecoveredFromPay, a.Reimbursed, a.AccountedAt, a.RejectReason, a.CreatedAt,
        overdue = a.Status == AdvanceStatus.PaidOut && a.AccountBy < Clock.Today,
    };

    public async Task<object> Get(Guid id)
    {
        var a = await Load(id);
        var people = await db.Employees.Where(e => e.Id == a.EmployeeId).ToDictionaryAsync(e => e.Id, e => e.Name);
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var accounts = await db.Accounts.Where(x => x.Type == AccountType.Expense).ToDictionaryAsync(x => x.Id, x => x.Name);
        var position = a.Status == AdvanceStatus.WithFinance && ctx.Can(Policies.Finance) ? await departments.Position(a.DepartmentId) : null;
        return new
        {
            advance = Row(a, people, depts),
            expenses = a.Expenses.OrderBy(x => x.Date).Select(x => new { x.Description, account = accounts.GetValueOrDefault(x.AccountId, ""), x.Amount, x.Receipt, x.Date }),
            budget = position is null ? null : new { position.Budget, position.Spent, position.Committed, available = position.Available, position.HasBudget, over = a.Amount > position.Available },
            trail = await register.Trail(a.Id),
        };
    }

    public async Task<object> List(string? view)
    {
        var rows = await db.CashAdvances.OrderByDescending(a => a.CreatedAt).Take(500).ToListAsync();
        var people = await db.Employees.ToDictionaryAsync(e => e.Id, e => e.Name);
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var today = Clock.Today;
        var counts = new
        {
            waiting = rows.Count(a => a.Status is AdvanceStatus.WithHead or AdvanceStatus.WithFinance),
            toPay = rows.Count(a => a.Status == AdvanceStatus.SignedOff),
            outstanding = rows.Count(a => a.Status == AdvanceStatus.PaidOut),
            overdue = rows.Count(a => a.Status == AdvanceStatus.PaidOut && a.AccountBy < today),
            done = rows.Count(a => a.Status is AdvanceStatus.Accounted or AdvanceStatus.Rejected or AdvanceStatus.Cancelled),
        };
        var shown = rows.Where(a => view switch
        {
            "pay" => a.Status == AdvanceStatus.SignedOff,
            "outstanding" => a.Status == AdvanceStatus.PaidOut,
            "done" => a.Status is AdvanceStatus.Accounted or AdvanceStatus.Rejected or AdvanceStatus.Cancelled,
            "all" => true,
            _ => a.Status is AdvanceStatus.WithHead or AdvanceStatus.WithFinance,
        });
        return new
        {
            counts, outstandingValue = rows.Where(a => a.Status == AdvanceStatus.PaidOut).Sum(a => a.Amount),
            items = shown.Select(a => Row(a, people, depts)),
        };
    }

    /// <summary>An employee's own advances, and those waiting for them as a head of department.</summary>
    public async Task<object> ForEmployee(Employee me)
    {
        var people = await db.Employees.ToDictionaryAsync(e => e.Id, e => e.Name);
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var mine = await db.CashAdvances.Where(a => a.EmployeeId == me.Id).OrderByDescending(a => a.CreatedAt).Take(30).ToListAsync();
        var heads = await db.Departments.Where(d => d.HeadEmployeeId == me.Id).Select(d => d.Id).ToListAsync();
        var waiting = heads.Count == 0 ? [] : await db.CashAdvances.Where(a => a.Status == AdvanceStatus.WithHead && heads.Contains(a.DepartmentId)).ToListAsync();
        return new { mine = mine.Select(a => Row(a, people, depts)), toApprove = waiting.Select(a => Row(a, people, depts)), isHead = heads.Count > 0 };
    }
}
