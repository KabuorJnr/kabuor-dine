// The asset register: add things (one or many at once, each with its own generated tag), find them by tag or
// serial number, move them to a location, give them to a custodian, mark repairs and losses, and record each scan as a
// sighting. Assets can sit inside other assets (a motor in a pump, a pump on a line, a line in a plant), so faults,
// jobs and costs can be followed up the hierarchy. Asset types are the built-in ones plus the business's own.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Assets;

public sealed record AssetInput(string Name, string? Category, int? Quantity, string? SerialNumber, Guid? LocationId, Guid? EmployeeId, string? AssignedTo,
    AssetStatus? Status, AssetCondition? Condition, DateOnly? PurchaseDate, long? Cost, string? Supplier, string? Notes,
    string? Manufacturer = null, string? Model = null, Guid? ParentId = null, Guid? DepartmentId = null, AssetCriticality? Criticality = null, DateOnly? WarrantyUntil = null, string? MeterUnit = null);
public sealed record AssetTypeInput(string Code, string Name);

/// <summary>One change: kind = move | assign | status | condition | seen | note.</summary>
public sealed record AssetAction(string Kind, Guid? LocationId, Guid? EmployeeId, string? AssignedTo, AssetStatus? Status, AssetCondition? Condition, string? Note);

public sealed class AssetService(ErpDb db, Numbers numbers, RequestContext ctx)
{
    const int MaxAtOnce = 200;

    static string Plain(AssetStatus s) => s switch
    {
        AssetStatus.InUse => "in use", AssetStatus.InStore => "in store", AssetStatus.UnderRepair => "under repair", AssetStatus.Lost => "lost", _ => "disposed",
    };

    async Task<string> Where(Guid? locationId) => locationId is { } id ? await db.Locations.Where(l => l.Id == id).Select(l => l.Name).SingleOrDefaultAsync() ?? "" : "";

    void Log(Asset a, string kind, string text) => db.AssetEvents.Add(new AssetEvent { AssetId = a.Id, Kind = kind, Text = text, Actor = ctx.Actor });

    async Task<string> Who(Guid? employeeId, string? name)
    {
        if (employeeId is { } id)
            return await db.Employees.Where(e => e.Id == id).Select(e => e.Name).SingleOrDefaultAsync() ?? throw new AppException("That employee isn't on the list.");
        return (name ?? "").Trim();
    }

    /// <summary>The names the register shows beside ids: places, departments, types, parents and how many parts each has.</summary>
    sealed record Lookups(Dictionary<Guid, string> Locations, Dictionary<Guid, string> Departments, Dictionary<string, string> Types, Dictionary<Guid, (string Tag, string Name)> Assets, Dictionary<Guid, int> Children);

