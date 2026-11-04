// Operations: departments, keeping assets working (faults, work orders, preventive plans) and buying for the
// business (requests from departments, tenders and quotations, stock takes). The flows follow what established
// systems do (SAP PM, Maximo, Dynamics 365) in plain words:
//   something breaks → a fault report → a work order → planned (parts, labour, contractors) → scheduled → done
//   → technically complete (asset back in service, what failed and why recorded) → financially closed (every cost
//   in, nothing more can be charged).
//   a department needs something → a request → its head approves → checked against the budget → issued from the
//   store, ordered from a supplier, or put out to tender → the bill turns the commitment into a cost.
namespace Kabuor.Erp.Domain;

// ---------- departments ----------

/// <summary>A part of the business that spends money: Operations, Sales, Finance, Workshop. Costs are charged to one,
/// requests come from one, and each can have a head who approves its requests and a monthly budget.</summary>
public sealed class Department : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    /// <summary>A short code for reports, e.g. OPS.</summary>
    public string Code { get; set; } = "";
    /// <summary>Approves the department's requests first (from their portal). Empty: requests go straight to procurement.</summary>
    public Guid? HeadEmployeeId { get; set; }
    /// <summary>What the department may spend in a month, in cents. 0: no budget set (nothing is checked).</summary>
    public long MonthlyBudget { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ---------- assets ----------

/// <summary>An asset type the business added beside the built-in ones (furniture, tools…): its code is part of the tag.</summary>
public sealed class AssetType : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    /// <summary>Three letters, e.g. GEN for generators: tags become AST-GEN-0001.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>How much it matters when the asset stops: decides which work goes first, and what has to be recorded.</summary>
public enum AssetCriticality
{
    Low,
    Medium,
    High, // the business stops or someone could get hurt
}

// ---------- maintenance ----------

public enum MaintenancePriority
{
    Emergency, // now: safety, or the business has stopped
    High,      // today
    Normal,    // this week
    Low,       // when there's time
}

public enum FaultStatus
{
    New,      // reported, nobody has looked at it yet
    Accepted, // a work order is dealing with it
    Rejected, // not a fault, or a duplicate (with the reason)
    Resolved, // its work order was completed
}

/// <summary>"Something is broken": what, where, how bad, and who said so. Anyone can report one, including employees
/// from their portal. Maintenance turns it into a work order, or says why not.</summary>
public sealed class FaultReport : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // FR-0001
    public Guid? AssetId { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>What's wrong, in a line: "Generator won't start".</summary>
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    /// <summary>What it's doing wrong (the failure mode), from the list: won't start, leaking, noise…</summary>
    public string Symptom { get; set; } = "";
    public MaintenancePriority Priority { get; set; } = MaintenancePriority.Normal;
    /// <summary>It can't be used at all: downtime is counted from the report.</summary>
    public bool AssetDown { get; set; }
    public FaultStatus Status { get; set; } = FaultStatus.New;
    public string ReportedBy { get; set; } = "";
    public Guid? ReportedByUserId { get; set; }
    public Guid? ReportedByEmployeeId { get; set; }
    public Guid? WorkOrderId { get; set; }
    public string RejectReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public enum WorkOrderType
{
    Breakdown,   // it stopped: fix it now
    Corrective,  // it's wearing or faulty but still running: fix it soon
    Preventive,  // planned service before it fails (from a maintenance plan)
    Inspection,  // look, measure and report
    Improvement, // make it better (a modification or upgrade)
}

public enum WorkOrderStatus
{
    Open,       // created, not planned yet
    Planned,    // who, when and what's needed are set
    InProgress, // being worked on
    OnHold,     // waiting (parts, a contractor, the asset to be free), with the reason
    Completed,  // technically complete: the work is done and the asset is back in service
    Closed,     // financially closed: every cost is in, nothing more can be charged
    Cancelled,
}

/// <summary>One job on an asset (or a place): what to do, who does it, when, what it needs and what it cost.</summary>
public sealed class WorkOrder : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // WO-0001
    public WorkOrderType Type { get; set; } = WorkOrderType.Corrective;
    public MaintenancePriority Priority { get; set; } = MaintenancePriority.Normal;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Open;
    public Guid? AssetId { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>The department the cost is charged to (defaults to the asset's).</summary>
    public Guid? DepartmentId { get; set; }
    public Guid? FaultReportId { get; set; }
    public Guid? MaintenancePlanId { get; set; }
    public required string Title { get; set; }
    /// <summary>The job: what to do, step by step if it helps.</summary>
    public string Description { get; set; } = "";
    /// <summary>Who does it: a technician's name, or a contractor.</summary>
    public string AssignedTo { get; set; } = "";
    public Guid? AssigneeEmployeeId { get; set; }
    /// <summary>A contractor doing the job (their bills are this job's service costs).</summary>
    public Guid? SupplierId { get; set; }
    public DateOnly? PlannedStart { get; set; }
    /// <summary>When it must be done by. Preventive work is on time if completed by this day.</summary>
    public DateOnly? DueDate { get; set; }
    public decimal EstimatedHours { get; set; }
    /// <summary>The cost expected when it was planned, in cents (parts, labour and services together).</summary>
    public long EstimatedCost { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    /// <summary>When the asset stopped being usable and when it was back: the downtime this job covers.</summary>
    public DateTimeOffset? DownFrom { get; set; }
    public DateTimeOffset? DownTo { get; set; }
    public string OnHoldReason { get; set; } = "";
    /// <summary>What failed, why, and what was done (ISO 14224's mode, cause and remedy), recorded at completion.</summary>
    public string FailureMode { get; set; } = "";
    public string FailureCause { get; set; } = "";
    public string Remedy { get; set; } = "";
    public string CompletionNotes { get; set; } = "";
    public string CancelReason { get; set; } = "";
    /// <summary>The final costs, fixed when the job is financially closed, in cents.</summary>
    public long PartsCost { get; set; }
    public long LabourCost { get; set; }
    public long ServicesCost { get; set; }
    public string CreatedBy { get; set; } = "";
    public string CompletedBy { get; set; } = "";
    public string ClosedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkOrderPart> Parts { get; set; } = [];
    public List<WorkOrderLabour> Labour { get; set; } = [];
    public List<WorkOrderCost> Costs { get; set; } = [];
}

/// <summary>A spare part or material the job needs (planned) and what was actually taken from the store (issued).</summary>
public sealed class WorkOrderPart : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid WorkOrderId { get; set; }
    /// <summary>A stocked item; empty for something that has to be bought.</summary>
    public Guid? ItemId { get; set; }
    public required string Description { get; set; }
    public string Unit { get; set; } = "";
    public decimal PlannedQuantity { get; set; }
    public decimal IssuedQuantity { get; set; }
    /// <summary>Cost of one unit before VAT, in cents: the estimate until issued, then the stock's average cost.</summary>
    public decimal UnitCost { get; set; }
}

/// <summary>Hours worked on the job. Wages are already in the books through payroll, so labour is costed to the job
/// (to know what a repair really cost) but not posted again.</summary>
public sealed class WorkOrderLabour : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid? EmployeeId { get; set; }
    public required string Worker { get; set; }
    public DateOnly Date { get; set; }
    public decimal Hours { get; set; }
    /// <summary>Cost of an hour, in cents.</summary>
    public long HourlyRate { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>Money paid out for the job directly: a fundi paid by M-Pesa, a part bought for cash. Posted to repairs and
/// maintenance when recorded. (Contractor bills are linked to the job instead.)</summary>
public sealed class WorkOrderCost : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid WorkOrderId { get; set; }
    public required string Description { get; set; }
    public long Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public string Reference { get; set; } = "";
    public string PaidTo { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Actor { get; set; } = "";
}

public enum IntervalUnit
{
    Days,
    Weeks,
    Months,
}

/// <summary>Planned servicing that repeats: "Generator: change oil and filters every 3 months". When it's nearly
/// due, a preventive work order is made for it.</summary>
public sealed class MaintenancePlan : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? LocationId { get; set; }
    /// <summary>What to do, one step per line (copied onto each work order as its checklist).</summary>
    public string Tasks { get; set; } = "";
    public int Every { get; set; } = 1;
    public IntervalUnit Unit { get; set; } = IntervalUnit.Months;
    public DateOnly NextDue { get; set; }
    /// <summary>How many days before it's due the work order is made, so parts can be ready.</summary>
    public int LeadDays { get; set; } = 7;
    public string AssignedTo { get; set; } = "";
    public decimal EstimatedHours { get; set; }
    public MaintenancePriority Priority { get; set; } = MaintenancePriority.Normal;
    public DateOnly? LastDoneOn { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>By use as well as by date (whichever comes first): every this many units on the asset's meter (500 hours,
    /// 1,000 cycles, 10,000 km). 0: by date only.</summary>
    public decimal MeterEvery { get; set; }
    /// <summary>The meter reading at which the next service is due.</summary>
    public decimal? NextDueMeter { get; set; }
    /// <summary>How many units before it's due the work order is made (the meter's version of lead days).</summary>
    public decimal LeadMeter { get; set; }
    public decimal? LastDoneMeter { get; set; }

    public DateOnly After(DateOnly from) => Unit switch
    {
        IntervalUnit.Days => from.AddDays(Every),
        IntervalUnit.Weeks => from.AddDays(7 * Every),
        _ => from.AddMonths(Every),
    };
}

// ---------- procurement ----------

public enum RequisitionStatus
{
    Draft,
    WithHead,      // waiting for the head of department
    WithBuying,    // waiting for procurement (or the owner) to approve
    WithFinance,   // procurement agreed: waiting for finance to sign off the money against the budget
    Approved,      // approved: being issued from the store, ordered or tendered
    Fulfilled,     // everything issued or ordered
    Rejected,
    Cancelled,
}

/// <summary>A department asking for things it needs (an internal request, before anything is bought). Approved
/// requests are committed against the department's budget until they're issued or billed.</summary>
public sealed class Requisition : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // PR-0001
    public Guid DepartmentId { get; set; }
    public string RequestedBy { get; set; } = "";
    public Guid? RequestedByUserId { get; set; }
    public Guid? RequestedByEmployeeId { get; set; }
    /// <summary>Parts for a maintenance job.</summary>
    public Guid? WorkOrderId { get; set; }
    public DateOnly? NeededBy { get; set; }
    /// <summary>Why it's needed.</summary>
    public string Purpose { get; set; } = "";
    public RequisitionStatus Status { get; set; } = RequisitionStatus.Draft;
    public long EstimatedTotal { get; set; }
    public string HeadApprovedBy { get; set; } = "";
    public DateTimeOffset? HeadApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }
    /// <summary>It was over the department's budget when approved (only the owner or an accountant may do that).</summary>
    public bool OverBudget { get; set; }
    /// <summary>Finance signed off the money: nothing is issued, ordered or tendered before this.</summary>
    public string FinanceSignedOffBy { get; set; } = "";
    public DateTimeOffset? FinanceSignedOffAt { get; set; }
    /// <summary>What finance wrote when signing off (required when it goes over the budget).</summary>
    public string FinanceNote { get; set; } = "";
    public string RejectReason { get; set; } = "";
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RequisitionLine> Lines { get; set; } = [];
}

public sealed class RequisitionLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid RequisitionId { get; set; }
    public Guid? ItemId { get; set; }
    public required string Description { get; set; }
    public string Unit { get; set; } = "";
    public decimal Quantity { get; set; }
    /// <summary>What one is expected to cost (VAT inclusive), in cents.</summary>
    public long EstimatedUnitCost { get; set; }
    /// <summary>Given out from the store.</summary>
    public decimal IssuedQuantity { get; set; }
    /// <summary>Put on a purchase order or a tender.</summary>
    public decimal OrderedQuantity { get; set; }
    public decimal Outstanding => Math.Max(0, Quantity - IssuedQuantity - OrderedQuantity);
}

public enum TenderMethod
{
    Quotation, // request for quotations: sent to chosen suppliers by link
    Open,      // open tender: posted on the business's public tenders page for anyone to bid
}

public enum TenderStatus
{
    Draft,
    Published, // taking bids until it closes; bids stay sealed
    Opened,    // closed and opened: bids can be seen and evaluated
    Awarded,
    Cancelled,
}

public enum TenderEvaluation
{
    LowestPrice, // the cheapest responsive bid wins
    Weighted,    // technical score and price together (quality and cost based)
}

/// <summary>Asking suppliers to bid for a supply: quotations from chosen suppliers, or an open tender. Bids are sealed
/// until the closing time has passed and the tender is opened; then they're evaluated and one is awarded, which makes
/// the purchase order. The others are told, and the order waits out the standstill period before it goes.</summary>
public sealed class Tender : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // TND-0001
    public required string Title { get; set; }
    /// <summary>What's wanted, the conditions and what bidders must send.</summary>
    public string Scope { get; set; } = "";
    public string Requirements { get; set; } = "";
    public TenderMethod Method { get; set; } = TenderMethod.Quotation;
    public TenderStatus Status { get; set; } = TenderStatus.Draft;
    public TenderEvaluation Evaluation { get; set; } = TenderEvaluation.LowestPrice;
    /// <summary>Weighted only: the technical score's share of the total (the rest is price), and the technical score
    /// a bid needs before its price counts.</summary>
    public int TechnicalWeight { get; set; } = 30;
    public int TechnicalPassMark { get; set; } = 70;
    public Guid? DepartmentId { get; set; }
    public Guid? RequisitionId { get; set; }
    /// <summary>The bid link's secret part (also how the public page finds it).</summary>
    public required string Token { get; set; }
    public DateTimeOffset ClosesAt { get; set; }
    /// <summary>Days between the award notice and sending the order, so other bidders can query it.</summary>
    public int StandstillDays { get; set; } = 14;
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public string OpenedBy { get; set; } = "";
    public DateTimeOffset? AwardedAt { get; set; }
    public string AwardedBy { get; set; } = "";
    public Guid? AwardedBidId { get; set; }
    public string AwardReason { get; set; } = "";
    public Guid? PurchaseOrderId { get; set; }
    public string CancelReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TenderLine> Lines { get; set; } = [];
    public DateTimeOffset? StandstillEnds => AwardedAt?.AddDays(StandstillDays);
}

