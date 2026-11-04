// The database. Two rules are enforced here so no endpoint can forget them:
//  1. Company isolation: every company-owned table is filtered by the signed-in user's company, and new rows
//     are stamped with it. A request without a company sees nothing.
//  2. Audit: every insert, change and delete is written to AuditEntries in the same transaction.
using System.Text.Json;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Kabuor.Erp.Data;

/// <summary>Who is making this request. Set by the auth middleware; empty for anonymous requests.</summary>
public sealed class RequestContext
{
    public Guid CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string Actor { get; set; } = "system";
    public Role Role { get; set; } = Role.Viewer;
    public Guid? EmployeeId { get; set; }
    public bool HasCompany => CompanyId != Guid.Empty;
    public bool Can(string policy) => Kabuor.Erp.Auth.Policies.Roles[policy].Contains(Role);
}

public sealed class ErpDb(DbContextOptions<ErpDb> options, RequestContext ctx) : DbContext(options)
{
    public Guid CompanyId => ctx.CompanyId;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<SalesDoc> SalesDocs => Set<SalesDoc>();
    public DbSet<SalesLine> SalesLines => Set<SalesLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Sequence> Sequences => Set<Sequence>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<PurchaseDoc> PurchaseDocs => Set<PurchaseDoc>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<StockMove> StockMoves => Set<StockMove>();
    public DbSet<StockBatch> StockBatches => Set<StockBatch>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListItem> PriceListItems => Set<PriceListItem>();
    public DbSet<ItemUnit> ItemUnits => Set<ItemUnit>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<Payslip> Payslips => Set<Payslip>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();
    public DbSet<ProductionRun> ProductionRuns => Set<ProductionRun>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<PosSession> PosSessions => Set<PosSession>();
    public DbSet<EmployeeDeduction> EmployeeDeductions => Set<EmployeeDeduction>();
    public DbSet<WithholdingCertificate> WithholdingCertificates => Set<WithholdingCertificate>();
    public DbSet<EtimsDevice> EtimsDevices => Set<EtimsDevice>();
    public DbSet<BillingPayment> BillingPayments => Set<BillingPayment>();
    public DbSet<MpesaRequest> MpesaRequests => Set<MpesaRequest>();
    public DbSet<Kabuor.Erp.Integrations.ApiKey> ApiKeys => Set<Kabuor.Erp.Integrations.ApiKey>();
    public DbSet<Kabuor.Erp.Integrations.WebhookEndpoint> WebhookEndpoints => Set<Kabuor.Erp.Integrations.WebhookEndpoint>();
    public DbSet<Kabuor.Erp.Integrations.WebhookDelivery> WebhookDeliveries => Set<Kabuor.Erp.Integrations.WebhookDelivery>();
    public DbSet<EtimsPurchase> EtimsPurchases => Set<EtimsPurchase>();
    public DbSet<StatementImport> StatementImports => Set<StatementImport>();
    public DbSet<StatementLine> StatementLines => Set<StatementLine>();
    public DbSet<AgentSetup> AgentSetups => Set<AgentSetup>();
    public DbSet<AgentTask> AgentTasks => Set<AgentTask>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();
    public DbSet<TrackingLink> TrackingLinks => Set<TrackingLink>();
    public DbSet<LinkVisit> LinkVisits => Set<LinkVisit>();
    public DbSet<Inquiry> Inquiries => Set<Inquiry>();
    public DbSet<InquiryMessage> InquiryMessages => Set<InquiryMessage>();
    public DbSet<PortalCode> PortalCodes => Set<PortalCode>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetEvent> AssetEvents => Set<AssetEvent>();
    public DbSet<DispatchTrip> DispatchTrips => Set<DispatchTrip>();
    public DbSet<JobOpening> JobOpenings => Set<JobOpening>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<DeliveryItem> DeliveryItems => Set<DeliveryItem>();
    public DbSet<DriverPing> DriverPings => Set<DriverPing>();
    public DbSet<VerifiedShare> VerifiedShares => Set<VerifiedShare>();
    public DbSet<VerifiedShareView> VerifiedShareViews => Set<VerifiedShareView>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<AssetType> AssetTypes => Set<AssetType>();
    public DbSet<FaultReport> FaultReports => Set<FaultReport>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderPart> WorkOrderParts => Set<WorkOrderPart>();
    public DbSet<WorkOrderLabour> WorkOrderLabour => Set<WorkOrderLabour>();
    public DbSet<WorkOrderCost> WorkOrderCosts => Set<WorkOrderCost>();
    public DbSet<MaintenancePlan> MaintenancePlans => Set<MaintenancePlan>();
    public DbSet<Requisition> Requisitions => Set<Requisition>();
    public DbSet<RequisitionLine> RequisitionLines => Set<RequisitionLine>();
    public DbSet<Tender> Tenders => Set<Tender>();
    public DbSet<TenderLine> TenderLines => Set<TenderLine>();
    public DbSet<Bid> Bids => Set<Bid>();
    public DbSet<BidLine> BidLines => Set<BidLine>();
    public DbSet<StockTake> StockTakes => Set<StockTake>();
    public DbSet<StockTakeLine> StockTakeLines => Set<StockTakeLine>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<WorkOrderTool> WorkOrderTools => Set<WorkOrderTool>();
    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();
    public DbSet<CashAdvance> CashAdvances => Set<CashAdvance>();
    public DbSet<CashAdvanceExpense> CashAdvanceExpenses => Set<CashAdvanceExpense>();
    public DbSet<BudgetTransfer> BudgetTransfers => Set<BudgetTransfer>();
    public DbSet<PlanningMeeting> PlanningMeetings => Set<PlanningMeeting>();
    public DbSet<ApprovalRecord> ApprovalRecords => Set<ApprovalRecord>();
    // Kabuor Dine (Dine/DineModels.cs)
    public DbSet<DineSettings> DineSettings => Set<DineSettings>();
    public DbSet<DineTable> DineTables => Set<DineTable>();
    public DbSet<DineMenuEntry> DineMenu => Set<DineMenuEntry>();
    public DbSet<DineSession> DineSessions => Set<DineSession>();
    public DbSet<DineOrder> DineOrders => Set<DineOrder>();
    public DbSet<DineOrderLine> DineOrderLines => Set<DineOrderLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Company isolation on every company-owned table (EF applies these to every query, including Includes).
        b.Entity<User>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Customer>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Item>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<SalesDoc>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Payment>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Account>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<JournalEntry>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Sequence>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<AuditEntry>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Proposal>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<SalesLine>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PaymentAllocation>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<JournalLine>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Supplier>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PurchaseDoc>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PurchaseLine>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<SupplierPayment>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Location>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StockLevel>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StockMove>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StockBatch>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StockBatch>().HasIndex(x => new { x.CompanyId, x.ItemId, x.LocationId, x.BatchNo }).IsUnique();
        b.Entity<PriceList>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PriceListItem>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PriceListItem>().HasIndex(x => new { x.PriceListId, x.ItemId }).IsUnique();
        b.Entity<ItemUnit>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<ItemUnit>().HasIndex(x => new { x.ItemId, x.Name }).IsUnique();
        b.Entity<Employee>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<LeaveRequest>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PayrollRun>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Payslip>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Lead>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Activity>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Recipe>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<RecipeLine>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<ProductionRun>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Campaign>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<CampaignRecipient>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PosSession>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PosSession>().HasIndex(x => new { x.CompanyId, x.Status });
        // Kabuor Dine: a table's QR secret finds the table (and the business) from the open internet.
        b.Entity<DineTable>().HasIndex(x => x.Token).IsUnique();
        b.Entity<DineSettings>().HasIndex(x => x.CompanyId).IsUnique();
        b.Entity<DineMenuEntry>().HasIndex(x => new { x.CompanyId, x.ItemId }).IsUnique();
        b.Entity<DineSession>().HasIndex(x => new { x.CompanyId, x.TableId, x.Status });
        b.Entity<DineOrder>().HasIndex(x => new { x.CompanyId, x.SessionId });
        b.Entity<DineOrder>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<DineOrder>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BillingPayment>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<MpesaRequest>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Kabuor.Erp.Integrations.ApiKey>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Kabuor.Erp.Integrations.ApiKey>().HasIndex(x => x.Hash).IsUnique();
        b.Entity<Kabuor.Erp.Integrations.WebhookEndpoint>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Kabuor.Erp.Integrations.WebhookDelivery>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Kabuor.Erp.Integrations.WebhookDelivery>().HasIndex(x => new { x.Status, x.NextAt });
        b.Entity<SalesDoc>().HasIndex(x => new { x.CompanyId, x.ClientRef });
        b.Entity<MpesaRequest>().HasIndex(x => x.CheckoutId);
        b.Entity<MpesaRequest>().HasIndex(x => x.SalesDocId);
        b.Entity<BillingPayment>().HasIndex(x => x.CheckoutId);
        b.Entity<EtimsDevice>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<EtimsDevice>().HasIndex(x => x.CompanyId).IsUnique();
        b.Entity<EtimsPurchase>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<EtimsPurchase>().HasIndex(x => new { x.CompanyId, x.SupplierPin, x.SupplierInvoiceNo }).IsUnique();
        b.Entity<EtimsPurchase>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<WithholdingCertificate>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<WithholdingCertificate>().HasIndex(x => new { x.CompanyId, x.Date });
        b.Entity<WithholdingCertificate>().HasIndex(x => new { x.CompanyId, x.SalesDocId });
        b.Entity<StatementImport>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StatementLine>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StatementLine>().HasIndex(x => new { x.CompanyId, x.Account, x.Status, x.Date });
        b.Entity<StatementLine>().HasIndex(x => new { x.CompanyId, x.Account, x.Reference });
        b.Entity<EmployeeDeduction>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<EmployeeDeduction>().HasIndex(x => new { x.CompanyId, x.EmployeeId, x.Status });
        b.Entity<EmployeeDeduction>().Ignore(x => x.Remaining);
        b.Entity<AgentSetup>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<AgentSetup>().HasIndex(x => new { x.CompanyId, x.Key }).IsUnique();
        b.Entity<AgentTask>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<AgentTask>().HasIndex(x => new { x.CompanyId, x.Status, x.CreatedAt });
        b.Entity<AgentTask>().HasIndex(x => new { x.CompanyId, x.AgentKey, x.SubjectId });
        b.Entity<AgentTask>().HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.AgentTaskId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AgentStep>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<TrackingLink>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<TrackingLink>().HasIndex(x => x.Code).IsUnique();
        b.Entity<TrackingLink>().HasIndex(x => new { x.CompanyId, x.Channel });
        b.Entity<LinkVisit>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<LinkVisit>().HasIndex(x => new { x.CompanyId, x.LinkId, x.At });
        b.Entity<Inquiry>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Inquiry>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Inquiry>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Inquiry>().HasIndex(x => new { x.CompanyId, x.Phone });
        b.Entity<Inquiry>().HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.InquiryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InquiryMessage>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<PortalCode>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<StockCountLine>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Asset>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<AssetEvent>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<Asset>().HasIndex(x => new { x.CompanyId, x.Tag }).IsUnique();
        b.Entity<AssetEvent>().HasIndex(x => new { x.CompanyId, x.AssetId });
        b.Entity<StockCountLine>().HasIndex(x => new { x.CompanyId, x.ItemId, x.At });
        b.Entity<PortalCode>().HasIndex(x => new { x.CompanyId, x.Phone, x.ExpiresAt });
        b.Entity<DispatchTrip>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<DispatchTrip>().HasIndex(x => new { x.CompanyId, x.TripNumber }).IsUnique();
        b.Entity<DispatchTrip>().HasMany(x => x.Deliveries).WithOne().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<DeliveryItem>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
        b.Entity<DeliveryItem>().HasIndex(x => new { x.CompanyId, x.WaybillNumber });
        b.Entity<DeliveryItem>().HasIndex(x => new { x.CompanyId, x.SalesDocId });
        b.Entity<DispatchTrip>().HasIndex(x => new { x.CompanyId, x.VehicleId, x.Status });
        b.Entity<DispatchTrip>().HasIndex(x => x.DriverToken);
        b.Entity<DriverPing>().HasIndex(x => new { x.CompanyId, x.TripId, x.At });
        b.Entity<Candidate>().HasIndex(x => new { x.CompanyId, x.OpeningId, x.Stage });