    async Task<Lookups> Lookup()
    {
        var all = await db.Assets.Select(a => new { a.Id, a.Tag, a.Name, a.ParentId }).ToListAsync();
        return new Lookups(
            await db.Locations.ToDictionaryAsync(l => l.Id, l => l.Name),
            await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Name),
            (await Types()).ToDictionary(t => t.Code, t => t.Name),
            all.ToDictionary(a => a.Id, a => (a.Tag, a.Name)),
            all.Where(a => a.ParentId != null).GroupBy(a => a.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>The built-in types and the business's own.</summary>
    public async Task<List<(string Code, string Name, bool Custom)>> Types()
    {
        var custom = await db.AssetTypes.OrderBy(t => t.Name).Select(t => new { t.Code, t.Name }).ToListAsync();
        return AssetCategories.All.Select(c => (c.Code, c.Name, false)).Concat(custom.Select(c => (c.Code, c.Name, true))).ToList();
    }

    public async Task<object> AddType(AssetTypeInput input)
    {
        var code = (input.Code ?? "").Trim().ToUpperInvariant();
        var name = (input.Name ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z]{3}$")) throw new AppException("The code is three letters, e.g. GEN for generators (tags become AST-GEN-0001).");
        if (name.Length < 2) throw new AppException("Name the type.");
        if ((await Types()).Any(t => t.Code == code)) throw new AppException($"{code} is already used.", 409);
        db.AssetTypes.Add(new AssetType { Code = code, Name = name });
        await db.SaveChangesAsync();
        return new { code, name };
    }

    public async Task<object> List(string? q, string? status, string? category, Guid? locationId)
    {
        var all = await db.Assets.ToListAsync();
        var look = await Lookup();
        var unseenSince = DateTimeOffset.UtcNow.AddDays(-90);
        bool Match(Asset a)
        {
            if (string.IsNullOrWhiteSpace(q)) return true;
            var t = q.Trim();
            return a.Tag.Contains(t, StringComparison.OrdinalIgnoreCase) || a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                || a.SerialNumber.Contains(t, StringComparison.OrdinalIgnoreCase) || a.AssignedTo.Contains(t, StringComparison.OrdinalIgnoreCase);
        }
        var current = all.Where(a => a.Status != AssetStatus.Disposed).ToList();
        var shown = all.Where(Match)
            .Where(a => status switch { null or "" or "all" => a.Status != AssetStatus.Disposed, "unseen" => a.Status != AssetStatus.Disposed && (a.LastSeenAt is null || a.LastSeenAt < unseenSince), _ => Enum.TryParse<AssetStatus>(status, true, out var s) && a.Status == s })
            .Where(a => string.IsNullOrEmpty(category) || a.Category == category)
            .Where(a => locationId is null || a.LocationId == locationId)
            .OrderBy(a => a.Tag).ToList();
        return new
        {
            counts = new
            {
                all = current.Count,
                inUse = current.Count(a => a.Status == AssetStatus.InUse), inStore = current.Count(a => a.Status == AssetStatus.InStore),
                underRepair = current.Count(a => a.Status == AssetStatus.UnderRepair), lost = current.Count(a => a.Status == AssetStatus.Lost),
                disposed = all.Count(a => a.Status == AssetStatus.Disposed), unseen = current.Count(a => a.LastSeenAt is null || a.LastSeenAt < unseenSince),
            },
            totalCost = current.Sum(a => a.Cost),
            categories = (await Types()).Select(c => new { code = c.Code, name = c.Name, custom = c.Custom, count = current.Count(a => a.Category == c.Code) }),
            items = shown.Select(a => Row(a, look)),
        };
    }

    static object Row(Asset a, Lookups look) => new
    {
        a.Id, a.Tag, a.Name, a.Category, categoryName = look.Types.GetValueOrDefault(a.Category) ?? AssetCategories.Name(a.Category), a.SerialNumber, a.Manufacturer, a.Model,
        a.LocationId, location = a.LocationId is { } l ? look.Locations.GetValueOrDefault(l, "") : "", a.EmployeeId, a.AssignedTo,
        a.DepartmentId, department = a.DepartmentId is { } d ? look.Departments.GetValueOrDefault(d, "") : "",
        a.ParentId, parent = a.ParentId is { } p && look.Assets.TryGetValue(p, out var par) ? $"{par.Tag} · {par.Name}" : null, children = look.Children.GetValueOrDefault(a.Id),
        criticality = a.Criticality.ToString(), a.WarrantyUntil, a.MeterUnit, a.MeterReading, a.MeterReadAt,
        status = a.Status.ToString(), condition = a.Condition.ToString(), a.PurchaseDate, a.Cost, a.Supplier, a.Notes, a.LastSeenAt, a.LastSeenBy, a.CreatedAt,
    };

    /// <summary>The parent it sits under, checked: it must exist and must not be the asset itself or one of its own parts.</summary>
    async Task<Guid?> Parent(Guid? parentId, Guid? self)
    {
        if (parentId is not { } pid) return null;
        var all = await db.Assets.Select(a => new { a.Id, a.ParentId }).ToDictionaryAsync(a => a.Id, a => a.ParentId);
        if (!all.ContainsKey(pid)) throw new AppException("The asset it belongs to isn't in the register.");
        for (Guid? at = pid; at is { } cur; at = all.GetValueOrDefault(cur))
            if (cur == self) throw new AppException("An asset can't sit inside itself or one of its own parts.");
        return pid;
    }

    /// <summary>Adds one asset, or many identical ones (20 chairs), each with its own tag in sequence.</summary>
    public async Task<object> Add(AssetInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Say what it is, e.g. \"Office chair\".");
        var category = (await Types()).Any(c => c.Code == input.Category) ? input.Category! : "OTH";
        var quantity = input.Quantity ?? 1;
        if (quantity < 1 || quantity > MaxAtOnce) throw new AppException($"Add between 1 and {MaxAtOnce} at a time.");
        var serial = (input.SerialNumber ?? "").Trim();
        if (quantity > 1 && serial.Length > 0) throw new AppException("A serial number belongs to one item. Add them one at a time, or leave it empty.");
        if ((input.Cost ?? 0) < 0) throw new AppException("The cost can't be negative.");
        if (input.LocationId is { } loc && !await db.Locations.AnyAsync(l => l.Id == loc)) throw new AppException("Choose a place from the list.");
        var assigned = await Who(input.EmployeeId, input.AssignedTo);
        var place = await Where(input.LocationId);
        var parent = await Parent(input.ParentId, null);
        if (input.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep)) throw new AppException("Choose a department from the list.");
        var added = new List<Asset>();
        for (var n = 0; n < quantity; n++)
        {
            var a = new Asset
            {
                Tag = await numbers.Next($"AST-{category}"), Name = name, Category = category, SerialNumber = serial, LocationId = input.LocationId,
                EmployeeId = input.EmployeeId, AssignedTo = assigned, Status = input.Status ?? (assigned.Length > 0 || parent is not null ? AssetStatus.InUse : AssetStatus.InStore),
                Condition = input.Condition ?? AssetCondition.Good, PurchaseDate = input.PurchaseDate, Cost = input.Cost ?? 0,
                Supplier = (input.Supplier ?? "").Trim(), Notes = (input.Notes ?? "").Trim(),
                Manufacturer = (input.Manufacturer ?? "").Trim(), Model = (input.Model ?? "").Trim(), ParentId = parent, DepartmentId = input.DepartmentId,
                Criticality = input.Criticality ?? AssetCriticality.Medium, WarrantyUntil = input.WarrantyUntil, MeterUnit = (input.MeterUnit ?? "").Trim().ToLowerInvariant(),
            };
            db.Assets.Add(a);
            Log(a, "added", $"Added{(place.Length > 0 ? $" at {place}" : "")}{(assigned.Length > 0 ? $", with {assigned}" : "")}");
            added.Add(a);
        }
        await db.SaveChangesAsync();
        var look = await Lookup();
        return new { added = added.Count, items = added.Select(a => Row(a, look)) };
    }

