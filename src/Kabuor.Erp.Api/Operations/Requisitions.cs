// Requests from departments for what they need (purchase requisitions). Anyone can ask, from the app or an employee's
// portal. The chain of approval, every step written to the approvals register:
//   head of department (if the department has one) -> procurement (is it needed, how will we source it) -> finance
//   signs off the money against the department's budget approved for the month, counting what's already spent and
//   committed. No approved budget, no approval. Over the budget, finance may still sign off, but must write why.
// Signed-off requests are then fulfilled: issued from stores (the cost is charged to the department, or to the
// maintenance job it's for), ordered from a supplier, or put out to tender.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Kabuor.Erp.Purchases;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record RequisitionLineInput(Guid? ItemId, string? Description, decimal Quantity, string? Unit, long? EstimatedUnitCost);
public sealed record RequisitionInput(Guid DepartmentId, DateOnly? NeededBy, string? Purpose, Guid? WorkOrderId, List<RequisitionLineInput> Lines, bool? Submit);
public sealed record ReasonInput(string? Reason);
public sealed record IssueLineInput(Guid LineId, decimal Quantity);
public sealed record RequisitionIssueInput(Guid? LocationId, List<IssueLineInput>? Lines);
public sealed record RequisitionOrderInput(Guid SupplierId, List<Guid>? LineIds);

