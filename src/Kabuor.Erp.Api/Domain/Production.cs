// Making things: a recipe (bill of materials) says what goes into one batch and what comes out; a production
// run uses ingredients from stock and adds the finished goods to stock at what they cost to make.
namespace Kabuor.Erp.Domain;

public sealed class Recipe : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public Guid OutputItemId { get; set; }
    /// <summary>How many of the finished item one batch makes.</summary>
    public decimal OutputQuantity { get; set; } = 1;
    /// <summary>Labour, gas, electricity and other costs per batch that go into the product's cost, in cents.</summary>
    public long ConversionCostPerBatch { get; set; }
    public string Notes { get; set; } = "";
    public bool Active { get; set; } = true;
    public List<RecipeLine> Lines { get; set; } = [];
}

public sealed class RecipeLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid RecipeId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>How much of the ingredient or packaging one batch uses.</summary>
    public decimal Quantity { get; set; }
}

public enum ProductionStatus
{
    Planned,
    Done,
    Cancelled,
}

public sealed class ProductionRun : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // PR-0001
    public Guid RecipeId { get; set; }
    public string RecipeName { get; set; } = "";
    public Guid OutputItemId { get; set; }
    public decimal Batches { get; set; } = 1;
    public Guid LocationId { get; set; }
    public ProductionStatus Status { get; set; } = ProductionStatus.Planned;
    public DateOnly PlannedFor { get; set; }
    /// <summary>What actually came out (can differ from the recipe: spills, a good yield).</summary>
    public decimal OutputQuantity { get; set; }
    public long MaterialCost { get; set; }
    public long ConversionCost { get; set; }
    /// <summary>Cost of one finished unit, before VAT, in cents.</summary>
    public decimal UnitCost { get; set; }
    public string Notes { get; set; } = "";
    public string CompletedBy { get; set; } = "";
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
