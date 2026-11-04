// Budgets and the approvals register: the controls between a department wanting money and money leaving.
//   A department prepares its year's budget, month by month, and sends it to finance. Finance approves or turns it
//   down (the person who prepared it can't approve it, unless they're the owner). Only an approved budget lets a
//   department's requests through: no budget, no spending. A revision replaces the approved budget only once it too
//   is approved, so there is always exactly one budget in force.
//   Every decision about money (budgets, requests, orders, awards, closed jobs) goes into the approvals register, a
//   chain where each record carries the fingerprint of the one before it. Nobody can quietly change or remove a
//   record: the chain stops matching from that point on, and the check says where.
using System.Security.Cryptography;
using System.Text;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record BudgetInput(Guid DepartmentId, int Year, List<long> Months, string? Justification, bool? Submit);
public sealed record TransferInput(Guid FromDepartmentId, Guid ToDepartmentId, int Year, int Month, long Amount, string? Reason);
public sealed record FingerprintInput(string Fingerprint);

public sealed class ApprovalRegister(ErpDb db, RequestContext ctx)
{
    /// <summary>The fingerprint of a record: everything it says, and the fingerprint of the record before it.</summary>
    public static string HashOf(ApprovalRecord r)
    {
        var text = string.Join('|', r.CompanyId, r.Seq, r.At.ToUnixTimeMilliseconds(), r.UserId?.ToString() ?? "", r.Actor, r.Subject, r.SubjectId,
            r.Reference, r.Action, r.Amount, r.Note, r.PrevHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Adds a record to the unit of work (saved with the decision it records, or not at all).</summary>
    public async Task Record(string subject, Guid subjectId, string reference, string action, long amount = 0, string note = "")
    {
        var pending = db.ApprovalRecords.Local.Where(r => db.Entry(r).State == EntityState.Added).OrderByDescending(r => r.Seq).FirstOrDefault();
        var last = pending ?? await db.ApprovalRecords.OrderByDescending(r => r.Seq).FirstOrDefaultAsync();
        var r = new ApprovalRecord
        {
            CompanyId = db.CompanyId, Seq = (last?.Seq ?? 0) + 1,
            // Kept to the millisecond so the fingerprint reads back the same from any database.
            At = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            UserId = ctx.UserId, Actor = ctx.Actor, Subject = subject, SubjectId = subjectId, Reference = reference, Action = action, Amount = amount,
            Note = note.Length > 500 ? note[..500] : note, PrevHash = last?.Hash ?? "",
        };
        r.Hash = HashOf(r);
        db.ApprovalRecords.Add(r);
    }

    /// <summary>Walks the whole chain: numbering without gaps, each record linked to the one before, each fingerprint matching what it says.</summary>
    public async Task<object> Verify()
    {
        var all = await db.ApprovalRecords.AsNoTracking().OrderBy(r => r.Seq).ToListAsync();
        string prev = "";
        long expected = 1;
        foreach (var r in all)
        {
            string? problem = r.Seq != expected ? $"Record {expected} is missing (the next one is {r.Seq})."
                : r.PrevHash != prev ? $"Record {r.Seq} doesn't follow on from record {r.Seq - 1}: something before it was changed or removed."
                : HashOf(r) != r.Hash ? $"Record {r.Seq} ({r.Reference}, {r.Action}) was changed after it was written."
                : null;
            if (problem is not null) return new { intact = false, records = all.Count, brokenAt = r.Seq, problem, fingerprint = all[^1].Hash, checkedAt = DateTimeOffset.UtcNow };
            prev = r.Hash;
            expected++;
        }
        return new { intact = true, records = all.Count, brokenAt = (long?)null, problem = (string?)null, fingerprint = prev, checkedAt = DateTimeOffset.UtcNow };
    }

    public async Task<object> List(string? subject, Guid? subjectId, string? q, int take)
    {
        var query = db.ApprovalRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(subject)) query = query.Where(r => r.Subject == subject);
        if (subjectId is { } id) query = query.Where(r => r.SubjectId == id);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(r => EF.Functions.Like(r.Reference, $"%{q.Trim()}%") || EF.Functions.Like(r.Actor, $"%{q.Trim()}%"));
        var rows = await query.OrderByDescending(r => r.Seq).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        return rows.Select(r => new { r.Seq, r.At, r.Actor, r.Subject, r.SubjectId, r.Reference, r.Action, r.Amount, r.Note, r.Hash, r.PrevHash });
    }

    /// <summary>Checks a fingerprint someone kept from earlier (written down, emailed to an auditor): it must still be the
    /// fingerprint of a record in the register. If the register was rewritten, even entirely, it won't be.</summary>
    public async Task<object> CheckKept(string fingerprint)
    {
        var f = (fingerprint ?? "").Trim().ToLowerInvariant();
        if (f.Length < 16) throw new AppException("Paste the whole fingerprint (64 letters and digits), or at least its first 16.");
        var match = await db.ApprovalRecords.AsNoTracking().Where(r => r.Hash.StartsWith(f)).OrderBy(r => r.Seq).FirstOrDefaultAsync();
        if (match is null) return new { found = false, message = "No record in the register has this fingerprint. Either it's from another business, or the register was rewritten after it was taken." };
        var check = (dynamic)await Verify();
        return new { found = true, seq = match.Seq, at = match.At, reference = match.Reference, action = match.Action, intactSince = (bool)check.intact, message = $"It's record {match.Seq} ({match.Reference}, {match.Action}). Everything up to it is as it was when the fingerprint was taken{((bool)check.intact ? ", and the whole register checks out" : ", but something later doesn't check out")}." };
    }

    /// <summary>A short statement to keep outside the system: the business, the number of records and the latest fingerprint.</summary>
    public async Task<string> Statement()
    {
        var company = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => new { c.Name, c.KraPin }).SingleAsync();
        var last = await db.ApprovalRecords.AsNoTracking().OrderByDescending(r => r.Seq).FirstOrDefaultAsync();
        var check = (dynamic)await Verify();
        return $"""
            Approvals register statement
            Business: {company.Name}{(company.KraPin.Length > 0 ? $" (KRA PIN {company.KraPin})" : "")}
            Taken: {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)):d MMM yyyy, HH:mm} (EAT) by {ctx.Actor}
            Records: {last?.Seq ?? 0}
            Latest record: {(last is null ? "none" : $"#{last.Seq}, {last.Reference}, {last.Action}, {last.At.ToOffset(TimeSpan.FromHours(3)):d MMM yyyy HH:mm}")}
            Fingerprint (SHA-256): {last?.Hash ?? "-"}
            Check result: {((bool)check.intact ? "intact" : $"NOT intact: {check.problem}")}

            Keep this. To check the register later, paste the fingerprint in Finance → Approvals register → "Check a fingerprint".
            If it is no longer found, the register was changed after this statement was taken.
            """;
    }

    /// <summary>The history of one document, oldest first.</summary>
    public async Task<List<object>> Trail(Guid subjectId) =>
        (await db.ApprovalRecords.AsNoTracking().Where(r => r.SubjectId == subjectId).OrderBy(r => r.Seq).ToListAsync())
            .Select(r => (object)new { r.Seq, r.At, r.Actor, r.Action, r.Amount, r.Note, r.Hash }).ToList();
}

public sealed class BudgetService(ErpDb db, Numbers numbers, ApprovalRegister register, DepartmentService departments, RequestContext ctx)
{
    static string Money(long cents) => $"KSh {cents / 100m:N0}";
    static readonly string[] MonthNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    bool CanPrepare => ctx.Can(Policies.Finance) || ctx.Can(Policies.People) || ctx.Can(Policies.Buy) || ctx.Can(Policies.Maintain);