public sealed class RequisitionService(ErpDb db, Numbers numbers, Ledger ledger, InventoryService stock, PurchaseService purchases, DepartmentService departments, ApprovalRegister register, RequestContext ctx)
{
    async Task<Requisition> Load(Guid id) => await db.Requisitions.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == id) ?? throw new AppException("Not found.", 404);

    bool Mine(Requisition r) => (r.RequestedByUserId is { } u && u == ctx.UserId) || (r.RequestedByEmployeeId is { } e && e == ctx.EmployeeId);

    public async Task<Requisition> Create(RequisitionInput input, Guid? employeeId = null)
    {
        var r = new Requisition { Number = await numbers.Next("PR"), RequestedBy = ctx.Actor, RequestedByUserId = ctx.UserId, RequestedByEmployeeId = employeeId };
        await Apply(r, input);
        db.Requisitions.Add(r);
        await db.SaveChangesAsync();
        if (input.Submit == true) await Submit(r.Id);
        return r;
    }

    public async Task<Requisition> Update(Guid id, RequisitionInput input)
    {
        var r = await Load(id);
        if (r.Status != RequisitionStatus.Draft) throw new AppException("Only drafts can be changed. Cancel it and ask again.");
        if (!Mine(r) && !ctx.Can(Policies.Buy)) throw new AppException("Only the person who asked can change it.", 403);
        db.RequisitionLines.RemoveRange(r.Lines);
        await Apply(r, input);
        await db.SaveChangesAsync();
        if (input.Submit == true) await Submit(r.Id);
        return r;
    }

    async Task Apply(Requisition r, RequisitionInput input)
    {
        var dept = await db.Departments.SingleOrDefaultAsync(d => d.Id == input.DepartmentId && d.Active) ?? throw new AppException("Choose the department it's for.");
        if (input.Lines is not { Count: > 0 }) throw new AppException("Add at least one thing you need.");
        if (input.Lines.Count > 100) throw new AppException("A request can have at most 100 lines.");
        if (input.NeededBy is { } nb && nb < Clock.Today) throw new AppException("The date it's needed by has passed.");
        if (input.WorkOrderId is { } w && !await db.WorkOrders.AnyAsync(x => x.Id == w && x.Status != WorkOrderStatus.Closed && x.Status != WorkOrderStatus.Cancelled))
            throw new AppException("That work order is closed or doesn't exist.");
        var ids = input.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        (r.DepartmentId, r.NeededBy, r.Purpose, r.WorkOrderId) = (dept.Id, input.NeededBy, (input.Purpose ?? "").Trim(), input.WorkOrderId);
        r.Lines.Clear();
        foreach (var l in input.Lines)
        {
            var item = l.ItemId is { } iid ? items.GetValueOrDefault(iid) ?? throw new AppException("One of the items no longer exists.") : null;
            var description = (l.Description ?? item?.Name ?? "").Trim();
            if (description.Length < 2) throw new AppException("Say what each line is.");
            if (l.Quantity <= 0 || l.Quantity > 1_000_000) throw new AppException($"Enter how many “{description}” are needed.");
            if ((l.EstimatedUnitCost ?? 0) < 0) throw new AppException("Costs can't be negative.");
            // A stocked item is estimated at what it costs the business; otherwise at what the requester expects.
            var estimate = l.EstimatedUnitCost is > 0 ? l.EstimatedUnitCost.Value : item is not null ? (long)Math.Round(item.AverageCost > 0 ? item.AverageCost : 0) : 0;
            r.Lines.Add(new RequisitionLine { RequisitionId = r.Id, ItemId = item?.Id, Description = description, Unit = (l.Unit ?? item?.Unit ?? "").Trim(), Quantity = l.Quantity, EstimatedUnitCost = estimate });
        }
        r.EstimatedTotal = r.Lines.Sum(l => (long)Math.Round(l.Quantity * l.EstimatedUnitCost));
    }

    /// <summary>Sends it on: to the department's head first if it has one (and the head isn't the one asking), else to procurement.</summary>
    public async Task<Requisition> Submit(Guid id)
    {
        var r = await Load(id);
        if (r.Status != RequisitionStatus.Draft) throw new AppException("It has already been sent.");
        if (!Mine(r) && !ctx.Can(Policies.Buy)) throw new AppException("Only the person who asked can send it.", 403);
        var head = await db.Departments.Where(d => d.Id == r.DepartmentId).Select(d => d.HeadEmployeeId).SingleAsync();
        r.Status = head is { } h && h != r.RequestedByEmployeeId ? RequisitionStatus.WithHead : RequisitionStatus.WithBuying;
        if (r.Status == RequisitionStatus.WithBuying && head is not null) (r.HeadApprovedBy, r.HeadApprovedAt) = ($"{r.RequestedBy} (head of department)", DateTimeOffset.UtcNow);
        r.SubmittedAt = DateTimeOffset.UtcNow;
        await register.Record("Request", r.Id, r.Number, "Submitted", r.EstimatedTotal, r.Purpose);
        await db.SaveChangesAsync();
        return r;
    }

    /// <summary>The head of department's answer, from their portal.</summary>
    public async Task<Requisition> HeadDecision(Guid id, Guid employeeId, bool approve, string? reason)
    {
        var r = await Load(id);
        var head = await db.Departments.Where(d => d.Id == r.DepartmentId).Select(d => d.HeadEmployeeId).SingleAsync();
        if (head != employeeId) throw new AppException("Only the head of the department can answer this.", 403);
        if (r.Status != RequisitionStatus.WithHead) throw new AppException("It isn't waiting for you.");
        if (approve) (r.Status, r.HeadApprovedBy, r.HeadApprovedAt) = (RequisitionStatus.WithBuying, ctx.Actor, DateTimeOffset.UtcNow);
        else
        {
            var why = (reason ?? "").Trim();
            if (why.Length < 3) throw new AppException("Say why, so they know.");
            (r.Status, r.RejectReason) = (RequisitionStatus.Rejected, why);
        }
        await register.Record("Request", r.Id, r.Number, approve ? "Approved by head of department" : "Turned down by head of department", r.EstimatedTotal, reason ?? "");
        await db.SaveChangesAsync();
        return r;
    }

    /// <summary>Procurement agrees it's needed and how it will be sourced; it then goes to finance to sign off the money.
    /// A department with no budget approved by finance for this month can't have requests approved.</summary>
    public async Task<object> Approve(Guid id)
    {
        var r = await Load(id);
        if (r.Status == RequisitionStatus.WithHead && ctx.Role != Role.Owner) throw new AppException("It's waiting for the head of the department.");
        if (r.Status is not (RequisitionStatus.WithBuying or RequisitionStatus.WithHead)) throw new AppException("It isn't waiting for approval.");
        var position = await departments.Position(r.DepartmentId, r.Id);
        await NeedsBudget(r, position);
        r.OverBudget = r.EstimatedTotal > position.Available;
        (r.Status, r.ApprovedBy, r.ApprovedAt) = (RequisitionStatus.WithFinance, ctx.Actor, DateTimeOffset.UtcNow);
        await register.Record("Request", r.Id, r.Number, "Approved by procurement", r.EstimatedTotal, r.OverBudget ? "Over the budget left this month" : "");
        await db.SaveChangesAsync();
        return await Get(id);
    }

    async Task NeedsBudget(Requisition r, BudgetPosition position)
    {
        if (position.HasBudget) return;
        var name = await db.Departments.Where(d => d.Id == r.DepartmentId).Select(d => d.Name).SingleAsync();
        throw new AppException($"{name} has no budget approved by finance for {BudgetService.MonthName(Clock.Today)}. Prepare it in Finance → Budgets; once finance approves it, this request can go ahead.", 409);
    }

    /// <summary>Finance signs off the money: checked against what's left of the month's approved budget. Over it, finance
    /// may still sign off, but has to write why (it's in the approvals register for good).</summary>
    public async Task<object> SignOff(Guid id, string? note)
    {
        if (!ctx.Can(Policies.Finance)) throw new AppException("Only finance (the owner or an accountant) signs off money.", 403);
        var r = await Load(id);
        if (r.Status != RequisitionStatus.WithFinance) throw new AppException(r.Status is RequisitionStatus.WithHead or RequisitionStatus.WithBuying ? "Procurement hasn't approved it yet." : "It isn't waiting for finance.");
        var position = await departments.Position(r.DepartmentId, r.Id);
        await NeedsBudget(r, position);
        var over = r.EstimatedTotal > position.Available;
        var why = (note ?? "").Trim();
        if (over && why.Length < 10)
            throw new AppException($"This is {Money(r.EstimatedTotal - Math.Max(0, position.Available))} over what's left of the budget this month ({Money(Math.Max(0, position.Available))}). Write why you're signing it off anyway.", 409);
        (r.Status, r.OverBudget, r.FinanceSignedOffBy, r.FinanceSignedOffAt, r.FinanceNote) = (RequisitionStatus.Approved, over, ctx.Actor, DateTimeOffset.UtcNow, why);
        await register.Record("Request", r.Id, r.Number, over ? "Signed off over budget" : "Signed off by finance", r.EstimatedTotal, why);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    static string Money(long cents) => $"KSh {cents / 100m:N0}";

    public async Task<Requisition> Reject(Guid id, string? reason)
    {
        var r = await Load(id);
        if (r.Status is not (RequisitionStatus.WithBuying or RequisitionStatus.WithHead or RequisitionStatus.WithFinance or RequisitionStatus.Approved)) throw new AppException("It isn't waiting for a decision.");
        if (r.Status == RequisitionStatus.Approved && !ctx.Can(Policies.Finance)) throw new AppException("Finance has signed it off: only finance can turn it down now.", 403);
        if (r.Lines.Any(l => l.IssuedQuantity > 0 || l.OrderedQuantity > 0)) throw new AppException("Some of it has already been issued or ordered.");
        var why = (reason ?? "").Trim();
        if (why.Length < 3) throw new AppException("Say why, so they know.");
        var stage = r.Status == RequisitionStatus.WithFinance ? "by finance" : r.Status == RequisitionStatus.Approved ? "after sign-off" : "by procurement";
        (r.Status, r.RejectReason) = (RequisitionStatus.Rejected, why);
        await register.Record("Request", r.Id, r.Number, $"Turned down {stage}", r.EstimatedTotal, why);
        await db.SaveChangesAsync();
        return r;
    }

    public async Task<Requisition> Cancel(Guid id)
    {
        var r = await Load(id);
        if (!Mine(r) && !ctx.Can(Policies.Buy)) throw new AppException("Only the person who asked, or procurement, can cancel it.", 403);
        if (r.Status is RequisitionStatus.Fulfilled or RequisitionStatus.Rejected or RequisitionStatus.Cancelled) throw new AppException("It's already finished.");
        if (r.Lines.Any(l => l.IssuedQuantity > 0 || l.OrderedQuantity > 0)) throw new AppException("Some of it has already been issued or ordered.");
        r.Status = RequisitionStatus.Cancelled;
        if (r.SubmittedAt is not null) await register.Record("Request", r.Id, r.Number, "Cancelled", r.EstimatedTotal);
        await db.SaveChangesAsync();
        return r;
    }

    void Settle(Requisition r)
    {
        if (r.Lines.All(l => l.Outstanding == 0)) r.Status = RequisitionStatus.Fulfilled;
    }

    /// <summary>Gives stocked lines out from the store. The cost goes to the department (supplies used), or to the
    /// maintenance job it's for (and onto that job's parts).</summary>
    public async Task<object> Issue(Guid id, RequisitionIssueInput input)
    {
        var r = await Load(id);
        if (r.Status != RequisitionStatus.Approved) throw new AppException("Only approved requests can be issued.");
        var loc = await stock.LocationOrDefault(input.LocationId);
        var ids = r.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        var job = r.WorkOrderId is { } wid ? await db.WorkOrders.Include(w => w.Parts).SingleOrDefaultAsync(w => w.Id == wid) : null;
        if (job?.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled) throw new AppException($"{job.Number} is closed: it can't take more parts.");
        var wanted = input.Lines is { Count: > 0 }
            ? input.Lines.Select(x => (Line: r.Lines.SingleOrDefault(l => l.Id == x.LineId) ?? throw new AppException("One of those lines isn't on this request."), x.Quantity)).ToList()
            : r.Lines.Where(l => l.ItemId is { } i && items.GetValueOrDefault(i)?.TrackStock == true && l.Outstanding > 0).Select(l => (Line: l, Quantity: l.Outstanding)).ToList();
        if (wanted.Count == 0) throw new AppException("Nothing on this request is kept in the store: order it instead.");
        long total = 0, fromSale = 0;
        foreach (var (line, qty) in wanted.Where(w => w.Quantity > 0))
        {
            if (line.ItemId is not { } itemId || items.GetValueOrDefault(itemId) is not { TrackStock: true } item) throw new AppException($"{line.Description} isn't kept in the store: order it instead.");
            if (qty > line.Outstanding) throw new AppException($"Only {line.Outstanding:0.##} of {line.Description} are still to be given out.");
            var cost = await stock.Issue(item, loc.Id, qty, "Requisition", r.Id, r.Number, kind: StockMoveKind.Consumed);
            line.IssuedQuantity += qty;
            total += cost;
            if (item.Use == ItemUse.Sale) fromSale += cost;
            if (job is not null)
            {
                var part = job.Parts.FirstOrDefault(p => p.ItemId == itemId);
                if (part is null) job.Parts.Add(part = new WorkOrderPart { WorkOrderId = job.Id, ItemId = itemId, Description = item.Name, Unit = item.Unit, PlannedQuantity = qty });
                var before = part.IssuedQuantity * part.UnitCost;
                part.IssuedQuantity += qty;
                part.UnitCost = (before + cost) / part.IssuedQuantity;
            }
        }
        if (total > 0)
        {
            var dims = job is null ? new Dims(r.DepartmentId) : new Dims(r.DepartmentId, job.AssetId, job.Id);
            await ledger.PostFor(dims, Clock.Today, $"{r.Number}: issued from {loc.Name}{(job is null ? "" : $" for {job.Number}")}", "Requisition", r.Id,
                (job is null ? SystemAccounts.SuppliesUsed : SystemAccounts.Maintenance, total, 0), (SystemAccounts.Stores, 0, total - fromSale), (SystemAccounts.Stock, 0, fromSale));
        }
        Settle(r);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>A draft purchase order to one supplier for the lines still outstanding (or the ones chosen).</summary>
    public async Task<object> Order(Guid id, RequisitionOrderInput input)
    {
        var r = await Load(id);
        if (r.Status != RequisitionStatus.Approved) throw new AppException("Only approved requests can be ordered.");
        var lines = r.Lines.Where(l => l.Outstanding > 0 && (input.LineIds is not { Count: > 0 } || input.LineIds.Contains(l.Id))).ToList();
        if (lines.Count == 0) throw new AppException("Nothing is left to order on this request.");
        var po = await purchases.Create(new PurchaseInput(PurchaseKind.Order, input.SupplierId, Clock.Today, 30, null, null, $"For request {r.Number}{(r.Purpose.Length > 0 ? $": {r.Purpose}" : "")}",
            lines.Select(l => new PurchaseLineInput(l.ItemId, null, l.Description, l.Outstanding, l.EstimatedUnitCost, null)).ToList(), r.DepartmentId, r.WorkOrderId));
        po.RequisitionId = r.Id;
        foreach (var l in lines) l.OrderedQuantity += l.Outstanding;
        Settle(r);
        await db.SaveChangesAsync();
        return new { po.Id, po.Number };
    }

    /// <summary>Lines put out to tender count as ordered (the tender's award makes the order).</summary>
    public async Task MarkTendered(Guid id, IEnumerable<Guid> lineIds)
    {
        var r = await Load(id);
        foreach (var l in r.Lines.Where(l => lineIds.Contains(l.Id))) l.OrderedQuantity += l.Outstanding;
        Settle(r);
    }

    public async Task<object> List(string? view)
    {
        var all = ctx.Can(Policies.BuyView);
        var q = db.Requisitions.Include(r => r.Lines).AsQueryable();
        if (!all) q = q.Where(r => r.RequestedByUserId == ctx.UserId);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync();
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var counts = new
        {
            waiting = rows.Count(r => r.Status is RequisitionStatus.WithBuying or RequisitionStatus.WithHead),
            finance = rows.Count(r => r.Status == RequisitionStatus.WithFinance),
            approved = rows.Count(r => r.Status == RequisitionStatus.Approved),
            done = rows.Count(r => r.Status is RequisitionStatus.Fulfilled or RequisitionStatus.Rejected or RequisitionStatus.Cancelled),
            drafts = rows.Count(r => r.Status == RequisitionStatus.Draft),
        };
        var shown = rows.Where(r => view switch
        {
            "approved" => r.Status == RequisitionStatus.Approved,
            "finance" => r.Status == RequisitionStatus.WithFinance,
            "done" => r.Status is RequisitionStatus.Fulfilled or RequisitionStatus.Rejected or RequisitionStatus.Cancelled,
            "drafts" => r.Status == RequisitionStatus.Draft,
            "all" => true,
            _ => r.Status is RequisitionStatus.WithBuying or RequisitionStatus.WithHead,
        });
        return new { counts, all, items = shown.Select(r => Row(r, depts)) };
    }

    static object Row(Requisition r, Dictionary<Guid, string> depts) => new
    {
        r.Id, r.Number, r.DepartmentId, department = depts.GetValueOrDefault(r.DepartmentId, ""), r.RequestedBy, r.NeededBy, r.Purpose, status = r.Status.ToString(),
        r.EstimatedTotal, r.OverBudget, r.CreatedAt, r.SubmittedAt, r.ApprovedAt, r.ApprovedBy, r.HeadApprovedBy, r.FinanceSignedOffBy, r.FinanceSignedOffAt, r.FinanceNote, r.RejectReason, r.WorkOrderId, lines = r.Lines.Count,
        outstanding = r.Lines.Sum(l => l.Outstanding),
    };

    public async Task<object> Get(Guid id)
    {
        var r = await Load(id);
        if (!ctx.Can(Policies.BuyView) && !Mine(r)) throw new AppException("Not found.", 404);
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var ids = r.Lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.TrackStock }).ToDictionaryAsync(i => i.Id, i => i.TrackStock);
        var onHand = (await db.StockLevels.Where(l => ids.Contains(l.ItemId)).Select(l => new { l.ItemId, l.Quantity }).ToListAsync()).GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var orders = await db.PurchaseDocs.Where(d => d.RequisitionId == r.Id).Select(d => new { d.Id, d.Number, kind = d.Kind.ToString(), status = d.Status.ToString(), d.Total }).ToListAsync();
        var tenders = await db.Tenders.Where(t => t.RequisitionId == r.Id).Select(t => new { t.Id, t.Number, status = t.Status.ToString() }).ToListAsync();
        var job = r.WorkOrderId is { } w ? await db.WorkOrders.Where(x => x.Id == w).Select(x => new { x.Id, x.Number, x.Title }).SingleOrDefaultAsync() : null;
        var budget = (ctx.Can(Policies.BuyView) || ctx.Can(Policies.Finance)) && r.Status is RequisitionStatus.WithBuying or RequisitionStatus.WithHead or RequisitionStatus.WithFinance ? await departments.Position(r.DepartmentId, r.Id) : null;
        return new
        {
            request = Row(r, depts), job, orders, tenders, trail = await register.Trail(r.Id),
            canSignOff = ctx.Can(Policies.Finance) && r.Status == RequisitionStatus.WithFinance,
            budget = budget is null ? null : new { budget.Budget, budget.Spent, budget.Committed, available = budget.Available, budget.HasBudget, over = budget.HasBudget && r.EstimatedTotal > budget.Available },
            lines = r.Lines.Select(l => new
            {
                l.Id, l.ItemId, l.Description, l.Unit, l.Quantity, l.EstimatedUnitCost, l.IssuedQuantity, l.OrderedQuantity, outstanding = l.Outstanding,
                stocked = l.ItemId is { } i && items.GetValueOrDefault(i), inStock = l.ItemId is { } j ? onHand.GetValueOrDefault(j) : (decimal?)null,
            }),
        };
    }

    // ------------------------------------------------------------------------------------------------ portal

    /// <summary>The requests an employee has made, and those waiting for them as a head of department.</summary>
    public async Task<object> ForEmployee(Employee me)
    {
        var depts = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name);
        var mine = await db.Requisitions.Include(r => r.Lines).Where(r => r.RequestedByEmployeeId == me.Id).OrderByDescending(r => r.CreatedAt).Take(50).ToListAsync();
        var heads = await db.Departments.Where(d => d.HeadEmployeeId == me.Id).Select(d => d.Id).ToListAsync();
        var waiting = heads.Count == 0 ? new List<Requisition>() : await db.Requisitions.Include(r => r.Lines).Where(r => r.Status == RequisitionStatus.WithHead && heads.Contains(r.DepartmentId)).OrderBy(r => r.SubmittedAt).ToListAsync();
        object Full(Requisition r) => new
        {
            r.Id, r.Number, department = depts.GetValueOrDefault(r.DepartmentId, ""), r.RequestedBy, r.NeededBy, r.Purpose, status = r.Status.ToString(), r.EstimatedTotal, r.RejectReason, r.CreatedAt,
            lines = r.Lines.Select(l => new { l.Description, l.Quantity, l.Unit, l.EstimatedUnitCost, l.IssuedQuantity, l.OrderedQuantity }),
        };
        return new { mine = mine.Select(Full), toApprove = waiting.Select(Full), isHead = heads.Count > 0 };
    }
}
