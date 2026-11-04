// The business records. Every record that belongs to a company implements ICompanyOwned: the database
// filters every query by the signed-in user's company, so one company can never read another's data.
// Money is stored as whole cents (long): KSh 1,250.50 is 125050. No floating point anywhere near money.
namespace Kabuor.Erp.Domain;

public interface ICompanyOwned
{
    Guid CompanyId { get; set; }
}

public sealed class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string KraPin { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string County { get; set; } = "";
    /// <summary>Street, building and P.O. Box, as printed under the name on invoices, waybills and reports.</summary>
    public string Address { get; set; } = "";
    /// <summary>The business's logo for its letterhead: a small PNG/JPEG/WebP data URL, resized in the browser.</summary>
    public string Logo { get; set; } = "";
    public bool VatRegistered { get; set; } = true;
    /// <summary>The business's public address for its shop and customer portal (/s/{slug}); unique.</summary>
    public string Slug { get; set; } = "";
    public bool PortalEnabled { get; set; }
    /// <summary>The owner's KPI targets, as JSON (key → target; money in cents).</summary>
    public string KpiTargets { get; set; } = "";
    /// <summary>Refuse a sale that would take stock below zero (Business Central's "prevent negative inventory").</summary>
    public bool PreventNegativeStock { get; set; }
    /// <summary>The owner oversees: sees every module, approves and signs off, sets targets and runs settings, but the
    /// daily work (selling, buying, stock, jobs, payments) is left to the teams. Program.cs enforces it.</summary>
    public bool OwnerOversight { get; set; }
    /// <summary>The product the business signed up for: "erp" (Kabuor ERP) or "dine" (Kabuor Dine, restaurants on the
    /// same books, with its own screens). A Dine business can move up to the full ERP without moving its data.</summary>
    public string Product { get; set; } = "erp";
    /// <summary>The Paybill/Till customers pay into; M-Pesa confirmations for it land in this company.</summary>
    public string MpesaShortcode { get; set; } = "";
    /// <summary>Secret part of this company's M-Pesa confirmation URL (Safaricom doesn't sign callbacks).</summary>
    public string MpesaCallbackKey { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    /// <summary>M-Pesa prompts (STK push) from invoices: the company's own Daraja app. Secrets are sealed.</summary>
    public string StkEnvironment { get; set; } = "sandbox";
    public string StkShortcode { get; set; } = "";
    /// <summary>For a Buy Goods till: the till number (StkShortcode is then the store number).</summary>
    public string StkTill { get; set; } = "";
    public string StkConsumerKey { get; set; } = "";
    public string StkConsumerSecret { get; set; } = "";
    public string StkPasskey { get; set; } = "";
    /// <summary>Industry vertical selected during onboarding: distribution | manufacturing | retail | transportation | services | general</summary>
    public string Industry { get; set; } = "general";
    /// <summary>Accounting period lock date: no transaction on or before this date can be posted, modified, or voided (internal audit control).</summary>
    public DateOnly? AccountingLockDate { get; set; }
    /// <summary>Subscription: "" while on the free trial, then starter | business | growth.</summary>
    public string Plan { get; set; } = "";
    public DateTimeOffset? TrialEndsAt { get; set; }
    public DateTimeOffset? PaidUntil { get; set; }
    /// <summary>Signed up but hasn't paid yet (Billing:PayToStart): only the plan and payment screens open until the
    /// first M-Pesa payment arrives. False for every company from before paying to start.</summary>
    public bool AwaitingFirstPayment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum MpesaRequestStatus
{
    Pending,
    Paid,
    Failed,
    Cancelled,
}

/// <summary>An M-Pesa prompt sent to a customer's phone to pay an invoice.</summary>
public sealed class MpesaRequest : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid SalesDocId { get; set; }
    public string Phone { get; set; } = "";
    public long Amount { get; set; }
    public string CheckoutId { get; set; } = "";
    public MpesaRequestStatus Status { get; set; } = MpesaRequestStatus.Pending;
    public string Receipt { get; set; } = "";
    public string Note { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
}

public enum BillingStatus
{
    Pending,
    Paid,
    Failed,
}

/// <summary>A subscription payment to Kabuor Builds, by M-Pesa prompt.</summary>
public sealed class BillingPayment : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string Plan { get; set; } = "";
    public int Months { get; set; }
    public long Amount { get; set; }
    public string Phone { get; set; } = "";
    public BillingStatus Status { get; set; } = BillingStatus.Pending;
    public string CheckoutId { get; set; } = "";
    public string Receipt { get; set; } = "";
    public bool Test { get; set; }
    public string Note { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>When Safaricom was last asked how the prompt went (the callback can't always reach the server).</summary>
    public DateTimeOffset? CheckedAt { get; set; }
}

public enum Role
{
    Owner,       // everything, including users and settings
    Accountant,  // finance, payments, reports, sales
    Sales,       // customers, quotes, invoices, record payments
    Storekeeper, // items and stock (inventory module)
    Viewer,      // read only (never salaries)
    HR,          // employees, leave and payroll
    Employee,    // self-service only: their own leave, payslips and P9 (nothing else in the business)
    Procurement, // suppliers, purchase orders, vendor bills, receiving
    Dispatcher,  // the fleet and dispatch office: trips, waybills, drivers and the live map
    Maintenance, // the maintenance team: assets, faults, work orders and the parts they use
}

public sealed class User : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public required string Email { get; set; }
    public string Phone { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public Role Role { get; set; } = Role.Sales;
    public string Language { get; set; } = "en"; // en | sw
    public bool Active { get; set; } = true;
    /// <summary>For self-service logins: the employee record this person is.</summary>
    public Guid? EmployeeId { get; set; }
    /// <summary>Set when someone else chose the password (a new or reset login): they pick their own first.</summary>
    public bool MustChangePassword { get; set; }
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>Goes up to end every session at once (password changed, access removed, role changed, "sign out everywhere").</summary>
    public int TokenVersion { get; set; }
    /// <summary>Two-step sign-in: the authenticator secret (encrypted), on or off, the last code used (no replays)
    /// and the one-time recovery codes (hashed).</summary>
    public string TotpSecret { get; set; } = "";
    public bool TwoFactorEnabled { get; set; }
    public long TotpLastStep { get; set; }
    public string RecoveryCodes { get; set; } = "";
    public DateTimeOffset? LastSignInAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Customer : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // C-0001
    public required string Name { get; set; }
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string KraPin { get; set; } = "";
    public string Address { get; set; } = "";
    public long CreditLimit { get; set; }
    /// <summary>Wholesale, a distributor's own prices…: their invoices start from this list instead of the shelf price.</summary>
    public Guid? PriceListId { get; set; }
    /// <summary>The currency they are usually invoiced in ("USD"; empty for shillings): new invoices start in it.</summary>
    public string Currency { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Asked not to get marketing messages (Data Protection Act): never included in campaigns.</summary>
    public bool MarketingOptOut { get; set; }
    /// <summary>Where they first found the business: the tracking link, channel, and salesperson who brought them.</summary>
    public Guid? FirstLinkId { get; set; }
    public Channel? AcquisitionChannel { get; set; }
    public Guid? AcquiredByUserId { get; set; }
    /// <summary>When they last opened their account on the customer portal.</summary>
    public DateTimeOffset? PortalLastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>KRA eTIMS tax types. B (16%) is the standard VAT rate.</summary>
public enum TaxType
{
    A_Exempt,
    B_Standard16,
    C_ZeroRated,
    D_NonVat,
    E_Reduced8,
}

public enum ItemKind
{
    Product,
    Service,
}

/// <summary>Why the business holds an item: to sell it, or to run the business with it (spares, tools, fuel,
/// protective gear, medical and office supplies). The two are kept apart in the books: stock for sale becomes cost of
/// sales when sold; stores become a department's or a job's cost when issued.</summary>
public enum ItemUse
{
    Sale,
    Stores,
}

public sealed class Item : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public string Brand { get; set; } = "";
    public string Sku { get; set; } = "";
    /// <summary>The manufacturer's barcode printed on the pack (EAN-13, UPC…), for scanning. Unique per company when set.</summary>
    public string Barcode { get; set; } = "";
    public ItemKind Kind { get; set; } = ItemKind.Product;
    public string Unit { get; set; } = "pc";
    /// <summary>Price customers pay, VAT included (how Kenyan shops quote prices).</summary>
    public long Price { get; set; }
    public TaxType TaxType { get; set; } = TaxType.B_Standard16;
    public bool Active { get; set; } = true;
    /// <summary>Counted in stock: buying adds to it, selling takes from it, and its cost goes to cost of sales.</summary>
    public bool TrackStock { get; set; }
    /// <summary>Warn when the total on hand falls to this or below.</summary>
    public decimal ReorderLevel { get; set; }
    /// <summary>Kept by batch with an expiry date (food, medicine, agro-chemicals): sold first-to-expire first.</summary>
    public bool TrackBatches { get; set; }
    /// <summary>KRA eTIMS: the item's registered code (set when first registered) and its KRA classification.</summary>
    public string EtimsItemCode { get; set; } = "";
    public string EtimsClassCode { get; set; } = "";
    /// <summary>Average cost of one unit before VAT, in cents (kept as a decimal so repeated averaging doesn't drift).</summary>
    public decimal AverageCost { get; set; }
    /// <summary>For sale, or stores for the business's own use (never sold, never on the till or an invoice).</summary>
    public ItemUse Use { get; set; } = ItemUse.Sale;
    /// <summary>For stores: Spares, Tools, Consumables, Fuel and lubricants, Protective gear, Medical supplies…</summary>
    public string StoresCategory { get; set; } = "";
}

public enum SalesDocKind
{
    Quote,
    Invoice,
}

public enum SalesDocStatus
{
    Draft,
    Sent,     // quote sent / invoice issued and posted
    PartPaid,
    Paid,
    Accepted, // quote turned into an invoice
    Void,
}

public enum EtimsStatus
{
    NotRequired,
    Pending,
    Signed,
    Failed,
}

/// <summary>A quote or an invoice. Quotes become invoices with one tap; only invoices touch the books.</summary>
public sealed class SalesDoc : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public SalesDocKind Kind { get; set; }
    public required string Number { get; set; } // INV-0001 / QT-0001
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public SalesDocStatus Status { get; set; } = SalesDocStatus.Draft;
    public long Net { get; set; }
    public long Tax { get; set; }
    public long Total { get; set; }
    public long Paid { get; set; }
    /// <summary>Invoiced in another currency ("USD"; empty for shillings). Net, Tax, Total and Paid stay in shillings at
    /// <see cref="Rate"/>, so the books, VAT and eTIMS are in KES; the Fx amounts are what the customer sees and owes.</summary>
    public string Currency { get; set; } = "";
    /// <summary>Shillings per one unit of <see cref="Currency"/>, fixed when the document is made (1 for shillings).</summary>
    public decimal Rate { get; set; } = 1;
    public long FxNet { get; set; }
    public long FxTax { get; set; }
    public long FxTotal { get; set; }
    public long FxPaid { get; set; }
    public string Notes { get; set; } = "";
    public Guid? FromQuoteId { get; set; }
    /// <summary>Where stocked items leave from (the default store when empty).</summary>
    public Guid? LocationId { get; set; }
    public EtimsStatus EtimsStatus { get; set; } = EtimsStatus.NotRequired;
    public string EtimsInvoiceNo { get; set; } = "";
    public string EtimsControlCode { get; set; } = "";
    /// <summary>A balance brought in when the business moved to Kabuor (not a sale this year).</summary>
    public bool Opening { get; set; }
    /// <summary>Set by an offline till: the device's key for the sale, so syncing it twice keeps it once.</summary>
    public string ClientRef { get; set; } = "";
    /// <summary>The sequence number this invoice was sent to KRA under (needed for its credit note).</summary>
    public long EtimsInvcNo { get; set; }
    public string EtimsQrUrl { get; set; } = "";
    public string EtimsError { get; set; } = "";
    /// <summary>Attribution: which tracking link, inquiry, marketing channel, or salesperson brought this sale.</summary>
    public Guid? LinkId { get; set; }
    public Guid? InquiryId { get; set; }
    public Channel? Channel { get; set; }
    public Guid? SalespersonUserId { get; set; }
    public string SalespersonName { get; set; } = "";
    public List<SalesLine> Lines { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Balance => Total - Paid;
    public long FxBalance => FxTotal - FxPaid;
}

public sealed class SalesLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid SalesDocId { get; set; }
    public Guid? ItemId { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; } = 1;
    /// <summary>VAT-inclusive unit price, in cents.</summary>
    public long UnitPrice { get; set; }
    public TaxType TaxType { get; set; } = TaxType.B_Standard16;
    public long Net { get; set; }
    public long Tax { get; set; }
    public long Total { get; set; }
    /// <summary>Cost of sales for this line (stocked items), fixed when the invoice is issued, in cents.</summary>
    public long Cost { get; set; }
    /// <summary>Sold in a bigger unit ("carton" of 24): Quantity is in that unit; stock moves Quantity × UnitFactor.</summary>
    public string Unit { get; set; } = "";
    public decimal UnitFactor { get; set; } = 1;
    /// <summary>On a foreign-currency document: the line as the customer sees it, in that currency (cents).</summary>
    public long FxUnitPrice { get; set; }
    public long FxNet { get; set; }
    public long FxTax { get; set; }
    public long FxTotal { get; set; }
}

public enum PaymentMethod
{
    Mpesa,
    Cash,
    Bank,
    Card,
}

public enum PaymentStatus
{
    Unmatched,
    PartlyMatched,
    Matched,
}

/// <summary>Money in. M-Pesa payments arrive by themselves from Safaricom; others are recorded by staff.</summary>
/// <summary>How a payment reached the books. Only Safaricom-sourced ones from Safaricom's servers count as proof of
/// revenue (Kabuor Verified); everything else was typed, uploaded or reported, and may be right but proves nothing.</summary>
public enum PaymentSource { Unknown, Manual, Till, StatementImport, ConnectedSystem, MpesaConfirmation, MpesaPrompt }

public sealed class Payment : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public PaymentMethod Method { get; set; }
    public long Amount { get; set; }
    public long Allocated { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>M-Pesa receipt (e.g. SGH4K2L9PQ), bank reference or cheque number. Unique per company.</summary>
    public string Reference { get; set; } = "";
    public string PayerPhone { get; set; } = "";
    public string PayerName { get; set; } = "";
    /// <summary>What the payer typed as the account number on a Paybill.</summary>
    public string AccountRef { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Unmatched;
    /// <summary>Set by the server where the payment is created; never taken from a request body.</summary>
    public PaymentSource Source { get; set; } = PaymentSource.Unknown;
    /// <summary>The address that delivered an M-Pesa callback (confirmation or prompt result).</summary>
    public string CallbackIp { get; set; } = "";
    /// <summary>An M-Pesa callback that came from one of Safaricom's callback addresses (SafaricomCallers).</summary>
    public bool FromSafaricom { get; set; }
    public List<PaymentAllocation> Allocations { get; set; } = [];
    public long Unallocated => Amount - Allocated;
}

public sealed class PaymentAllocation : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid PaymentId { get; set; }
    public Guid SalesDocId { get; set; }
    public long Amount { get; set; }
    /// <summary>On a foreign-currency invoice: how much of it, in that currency, this payment settled.</summary>
    public long FxAmount { get; set; }
    public string MatchedBy { get; set; } = ""; // "auto: account number", "user: Wanjiku", "assistant"
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Income,
    Expense,
}

public sealed class Account : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public AccountType Type { get; set; }
    /// <summary>Accounts the system posts to by itself (AR, SALES, VAT_OUT, MPESA…). Null for the user's own.</summary>
    public string? SystemKey { get; set; }
}

/// <summary>A balanced double-entry posting. Created only through Ledger.Post, which refuses unbalanced entries.</summary>
public sealed class JournalEntry : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public DateOnly Date { get; set; }
    public required string Memo { get; set; }
    public string SourceType { get; set; } = ""; // Invoice, Payment, Allocation, Manual
    public Guid? SourceId { get; set; }
    public List<JournalLine> Lines { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JournalLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public long Debit { get; set; }
    public long Credit { get; set; }
    /// <summary>What the money was for, beyond the account: the department (cost centre), the asset and the
    /// maintenance job. Lets the books answer "what did the workshop spend" and "what has this machine cost us".</summary>
    public Guid? DepartmentId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? WorkOrderId { get; set; }
}

/// <summary>Running numbers per company (INV-0001, C-0001…), so numbers never repeat or skip.</summary>
public sealed class Sequence : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public required string Key { get; set; }
    public int Next { get; set; } = 1;
}

/// <summary>Who changed what, when. Written for every change by ErpDb; never edited.</summary>
public sealed class AuditEntry : ICompanyOwned
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public string Actor { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public required string Action { get; set; } // Added | Modified | Deleted
    public required string Entity { get; set; }
    public string EntityId { get; set; } = "";
    public string Changes { get; set; } = "";
}

public enum ProposalStatus
{
    Pending,
    Approved,
    Rejected,
    Failed,
}

/// <summary>Something the assistant wants to do. Nothing happens until a person approves it.</summary>
public sealed class Proposal : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid RequestedBy { get; set; }
    public required string Kind { get; set; } // match_payment | send_reminder
    public required string Summary { get; set; } // plain words shown to the approver
    public string Payload { get; set; } = "{}";
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string Result { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ---------- buying ----------

public sealed class Supplier : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Number { get; set; } // S-0001
    public required string Name { get; set; }
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string KraPin { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>How we usually pay them, e.g. "Paybill 247247 acc 0123456" or bank details.</summary>
    public string PaymentDetails { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PurchaseKind
{
    Order, // what we asked the supplier for
    Bill,  // what the supplier says we owe (their invoice)
}

public enum PurchaseStatus
{
    Draft,
    Sent,     // order sent to the supplier
    Received, // order received and turned into a bill
    Open,     // bill posted, nothing paid
    PartPaid,
    Paid,
    Void,
}

public sealed class PurchaseDoc : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public PurchaseKind Kind { get; set; }
    public required string Number { get; set; } // PO-0001 / BILL-0001
    public Guid SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    /// <summary>The supplier's own invoice number (needed to claim input VAT).</summary>
    public string SupplierRef { get; set; } = "";
    public DateOnly IssueDate { get; set; }
    public DateOnly DueDate { get; set; }
    public PurchaseStatus Status { get; set; } = PurchaseStatus.Draft;
    /// <summary>Where stocked items are received.</summary>
    public Guid? LocationId { get; set; }
    public long Net { get; set; }
    public long Tax { get; set; }
    public long Total { get; set; }
    public long Paid { get; set; }
    public string Notes { get; set; } = "";
    public Guid? FromOrderId { get; set; }
    /// <summary>Who it's for: the department charged, the maintenance job it's for, the request and tender it came from.</summary>
    public Guid? DepartmentId { get; set; }
    public Guid? WorkOrderId { get; set; }
    public Guid? RequisitionId { get; set; }
    public Guid? TenderId { get; set; }
    /// <summary>Finance released the money for a department's or a job's order (orders from a signed-off request need nothing more).</summary>
    public string FinanceSignedOffBy { get; set; } = "";
    public DateTimeOffset? FinanceSignedOffAt { get; set; }
    /// <summary>A balance brought in when the business moved to Kabuor.</summary>
    public bool Opening { get; set; }
    public List<PurchaseLine> Lines { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Balance => Total - Paid;
}

public sealed class PurchaseLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid PurchaseDocId { get; set; }
    /// <summary>A stocked item goes to Stock; anything else goes to the expense account chosen.</summary>
    public Guid? ItemId { get; set; }
    public Guid? AccountId { get; set; }
    public required string Description { get; set; }
    public string Brand { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    /// <summary>VAT-inclusive unit cost, in cents.</summary>
    public long UnitCost { get; set; }
    public TaxType TaxType { get; set; } = TaxType.B_Standard16;
    public long Net { get; set; }
    public long Tax { get; set; }
    public long Total { get; set; }
    public string Unit { get; set; } = "";
    public decimal UnitFactor { get; set; } = 1;
    /// <summary>For items kept by batch: the supplier's batch (lot) number and its expiry.</summary>
    public string BatchNo { get; set; } = "";
    public DateOnly? Expiry { get; set; }
}

/// <summary>A named set of prices (Wholesale, Hotels, Distributor A). Items not on the list sell at their own price.</summary>
public sealed class PriceList : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public List<PriceListItem> Items { get; set; } = [];
}

public sealed class PriceListItem : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid PriceListId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>VAT-inclusive price of one base unit, in cents.</summary>
    public long Price { get; set; }
}

/// <summary>Another unit an item is bought or sold in: a carton of 24, a 50 kg bag, a crate.</summary>
public sealed class ItemUnit : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid ItemId { get; set; }
    public required string Name { get; set; }
    /// <summary>How many base units are in one of these.</summary>
    public decimal Factor { get; set; }
    /// <summary>Its own price (a carton is often cheaper than 24 singles); 0 means base price × factor.</summary>
    public long Price { get; set; }
}

/// <summary>Stock of one batch at one place.</summary>
public sealed class StockBatch : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public required string BatchNo { get; set; }
    public DateOnly? Expiry { get; set; }
    public decimal Quantity { get; set; }
    public DateOnly ReceivedOn { get; set; }
}

