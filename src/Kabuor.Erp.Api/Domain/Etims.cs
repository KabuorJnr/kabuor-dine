// The company's KRA eTIMS connection (an OSCU device) and the invoices suppliers filed against its PIN.
namespace Kabuor.Erp.Domain;

public enum EtimsMode
{
    Test,    // no KRA connection: clearly marked test signatures (the default)
    Sandbox, // KRA's eTIMS sandbox, for certification
    Live,    // KRA production
}

/// <summary>One OSCU "device" (KRA's word for an integration install) per company and branch.</summary>
public sealed class EtimsDevice : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public EtimsMode Mode { get; set; } = EtimsMode.Test;
    public string BranchId { get; set; } = "00";        // bhfId: "00" is the head office
    public string DeviceSerial { get; set; } = "";      // dvcSrlNo, issued by KRA when the OSCU application is approved
    public string CmcKey { get; set; } = "";            // communication key from KRA (encrypted at rest)
    public string SdcId { get; set; } = "";
    public string MrcNo { get; set; } = "";
    public string TaxpayerName { get; set; } = "";      // as KRA has it, shown back to confirm the right PIN
    public string BranchName { get; set; } = "";
    public DateTimeOffset? InitializedAt { get; set; }
    public long LastInvoiceNo { get; set; }             // invcNo: KRA wants 1, 2, 3… per branch
    public long LastPurchaseNo { get; set; }
    public int LastItemSeq { get; set; }                // for item codes KE2NTU0000001…
    /// <summary>KRA item classification for invoice lines typed in rather than picked from products (services).</summary>
    public string DefaultClassCode { get; set; } = "";
    public string DefaultItemCode { get; set; } = "";
    public string LastPurchaseSync { get; set; } = "20200101000000";
    public string LastError { get; set; } = "";
}

public enum EtimsPurchaseStatus
{
    New,
    Accepted, // turned into a bill and confirmed to KRA
    Rejected,
}

/// <summary>A sale a supplier signed with eTIMS naming this business as the buyer: KRA now checks expenses against
/// these, so each one becomes a bill (accept) or is rejected with a reason.</summary>
public sealed class EtimsPurchase : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string SupplierPin { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string SupplierBranch { get; set; } = "";
    public long SupplierInvoiceNo { get; set; }
    public DateOnly Date { get; set; }
    public long Total { get; set; } // cents, VAT included
    public long Tax { get; set; }
    public string Lines { get; set; } = "[]"; // JSON: name, qty, price, tax type, total
    public EtimsPurchaseStatus Status { get; set; } = EtimsPurchaseStatus.New;
    public Guid? BillId { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
