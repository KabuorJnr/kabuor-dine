// Keeping assets working. A fault report says what's broken; maintenance accepts it as a work order (or says why
// not). A work order is planned (who, when, which parts, how long), scheduled, started, then technically completed:
// the asset is back in service and what failed, why, and what was done is recorded, because without that the
// reliability figures (time between failures, time to repair, repeat failures) mean nothing. Its costs build up as
// it goes: parts issued from the store (Dr repairs and maintenance, Cr stock), money paid out directly, contractor
// bills linked to it, and labour hours (costed, not posted: wages are already in the books through payroll). Closing
// it financially checks nothing is still on order, fixes the final costs, and stops anything more being charged.
// Preventive plans make their work orders a few days before each service is due.
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Kabuor.Erp.Purchases;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Operations;

public sealed record FaultInput(Guid? AssetId, Guid? LocationId, string Title, string? Description, string? Symptom, MaintenancePriority? Priority, bool? AssetDown);
public sealed record WorkOrderInput(string Title, string? Description, WorkOrderType? Type, MaintenancePriority? Priority, Guid? AssetId, Guid? LocationId, Guid? DepartmentId,
    string? AssignedTo, Guid? AssigneeEmployeeId, Guid? SupplierId, DateOnly? PlannedStart, DateOnly? DueDate, decimal? EstimatedHours, long? EstimatedCost);
/// <summary>kind = start | hold | resume | complete | reopen | close | cancel.</summary>
public sealed record WorkOrderAction(string Kind, string? Reason, string? FailureMode, string? FailureCause, string? Remedy, string? Notes, DateTimeOffset? BackInServiceAt);
public sealed record PartInput(Guid? ItemId, string? Description, decimal Quantity, long? UnitCost, string? Unit);
public sealed record IssueInput(decimal Quantity, Guid? LocationId);
public sealed record LabourInput(Guid? EmployeeId, string? Worker, DateOnly? Date, decimal Hours, long? HourlyRate, string? Note);
public sealed record DirectCostInput(string Description, long Amount, PaymentMethod Method, string? Reference, string? PaidTo, DateOnly? Date);
public sealed record PlanInput(string Name, Guid? AssetId, Guid? LocationId, string? Tasks, int Every, IntervalUnit Unit, DateOnly NextDue, int? LeadDays, string? AssignedTo,
    decimal? EstimatedHours, MaintenancePriority? Priority, bool? Active, decimal? MeterEvery = null, decimal? LeadMeter = null, decimal? NextDueMeter = null);
public sealed record MeterInput(decimal Reading, string? Note, DateTimeOffset? At);
public sealed record ToolInput(Guid AssetId);
public sealed record ToolMoveInput(string? To, string? Condition, string? Note);
public sealed record OrderPartsInput(Guid SupplierId, string? ServiceDescription, long? ServiceCost);

public sealed class MaintenanceService(ErpDb db, Numbers numbers, Ledger ledger, InventoryService stock, PurchaseService purchases, ApprovalRegister register, RequestContext ctx)
{
    /// <summary>The failure coding (a short version of ISO 14224's mode, cause and remedy lists).</summary>
    public static readonly string[] Modes = ["Won't start", "Stopped while running", "Overheating", "Leaking", "Noise or vibration", "Electrical fault", "Low output or slow", "Broken or damaged", "Comes and goes", "Other"];
    public static readonly string[] Causes = ["Wear and tear", "Missed servicing or lubrication", "Misuse or operator error", "Overloaded", "Poor installation", "Design or factory defect", "Power supply problem", "Dirt, water or bad fuel", "Accident or impact", "Not known"];
    public static readonly string[] Remedies = ["Repaired", "Part replaced", "Adjusted or calibrated", "Cleaned or lubricated", "Whole unit replaced", "Temporary fix", "No fault found"];

    static readonly WorkOrderStatus[] Live = [WorkOrderStatus.Open, WorkOrderStatus.Planned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold];
    bool SeesPay => ctx.Can(Policies.People) || ctx.Can(Policies.Finance);

    async Task<WorkOrder> Load(Guid id) =>
        await db.WorkOrders.Include(w => w.Parts).Include(w => w.Labour).Include(w => w.Costs).SingleOrDefaultAsync(w => w.Id == id) ?? throw new AppException("Not found.", 404);

    static void Editable(WorkOrder w)
    {
        if (w.Status is WorkOrderStatus.Closed) throw new AppException($"{w.Number} is financially closed: nothing more can be charged to it.");
        if (w.Status is WorkOrderStatus.Cancelled) throw new AppException($"{w.Number} was cancelled.");
    }

    void AssetLog(Guid assetId, string kind, string text) => db.AssetEvents.Add(new AssetEvent { AssetId = assetId, Kind = kind, Text = text, Actor = ctx.Actor });

    static Dims DimsOf(WorkOrder w) => new(w.DepartmentId, w.AssetId, w.Id);

    // ------------------------------------------------------------------------------------------------ faults