    async Task<Budget> Load(Guid id) => await db.Budgets.SingleOrDefaultAsync(b => b.Id == id) ?? throw new AppException("Not found.", 404);

    public async Task<object> Create(BudgetInput input)
    {
        if (!CanPrepare) throw new AppException("Ask finance, procurement or HR to prepare the budget.", 403);
        var dept = await db.Departments.SingleOrDefaultAsync(d => d.Id == input.DepartmentId && d.Active) ?? throw new AppException("Choose the department.");
        if (input.Year < Clock.Today.Year - 1 || input.Year > Clock.Today.Year + 2) throw new AppException("Budgets are for last year, this year or the next two.");
        if (await db.Budgets.AnyAsync(b => b.DepartmentId == dept.Id && b.Year == input.Year && (b.Status == BudgetStatus.Draft || b.Status == BudgetStatus.Submitted)))
            throw new AppException($"{dept.Name} already has a {input.Year} budget being prepared or waiting for finance. Change that one.", 409);
        var approved = await db.Budgets.Where(b => b.DepartmentId == dept.Id && b.Year == input.Year && b.Status == BudgetStatus.Approved).Select(b => (Guid?)b.Id).FirstOrDefaultAsync();
        var b = new Budget { Number = await numbers.Next("BG"), DepartmentId = dept.Id, Year = input.Year, PreparedBy = ctx.Actor, PreparedByUserId = ctx.UserId, RevisesId = approved };
        Apply(b, input);
        db.Budgets.Add(b);
        await db.SaveChangesAsync();
        if (input.Submit == true) await Submit(b.Id);
        return await Get(b.Id);
    }