        // Lookups the app does all the time, indexed with the company first.
        b.Entity<User>().HasIndex(x => x.Email).IsUnique().HasFilter("\"Email\" <> ''"); // one login per email across the platform
        b.Entity<User>().HasIndex(x => x.Phone); // employees sign in with their phone
        b.Entity<User>().HasIndex(x => new { x.CompanyId, x.EmployeeId });
        b.Entity<Customer>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Customer>().HasIndex(x => new { x.CompanyId, x.Phone });
        b.Entity<Customer>().HasIndex(x => new { x.CompanyId, x.Name });
        b.Entity<Customer>().HasIndex(x => new { x.CompanyId, x.FirstLinkId });
        b.Entity<Company>().HasIndex(x => x.Slug);
        b.Entity<Item>().HasIndex(x => new { x.CompanyId, x.Name });
        b.Entity<Lead>().HasIndex(x => new { x.CompanyId, x.LinkId });
        b.Entity<SalesDoc>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<SalesDoc>().HasIndex(x => new { x.CompanyId, x.Kind, x.Status, x.DueDate });
        b.Entity<SalesDoc>().HasIndex(x => new { x.CompanyId, x.CustomerId });
        b.Entity<SalesDoc>().HasIndex(x => new { x.CompanyId, x.LinkId });
        b.Entity<SalesDoc>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesDocId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SalesDoc>().Ignore(x => x.Balance);
        b.Entity<SalesDoc>().Ignore(x => x.FxBalance);
        b.Entity<SalesDoc>().Property(x => x.Rate).HasPrecision(18, 6);
        // Two people paying the same invoice at the same moment: the second save fails instead of overpaying.
        b.Entity<SalesDoc>().Property(x => x.Paid).IsConcurrencyToken();
        b.Entity<SalesDoc>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<Payment>().HasIndex(x => new { x.CompanyId, x.Reference }).IsUnique().HasFilter("\"Reference\" <> ''");
        b.Entity<Payment>().HasIndex(x => new { x.CompanyId, x.Status, x.ReceivedAt });
        b.Entity<Payment>().HasIndex(x => new { x.CompanyId, x.Source, x.ReceivedAt });
        b.Entity<Payment>().HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Payment>().Ignore(x => x.Unallocated);
        b.Entity<Payment>().Property(x => x.Allocated).IsConcurrencyToken();
        b.Entity<PaymentAllocation>().HasIndex(x => x.SalesDocId);
        b.Entity<Account>().HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Entity<Account>().HasIndex(x => new { x.CompanyId, x.SystemKey });
        b.Entity<JournalEntry>().HasIndex(x => new { x.CompanyId, x.Date });
        b.Entity<JournalEntry>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<JournalLine>().HasIndex(x => new { x.CompanyId, x.AccountId });
        b.Entity<Sequence>().HasKey(x => new { x.CompanyId, x.Key });
        b.Entity<Sequence>().Property(x => x.Next).IsConcurrencyToken();
        b.Entity<AuditEntry>().HasIndex(x => new { x.CompanyId, x.At });
        b.Entity<AuditEntry>().HasIndex(x => new { x.CompanyId, x.Id }); // the business's newest change, for cached answers (Platform/CompanyCache.cs)
        b.Entity<Proposal>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Company>().HasIndex(x => x.MpesaShortcode);
        b.Entity<Supplier>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Supplier>().HasIndex(x => new { x.CompanyId, x.Name });
        b.Entity<PurchaseDoc>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<PurchaseDoc>().HasIndex(x => new { x.CompanyId, x.Kind, x.Status, x.DueDate });
        b.Entity<PurchaseDoc>().HasIndex(x => new { x.CompanyId, x.SupplierId });
        b.Entity<PurchaseDoc>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseDocId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PurchaseDoc>().Ignore(x => x.Balance);
        b.Entity<PurchaseDoc>().Property(x => x.Paid).IsConcurrencyToken();
        b.Entity<PurchaseDoc>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<SupplierPayment>().HasIndex(x => new { x.CompanyId, x.PurchaseDocId });
        b.Entity<SupplierPayment>().HasIndex(x => new { x.CompanyId, x.PaidAt });
        b.Entity<Location>().HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Entity<StockLevel>().HasKey(x => new { x.CompanyId, x.ItemId, x.LocationId });
        b.Entity<StockLevel>().Property(x => x.Quantity).IsConcurrencyToken();
        b.Entity<StockBatch>().Property(x => x.Quantity).IsConcurrencyToken();
        b.Entity<StockMove>().HasIndex(x => new { x.CompanyId, x.ItemId, x.At });
        b.Entity<StockMove>().HasIndex(x => new { x.CompanyId, x.SourceType, x.SourceId });
        b.Entity<Employee>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Employee>().HasIndex(x => new { x.CompanyId, x.Name });
        b.Entity<LeaveRequest>().HasIndex(x => new { x.CompanyId, x.Status, x.From });
        b.Entity<LeaveRequest>().HasIndex(x => new { x.CompanyId, x.EmployeeId });
        b.Entity<PayrollRun>().HasIndex(x => new { x.CompanyId, x.Period }).IsUnique();
        b.Entity<PayrollRun>().HasMany(x => x.Payslips).WithOne().HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PayrollRun>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<Payslip>().HasIndex(x => new { x.CompanyId, x.EmployeeId });
        b.Entity<Lead>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Lead>().HasIndex(x => new { x.CompanyId, x.Stage });
        b.Entity<Lead>().HasIndex(x => new { x.CompanyId, x.Phone });
        b.Entity<Activity>().HasIndex(x => new { x.CompanyId, x.LeadId });
        b.Entity<Activity>().HasIndex(x => new { x.CompanyId, x.CustomerId });
        b.Entity<Activity>().HasIndex(x => new { x.CompanyId, x.FollowUpDone, x.FollowUpOn });
        b.Entity<Recipe>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionRun>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<ProductionRun>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<ProductionRun>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<Campaign>().HasMany(x => x.Recipients).WithOne().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Campaign>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<CampaignRecipient>().HasIndex(x => new { x.CompanyId, x.CampaignId });
        b.Entity<VerifiedShare>().HasIndex(x => new { x.CompanyId, x.TokenHash });
        b.Entity<VerifiedShare>().HasIndex(x => new { x.CompanyId, x.ExpiresAt });
        b.Entity<VerifiedShareView>().HasIndex(x => new { x.CompanyId, x.ShareId, x.At });

