// The things a business owns and uses rather than sells: chairs, tools, laptops, vehicles. Each has a tag the system
// generates (AST-FUR-0001) and prints as a QR label, so anyone can scan it to see what it is, where it should be and
// who has it, and a count is a walk round the premises with a phone.
namespace Kabuor.Erp.Domain;

public enum AssetStatus
{
    InUse,       // in use, with a person or at a place
    InStore,     // kept in a store, not in use
    UnderRepair,
    Lost,
    Disposed,    // sold, scrapped or given away
}

public enum AssetCondition
{
    Good,
    Fair,
    Poor,
}

public sealed class Asset : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    /// <summary>The generated tag on the label, e.g. AST-FUR-0001. Unique per company.</summary>
    public required string Tag { get; set; }
    public required string Name { get; set; }
    /// <summary>FUR, TLS, ELC, VEH, EQP or OTH (see AssetCategories).</summary>
    public string Category { get; set; } = "OTH";
    /// <summary>The maker's serial number, if it has one.</summary>
    public string SerialNumber { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>The asset it's part of: a motor in a machine, a machine on a production line, a line in a plant.</summary>
    public Guid? ParentId { get; set; }
    /// <summary>The location: where it is.</summary>
    public Guid? LocationId { get; set; }
    /// <summary>The custodian: the employee answerable for it.</summary>
    public Guid? EmployeeId { get; set; }
    /// <summary>The custodian's name (an employee's, or anyone else's).</summary>
    public string AssignedTo { get; set; } = "";
    /// <summary>The department that uses it and carries its running costs.</summary>
    public Guid? DepartmentId { get; set; }
    public AssetCriticality Criticality { get; set; } = AssetCriticality.Medium;
    public DateOnly? WarrantyUntil { get; set; }
    /// <summary>What its use is measured in, if it has a meter: hours, cycles, km, landings, starts. Empty: no meter.</summary>
    public string MeterUnit { get; set; } = "";
    /// <summary>The latest reading of that meter (it only goes up).</summary>
    public decimal MeterReading { get; set; }
    public DateTimeOffset? MeterReadAt { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.InUse;
    public AssetCondition Condition { get; set; } = AssetCondition.Good;
    public DateOnly? PurchaseDate { get; set; }
    /// <summary>What it cost, in cents.</summary>
    public long Cost { get; set; }
    public string Supplier { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset? LastSeenAt { get; set; }
    public string LastSeenBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What happened to an asset, in plain words: added, moved, given to someone, repaired, seen in a count.</summary>
public sealed class AssetEvent : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid AssetId { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public static class AssetCategories
{
    public static readonly (string Code, string Name)[] All =
    [
        ("FUR", "Furniture"), ("TLS", "Tools"), ("ELC", "Electronics and computers"), ("VEH", "Vehicles"), ("EQP", "Machines and equipment"), ("OTH", "Other"),
    ];

    public static string Name(string code) => All.FirstOrDefault(c => c.Code == code).Name ?? "Other";
}