public sealed class TenderLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid TenderId { get; set; }
    public Guid? ItemId { get; set; }
    public required string Description { get; set; }
    public string Specification { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Quantity { get; set; }
}

/// <summary>A supplier's bid, sent from the public bid page. Amounts are VAT inclusive, in cents.</summary>
public sealed class Bid : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid TenderId { get; set; }
    public Guid? SupplierId { get; set; }
    public required string BidderName { get; set; }
    public string KraPin { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public int DeliveryDays { get; set; }
    public int ValidityDays { get; set; } = 90;
    public string Notes { get; set; } = "";
    public long Total { get; set; }
    /// <summary>A short code the bidder keeps as proof the bid arrived.</summary>
    public string Receipt { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Evaluation: meets the mandatory requirements, its technical score (0-100), and why.</summary>
    public bool? Responsive { get; set; }
    public int? TechnicalScore { get; set; }
    public string EvaluationNote { get; set; } = "";
    public List<BidLine> Lines { get; set; } = [];
}

public sealed class BidLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid BidId { get; set; }
    public Guid TenderLineId { get; set; }
    public long UnitPrice { get; set; }
    public long Total { get; set; }
    public string Note { get; set; } = "";
}

// ---------- stock takes ----------

public enum StockTakeStatus
{
    Counting, // counts are being entered
    Posted,   // differences booked: stock and the books now match the shelves
    Cancelled,
}