        // Operations: maintenance, procurement and stock takes (Domain/Operations.cs).
        b.Entity<Department>().HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
        b.Entity<AssetType>().HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        b.Entity<Asset>().HasIndex(x => new { x.CompanyId, x.ParentId });
        b.Entity<FaultReport>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<FaultReport>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<FaultReport>().HasIndex(x => new { x.CompanyId, x.AssetId });
        b.Entity<WorkOrder>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<WorkOrder>().HasIndex(x => new { x.CompanyId, x.Status, x.DueDate });
        b.Entity<WorkOrder>().HasIndex(x => new { x.CompanyId, x.AssetId });
        b.Entity<WorkOrder>().HasIndex(x => new { x.CompanyId, x.MaintenancePlanId });
        b.Entity<WorkOrder>().HasMany(x => x.Parts).WithOne().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkOrder>().HasMany(x => x.Labour).WithOne().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkOrder>().HasMany(x => x.Costs).WithOne().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkOrder>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<MaintenancePlan>().HasIndex(x => new { x.CompanyId, x.Active, x.NextDue });
        b.Entity<Requisition>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Requisition>().HasIndex(x => new { x.CompanyId, x.Status });
        b.Entity<Requisition>().HasIndex(x => new { x.CompanyId, x.DepartmentId });
        b.Entity<Requisition>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RequisitionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Requisition>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<RequisitionLine>().Ignore(x => x.Outstanding);
        b.Entity<Tender>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<Tender>().HasIndex(x => x.Token).IsUnique();
        b.Entity<Tender>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TenderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Tender>().Ignore(x => x.StandstillEnds);
        b.Entity<Tender>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<Bid>().HasIndex(x => new { x.CompanyId, x.TenderId });
        b.Entity<Bid>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.BidId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StockTake>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<StockTake>().HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockTakeId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<StockTake>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<JournalLine>().HasIndex(x => new { x.CompanyId, x.DepartmentId });
        b.Entity<Budget>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<WorkOrderTool>().HasIndex(x => new { x.CompanyId, x.WorkOrderId });
        b.Entity<MeterReading>().HasIndex(x => new { x.CompanyId, x.AssetId, x.At });
        b.Entity<CashAdvance>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<CashAdvance>().HasIndex(x => new { x.CompanyId, x.EmployeeId });
        b.Entity<CashAdvance>().HasMany(x => x.Expenses).WithOne().HasForeignKey(x => x.CashAdvanceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CashAdvance>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<BudgetTransfer>().HasIndex(x => new { x.CompanyId, x.Number }).IsUnique();
        b.Entity<BudgetTransfer>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<PlanningMeeting>().HasIndex(x => new { x.CompanyId, x.Month }).IsUnique();
        b.Entity<WorkOrderTool>().HasIndex(x => new { x.CompanyId, x.AssetId });
        b.Entity<Budget>().HasIndex(x => new { x.CompanyId, x.DepartmentId, x.Year });
        b.Entity<Budget>().Property(x => x.Status).IsConcurrencyToken();
        b.Entity<ApprovalRecord>().HasIndex(x => new { x.CompanyId, x.Seq }).IsUnique();
        b.Entity<ApprovalRecord>().HasIndex(x => new { x.CompanyId, x.Subject, x.SubjectId });
        b.Entity<JournalLine>().HasIndex(x => new { x.CompanyId, x.AssetId });
        b.Entity<PurchaseDoc>().HasIndex(x => new { x.CompanyId, x.WorkOrderId });

        // Enums as readable text, so the data makes sense to anyone reading the database.
        // SQLite (local development only) can't sort or compare DateTimeOffset, so there they're stored as
        // sortable numbers; PostgreSQL keeps them as real timestamps.
        var sqlite = Database.IsSqlite();
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            // Ids are made in code (Guid.NewGuid), so a new child added to a loaded record is an insert, not an update.
            if (prop.IsPrimaryKey() && prop.ClrType == typeof(Guid)) prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            if (prop.ClrType.IsEnum) prop.SetProviderClrType(typeof(string));
            if (sqlite && (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?)))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            // PostgreSQL's timestamptz takes UTC only; Kenya-time values (e.g. an M-Pesa TransTime) are converted.
            else if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v));
            // SQLite can't add up or compare decimals; there quantities and unit costs are stored as doubles.
            if (sqlite && prop.ClrType == typeof(decimal))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.CastingConverter<decimal, double>());
        }

        // Universal company isolation guarantee: ensures EVERY entity implementing ICompanyOwned
        // has an airtight tenant filter, preventing data leakage across tenants under all conditions.
        var setFilterMethod = typeof(ErpDb).GetMethod(nameof(SetCompanyFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var entity in b.Model.GetEntityTypes())
        {
            if (typeof(ICompanyOwned).IsAssignableFrom(entity.ClrType))
            {
                setFilterMethod.MakeGenericMethod(entity.ClrType).Invoke(this, [b]);
            }
        }
    }

    void SetCompanyFilter<T>(ModelBuilder b) where T : class, ICompanyOwned
    {
        b.Entity<T>().HasQueryFilter(x => x.CompanyId == ctx.CompanyId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw new NotSupportedException("Use SaveChangesAsync.");

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampAndAudit();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    // Never copied into the audit trail (which the owner and accountant can read): passwords, keys and the secrets
    // behind two-step sign-in, M-Pesa prompts, KRA eTIMS and signed webhooks. The log says they changed, not to what.
    static readonly HashSet<string> Secret = ["PasswordHash", "MpesaCallbackKey", "TotpSecret", "RecoveryCodes", "StkConsumerKey", "StkConsumerSecret", "StkPasskey", "CmcKey", "Secret", "DriverToken", "TokenHash"];

    void StampAndAudit()
    {
        var audit = new List<AuditEntry>();
        foreach (var e in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            if (e.Entity is AuditEntry) continue;
            // The approvals register is written once: a change or removal would break its chain of fingerprints anyway.
            if (e.Entity is ApprovalRecord && e.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("The approvals register can't be changed or deleted.");
            if (e.Entity is ICompanyOwned owned)
            {
                if (e.State == EntityState.Added)
                {
                    if (ctx.HasCompany)
                    {
                        if (owned.CompanyId != Guid.Empty && owned.CompanyId != ctx.CompanyId)
                            throw new UnauthorizedAccessException($"Tenant violation: cannot insert {e.Entity.GetType().Name} for a different company.");
                        owned.CompanyId = ctx.CompanyId;
                    }
                    else if (owned.CompanyId == Guid.Empty)
                    {
                        throw new InvalidOperationException($"No company for new {e.Entity.GetType().Name}.");
                    }
                }
                else if (e.State is EntityState.Modified or EntityState.Deleted)
                {
                    if (ctx.HasCompany && owned.CompanyId != ctx.CompanyId)
                        throw new UnauthorizedAccessException($"Tenant violation: cannot modify or delete {e.Entity.GetType().Name} belonging to another company.");
                }
            }
            // Location pings, and the trip's last position they move, would bury the trail people read: stamped and guarded
            // like everything else above, but not audited.
            if (e.Entity is DriverPing || (e.Entity is DispatchTrip && e.State == EntityState.Modified && e.Properties.Where(p => p.IsModified).All(p => p.Metadata.Name.StartsWith("Last")))) continue;
            var companyId = e.Entity switch
            {
                ICompanyOwned o => o.CompanyId,
                Company c => c.Id,
                _ => ctx.CompanyId,
            };
            if (companyId == Guid.Empty) continue;
            audit.Add(new AuditEntry
            {
                CompanyId = companyId,
                UserId = ctx.UserId,
                Actor = ctx.Actor,
                Action = e.State.ToString(),
                Entity = e.Entity.GetType().Name,
                EntityId = KeyOf(e),
                Changes = ChangesOf(e),
            });
        }
        AuditEntries.AddRange(audit);
    }

    static string KeyOf(EntityEntry e) =>
        string.Join("/", e.Metadata.FindPrimaryKey()?.Properties.Select(p => e.Property(p.Name).CurrentValue?.ToString()) ?? []);

    static string ChangesOf(EntityEntry e)
    {
        var props = e.Properties.Where(p => !Secret.Contains(p.Metadata.Name) && (e.State != EntityState.Modified || p.IsModified));
        var map = e.State switch
        {
            EntityState.Modified => props.ToDictionary(p => p.Metadata.Name, p => (object?)new { from = p.OriginalValue, to = p.CurrentValue }),
            EntityState.Deleted => props.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue),
            _ => props.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue),
        };
        if (e.State == EntityState.Modified)
            foreach (var p in e.Properties.Where(p => p.IsModified && Secret.Contains(p.Metadata.Name)))
                map[p.Metadata.Name] = "(changed)";
        return JsonSerializer.Serialize(map);
    }
}