/// <summary>Money out to a supplier against a bill.</summary>
public sealed class SupplierPayment : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid PurchaseDocId { get; set; }
    public Guid SupplierId { get; set; }
    public PaymentMethod Method { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset PaidAt { get; set; } = DateTimeOffset.UtcNow;
    public string Reference { get; set; } = "";
    public string Actor { get; set; } = "";
}

// ---------- stock ----------

/// <summary>A store, shop, branch or van that holds stock.</summary>
public sealed class Location : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    public bool IsDefault { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>How many of an item are at a location right now. Changed only through stock moves.</summary>
public sealed class StockLevel : ICompanyOwned
{
    public Guid CompanyId { get; set; }
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public decimal Quantity { get; set; }
}

public enum StockMoveKind
{
    Received,    // from a bill
    Sold,        // on an issued invoice
    Returned,    // invoice voided: back in
    Reversed,    // bill voided: back out
    Adjusted,    // counted or corrected by hand
    TransferOut,
    TransferIn,
    Consumed,    // used up making something
    Produced,    // made in a production run
}

/// <summary>Every change to stock, never edited: the stock history of an item.</summary>
public sealed class StockMove : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public StockMoveKind Kind { get; set; }
    /// <summary>Positive in, negative out.</summary>
    public decimal Quantity { get; set; }
    /// <summary>Cost of one unit before VAT, in cents.</summary>
    public decimal UnitCost { get; set; }
    public string SourceType { get; set; } = "";
    public Guid? SourceId { get; set; }
    public string Reference { get; set; } = ""; // INV-0003, BILL-0002, "Count 20 Oct"
    public string BatchNo { get; set; } = "";
    public string Note { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public enum PosSessionStatus { Open, Closed }

/// <summary>A cash register shift/session: opening float, sales recorded, and closing reconciliation.</summary>
public sealed class PosSession : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid CashierId { get; set; }
    public string CashierName { get; set; } = "";
    public string SessionNumber { get; set; } = "";
    public PosSessionStatus Status { get; set; } = PosSessionStatus.Open;
    public long OpeningFloat { get; set; } // in cents
    public long CashSales { get; set; }
    public long MpesaSales { get; set; }
    public long BankSales { get; set; }
    public long ClosingCount { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
}

public enum DispatchStatus { Draft, Dispatched, Delivered, Completed, Cancelled }

/// <summary>A delivery trip / route dispatch carrying customer orders in a specific vehicle.</summary>
public sealed class DispatchTrip : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string TripNumber { get; set; } = "";
    /// <summary>The vehicle from the asset register (category VEH), when the trip uses one of the business's own.</summary>
    public Guid? VehicleId { get; set; }
    public string VehiclePlate { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string DriverPhone { get; set; } = "";
    public string RouteName { get; set; } = "";
    public DispatchStatus Status { get; set; } = DispatchStatus.Draft;
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<DeliveryItem> Deliveries { get; set; } = [];
    /// <summary>The secret in the driver's link (/d/{token}): opens this trip's drops and shares the phone's location
    /// while the trip is on the road. Made when the link is first sent; empty for trips from before driver links.</summary>
    public string DriverToken { get; set; } = "";
    /// <summary>Where the driver's phone last was, so the live map needs no trawl through the pings.</summary>
    public double? LastLat { get; set; }
    public double? LastLng { get; set; }
    /// <summary>Metres per second and degrees from north, when the phone knows them.</summary>
    public double? LastSpeed { get; set; }
    public double? LastHeading { get; set; }
    public double? LastAccuracy { get; set; }
    public DateTimeOffset? LastPingAt { get; set; }
}

/// <summary>One reading of a driver's phone location while their trip was on the road: the trail on the live map.</summary>
public sealed class DriverPing : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid TripId { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? Accuracy { get; set; }
    public double? Speed { get; set; }
    public double? Heading { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public enum DeliveryItemStatus { Pending, Dispatched, Delivered, PartialReturn, Failed }

/// <summary>An individual consignment / order delivery on a dispatch trip, tracking proof of delivery.</summary>
public sealed class DeliveryItem : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid TripId { get; set; }
    public Guid? SalesDocId { get; set; }
    public string WaybillNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string DeliveryAddress { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public DeliveryItemStatus Status { get; set; } = DeliveryItemStatus.Pending;
    public string RecipientName { get; set; } = "";
    public string PodNotes { get; set; } = "";
    public DateTimeOffset? PodConfirmedAt { get; set; }
    /// <summary>Where the driver's phone was when the drop was confirmed from the driver's link (proof it was there).</summary>
    public double? PodLat { get; set; }
    public double? PodLng { get; set; }
}

/// <summary>Owner-approved share of verified revenue and eTIMS compliance with a named lender.</summary>
public sealed class VerifiedShare : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string LenderName { get; set; }
    public string LenderContact { get; set; } = "";
    public int Months { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>Access record of a lender opening a shared verified report.</summary>
public sealed class VerifiedShareView : ICompanyOwned
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public Guid ShareId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string IpHash { get; set; } = "";
    public string Agent { get; set; } = "";
}