    public async Task<object> Update(Guid id, BudgetInput input)
    {
        var b = await Load(id);
        if (!CanPrepare) throw new AppException("Ask finance, procurement or HR to change the budget.", 403);
        if (b.Status is not (BudgetStatus.Draft or BudgetStatus.Rejected)) throw new AppException("Only a draft or a turned-down budget can be changed. To change an approved budget, prepare a revision.");
        Apply(b, input);
        b.Status = BudgetStatus.Draft;
        await db.SaveChangesAsync();
        if (input.Submit == true) await Submit(b.Id);
        return await Get(b.Id);
    }

    static void Apply(Budget b, BudgetInput input)
    {
        if (input.Months is not { Count: 12 }) throw new AppException("Give an amount for each of the twelve months (0 for none).");
        if (input.Months.Any(m => m < 0)) throw new AppException("Amounts can't be negative.");
        if (input.Months.Sum() <= 0) throw new AppException("A budget needs at least one month with money in it.");
        var why = (input.Justification ?? "").Trim();
        if (why.Length < 10) throw new AppException("Say what the money will pay for, so finance can judge it.");
        (b.Months, b.Total, b.Justification) = ([.. input.Months], input.Months.Sum(), why);
    }

    public async Task<object> Submit(Guid id)
    {
        var b = await Load(id);
        if (b.Status is not (BudgetStatus.Draft or BudgetStatus.Rejected)) throw new AppException("It has already gone to finance.");
        (b.Status, b.SubmittedAt, b.DecisionNote) = (BudgetStatus.Submitted, DateTimeOffset.UtcNow, "");
        await register.Record("Budget", b.Id, b.Number, b.RevisesId is null ? "Submitted" : "Revision submitted", b.Total, b.Justification);
        await db.SaveChangesAsync();
        return await Get(b.Id);
    }

    /// <summary>Finance's decision. The one who prepared it can't approve it (the owner excepted, for small teams).</summary>
    public async Task<object> Decide(Guid id, bool approve, string? note)
    {
        if (!ctx.Can(Policies.Finance)) throw new AppException("Only finance (the owner or an accountant) decides budgets.", 403);
        var b = await Load(id);
        if (b.Status != BudgetStatus.Submitted) throw new AppException("It isn't waiting for a decision.");
        if (approve && b.PreparedByUserId == ctx.UserId && ctx.Role != Role.Owner) throw new AppException("You prepared this budget, so someone else in finance has to approve it.", 403);
        var why = (note ?? "").Trim();
        if (!approve && why.Length < 3) throw new AppException("Say why, so the department can fix it.");
        (b.Status, b.DecidedBy, b.DecidedAt, b.DecisionNote) = (approve ? BudgetStatus.Approved : BudgetStatus.Rejected, ctx.Actor, DateTimeOffset.UtcNow, why);
        if (approve)
            foreach (var old in await db.Budgets.Where(x => x.DepartmentId == b.DepartmentId && x.Year == b.Year && x.Status == BudgetStatus.Approved && x.Id != b.Id).ToListAsync())
            {
                old.Status = BudgetStatus.Replaced;
                await register.Record("Budget", old.Id, old.Number, $"Replaced by {b.Number}", old.Total);
            }
        await register.Record("Budget", b.Id, b.Number, approve ? "Approved" : "Turned down", b.Total, why);
        await db.SaveChangesAsync();
        return await Get(b.Id);
    }

    public async Task<object> Get(Guid id)
    {
        var b = await Load(id);
        var dept = await db.Departments.Where(d => d.Id == b.DepartmentId).Select(d => d.Name).SingleOrDefaultAsync() ?? "";
        var actual = await Actuals(b.DepartmentId, b.Year);
        var current = b.RevisesId is { } r ? await db.Budgets.Where(x => x.Id == r).Select(x => new { x.Number, x.Months, x.Total }).SingleOrDefaultAsync() : null;
        return new
        {
            budget = Row(b, dept), months = b.Months.Select((m, i) => new { month = MonthNames[i], budget = m, actual = actual[i], current = current?.Months.ElementAtOrDefault(i) }),
            revises = current is null ? null : new { current.Number, current.Total },
            trail = await register.Trail(b.Id),
            canDecide = ctx.Can(Policies.Finance) && b.Status == BudgetStatus.Submitted && (b.PreparedByUserId != ctx.UserId || ctx.Role == Role.Owner),
        };
    }