    public async Task<object> Update(Guid id, AssetInput input)
    {
        var a = await db.Assets.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        var name = (input.Name ?? "").Trim();
        if (name.Length < 2) throw new AppException("Say what it is.");
        if ((input.Cost ?? 0) < 0) throw new AppException("The cost can't be negative.");
        if (input.DepartmentId is { } dep && !await db.Departments.AnyAsync(d => d.Id == dep)) throw new AppException("Choose a department from the list.");
        var parent = await Parent(input.ParentId, a.Id);
        if (parent != a.ParentId) Log(a, "moved", parent is { } pid ? $"Now part of {await db.Assets.Where(x => x.Id == pid).Select(x => x.Tag + " " + x.Name).SingleAsync()}" : "No longer part of another asset");
        (a.Name, a.SerialNumber, a.PurchaseDate, a.Cost, a.Supplier, a.Notes) = (name, (input.SerialNumber ?? "").Trim(), input.PurchaseDate, input.Cost ?? a.Cost, (input.Supplier ?? "").Trim(), (input.Notes ?? "").Trim());
        (a.Manufacturer, a.Model, a.ParentId, a.DepartmentId, a.Criticality, a.WarrantyUntil) = ((input.Manufacturer ?? "").Trim(), (input.Model ?? "").Trim(), parent, input.DepartmentId, input.Criticality ?? a.Criticality, input.WarrantyUntil);
        if (input.MeterUnit is not null) a.MeterUnit = input.MeterUnit.Trim().ToLowerInvariant();
        // The category is part of the tag, so it stays as it was; moves and assignments go through actions, so they are logged.
        await db.SaveChangesAsync();
        return await Get(id);
    }

