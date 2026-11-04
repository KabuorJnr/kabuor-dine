// Departments: who spends, who approves, and how much is left this month. A department's spending is read from the
// books (expense lines charged to it, before VAT, which is claimable) plus what it has committed but not yet been
// billed for: approved requests not yet fulfilled and purchase orders not yet billed (the "commitment" stage of
// budget control, so money promised can't be promised twice).
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record DepartmentInput(string Name, string? Code, Guid? HeadEmployeeId, long? MonthlyBudget, bool? Active);

/// <summary>A department's month: its approved budget (null: finance hasn't approved one), what it spent and committed.</summary>
public sealed record BudgetPosition(long? Budget, long Spent, long Committed)
{
    public long Available => (Budget ?? 0) - Spent - Committed;
    public bool HasBudget => Budget is not null;
}

public sealed class DepartmentService(ErpDb db)
{
    static (DateOnly From, DateOnly To) Month(DateOnly day) => (new DateOnly(day.Year, day.Month, 1), new DateOnly(day.Year, day.Month, 1).AddMonths(1).AddDays(-1));

    /// <summary>Expense charged to each department in a period, before VAT, from the books.</summary>
    public async Task<Dictionary<Guid, long>> Spent(DateOnly from, DateOnly to)
    {
        var rows = await db.JournalLines.Where(l => l.DepartmentId != null)
            .Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l, e.Date })
            .Join(db.Accounts, x => x.l.AccountId, a => a.Id, (x, a) => new { x.l.DepartmentId, x.l.Debit, x.l.Credit, x.Date, a.Type })
            .Where(x => x.Type == AccountType.Expense && x.Date >= from && x.Date <= to)
            .Select(x => new { x.DepartmentId, x.Debit, x.Credit }).ToListAsync();
        return rows.GroupBy(r => r.DepartmentId!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.Debit - r.Credit));
    }

    /// <summary>Promised but not yet billed: approved requests' outstanding lines, and purchase orders not yet billed.</summary>
    public async Task<Dictionary<Guid, long>> Committed(Guid? exceptRequisition = null)
    {
        var reqs = await db.Requisitions.Include(r => r.Lines).Where(r => r.Status == RequisitionStatus.Approved && r.Id != exceptRequisition).ToListAsync();
        var orders = await db.PurchaseDocs.Where(d => d.Kind == PurchaseKind.Order && d.DepartmentId != null && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent))
            .Select(d => new { d.DepartmentId, d.Net }).ToListAsync();
        var map = new Dictionary<Guid, long>();
        foreach (var r in reqs)
            map[r.DepartmentId] = map.GetValueOrDefault(r.DepartmentId) + r.Lines.Sum(l => (long)Math.Round(l.Outstanding * l.EstimatedUnitCost));
        foreach (var o in orders)
            map[o.DepartmentId!.Value] = map.GetValueOrDefault(o.DepartmentId!.Value) + o.Net;
        // Cash advances signed off or out with staff: promised until accounted for (then the receipts are the spending).
        foreach (var a in await db.CashAdvances.Where(a => a.Status == AdvanceStatus.SignedOff || a.Status == AdvanceStatus.PaidOut).Select(a => new { a.DepartmentId, a.Amount }).ToListAsync())
            map[a.DepartmentId] = map.GetValueOrDefault(a.DepartmentId) + a.Amount;
        return map;
    }

    /// <summary>The month's amount from the department's budget approved by finance, or null without one.</summary>
    public async Task<long?> Approved(Guid departmentId, DateOnly day)
    {
        var months = await db.Budgets.Where(b => b.DepartmentId == departmentId && b.Year == day.Year && b.Status == BudgetStatus.Approved).Select(b => b.Months).FirstOrDefaultAsync();
        return months?.ElementAtOrDefault(day.Month - 1);
    }

    /// <summary>Every department's approved amount for a month (departments without an approved budget are left out).</summary>
    public async Task<Dictionary<Guid, long>> ApprovedAll(DateOnly day) =>
        (await db.Budgets.Where(b => b.Year == day.Year && b.Status == BudgetStatus.Approved).Select(b => new { b.DepartmentId, b.Months }).ToListAsync())
            .ToDictionary(b => b.DepartmentId, b => b.Months.ElementAtOrDefault(day.Month - 1));

    /// <summary>A department's budget this month, what it has spent and what it has committed.</summary>
    public async Task<BudgetPosition> Position(Guid departmentId, Guid? exceptRequisition = null)
    {
        var d = await db.Departments.SingleOrDefaultAsync(x => x.Id == departmentId) ?? throw new AppException("That department doesn't exist.");
        var (from, to) = Month(Clock.Today);
        var spent = (await Spent(from, to)).GetValueOrDefault(d.Id);
        var committed = (await Committed(exceptRequisition)).GetValueOrDefault(d.Id);
        return new BudgetPosition(await Approved(d.Id, Clock.Today), spent, committed);
    }

    public async Task<object> List()
    {
        var all = await db.Departments.OrderBy(d => d.Name).ToListAsync();
        var heads = await db.Employees.Select(e => new { e.Id, e.Name }).ToDictionaryAsync(e => e.Id, e => e.Name);
        var (from, to) = Month(Clock.Today);
        var spent = await Spent(from, to);
        var committed = await Committed();
        var budgets = await ApprovedAll(Clock.Today);
        var waiting = await db.Budgets.Where(b => b.Status == BudgetStatus.Submitted).Select(b => b.DepartmentId).ToListAsync();
        var staff = await db.Employees.Where(e => e.EndDate == null || e.EndDate >= Clock.Today).GroupBy(e => e.Department).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
        return all.Select(d => new
        {
            d.Id, d.Name, d.Code, d.HeadEmployeeId, head = d.HeadEmployeeId is { } h ? heads.GetValueOrDefault(h, "") : "",
            monthlyBudget = budgets.TryGetValue(d.Id, out var mb) ? mb : (long?)null, hasBudget = budgets.ContainsKey(d.Id), budgetWaiting = waiting.Contains(d.Id), d.Active,
            spent = spent.GetValueOrDefault(d.Id), committed = committed.GetValueOrDefault(d.Id),
            available = budgets.TryGetValue(d.Id, out var b) ? b - spent.GetValueOrDefault(d.Id) - committed.GetValueOrDefault(d.Id) : (long?)null,
            staff = staff.FirstOrDefault(s => string.Equals(s.Key, d.Name, StringComparison.OrdinalIgnoreCase))?.n ?? 0,
        });
    }

    public async Task<Department> Save(Guid? id, DepartmentInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Name the department, e.g. “Workshop”.");
        if (input.HeadEmployeeId is { } h && !await db.Employees.AnyAsync(e => e.Id == h)) throw new AppException("Choose the head from the employee list.");
        if (await db.Departments.AnyAsync(d => d.Name == name && d.Id != id)) throw new AppException("There's already a department with that name.", 409);
        var d = id is { } did ? await db.Departments.SingleOrDefaultAsync(x => x.Id == did) ?? throw new AppException("Not found.", 404) : null;
        if (d is null) db.Departments.Add(d = new Department { Name = name });
        var oldName = d.Name;
        // Money comes from the budget finance approves (Finance → Budgets), not from here.
        (d.Name, d.Code, d.HeadEmployeeId, d.Active) = (name, (input.Code ?? "").Trim().ToUpperInvariant(), input.HeadEmployeeId, input.Active ?? true);
        // Employees name their department in text: renaming it renames it on their records too.
        if (id is not null && oldName != name)
            foreach (var e in await db.Employees.Where(e => e.Department == oldName).ToListAsync()) e.Department = name;
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>The first time departments are opened: one per department already written on employee records.</summary>
    public async Task SeedFromEmployees()
    {
        if (await db.Departments.AnyAsync()) return;
        var names = (await db.Employees.Select(e => e.Department).Distinct().ToListAsync())
            .Select(n => (n ?? "").Trim()).Where(n => n.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var n in names) db.Departments.Add(new Department { Name = n });
        if (names.Count > 0) await db.SaveChangesAsync();
    }
}