    public async Task<FaultReport> Report(FaultInput input, Guid? employeeId = null)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length < 3) throw new AppException("Say what's wrong, e.g. “Generator won't start”.");
        Asset? asset = input.AssetId is { } aid ? await db.Assets.SingleOrDefaultAsync(a => a.Id == aid) ?? throw new AppException("That asset isn't in the register.") : null;
        if (asset is null && input.LocationId is null) throw new AppException("Say which asset it is, or at least where.");
        if (input.LocationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid)) throw new AppException("Choose a place from the list.");
        var f = new FaultReport
        {
            Number = await numbers.Next("FR"), AssetId = asset?.Id, LocationId = input.LocationId ?? asset?.LocationId, Title = title, Description = (input.Description ?? "").Trim(),
            Symptom = Modes.Contains(input.Symptom) ? input.Symptom! : "", Priority = input.Priority ?? MaintenancePriority.Normal, AssetDown = input.AssetDown == true,
            ReportedBy = ctx.Actor, ReportedByUserId = ctx.UserId, ReportedByEmployeeId = employeeId,
        };
        db.FaultReports.Add(f);
        if (asset is not null) AssetLog(asset.Id, "fault", $"Fault reported ({f.Number}): {title}{(f.AssetDown ? ", out of use" : "")}");
        await db.SaveChangesAsync();
        return f;
    }

    public async Task<object> Faults(string? status)
    {
        var q = db.FaultReports.AsQueryable();
        q = status switch { "all" => q, "closed" => q.Where(f => f.Status == FaultStatus.Rejected || f.Status == FaultStatus.Resolved), _ => q.Where(f => f.Status == FaultStatus.New || f.Status == FaultStatus.Accepted) };
        var faults = await q.OrderByDescending(f => f.CreatedAt).Take(300).ToListAsync();
        var assets = await AssetNames();
        var places = await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);
        var jobs = await db.WorkOrders.Where(w => w.FaultReportId != null).Select(w => new { w.FaultReportId, w.Number, w.Status }).ToListAsync();
        var counts = await db.FaultReports.GroupBy(f => f.Status).Select(g => new { g.Key, n = g.Count() }).ToListAsync();
        return new
        {
            counts = new { open = counts.Where(c => c.Key is FaultStatus.New or FaultStatus.Accepted).Sum(c => c.n), @new = counts.FirstOrDefault(c => c.Key == FaultStatus.New)?.n ?? 0, closed = counts.Where(c => c.Key is FaultStatus.Rejected or FaultStatus.Resolved).Sum(c => c.n) },
            items = faults.Select(f => new
            {
                f.Id, f.Number, f.Title, f.Description, f.Symptom, priority = f.Priority.ToString(), f.AssetDown, status = f.Status.ToString(), f.ReportedBy, f.CreatedAt, f.ResolvedAt, f.RejectReason,
                f.AssetId, asset = f.AssetId is { } a ? assets.GetValueOrDefault(a) : null, location = f.LocationId is { } l ? places.GetValueOrDefault(l, "") : "",
                workOrder = jobs.Where(j => j.FaultReportId == f.Id).Select(j => new { j.Number, status = j.Status.ToString() }).FirstOrDefault(), f.WorkOrderId,
            }),
        };
    }

    async Task<Dictionary<Guid, string>> AssetNames() =>
        (await db.Assets.Select(a => new { a.Id, a.Tag, a.Name }).ToListAsync()).ToDictionary(a => a.Id, a => $"{a.Tag} · {a.Name}");

    /// <summary>Takes a fault on: makes its work order (a breakdown if the asset is out of use) and counts its downtime from the report.</summary>
    public async Task<object> Accept(Guid faultId, WorkOrderInput? extra)
    {
        var f = await db.FaultReports.SingleOrDefaultAsync(x => x.Id == faultId) ?? throw new AppException("Not found.", 404);
        if (f.Status != FaultStatus.New) throw new AppException($"{f.Number} has already been dealt with.");
        // The reporter's words stay on the fault (shown on the job); the job's instructions are maintenance's own.
        var input = new WorkOrderInput(extra?.Title is { Length: > 0 } t ? t : f.Title, extra?.Description ?? "", extra?.Type ?? (f.AssetDown ? WorkOrderType.Breakdown : WorkOrderType.Corrective),
            extra?.Priority ?? f.Priority, f.AssetId, f.LocationId, extra?.DepartmentId, extra?.AssignedTo, extra?.AssigneeEmployeeId, extra?.SupplierId, extra?.PlannedStart, extra?.DueDate, extra?.EstimatedHours, extra?.EstimatedCost);
        var w = await NewWorkOrder(input);
        (w.FaultReportId, w.FailureMode) = (f.Id, f.Symptom);
        if (f.AssetDown) w.DownFrom = f.CreatedAt;
        (f.Status, f.WorkOrderId) = (FaultStatus.Accepted, w.Id);
        await db.SaveChangesAsync();
        return await Get(w.Id);
    }

    public async Task<FaultReport> Reject(Guid faultId, string? reason)
    {
        var f = await db.FaultReports.SingleOrDefaultAsync(x => x.Id == faultId) ?? throw new AppException("Not found.", 404);
        if (f.Status != FaultStatus.New) throw new AppException($"{f.Number} has already been dealt with.");
        var why = (reason ?? "").Trim();
        if (why.Length < 3) throw new AppException("Say why (not a fault, a duplicate of another report, already fixed…), so whoever reported it knows.");
        (f.Status, f.RejectReason, f.ResolvedAt) = (FaultStatus.Rejected, why, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return f;
    }

    // ------------------------------------------------------------------------------------------------ work orders

    async Task<WorkOrder> NewWorkOrder(WorkOrderInput input)
    {
        var w = new WorkOrder { Number = await numbers.Next("WO"), Title = "", CreatedBy = ctx.Actor };
        await Apply(w, input);
        db.WorkOrders.Add(w);
        if (w.AssetId is { } a) AssetLog(a, "workorder", $"Work order {w.Number}: {w.Title}");
        return w;
    }

    public async Task<object> Create(WorkOrderInput input)
    {
        var w = await NewWorkOrder(input);
        await db.SaveChangesAsync();
        return await Get(w.Id);
    }

    public async Task<object> Update(Guid id, WorkOrderInput input)
    {
        var w = await Load(id);
        Editable(w);
        if (w.Status == WorkOrderStatus.Completed) throw new AppException($"{w.Number} is complete. Reopen it to change the job.");
        await Apply(w, input);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    async Task Apply(WorkOrder w, WorkOrderInput input)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length < 3) throw new AppException("Say what the job is, e.g. “Replace the conveyor belt”.");
        Asset? asset = input.AssetId is { } aid ? await db.Assets.SingleOrDefaultAsync(a => a.Id == aid) ?? throw new AppException("That asset isn't in the register.") : null;
        if (input.LocationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid)) throw new AppException("Choose a place from the list.");
        if (input.DepartmentId is { } did && !await db.Departments.AnyAsync(d => d.Id == did)) throw new AppException("Choose a department from the list.");
        if (input.SupplierId is { } sid && !await db.Suppliers.AnyAsync(s => s.Id == sid)) throw new AppException("Choose the contractor from your suppliers.");
        if ((input.EstimatedHours ?? 0) < 0 || (input.EstimatedCost ?? 0) < 0) throw new AppException("Estimates can't be negative.");
        if (input.PlannedStart is { } ps && input.DueDate is { } due && due < ps) throw new AppException("The due date is before the planned start.");
        var who = (input.AssignedTo ?? "").Trim();
        if (input.AssigneeEmployeeId is { } eid)
            who = await db.Employees.Where(e => e.Id == eid).Select(e => e.Name).SingleOrDefaultAsync() ?? throw new AppException("That employee isn't on the list.");
        w.Title = title;
        w.Description = (input.Description ?? "").Trim();
        w.Type = input.Type ?? w.Type;
        w.Priority = input.Priority ?? w.Priority;
        w.AssetId = asset?.Id;
        w.LocationId = input.LocationId ?? asset?.LocationId;
        w.DepartmentId = input.DepartmentId ?? asset?.DepartmentId;
        (w.AssignedTo, w.AssigneeEmployeeId, w.SupplierId) = (who, input.AssigneeEmployeeId, input.SupplierId);
        (w.PlannedStart, w.DueDate) = (input.PlannedStart, input.DueDate ?? input.PlannedStart);
        (w.EstimatedHours, w.EstimatedCost) = (input.EstimatedHours ?? 0, input.EstimatedCost ?? 0);
        // Planned once someone and a day are set; back to open if either is taken away before work starts.
        if (w.Status is WorkOrderStatus.Open or WorkOrderStatus.Planned)
            w.Status = who.Length > 0 && w.PlannedStart is not null ? WorkOrderStatus.Planned : WorkOrderStatus.Open;
    }

    public async Task<object> Act(Guid id, WorkOrderAction input)
    {
        var w = await Load(id);
        var asset = w.AssetId is { } aid ? await db.Assets.SingleOrDefaultAsync(a => a.Id == aid) : null;
        var reason = (input.Reason ?? "").Trim();
        var now = DateTimeOffset.UtcNow;
        switch ((input.Kind ?? "").ToLowerInvariant())
        {
            case "start":
                Editable(w);
                if (w.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Planned or WorkOrderStatus.OnHold)) throw new AppException($"{w.Number} is {Plain(w.Status)}.");
                (w.Status, w.StartedAt, w.OnHoldReason) = (WorkOrderStatus.InProgress, w.StartedAt ?? now, "");
                // A breakdown, or an asset reported out of use: it's down until the job is complete.
                if (w.DownFrom is null && w.Type == WorkOrderType.Breakdown) w.DownFrom = now;
                if (asset is not null && w.DownFrom is not null && asset.Status is AssetStatus.InUse or AssetStatus.InStore)
                {
                    asset.Status = AssetStatus.UnderRepair;
                    AssetLog(asset.Id, "status", $"Under repair ({w.Number})");
                }
                break;
            case "hold":
                Editable(w);
                if (w.Status is WorkOrderStatus.Completed) throw new AppException($"{w.Number} is complete.");
                if (reason.Length < 3) throw new AppException("Say what it's waiting for (parts, the contractor, the machine to be free…).");
                (w.Status, w.OnHoldReason) = (WorkOrderStatus.OnHold, reason);
                break;
            case "resume":
                if (w.Status != WorkOrderStatus.OnHold) throw new AppException($"{w.Number} isn't on hold.");
                (w.Status, w.OnHoldReason) = (w.StartedAt is null ? (w.AssignedTo.Length > 0 && w.PlannedStart is not null ? WorkOrderStatus.Planned : WorkOrderStatus.Open) : WorkOrderStatus.InProgress, "");
                break;
            case "complete":
                Editable(w);
                if (w.Status is WorkOrderStatus.Completed) throw new AppException($"{w.Number} is already complete.");
                var stillOut = await (from t in db.WorkOrderTools where t.WorkOrderId == w.Id && t.CheckedOutAt != null && t.ReturnedAt == null
                                      join a in db.Assets on t.AssetId equals a.Id select a.Name).ToListAsync();
                if (stillOut.Count > 0) throw new AppException($"Hand the tools back first: {string.Join(", ", stillOut)}.");
                await Complete(w, asset, input, now);
                break;
            case "reopen":
                if (w.Status != WorkOrderStatus.Completed) throw new AppException("Only completed work orders can be reopened (a closed one is final).");
                (w.Status, w.CompletedAt) = (WorkOrderStatus.InProgress, null);
                break;
            case "close":
                await Close(w);
                break;
            case "cancel":
                Editable(w);
                if (w.Status is WorkOrderStatus.Completed) throw new AppException($"{w.Number} is complete: close it instead.");
                if (reason.Length < 3) throw new AppException("Say why it's cancelled.");
                if (w.Parts.Any(p => p.IssuedQuantity > 0)) throw new AppException("Parts were issued to this job: return them to the store first, or complete the job.");
                if (w.Costs.Count > 0 || await db.PurchaseDocs.AnyAsync(d => d.WorkOrderId == w.Id && d.Status != PurchaseStatus.Void))
                    throw new AppException("Money has been spent on this job (payments or purchase orders): complete and close it so the costs stay on record.");
                (w.Status, w.CancelReason) = (WorkOrderStatus.Cancelled, reason);
                if (w.FaultReportId is { } fid && await db.FaultReports.SingleOrDefaultAsync(f => f.Id == fid) is { } fault)
                    (fault.Status, fault.RejectReason, fault.ResolvedAt) = (FaultStatus.Rejected, $"Work order {w.Number} cancelled: {reason}", now);
                if (asset?.Status == AssetStatus.UnderRepair) asset.Status = AssetStatus.InUse;
                break;
            default:
                throw new AppException("Unknown action.");
        }
        await db.SaveChangesAsync();
        return await Get(id);
    }

    static string Plain(WorkOrderStatus s) => s switch { WorkOrderStatus.InProgress => "in progress", WorkOrderStatus.OnHold => "on hold", _ => s.ToString().ToLowerInvariant() };

    /// <summary>Technically complete: the work is done, the asset is back in service, and a breakdown or repair says what
    /// failed and what was done (and why, for an asset the business can't run without).</summary>
    async Task Complete(WorkOrder w, Asset? asset, WorkOrderAction input, DateTimeOffset now)
    {
        var (mode, cause, remedy) = ((input.FailureMode ?? w.FailureMode ?? "").Trim(), (input.FailureCause ?? "").Trim(), (input.Remedy ?? "").Trim());
        if (w.Type is WorkOrderType.Breakdown or WorkOrderType.Corrective)
        {
            if (mode.Length == 0 || remedy.Length == 0) throw new AppException("Record what failed and what was done: the reliability figures are built from these.");
            if (asset?.Criticality == AssetCriticality.High && cause.Length == 0) throw new AppException($"{asset.Name} is a critical asset: record why it failed too.");
        }
        var back = input.BackInServiceAt ?? now;
        if (w.DownFrom is { } down && back < down) throw new AppException("It can't be back in service before it went down.");
        w.Status = WorkOrderStatus.Completed;
        (w.CompletedAt, w.CompletedBy, w.StartedAt) = (now, ctx.Actor, w.StartedAt ?? now);
        (w.FailureMode, w.FailureCause, w.Remedy, w.CompletionNotes) = (mode, cause, remedy, (input.Notes ?? "").Trim());
        if (w.DownFrom is not null) w.DownTo = back;
        if (asset is not null)
        {
            if (asset.Status == AssetStatus.UnderRepair) asset.Status = AssetStatus.InUse;
            AssetLog(asset.Id, "workorder", $"{w.Number} completed{(remedy.Length > 0 ? $": {remedy.ToLowerInvariant()}" : "")}{(w.DownTo is { } to && w.DownFrom is { } from ? $" after {Hours(from, to):0.#} h out of use" : "")}");
        }
        if (w.FaultReportId is { } fid && await db.FaultReports.SingleOrDefaultAsync(f => f.Id == fid) is { } fault)
            (fault.Status, fault.ResolvedAt) = (FaultStatus.Resolved, now);
        // A planned service done: the plan's next one is counted from when it was due (or today, if it's later), so
        // the schedule doesn't drift.
        if (w.MaintenancePlanId is { } pid && await db.MaintenancePlans.SingleOrDefaultAsync(p => p.Id == pid) is { } plan)
        {
            plan.LastDoneOn = Clock.Today;
            // By use: the next one is due that many units after the reading now (the service was done at this reading).
            if (plan.MeterEvery > 0 && plan.AssetId is { } mAsset && await db.Assets.Where(a => a.Id == mAsset).Select(a => (decimal?)a.MeterReading).SingleOrDefaultAsync() is { } reading)
                (plan.LastDoneMeter, plan.NextDueMeter) = (reading, reading + plan.MeterEvery);
            var dueOn = w.DueDate ?? Clock.Today;
            if (plan.NextDue <= dueOn) plan.NextDue = plan.After(dueOn);
            while (plan.NextDue <= Clock.Today) plan.NextDue = plan.After(plan.NextDue);
        }
    }

    static double Hours(DateTimeOffset from, DateTimeOffset to) => Math.Max(0, (to - from).TotalHours);

    /// <summary>Financially closed: nothing is still on order for it, and the final costs are fixed.</summary>
    async Task Close(WorkOrder w)
    {
        if (!ctx.Can(Policies.Maintain) && !ctx.Can(Policies.Finance)) throw new AppException("Only maintenance or finance can close work orders.", 403);
        if (w.Status != WorkOrderStatus.Completed) throw new AppException(w.Status == WorkOrderStatus.Closed ? $"{w.Number} is already closed." : $"Complete {w.Number} first: the work has to be done before its costs are closed.");
        var open = await db.PurchaseDocs.Where(d => d.WorkOrderId == w.Id && ((d.Kind == PurchaseKind.Order && (d.Status == PurchaseStatus.Draft || d.Status == PurchaseStatus.Sent)) || (d.Kind == PurchaseKind.Bill && d.Status == PurchaseStatus.Draft)))
            .Select(d => d.Number).ToListAsync();
        if (open.Count > 0) throw new AppException($"Still open for this job: {string.Join(", ", open)}. Receive and post them, or void them, before closing.");
        var c = await Costs(w);
        (w.PartsCost, w.LabourCost, w.ServicesCost) = (c.Parts, c.Labour, c.Services);
        (w.Status, w.ClosedAt, w.ClosedBy) = (WorkOrderStatus.Closed, DateTimeOffset.UtcNow, ctx.Actor);
        await register.Record("WorkOrder", w.Id, w.Number, "Costs closed", c.Parts + c.Labour + c.Services, w.EstimatedCost > 0 ? $"Estimate {w.EstimatedCost / 100m:N0}" : "");
    }

    sealed record CostSheet(long Parts, long Labour, long Direct, long Bills, long OnOrder, long PlannedParts)
    {
        public long Services => Direct + Bills;
        public long Total => Parts + Labour + Services;
    }

    async Task<CostSheet> Costs(WorkOrder w)
    {
        var parts = (long)Math.Round(w.Parts.Sum(p => p.IssuedQuantity * p.UnitCost));
        var labour = w.Labour.Sum(l => (long)Math.Round(l.Hours * l.HourlyRate));
        var direct = w.Costs.Sum(c => c.Amount);
        var docs = await db.PurchaseDocs.Where(d => d.WorkOrderId == w.Id && d.Status != PurchaseStatus.Void && d.Status != PurchaseStatus.Draft).Select(d => new { d.Kind, d.Status, d.Net }).ToListAsync();
        var bills = docs.Where(d => d.Kind == PurchaseKind.Bill).Sum(d => d.Net);
        var onOrder = docs.Where(d => d.Kind == PurchaseKind.Order && d.Status == PurchaseStatus.Sent).Sum(d => d.Net);
        var planned = (long)Math.Round(w.Parts.Sum(p => p.PlannedQuantity * p.UnitCost));
        return new CostSheet(parts, labour, direct, bills, onOrder, planned);
    }

    public async Task<object> Get(Guid id)
    {
        var w = await Load(id);
        var asset = w.AssetId is { } aid ? await db.Assets.Where(a => a.Id == aid).Select(a => new { a.Id, a.Tag, a.Name, criticality = a.Criticality.ToString(), status = a.Status.ToString() }).SingleOrDefaultAsync() : null;
        var place = w.LocationId is { } lid ? await db.Locations.Where(l => l.Id == lid).Select(l => l.Name).SingleOrDefaultAsync() : null;
        var dept = w.DepartmentId is { } did ? await db.Departments.Where(d => d.Id == did).Select(d => d.Name).SingleOrDefaultAsync() : null;
        var contractor = w.SupplierId is { } sid ? await db.Suppliers.Where(s => s.Id == sid).Select(s => s.Name).SingleOrDefaultAsync() : null;
        var fault = w.FaultReportId is { } fid ? await db.FaultReports.Where(f => f.Id == fid).Select(f => new { f.Number, f.ReportedBy, f.CreatedAt, f.Description }).SingleOrDefaultAsync() : null;
        var docs = await db.PurchaseDocs.Where(d => d.WorkOrderId == w.Id).Include(d => d.Supplier).OrderBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.Number, kind = d.Kind.ToString(), status = d.Status.ToString(), d.Net, d.Total, supplier = d.Supplier!.Name }).ToListAsync();
        var itemIds = w.Parts.Where(p => p.ItemId != null).Select(p => p.ItemId!.Value).ToList();
        var onHand = (await db.StockLevels.Where(l => itemIds.Contains(l.ItemId)).Select(l => new { l.ItemId, l.Quantity }).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var c = await Costs(w);
        var pay = SeesPay;
        var tools = await Tools(w.Id);
        // Can the job start? Parts on the shelf, tools free, someone to do it, a day to do it on.
        var short_ = w.Parts.Where(p => p.ItemId is { } i && p.PlannedQuantity - p.IssuedQuantity > onHand.GetValueOrDefault(i)).Select(p => p.Description).ToList();
        var busyTools = tools.Where(t => t.busyOn is not null).Select(t => $"{t.name} (out on {t.busyOn})").ToList();
        var readiness = new[]
        {
            new { key = "parts", ok = short_.Count == 0, label = short_.Count == 0 ? "Parts are in stores" : $"Short in stores: {string.Join(", ", short_)}" },
            new { key = "tools", ok = busyTools.Count == 0, label = busyTools.Count == 0 ? (tools.Count == 0 ? "No special tools needed" : "Tools are free") : $"Busy elsewhere: {string.Join(", ", busyTools)}" },
            new { key = "people", ok = w.AssignedTo.Length > 0 || w.SupplierId is not null, label = w.AssignedTo.Length > 0 ? $"Assigned to {w.AssignedTo}" : w.SupplierId is not null ? "A contractor does it" : "Nobody assigned yet" },
            new { key = "time", ok = w.PlannedStart is not null, label = w.PlannedStart is { } ps ? $"Planned for {ps:d MMM}" : "Not scheduled yet" },
        };
        return new
        {
            tools, readiness, ready = readiness.All(r => r.ok),
            w.Id, w.Number, type = w.Type.ToString(), priority = w.Priority.ToString(), status = w.Status.ToString(), w.Title, w.Description,
            asset, w.AssetId, w.LocationId, location = place ?? "", w.DepartmentId, department = dept ?? "", w.SupplierId, contractor = contractor ?? "",
            w.AssignedTo, w.AssigneeEmployeeId, w.PlannedStart, w.DueDate, w.EstimatedHours, w.EstimatedCost,
            w.StartedAt, w.CompletedAt, w.ClosedAt, w.DownFrom, w.DownTo, downtimeHours = w.DownFrom is { } df ? Math.Round(Hours(df, w.DownTo ?? DateTimeOffset.UtcNow), 1) : (double?)null,
            w.OnHoldReason, w.FailureMode, w.FailureCause, w.Remedy, w.CompletionNotes, w.CancelReason, w.CreatedBy, w.CompletedBy, w.ClosedBy, w.CreatedAt,
            overdue = Live.Contains(w.Status) && w.DueDate < Clock.Today,
            fault,
            parts = w.Parts.Select(p => new { p.Id, p.ItemId, p.Description, p.Unit, p.PlannedQuantity, p.IssuedQuantity, unitCost = (long)Math.Round(p.UnitCost), inStock = p.ItemId is { } i ? onHand.GetValueOrDefault(i) : (decimal?)null }),
            labour = w.Labour.OrderBy(l => l.Date).Select(l => new { l.Id, l.Worker, l.Date, l.Hours, l.Note, hourlyRate = pay ? l.HourlyRate : (long?)null, cost = pay ? (long)Math.Round(l.Hours * l.HourlyRate) : (long?)null }),
            costs = w.Costs.OrderBy(x => x.Date).Select(x => new { x.Id, x.Description, x.Amount, method = x.Method.ToString(), x.Reference, x.PaidTo, x.Date, x.Actor }),
            purchases = docs,
            totals = new
            {
                parts = c.Parts, labour = pay ? c.Labour : (long?)null, direct = c.Direct, bills = c.Bills, services = c.Services, onOrder = c.OnOrder,
                plannedParts = c.PlannedParts, total = pay ? c.Total : c.Parts + c.Services, hours = w.Labour.Sum(l => l.Hours),
                estimate = w.EstimatedCost, labourHidden = !pay,
            },
            codes = new { modes = Modes, causes = Causes, remedies = Remedies },
        };
    }

    public async Task<object> List(string? view, string? q, Guid? assetId)
    {
        var all = db.WorkOrders.AsQueryable();
        if (assetId is { } aid) all = all.Where(w => w.AssetId == aid);
        var today = Clock.Today;
        var rows = await all.OrderByDescending(w => w.CreatedAt).Take(1000)
            .Select(w => new { w.Id, w.Number, w.Type, w.Priority, w.Status, w.Title, w.AssetId, w.LocationId, w.AssignedTo, w.PlannedStart, w.DueDate, w.EstimatedHours, w.CreatedAt, w.CompletedAt, w.OnHoldReason }).ToListAsync();
        var assets = await AssetNames();
        var places = await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name);
        bool Open(WorkOrderStatus s) => Live.Contains(s);
        var counts = new
        {
            open = rows.Count(r => Open(r.Status)), overdue = rows.Count(r => Open(r.Status) && r.DueDate < today), unplanned = rows.Count(r => r.Status == WorkOrderStatus.Open),
            inProgress = rows.Count(r => r.Status == WorkOrderStatus.InProgress), onHold = rows.Count(r => r.Status == WorkOrderStatus.OnHold),
            toClose = rows.Count(r => r.Status == WorkOrderStatus.Completed), closed = rows.Count(r => r.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled),
        };
        var shown = rows.Where(r => view switch
        {
            "overdue" => Open(r.Status) && r.DueDate < today,
            "unplanned" => r.Status == WorkOrderStatus.Open,
            "inprogress" => r.Status == WorkOrderStatus.InProgress,
            "onhold" => r.Status == WorkOrderStatus.OnHold,
            "toclose" => r.Status == WorkOrderStatus.Completed,
            "closed" => r.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled,
            "all" => true,
            _ => Open(r.Status),
        }).Where(r => string.IsNullOrWhiteSpace(q) || r.Number.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || r.Title.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
            || (r.AssetId is { } a && assets.GetValueOrDefault(a, "").Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)) || r.AssignedTo.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Priority).ThenBy(r => r.DueDate ?? DateOnly.MaxValue).ThenByDescending(r => r.CreatedAt);
        return new
        {
            counts,
            items = shown.Select(r => new
            {
                r.Id, r.Number, type = r.Type.ToString(), priority = r.Priority.ToString(), status = r.Status.ToString(), r.Title, r.AssetId,
                asset = r.AssetId is { } a ? assets.GetValueOrDefault(a) : null, location = r.LocationId is { } l ? places.GetValueOrDefault(l, "") : "",
                r.AssignedTo, r.PlannedStart, r.DueDate, r.EstimatedHours, r.CreatedAt, r.CompletedAt, r.OnHoldReason, overdue = Open(r.Status) && r.DueDate < today,
            }),
        };
    }

    /// <summary>The schedule: live work by planned day for the next weeks, what's late, and what isn't scheduled yet.</summary>
    public async Task<object> Schedule(DateOnly? from, int days)
    {
        days = Math.Clamp(days, 7, 42);
        var start = from ?? Clock.Today;
        var end = start.AddDays(days - 1);
        var live = await db.WorkOrders.Where(w => w.Status == WorkOrderStatus.Open || w.Status == WorkOrderStatus.Planned || w.Status == WorkOrderStatus.InProgress || w.Status == WorkOrderStatus.OnHold)
            .Select(w => new { w.Id, w.Number, w.Type, w.Priority, w.Status, w.Title, w.AssetId, w.AssignedTo, w.PlannedStart, w.DueDate, w.EstimatedHours }).ToListAsync();
        var assets = await AssetNames();
        object Row(Guid id, string number, WorkOrderType type, MaintenancePriority priority, WorkOrderStatus status, string title, Guid? assetId, string assignedTo, DateOnly? plannedStart, DateOnly? dueDate, decimal hours) =>
            new { id, number, type = type.ToString(), priority = priority.ToString(), status = status.ToString(), title, asset = assetId is { } a ? assets.GetValueOrDefault(a) : null, assignedTo, plannedStart, dueDate, estimatedHours = hours };
        var scheduled = live.Where(w => w.PlannedStart is { } d && d >= start && d <= end).ToList();
        return new
        {
            from = start, to = end,
            days = Enumerable.Range(0, days).Select(i => start.AddDays(i)).Select(d => new
            {
                date = d,
                hours = scheduled.Where(w => w.PlannedStart == d).Sum(w => w.EstimatedHours),
                items = scheduled.Where(w => w.PlannedStart == d).OrderBy(w => w.Priority).Select(w => Row(w.Id, w.Number, w.Type, w.Priority, w.Status, w.Title, w.AssetId, w.AssignedTo, w.PlannedStart, w.DueDate, w.EstimatedHours)),
            }),
            late = live.Where(w => w.DueDate < Clock.Today).OrderBy(w => w.DueDate).Select(w => Row(w.Id, w.Number, w.Type, w.Priority, w.Status, w.Title, w.AssetId, w.AssignedTo, w.PlannedStart, w.DueDate, w.EstimatedHours)),
            unscheduled = live.Where(w => w.PlannedStart is null).OrderBy(w => w.Priority).ThenBy(w => w.DueDate ?? DateOnly.MaxValue).Select(w => Row(w.Id, w.Number, w.Type, w.Priority, w.Status, w.Title, w.AssetId, w.AssignedTo, w.PlannedStart, w.DueDate, w.EstimatedHours)),
            byPerson = scheduled.GroupBy(w => w.AssignedTo.Length > 0 ? w.AssignedTo : "Not assigned").Select(g => new { person = g.Key, jobs = g.Count(), hours = g.Sum(w => w.EstimatedHours) }).OrderByDescending(x => x.hours),
        };
    }

    // ------------------------------------------------------------------------------------------------ parts, labour, costs

    public async Task<object> AddPart(Guid id, PartInput input)
    {
        var w = await Load(id);
        Editable(w);
        if (input.Quantity <= 0) throw new AppException("Enter how many are needed.");
        Item? item = input.ItemId is { } iid ? await db.Items.SingleOrDefaultAsync(i => i.Id == iid) ?? throw new AppException("That item no longer exists.") : null;
        var description = (input.Description ?? item?.Name ?? "").Trim();
        if (description.Length < 2) throw new AppException("Say what part it is.");
        if ((input.UnitCost ?? 0) < 0) throw new AppException("Costs can't be negative.");
        w.Parts.Add(new WorkOrderPart
        {
            WorkOrderId = w.Id, ItemId = item?.Id, Description = description, Unit = (input.Unit ?? item?.Unit ?? "").Trim(), PlannedQuantity = input.Quantity,
            UnitCost = item is not null && item.AverageCost > 0 ? item.AverageCost : input.UnitCost ?? 0,
        });
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> RemovePart(Guid id, Guid partId)
    {
        var w = await Load(id);
        Editable(w);
        var p = w.Parts.SingleOrDefault(x => x.Id == partId) ?? throw new AppException("Not found.", 404);
        if (p.IssuedQuantity > 0) throw new AppException("Some of this part was issued: return it to the store first.");
        db.WorkOrderParts.Remove(p);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Hands parts over from the store to the job: they leave stock and their cost is booked to the job.</summary>
    public async Task<object> Issue(Guid id, Guid partId, IssueInput input)
    {
        var w = await Load(id);
        Editable(w);
        var p = w.Parts.SingleOrDefault(x => x.Id == partId) ?? throw new AppException("Not found.", 404);
        if (p.ItemId is not { } itemId) throw new AppException($"{p.Description} isn't a stocked item: order it (it arrives on the job's purchase order), or pay for it as a cost.");
        if (input.Quantity <= 0) throw new AppException("Enter how many were handed over.");
        var item = await db.Items.SingleAsync(i => i.Id == itemId);
        if (!item.TrackStock) throw new AppException($"{item.Name} isn't tracked in stock.");
        // Parts come from the store chosen, or the main store (the job's location is where the asset is, not a store).
        var loc = await stock.LocationOrDefault(input.LocationId);
        var cost = await stock.Issue(item, loc.Id, input.Quantity, "WorkOrder", w.Id, w.Number, kind: StockMoveKind.Consumed);
        // The part's unit cost becomes the average of everything issued to it.
        var before = p.IssuedQuantity * p.UnitCost;
        p.IssuedQuantity += input.Quantity;
        p.UnitCost = (before + cost) / p.IssuedQuantity;
        await ledger.PostFor(DimsOf(w), Clock.Today, $"{w.Number}: {input.Quantity:0.##} {item.Unit} {item.Name} from {loc.Name}", "WorkOrderPart", w.Id,
            (SystemAccounts.Maintenance, cost, 0), (SystemAccounts.StockFor(item), 0, cost));
        if (w.Status is WorkOrderStatus.Open or WorkOrderStatus.Planned) (w.Status, w.StartedAt) = (WorkOrderStatus.InProgress, w.StartedAt ?? DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Unused parts back to the store, at what they were issued at.</summary>
    public async Task<object> ReturnPart(Guid id, Guid partId, IssueInput input)
    {
        var w = await Load(id);
        Editable(w);
        var p = w.Parts.SingleOrDefault(x => x.Id == partId) ?? throw new AppException("Not found.", 404);
        if (p.ItemId is not { } itemId) throw new AppException("Only stocked parts go back to the store.");
        if (input.Quantity <= 0 || input.Quantity > p.IssuedQuantity) throw new AppException($"Return between 1 and {p.IssuedQuantity:0.##}.");
        var item = await db.Items.SingleAsync(i => i.Id == itemId);
        var loc = await stock.LocationOrDefault(input.LocationId);
        await stock.Receive(item, loc.Id, input.Quantity, p.UnitCost, StockMoveKind.Returned, "WorkOrder", w.Id, $"{w.Number} return");
        p.IssuedQuantity -= input.Quantity;
        var value = (long)Math.Round(input.Quantity * p.UnitCost);
        await ledger.PostFor(DimsOf(w), Clock.Today, $"{w.Number}: {input.Quantity:0.##} {item.Unit} {item.Name} back to {loc.Name}", "WorkOrderPart", w.Id,
            (SystemAccounts.StockFor(item), value, 0), (SystemAccounts.Maintenance, 0, value));
        await db.SaveChangesAsync();
        return await Get(id);
    }

    // ------------------------------------------------------------------------------------------------ tools

    sealed record ToolRow(Guid id, Guid assetId, string tag, string name, string status, string checkedOutTo, DateTimeOffset? checkedOutAt, DateTimeOffset? returnedAt, string returnCondition, string? busyOn);

    async Task<List<ToolRow>> Tools(Guid workOrderId)
    {
        var rows = await (from t in db.WorkOrderTools where t.WorkOrderId == workOrderId
                          join a in db.Assets on t.AssetId equals a.Id
                          orderby t.CreatedAt
                          select new { t, a.Tag, a.Name, a.Status }).ToListAsync();
        var ids = rows.Select(r => r.t.AssetId).ToList();
        // The same tool out on another job right now.
        var elsewhere = await (from t in db.WorkOrderTools where ids.Contains(t.AssetId) && t.WorkOrderId != workOrderId && t.CheckedOutAt != null && t.ReturnedAt == null
                               join w in db.WorkOrders on t.WorkOrderId equals w.Id select new { t.AssetId, w.Number }).ToListAsync();
        return rows.Select(r => new ToolRow(r.t.Id, r.t.AssetId, r.Tag, r.Name, r.Status.ToString(), r.t.CheckedOutTo, r.t.CheckedOutAt, r.t.ReturnedAt, r.t.ReturnCondition,
            r.t.CheckedOutAt is null ? elsewhere.FirstOrDefault(e => e.AssetId == r.t.AssetId)?.Number : null)).ToList();
    }

    /// <summary>Reserves a tool from the asset register for the job.</summary>
    public async Task<object> AddTool(Guid id, ToolInput input)
    {
        var w = await Load(id);
        Editable(w);
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.Id == input.AssetId) ?? throw new AppException("Choose a tool from the asset register.");
        if (asset.Status is AssetStatus.Disposed or AssetStatus.Lost) throw new AppException($"{asset.Name} is no longer in use.");
        if (asset.Id == w.AssetId) throw new AppException("That's the asset being repaired, not a tool for the job.");
        if (await db.WorkOrderTools.AnyAsync(t => t.WorkOrderId == w.Id && t.AssetId == asset.Id && t.ReturnedAt == null)) throw new AppException($"{asset.Name} is already on this job.");
        db.WorkOrderTools.Add(new WorkOrderTool { WorkOrderId = w.Id, AssetId = asset.Id, Actor = ctx.Actor });
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> RemoveTool(Guid id, Guid toolId)
    {
        var t = await db.WorkOrderTools.SingleOrDefaultAsync(x => x.Id == toolId && x.WorkOrderId == id) ?? throw new AppException("Not found.", 404);
        if (t.CheckedOutAt is not null) throw new AppException("It has been handed out: hand it back instead.");
        db.WorkOrderTools.Remove(t);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Hands the tool to the technician. A tool can be out on one job at a time.</summary>
    public async Task<object> CheckOutTool(Guid id, Guid toolId, ToolMoveInput input)
    {
        var w = await Load(id);
        Editable(w);
        var t = await db.WorkOrderTools.SingleOrDefaultAsync(x => x.Id == toolId && x.WorkOrderId == id) ?? throw new AppException("Not found.", 404);
        if (t.CheckedOutAt is not null) throw new AppException("It has already been handed out.");
        var asset = await db.Assets.SingleAsync(a => a.Id == t.AssetId);
        if (asset.Status == AssetStatus.UnderRepair) throw new AppException($"{asset.Name} is under repair itself.");
        var other = await (from x in db.WorkOrderTools where x.AssetId == t.AssetId && x.Id != t.Id && x.CheckedOutAt != null && x.ReturnedAt == null
                           join o in db.WorkOrders on x.WorkOrderId equals o.Id select new { o.Number, x.CheckedOutTo }).FirstOrDefaultAsync();
        if (other is not null) throw new AppException($"{asset.Name} is out on {other.Number}{(other.CheckedOutTo.Length > 0 ? $" with {other.CheckedOutTo}" : "")}. It has to come back first.", 409);
        var to = (input.To ?? w.AssignedTo).Trim();
        if (to.Length < 2) throw new AppException("Say who it's handed to.");
        (t.CheckedOutTo, t.CheckedOutAt, t.Actor) = (to, DateTimeOffset.UtcNow, ctx.Actor);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>The tool comes back. Damaged, it's reported as a fault on the tool so it gets fixed before the next job.</summary>
    public async Task<object> ReturnTool(Guid id, Guid toolId, ToolMoveInput input)
    {
        var w = await Load(id);
        var t = await db.WorkOrderTools.SingleOrDefaultAsync(x => x.Id == toolId && x.WorkOrderId == id) ?? throw new AppException("Not found.", 404);
        if (t.CheckedOutAt is null || t.ReturnedAt is not null) throw new AppException("It isn't out.");
        var damaged = string.Equals(input.Condition, "Damaged", StringComparison.OrdinalIgnoreCase);
        (t.ReturnedAt, t.ReturnCondition) = (DateTimeOffset.UtcNow, damaged ? "Damaged" : "Good");
        await db.SaveChangesAsync();
        if (damaged)
        {
            var asset = await db.Assets.SingleAsync(a => a.Id == t.AssetId);
            await Report(new FaultInput(asset.Id, null, $"{asset.Name} came back damaged from {w.Number}", (input.Note ?? "").Trim(), "Damaged", MaintenancePriority.Normal, true));
        }
        return await Get(id);
    }

    public async Task<object> AddLabour(Guid id, LabourInput input)
    {
        var w = await Load(id);
        Editable(w);
        if (input.Hours <= 0 || input.Hours > 24) throw new AppException("Enter the hours worked that day (up to 24).");
        var worker = (input.Worker ?? "").Trim();
        long rate = input.HourlyRate ?? 0;
        if (input.EmployeeId is { } eid)
        {
            var e = await db.Employees.Where(x => x.Id == eid).Select(x => new { x.Name, x.BasicSalary, x.TaxableAllowances }).SingleOrDefaultAsync() ?? throw new AppException("That employee isn't on the list.");
            worker = e.Name;
            // Someone who can't see salaries can't set a rate from one either: the job is costed at the employee's pay
            // (a month is about 173 working hours) without showing it.
            if (input.HourlyRate is null) rate = (long)Math.Round((e.BasicSalary + e.TaxableAllowances) / 173.33m);
        }
        if (worker.Length < 2) throw new AppException("Say who did the work.");
        if (rate < 0) throw new AppException("The rate can't be negative.");
        if (!SeesPay && input.HourlyRate is not null && input.EmployeeId is not null) throw new AppException("Only finance or HR can set an employee's rate.", 403);
        w.Labour.Add(new WorkOrderLabour { WorkOrderId = w.Id, EmployeeId = input.EmployeeId, Worker = worker, Date = input.Date ?? Clock.Today, Hours = input.Hours, HourlyRate = rate, Note = (input.Note ?? "").Trim() });
        if (w.Status is WorkOrderStatus.Open or WorkOrderStatus.Planned) (w.Status, w.StartedAt) = (WorkOrderStatus.InProgress, w.StartedAt ?? DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> RemoveLabour(Guid id, Guid labourId)
    {
        var w = await Load(id);
        Editable(w);
        db.WorkOrderLabour.Remove(w.Labour.SingleOrDefault(x => x.Id == labourId) ?? throw new AppException("Not found.", 404));
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>Money paid out for the job directly (a fundi by M-Pesa, a part bought for cash): booked to repairs and maintenance.</summary>
    public async Task<object> AddCost(Guid id, DirectCostInput input)
    {
        if (!ctx.Can(Policies.Maintain) && !ctx.Can(Policies.Finance)) throw new AppException("Only maintenance or finance can record what a job cost.", 403);
        var w = await Load(id);
        Editable(w);
        var description = (input.Description ?? "").Trim();
        if (description.Length < 3) throw new AppException("Say what it was for.");
        if (input.Amount <= 0) throw new AppException("Enter the amount paid.");
        var date = input.Date ?? Clock.Today;
        if (date > Clock.Today) throw new AppException("That date is in the future.");
        var company = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => new { c.AccountingLockDate }).SingleAsync();
        if (company.AccountingLockDate is { } lockDate && date <= lockDate) throw new AppException($"The books are locked up to {lockDate:d MMM yyyy}.", 409);
        var cost = new WorkOrderCost { WorkOrderId = w.Id, Description = description, Amount = input.Amount, Method = input.Method, Reference = (input.Reference ?? "").Trim(), PaidTo = (input.PaidTo ?? "").Trim(), Date = date, Actor = ctx.Actor };
        w.Costs.Add(cost);
        await ledger.PostFor(DimsOf(w), date, $"{w.Number}: {description}{(cost.PaidTo.Length > 0 ? $" ({cost.PaidTo})" : "")}", "WorkOrderCost", cost.Id,
            (SystemAccounts.Maintenance, input.Amount, 0), (SystemAccounts.ForMethod(input.Method), 0, input.Amount));
        await db.SaveChangesAsync();
        return await Get(id);
    }

    /// <summary>A draft purchase order for the job: the planned parts that aren't in stock (or not enough), and the
    /// contractor's service if there is one. Its bill becomes the job's cost.</summary>
    public async Task<object> OrderParts(Guid id, OrderPartsInput input)
    {
        var w = await Load(id);
        Editable(w);
        var itemIds = w.Parts.Where(p => p.ItemId != null).Select(p => p.ItemId!.Value).ToList();
        var onHand = (await db.StockLevels.Where(l => itemIds.Contains(l.ItemId)).Select(l => new { l.ItemId, l.Quantity }).ToListAsync())
            .GroupBy(l => l.ItemId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var lines = new List<PurchaseLineInput>();
        // Order lines are priced with VAT (when the business charges it); a part's cost is kept before VAT.
        var withVat = await db.Companies.Where(c => c.Id == db.CompanyId).Select(c => c.VatRegistered).SingleAsync() ? 1.16m : 1m;
        foreach (var p in w.Parts)
        {
            var missing = p.PlannedQuantity - p.IssuedQuantity - (p.ItemId is { } i ? Math.Max(0, onHand.GetValueOrDefault(i)) : 0);
            if (missing > 0) lines.Add(new PurchaseLineInput(p.ItemId, null, p.Description, missing, (long)Math.Round(p.UnitCost * withVat), null));
        }
        if ((input.ServiceCost ?? 0) > 0)
            lines.Add(new PurchaseLineInput(null, await ledger.Id(SystemAccounts.Maintenance), (input.ServiceDescription ?? "").Trim() is { Length: > 0 } sd ? sd : $"Labour and service: {w.Title}", 1, input.ServiceCost, null));
        if (lines.Count == 0) throw new AppException("Everything planned is in the store: issue it. To order a contractor, enter the service and its price.");
        var po = await purchases.Create(new PurchaseInput(PurchaseKind.Order, input.SupplierId, Clock.Today, 30, null, w.LocationId, $"For work order {w.Number}: {w.Title}", lines, w.DepartmentId, w.Id));
        return new { po.Id, po.Number };
    }

    // ------------------------------------------------------------------------------------------------ plans

    public async Task<object> Plans()
    {
        var plans = await db.MaintenancePlans.OrderBy(p => p.NextDue).ToListAsync();
        var assets = await AssetNames();
        var meterIds = plans.Where(p => p.MeterEvery > 0 && p.AssetId != null).Select(p => p.AssetId!.Value).Distinct().ToList();
        var meterInfo = new Dictionary<Guid, (string Unit, decimal Reading, decimal? PerDay)>();
        foreach (var id in meterIds) meterInfo[id] = await MeterInfo(id);
        var open = await db.WorkOrders.Where(w => w.MaintenancePlanId != null && (w.Status == WorkOrderStatus.Open || w.Status == WorkOrderStatus.Planned || w.Status == WorkOrderStatus.InProgress || w.Status == WorkOrderStatus.OnHold))
            .Select(w => new { w.MaintenancePlanId, w.Id, w.Number, w.DueDate }).ToListAsync();
        return plans.Select(p => new
        {
            p.Id, p.Name, p.AssetId, asset = p.AssetId is { } a ? assets.GetValueOrDefault(a) : null, p.LocationId, p.Tasks, p.Every, unit = p.Unit.ToString(), p.NextDue, p.LeadDays,
            p.AssignedTo, p.EstimatedHours, priority = p.Priority.ToString(), p.LastDoneOn, p.Active, due = p.Active && p.NextDue.AddDays(-p.LeadDays) <= Clock.Today,
            openWorkOrder = open.Where(o => o.MaintenancePlanId == p.Id).Select(o => new { o.Id, o.Number, o.DueDate }).FirstOrDefault(),
            p.MeterEvery, p.NextDueMeter, p.LeadMeter, p.LastDoneMeter,
            meter = p.MeterEvery > 0 && p.AssetId is { } ma && meterInfo.TryGetValue(ma, out var mi) ? new
            {
                unit = mi.Unit, reading = mi.Reading, perDay = mi.PerDay, left = p.NextDueMeter - mi.Reading,
                // When the meter will reach the service at the rate the asset has been used.
                dueAround = mi.PerDay is > 0 && p.NextDueMeter is { } nd ? Clock.Today.AddDays((int)Math.Clamp(Math.Ceiling((nd - mi.Reading) / mi.PerDay.Value), 0, 3650)) : (DateOnly?)null,
            } : null,
        });
    }

    public async Task<MaintenancePlan> SavePlan(Guid? id, PlanInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length < 3) throw new AppException("Name the service, e.g. “Generator: oil and filters”.");
        if (input.Every < 1 || input.Every > 365) throw new AppException("Repeat every 1 to 365 days, weeks or months.");
        if ((input.LeadDays ?? 7) is < 0 or > 90) throw new AppException("Make the work order 0 to 90 days before it's due.");
        if (input.AssetId is { } aid && !await db.Assets.AnyAsync(a => a.Id == aid)) throw new AppException("That asset isn't in the register.");
        if (input.AssetId is null && input.LocationId is null) throw new AppException("Choose the asset (or at least the place) it's for.");
        var p = id is { } pid ? await db.MaintenancePlans.SingleOrDefaultAsync(x => x.Id == pid) ?? throw new AppException("Not found.", 404) : null;
        if (p is null) db.MaintenancePlans.Add(p = new MaintenancePlan { Name = name });
        (p.Name, p.AssetId, p.LocationId, p.Tasks, p.Every, p.Unit, p.NextDue, p.LeadDays) = (name, input.AssetId, input.LocationId, (input.Tasks ?? "").Trim(), input.Every, input.Unit, input.NextDue, input.LeadDays ?? 7);
        (p.AssignedTo, p.EstimatedHours, p.Priority, p.Active) = ((input.AssignedTo ?? "").Trim(), Math.Max(0, input.EstimatedHours ?? 0), input.Priority ?? MaintenancePriority.Normal, input.Active ?? true);
        var meterEvery = input.MeterEvery ?? 0;
        if (meterEvery < 0 || (input.LeadMeter ?? 0) < 0) throw new AppException("Meter intervals can't be negative.");
        if (meterEvery > 0)
        {
            var asset = input.AssetId is { } ma ? await db.Assets.SingleOrDefaultAsync(a => a.Id == ma) : null;
            if (asset is null || asset.MeterUnit.Length == 0) throw new AppException("Services by use need an asset with a meter (hours, cycles, km). Set its meter on the asset first.");
            if ((input.LeadMeter ?? 0) >= meterEvery) throw new AppException("Make the work order before the service is due: the lead has to be less than the interval.");
            (p.MeterEvery, p.LeadMeter) = (meterEvery, input.LeadMeter ?? 0);
            p.NextDueMeter = input.NextDueMeter ?? p.NextDueMeter ?? asset.MeterReading + meterEvery;
        }
        else (p.MeterEvery, p.LeadMeter, p.NextDueMeter) = (0, 0, null);
        await db.SaveChangesAsync();
        return p;
    }

    // ------------------------------------------------------------------------------------------------ meters

    /// <summary>An asset's meter: its unit, the latest reading, and how much it's used a day (from the last 90 days).</summary>
    public async Task<(string Unit, decimal Reading, decimal? PerDay)> MeterInfo(Guid assetId)
    {
        var a = await db.Assets.Where(x => x.Id == assetId).Select(x => new { x.MeterUnit, x.MeterReading }).SingleAsync();
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var readings = await db.MeterReadings.Where(r => r.AssetId == assetId).OrderBy(r => r.At).Select(r => new { r.Reading, r.At }).ToListAsync();
        var recent = readings.Where(r => r.At >= since).ToList();
        if (recent.Count < 2 && readings.Count >= 2) recent = readings.TakeLast(2).ToList();
        decimal? perDay = null;
        if (recent.Count >= 2)
        {
            var days = (decimal)(recent[^1].At - recent[0].At).TotalDays;
            if (days >= 1) perDay = Math.Round((recent[^1].Reading - recent[0].Reading) / days, 2);
        }
        return (a.MeterUnit, a.MeterReading, perDay);
    }

    /// <summary>A new reading of an asset's meter. Readings only go up; a service that falls due by use gets its work order.</summary>
    public async Task<object> RecordMeter(Guid assetId, MeterInput input)
    {
        if (!ctx.Can(Policies.Maintain) && !ctx.Can(Policies.Assets)) throw new AppException("Only maintenance or whoever keeps the asset register records meter readings.", 403);
        var a = await db.Assets.SingleOrDefaultAsync(x => x.Id == assetId) ?? throw new AppException("Not found.", 404);
        if (a.MeterUnit.Length == 0) throw new AppException($"{a.Name} has no meter. Set what it's measured in (hours, cycles, km) on the asset first.");
        if (input.Reading < a.MeterReading) throw new AppException($"The meter was already at {a.MeterReading:0.##} {a.MeterUnit}: readings only go up. Check the figure.");
        if (input.Reading > a.MeterReading + 100_000) throw new AppException("That's a very big jump from the last reading. Check the figure.");
        var at = input.At ?? DateTimeOffset.UtcNow;
        if (at > DateTimeOffset.UtcNow.AddMinutes(5)) throw new AppException("A reading can't be in the future.");
        db.MeterReadings.Add(new MeterReading { AssetId = a.Id, Reading = input.Reading, At = at, Actor = ctx.Actor, Note = (input.Note ?? "").Trim() });
        (a.MeterReading, a.MeterReadAt) = (input.Reading, at);
        await db.SaveChangesAsync();
        var made = await GenerateDue();
        var (unit, reading, perDay) = await MeterInfo(a.Id);
        return new { a.Id, unit, reading, perDay, made };
    }

    public async Task<object> MeterHistory(Guid assetId) =>
        await db.MeterReadings.Where(r => r.AssetId == assetId).OrderByDescending(r => r.At).Take(100).Select(r => new { r.Reading, r.At, r.Actor, r.Note }).ToListAsync();

    /// <summary>Makes a preventive work order for every active plan that's within its lead time and doesn't already
    /// have one open. Safe to call any number of times.</summary>
    public async Task<object> GenerateDue()
    {
        var today = Clock.Today;
        var meters = await db.Assets.Where(a => a.MeterUnit != "").ToDictionaryAsync(a => a.Id, a => a.MeterReading);
        // Due by date, or by use (whichever comes first).
        bool ByMeter(MaintenancePlan p) => p.MeterEvery > 0 && p.NextDueMeter is { } at && p.AssetId is { } a && meters.TryGetValue(a, out var r) && r >= at - p.LeadMeter;
        var plans = (await db.MaintenancePlans.Where(p => p.Active).ToListAsync()).Where(p => p.NextDue.AddDays(-p.LeadDays) <= today || ByMeter(p)).ToList();
        var busy = (await db.WorkOrders.Where(w => w.MaintenancePlanId != null && w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled)
            .Select(w => w.MaintenancePlanId!.Value).ToListAsync()).ToHashSet();
        var made = new List<string>();
        foreach (var p in plans.Where(p => !busy.Contains(p.Id)))
        {
            var w = await NewWorkOrder(new WorkOrderInput(p.Name, p.Tasks, WorkOrderType.Preventive, p.Priority, p.AssetId, p.LocationId, null, p.AssignedTo, null, null,
                p.AssignedTo.Length > 0 ? (p.NextDue < today ? today : p.NextDue) : null, p.NextDue, p.EstimatedHours, null));
            w.MaintenancePlanId = p.Id;
            made.Add(w.Number);
        }
        if (made.Count > 0) await db.SaveChangesAsync();
        return new { made = made.Count, numbers = made };
    }

    // ------------------------------------------------------------------------------------------------ insights

    /// <summary>How maintenance is doing over a period: the backlog, reliability (time between failures, time to repair,
    /// repeat failures), preventive work done on time, the planned/reactive split, and what it cost.</summary>
    public async Task<object> Insights(int days)
    {
        days = Math.Clamp(days, 30, 365);
        var today = Clock.Today;
        var from = today.AddDays(-days + 1);
        var since = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        var jobs = await db.WorkOrders.Include(w => w.Labour).Include(w => w.Parts).ToListAsync();
        var assets = await db.Assets.Where(a => a.Status != AssetStatus.Disposed).Select(a => new { a.Id, a.Tag, a.Name, a.Category }).ToListAsync();
        var live = jobs.Where(w => Live.Contains(w.Status)).ToList();
        var done = jobs.Where(w => w.CompletedAt >= since).ToList();
        var breakdowns = jobs.Where(w => w.Type == WorkOrderType.Breakdown && w.CreatedAt >= since && w.Status != WorkOrderStatus.Cancelled).ToList();
        var repaired = breakdowns.Where(w => w.CompletedAt is not null).ToList();
        // Time to repair: from going down (or the job starting) to back in service.
        var repairHours = repaired.Select(w => Hours(w.DownFrom ?? w.StartedAt ?? w.CreatedAt, w.DownTo ?? w.CompletedAt!.Value)).ToList();
        // Time between failures: the register's asset-days in the period over the breakdowns in it.
        double? mtbfDays = breakdowns.Count > 0 ? Math.Round((double)assets.Count * days / breakdowns.Count, 1) : null;
        // A repeat: the same asset breaking down again within 30 days of a repair.
        var repeats = breakdowns.Count(b => b.AssetId is { } a && jobs.Any(o => o.Id != b.Id && o.AssetId == a && o.Type == WorkOrderType.Breakdown && o.CompletedAt is { } c && c < b.CreatedAt && b.CreatedAt - c <= TimeSpan.FromDays(30)));
        var pmDue = jobs.Where(w => w.Type == WorkOrderType.Preventive && w.DueDate is { } d && d >= from && d <= today && w.Status != WorkOrderStatus.Cancelled).ToList();
        var pmOnTime = pmDue.Count(w => w.CompletedAt is { } c && DateOnly.FromDateTime(c.ToOffset(TimeSpan.FromHours(3)).DateTime) <= w.DueDate);
        var planned = done.Count(w => w.Type is WorkOrderType.Preventive or WorkOrderType.Inspection);
        var reactive = done.Count(w => w.Type is WorkOrderType.Breakdown or WorkOrderType.Corrective);

        // Spend from the books (parts, payments and bills on repairs and maintenance), plus labour for those who see pay.
        var account = await ledger.Id(SystemAccounts.Maintenance);
        var spend = await db.JournalLines.Where(l => l.AccountId == account).Join(db.JournalEntries, l => l.JournalEntryId, e => e.Id, (l, e) => new { l.AssetId, l.WorkOrderId, l.Debit, l.Credit, e.Date })
            .Where(x => x.Date >= from && x.Date <= today).ToListAsync();
        var pay = SeesPay;
        var labour = jobs.SelectMany(w => w.Labour.Select(l => new { w.AssetId, l.Date, cost = (long)Math.Round(l.Hours * l.HourlyRate) })).Where(l => l.Date >= from && l.Date <= today).ToList();
        var byAsset = assets.Select(a => new
        {
            a.Id, a.Tag, a.Name,
            cost = spend.Where(s => s.AssetId == a.Id).Sum(s => s.Debit - s.Credit) + (pay ? labour.Where(l => l.AssetId == a.Id).Sum(l => l.cost) : 0),
            failures = breakdowns.Count(b => b.AssetId == a.Id),
            downtimeHours = Math.Round(jobs.Where(w => w.AssetId == a.Id && w.DownFrom is not null).Sum(w => Hours(w.DownFrom! < since ? since : w.DownFrom!.Value, w.DownTo ?? DateTimeOffset.UtcNow)), 1),
        }).Where(x => x.cost != 0 || x.failures > 0).OrderByDescending(x => x.cost).Take(10).ToList();
        var modes = done.Where(w => w.FailureMode.Length > 0 && w.Type is WorkOrderType.Breakdown or WorkOrderType.Corrective).GroupBy(w => w.FailureMode)
            .Select(g => new { mode = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
        var causes = done.Where(w => w.FailureCause.Length > 0).GroupBy(w => w.FailureCause).Select(g => new { cause = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
        return new
        {
            days, from, to = today,
            backlog = new { open = live.Count, overdue = live.Count(w => w.DueDate < today), hours = live.Sum(w => w.EstimatedHours), onHold = live.Count(w => w.Status == WorkOrderStatus.OnHold), toClose = jobs.Count(w => w.Status == WorkOrderStatus.Completed) },
            completed = done.Count,
            reliability = new
            {
                breakdowns = breakdowns.Count, mttrHours = repairHours.Count > 0 ? Math.Round(repairHours.Average(), 1) : (double?)null, mtbfDays,
                repeats, repeatRate = breakdowns.Count > 0 ? Math.Round(100.0 * repeats / breakdowns.Count) : (double?)null,
            },
            preventive = new { due = pmDue.Count, onTime = pmOnTime, compliance = pmDue.Count > 0 ? Math.Round(100.0 * pmOnTime / pmDue.Count) : (double?)null },
            mix = new { planned, reactive, plannedShare = planned + reactive > 0 ? Math.Round(100.0 * planned / (planned + reactive)) : (double?)null },
            cost = new { booked = spend.Sum(s => s.Debit - s.Credit), labour = pay ? labour.Sum(l => l.cost) : (long?)null },
            byAsset, modes, causes,
        };
    }
}
