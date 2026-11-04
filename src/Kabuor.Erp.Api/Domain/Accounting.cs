// Records accountants need beyond invoices and payments: tax customers withheld (with their certificates) and
// lines imported from M-Pesa and bank statements, matched against the books.
namespace Kabuor.Erp.Domain;

public enum WithholdingKind
{
    Vat,       // withholding VAT (2% of the taxable value) by an appointed agent
    IncomeTax, // withholding income tax on services (e.g. 5% on professional fees)
}

/// <summary>A customer paid less because they withheld tax for KRA; the certificate is what you claim it with.</summary>
public sealed class WithholdingCertificate : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid SalesDocId { get; set; }
    public Guid CustomerId { get; set; }
    public WithholdingKind Kind { get; set; }
    public long Amount { get; set; }
    public string Certificate { get; set; } = ""; // the certificate number from iTax
    public DateOnly Date { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum StatementLineStatus
{
    Unmatched,
    Matched,
    Ignored,
}

/// <summary>One upload of an M-Pesa or bank statement.</summary>
public sealed class StatementImport : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public string Account { get; set; } = ""; // MPESA | BANK | CASH (system account key)
    public string FileName { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public long? ClosingBalance { get; set; }
    public int Lines { get; set; }
    public int Skipped { get; set; } // already imported before
    public string ImportedBy { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A line from a statement and what in the books it is (a payment received, a supplier paid, salaries…).</summary>
public sealed class StatementLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid ImportId { get; set; }
    public string Account { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Reference { get; set; } = "";
    public string Description { get; set; } = "";
    public long MoneyIn { get; set; }
    public long MoneyOut { get; set; }
    public long? Balance { get; set; }
    public StatementLineStatus Status { get; set; } = StatementLineStatus.Unmatched;
    public string MatchKind { get; set; } = ""; // Payment | SupplierPayment | Journal
    public Guid? MatchId { get; set; }
    public string Note { get; set; } = "";
}