    public async Task<object> Get(Guid id)
    {
        var a = await db.Assets.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        var look = await Lookup();
        var history = await db.AssetEvents.Where(e => e.AssetId == id).OrderByDescending(e => e.At).Take(100).Select(e => new { e.Kind, e.Text, e.Actor, e.At }).ToListAsync();
        // Where it sits (the chain of parents up to the top) and what sits in it.
        var parents = await db.Assets.Select(x => new { x.Id, x.ParentId }).ToDictionaryAsync(x => x.Id, x => x.ParentId);
        var path = new List<object>();
        for (var at = a.ParentId; at is { } cur && path.Count < 20; at = parents.GetValueOrDefault(cur))
            if (look.Assets.TryGetValue(cur, out var p)) path.Insert(0, new { id = cur, tag = p.Tag, name = p.Name });
        var children = await db.Assets.Where(x => x.ParentId == id).OrderBy(x => x.Tag).Select(x => new { x.Id, x.Tag, x.Name, status = x.Status.ToString() }).ToListAsync();
        var jobs = await db.WorkOrders.Where(w => w.AssetId == id).OrderByDescending(w => w.CreatedAt).Take(30)
            .Select(w => new { w.Id, w.Number, w.Title, type = w.Type.ToString(), status = w.Status.ToString(), w.CreatedAt, w.CompletedAt }).ToListAsync();
        var faults = await db.FaultReports.CountAsync(f => f.AssetId == id && f.Status == FaultStatus.New);
        var spent = await db.JournalLines.Where(l => l.AssetId == id).Join(db.Accounts, l => l.AccountId, x => x.Id, (l, x) => new { l.Debit, l.Credit, x.Type }).Where(x => x.Type == AccountType.Expense).ToListAsync();
        var plans = await db.MaintenancePlans.Where(p => p.AssetId == id && p.Active).OrderBy(p => p.NextDue).Select(p => new { p.Id, p.Name, p.NextDue, p.Every, unit = p.Unit.ToString() }).ToListAsync();
        return new
        {
            asset = Row(a, look), history, path, children, workOrders = jobs, plans, openFaults = faults,
            maintenance = new { cost = spent.Sum(x => x.Debit - x.Credit), breakdowns = jobs.Count(j => j.type == nameof(WorkOrderType.Breakdown)), open = jobs.Count(j => j.status is "Open" or "Planned" or "InProgress" or "OnHold") },
        };
    }

    /// <summary>A scanned label: the tag itself, or the label's link (…/assets?tag=AST-FUR-0001). Scanning counts as a sighting.</summary>
    public async Task<object> Scan(string code, bool seen)
    {
        var tag = (code ?? "").Trim();
        if (Uri.TryCreate(tag, UriKind.Absolute, out var u) && System.Web.HttpUtility.ParseQueryString(u.Query)["tag"] is { Length: > 0 } fromLink) tag = fromLink;
        var a = await db.Assets.SingleOrDefaultAsync(x => x.Tag == tag.ToUpperInvariant())
            ?? await db.Assets.FirstOrDefaultAsync(x => x.SerialNumber != "" && x.SerialNumber == tag)
            ?? throw new AppException($"No asset has the tag or serial number {tag}.", 404);
        if (seen) await Act(a.Id, new AssetAction("seen", null, null, null, null, null, null));
        return await Get(a.Id);
    }

    public async Task<object> Act(Guid id, AssetAction input)
    {
        var a = await db.Assets.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        var note = (input.Note ?? "").Trim();
        switch ((input.Kind ?? "").ToLowerInvariant())
        {
            case "move":
                if (input.LocationId is not { } loc || !await db.Locations.AnyAsync(l => l.Id == loc)) throw new AppException("Choose where it went.");
                var from = await Where(a.LocationId);
                a.LocationId = loc;
                Log(a, "moved", $"Moved{(from.Length > 0 ? $" from {from}" : "")} to {await Where(loc)}");
                break;
            case "assign":
                var who = await Who(input.EmployeeId, input.AssignedTo);
                (a.EmployeeId, a.AssignedTo) = (input.EmployeeId, who);
                if (who.Length > 0 && a.Status == AssetStatus.InStore) a.Status = AssetStatus.InUse;
                Log(a, "assigned", who.Length > 0 ? $"Given to {who}" : "Returned (no one has it now)");
                break;
            case "status":
                if (input.Status is not { } st) throw new AppException("Choose the new status.");
                if (st is AssetStatus.Lost or AssetStatus.Disposed && note.Length < 3) throw new AppException("Say what happened, for the record.");
                a.Status = st;
                if (st is AssetStatus.InStore or AssetStatus.Disposed) (a.EmployeeId, a.AssignedTo) = (null, "");
                Log(a, "status", $"Marked {Plain(st)}");
                break;
            case "condition":
                if (input.Condition is not { } c) throw new AppException("Choose the condition.");
                a.Condition = c;
                Log(a, "condition", $"Condition: {c.ToString().ToLowerInvariant()}");
                break;
            case "seen":
                (a.LastSeenAt, a.LastSeenBy) = (DateTimeOffset.UtcNow, ctx.Actor);
                if (a.Status == AssetStatus.Lost) { a.Status = AssetStatus.InStore; Log(a, "status", "Found again"); }
                Log(a, "seen", "Seen (scanned)");
                break;
            case "note":
                if (note.Length == 0) throw new AppException("Write the note.");
                break;
            default:
                throw new AppException("Unknown action.");
        }
        if (note.Length > 0) Log(a, "note", note);
        await db.SaveChangesAsync();
        return await Get(id);
    }
}