    static object Row(Budget b, string dept) => new
    {
        b.Id, b.Number, b.DepartmentId, department = dept, b.Year, b.Total, status = b.Status.ToString(), b.Justification, b.PreparedBy, b.SubmittedAt,
        b.DecidedBy, b.DecidedAt, b.DecisionNote, isRevision = b.RevisesId is not null, b.CreatedAt,
    };

    /// <summary>What a department spent each month of a year, from the books (expense lines charged to it, before VAT).</summary>
    async Task<long[]> Actuals(Guid departmentId, int year)
    {
        var result = new long[12];
        for (var m = 1; m <= 12; m++)
        {
            var from = new DateOnly(year, m, 1);
            if (from > Clock.Today) break;
            result[m - 1] = (await departments.Spent(from, from.AddMonths(1).AddDays(-1))).GetValueOrDefault(departmentId);
        }
        return result;
    }

    /// <summary>Every department for a year: the budget in force, any waiting for finance, and spending so far.</summary>
    public async Task<object> Year(int year)
    {
        var depts = await db.Departments.Where(d => d.Active).OrderBy(d => d.Name).ToListAsync();
        var budgets = await db.Budgets.Where(b => b.Year == year).OrderByDescending(b => b.CreatedAt).ToListAsync();
        var from = new DateOnly(year, 1, 1);
        var to = year == Clock.Today.Year ? Clock.Today : new DateOnly(year, 12, 31);
        var spent = from > Clock.Today ? [] : await departments.Spent(from, to);
        var committed = await departments.Committed();
        var monthIndex = year == Clock.Today.Year ? Clock.Today.Month : year < Clock.Today.Year ? 12 : 0;
        var rows = depts.Select(d =>
        {
            var inForce = budgets.FirstOrDefault(b => b.DepartmentId == d.Id && b.Status == BudgetStatus.Approved);
            var pending = budgets.FirstOrDefault(b => b.DepartmentId == d.Id && b.Status is BudgetStatus.Draft or BudgetStatus.Submitted or BudgetStatus.Rejected);
            var toDate = inForce?.Months.Take(monthIndex).Sum() ?? 0;
            return new
            {
                departmentId = d.Id, department = d.Name,
                approved = inForce is null ? null : Row(inForce, d.Name),
                pending = pending is null ? null : Row(pending, d.Name),
                annual = inForce?.Total ?? 0, budgetToDate = toDate, spentToDate = spent.GetValueOrDefault(d.Id), committed = committed.GetValueOrDefault(d.Id),
                thisMonth = inForce is null || year != Clock.Today.Year ? (long?)null : inForce.Months[Clock.Today.Month - 1],
            };
        }).ToList();
        return new
        {
            year, rows,
            waiting = budgets.Count(b => b.Status == BudgetStatus.Submitted),
            withoutBudget = rows.Count(r => r.approved is null),
            annual = rows.Sum(r => r.annual), budgetToDate = rows.Sum(r => r.budgetToDate), spentToDate = rows.Sum(r => r.spentToDate),
            canDecide = ctx.Can(Policies.Finance), canPrepare = CanPrepare,
        };
    }

    // ------------------------------------------------------------------------------------------------ transfers

    /// <summary>Asks to move part of one department's approved budget for a month to another's.</summary>
    public async Task<object> RequestTransfer(TransferInput input)
    {
        if (!CanPrepare) throw new AppException("Ask finance, procurement or HR to request the transfer.", 403);
        if (input.FromDepartmentId == input.ToDepartmentId) throw new AppException("Choose two different departments.");
        if (input.Month is < 1 or > 12) throw new AppException("Choose the month.");
        if (input.Amount <= 0) throw new AppException("Enter the amount to move.");
        if (new DateOnly(input.Year, input.Month, 1).AddMonths(1) <= new DateOnly(Clock.Today.Year, Clock.Today.Month, 1)) throw new AppException("That month has passed.");
        var why = (input.Reason ?? "").Trim();
        if (why.Length < 10) throw new AppException("Say why the money is moving, so finance can judge it.");
        await InForce(input.FromDepartmentId, input.Year);
        await InForce(input.ToDepartmentId, input.Year);
        var t = new BudgetTransfer { Number = await numbers.Next("BT"), FromDepartmentId = input.FromDepartmentId, ToDepartmentId = input.ToDepartmentId, Year = input.Year, Month = input.Month, Amount = input.Amount, Reason = why, RequestedBy = ctx.Actor, RequestedByUserId = ctx.UserId };
        db.BudgetTransfers.Add(t);
        await register.Record("BudgetTransfer", t.Id, t.Number, "Asked for", t.Amount, why);
        await db.SaveChangesAsync();
        return await Transfers();
    }