/// <summary>A count of one store: what the system expected is noted when it starts, counters enter what's on the
/// shelves (without seeing the expected figure in a blind count), and someone reviews the differences and posts them.</summary>
public sealed class StockTake : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // ST-0001
    public Guid LocationId { get; set; }
    public StockTakeStatus Status { get; set; } = StockTakeStatus.Counting;
    public bool Blind { get; set; } = true;
    public string Note { get; set; } = "";
    public string StartedBy { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string PostedBy { get; set; } = "";
    public DateTimeOffset? PostedAt { get; set; }
    /// <summary>The value of the differences booked, in cents (negative: stock was missing).</summary>
    public long VarianceValue { get; set; }
    public List<StockTakeLine> Lines { get; set; } = [];
}

public sealed class StockTakeLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid StockTakeId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Expected { get; set; }
    public decimal? Counted { get; set; }
    public string CountedBy { get; set; } = "";
    public DateTimeOffset? CountedAt { get; set; }
    public decimal UnitCost { get; set; }
}

// ---------- budgets and the approvals register ----------

public enum BudgetStatus
{
    Draft,
    Submitted, // waiting for finance
    Approved,  // the department may spend against it
    Rejected,
    Replaced,  // a revision was approved in its place
}

/// <summary>What a department may spend in a year, month by month, approved by finance. A department without an
/// approved budget for the month can't have requests approved: no budget, no spending.</summary>
public sealed class Budget : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // BG-0001
    public Guid DepartmentId { get; set; }
    public int Year { get; set; }
    /// <summary>Twelve amounts in cents, January first.</summary>
    public List<long> Months { get; set; } = [.. new long[12]];
    public long Total { get; set; }
    /// <summary>Why the department needs it: what it will pay for.</summary>
    public string Justification { get; set; } = "";
    public BudgetStatus Status { get; set; } = BudgetStatus.Draft;
    public string PreparedBy { get; set; } = "";
    public Guid? PreparedByUserId { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string DecidedBy { get; set; } = "";
    public DateTimeOffset? DecidedAt { get; set; }
    public string DecisionNote { get; set; } = "";
    /// <summary>A revision: the approved budget it will replace once approved.</summary>
    public Guid? RevisesId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One decision about money, in the order it happened: a budget submitted or approved, a request signed off,
/// an order released, a tender awarded, a job's cost closed. Never changed or removed. Each record carries a
/// fingerprint (SHA-256) of itself and of the record before it, so changing, removing or reordering any record
/// breaks every fingerprint after it, and a check shows where.</summary>
public sealed class ApprovalRecord : ICompanyOwned
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    /// <summary>1, 2, 3… per company, with no gaps.</summary>
    public long Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public string Actor { get; set; } = "";
    /// <summary>Budget, Request, PurchaseOrder, Tender, WorkOrder.</summary>
    public required string Subject { get; set; }
    public Guid SubjectId { get; set; }
    /// <summary>The document's number, e.g. PR-0004.</summary>
    public string Reference { get; set; } = "";
    /// <summary>Submitted, Approved, Signed off, Rejected…</summary>
    public required string Action { get; set; }
    public long Amount { get; set; }
    public string Note { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>A tool a job needs (a torque wrench, a test meter, a lifting jack): an asset from the register, reserved for
/// the job, handed to the technician and handed back. One tool can be out on only one job at a time, and a job can't be
/// completed while its tools are still out. A tool that comes back damaged is reported as a fault straight away.</summary>
public sealed class WorkOrderTool : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid AssetId { get; set; }
    public string CheckedOutTo { get; set; } = "";
    public DateTimeOffset? CheckedOutAt { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    /// <summary>How it came back: Good, or Damaged (then a fault is reported on it).</summary>
    public string ReturnCondition { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A reading of an asset's meter (hours flown, cycles, km), in the order they were taken.</summary>
public sealed class MeterReading : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid AssetId { get; set; }
    public decimal Reading { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = "";
    public string Note { get; set; } = "";
}

// ---------- cash advances (imprest) ----------

public enum AdvanceStatus
{
    WithHead,     // waiting for the head of department
    WithFinance,  // waiting for finance to sign off against the budget
    SignedOff,    // signed off, waiting to be paid out
    PaidOut,      // the employee has the money and must account for it
    Accounted,    // receipts in, balance returned or recovered
    Rejected,
    Cancelled,
}

/// <summary>Money given to an employee ahead of spending it for the business (travel, a site visit, petty cash), which
/// they account for with receipts. Until then it's money the employee owes the business.</summary>
public sealed class CashAdvance : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // ADV-0001
    public Guid EmployeeId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Purpose { get; set; } = "";
    public long Amount { get; set; }
    /// <summary>When receipts are due (usually a few days after the trip).</summary>
    public DateOnly AccountBy { get; set; }
    public AdvanceStatus Status { get; set; } = AdvanceStatus.WithFinance;
    public string RequestedBy { get; set; } = "";
    public Guid? RequestedByUserId { get; set; }
    public string HeadApprovedBy { get; set; } = "";
    public string SignedOffBy { get; set; } = "";
    public DateTimeOffset? SignedOffAt { get; set; }
    public string FinanceNote { get; set; } = "";
    public bool OverBudget { get; set; }
    public PaymentMethod? PaidBy { get; set; }
    public string PaidReference { get; set; } = "";
    public DateTimeOffset? PaidAt { get; set; }
    public long Spent { get; set; }
    public long Returned { get; set; }
    public long RecoveredFromPay { get; set; }
    public long Reimbursed { get; set; }
    public DateTimeOffset? AccountedAt { get; set; }
    public string RejectReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<CashAdvanceExpense> Expenses { get; set; } = [];
}

public sealed class CashAdvanceExpense : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid CashAdvanceId { get; set; }
    public required string Description { get; set; }
    public Guid AccountId { get; set; }
    public long Amount { get; set; }
    /// <summary>The receipt's number (an ETR receipt, an M-Pesa code).</summary>
    public string Receipt { get; set; } = "";
    public DateOnly Date { get; set; }
}

// ---------- budget transfers ----------

public enum TransferStatus
{
    Submitted,
    Approved,
    Rejected,
}

/// <summary>Moving part of one department's approved budget for a month to another's, approved by finance.</summary>
public sealed class BudgetTransfer : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // BT-0001
    public Guid FromDepartmentId { get; set; }
    public Guid ToDepartmentId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public long Amount { get; set; }
    public string Reason { get; set; } = "";
    public TransferStatus Status { get; set; } = TransferStatus.Submitted;
    public string RequestedBy { get; set; } = "";
    public Guid? RequestedByUserId { get; set; }
    public string DecidedBy { get; set; } = "";
    public DateTimeOffset? DecidedAt { get; set; }
    public string DecisionNote { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ---------- the monthly planning meeting (sales and operations planning) ----------

/// <summary>The month's planning meeting: what was decided about demand, supply, capacity and money, by whom.
/// Once agreed it goes into the approvals register and can't be changed.</summary>
public sealed class PlanningMeeting : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    /// <summary>The first day of the month it plans.</summary>
    public DateOnly Month { get; set; }
    public string Attendees { get; set; } = "";
    /// <summary>One decision per line.</summary>
    public string Decisions { get; set; } = "";
    /// <summary>The numbers on the table when it was agreed (JSON), so the decisions can be read against them later.</summary>
    public string Snapshot { get; set; } = "";
    public string AgreedBy { get; set; } = "";
    public DateTimeOffset? AgreedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