    async Task<Budget> InForce(Guid departmentId, int year)
    {
        var b = await db.Budgets.SingleOrDefaultAsync(x => x.DepartmentId == departmentId && x.Year == year && x.Status == BudgetStatus.Approved);
        if (b is not null) return b;
        var name = await db.Departments.Where(d => d.Id == departmentId).Select(d => d.Name).SingleOrDefaultAsync() ?? "That department";
        throw new AppException($"{name} has no approved {year} budget to move money in or out of.", 409);
    }

    /// <summary>Finance's decision. Approved, both budgets change for that month (the giver keeps what it has already spent
    /// and committed), and both changes are in the register.</summary>
    public async Task<object> DecideTransfer(Guid id, bool approve, string? note)
    {
        if (!ctx.Can(Policies.Finance)) throw new AppException("Only finance decides budget transfers.", 403);
        var t = await db.BudgetTransfers.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (t.Status != TransferStatus.Submitted) throw new AppException("It's already been decided.");
        if (approve && t.RequestedByUserId == ctx.UserId && ctx.Role != Role.Owner) throw new AppException("You asked for this transfer, so someone else in finance has to approve it.", 403);
        var why = (note ?? "").Trim();
        if (!approve && why.Length < 3) throw new AppException("Say why.");
        if (approve)
        {
            var from = await InForce(t.FromDepartmentId, t.Year);
            var to = await InForce(t.ToDepartmentId, t.Year);
            var i = t.Month - 1;
            // What the giver can spare: its budget for the month, less (for the current month) what's spent and committed.
            var spare = from.Months[i];
            if (t.Year == Clock.Today.Year && t.Month == Clock.Today.Month) spare = (await departments.Position(t.FromDepartmentId)).Available;
            if (t.Amount > spare) throw new AppException($"Only {Ksh(Math.Max(0, spare))} of that month's budget is free to move.", 409);
            var fm = from.Months.ToList(); fm[i] -= t.Amount; (from.Months, from.Total) = (fm, fm.Sum());
            var tm = to.Months.ToList(); tm[i] += t.Amount; (to.Months, to.Total) = (tm, tm.Sum());
            await register.Record("Budget", from.Id, from.Number, $"{Ksh(t.Amount)} moved out ({t.Number})", -t.Amount, t.Reason);
            await register.Record("Budget", to.Id, to.Number, $"{Ksh(t.Amount)} moved in ({t.Number})", t.Amount, t.Reason);
        }
        (t.Status, t.DecidedBy, t.DecidedAt, t.DecisionNote) = (approve ? TransferStatus.Approved : TransferStatus.Rejected, ctx.Actor, DateTimeOffset.UtcNow, why);
        await register.Record("BudgetTransfer", t.Id, t.Number, approve ? "Approved" : "Turned down", t.Amount, why);
        await db.SaveChangesAsync();
        return await Transfers();
    }

    public async Task<object> Transfers()
    {
        var names = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var rows = await db.BudgetTransfers.OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync();
        return rows.Select(t => new
        {
            t.Id, t.Number, from = names.GetValueOrDefault(t.FromDepartmentId, ""), to = names.GetValueOrDefault(t.ToDepartmentId, ""), t.Year, t.Month, monthName = MonthNames[t.Month - 1],
            t.Amount, t.Reason, status = t.Status.ToString(), t.RequestedBy, t.DecidedBy, t.DecisionNote, t.CreatedAt,
            canDecide = ctx.Can(Policies.Finance) && t.Status == TransferStatus.Submitted && (t.RequestedByUserId != ctx.UserId || ctx.Role == Role.Owner),
        });
    }

    public static string MonthName(DateOnly d) => $"{MonthNames[d.Month - 1]} {d.Year}";
    public static string Ksh(long cents) => Money(cents);
}
