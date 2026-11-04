// A customer who is a withholding agent pays the invoice less the tax they withheld and gives a certificate.
// Recording the certificate closes that part of the invoice and keeps the tax as something to claim:
//   Dr Tax withheld by customers (VAT, or income tax)   Cr Money customers owe us
// Withheld VAT reduces the VAT you pay KRA; withheld income tax is a credit against your income tax.
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Accounting;

public sealed record WithholdingInput(WithholdingKind Kind, long Amount, string? Certificate, DateOnly? Date);

public sealed class WithholdingService(ErpDb db, Ledger ledger, RequestContext ctx)
{
    public async Task<WithholdingCertificate> Record(Guid invoiceId, WithholdingInput input)
    {
        var inv = await db.SalesDocs.SingleOrDefaultAsync(d => d.Id == invoiceId) ?? throw new AppException("Not found.", 404);
        if (inv.Kind != SalesDocKind.Invoice || inv.Status is SalesDocStatus.Draft or SalesDocStatus.Void) throw new AppException("Only an issued invoice can have tax withheld.");
        if (inv.Currency.Length > 0) throw new AppException($"{inv.Number} is in {inv.Currency}. Withholding certificates are for shilling invoices.");
        if (input.Amount <= 0) throw new AppException("Enter the amount withheld.");
        if (input.Amount > inv.Balance) throw new AppException($"That's more than is left to pay on {inv.Number} ({inv.Balance / 100m:N2}).");
        var certificate = (input.Certificate ?? "").Trim().ToUpperInvariant();
        if (certificate.Length < 4) throw new AppException("Enter the withholding certificate number from the customer (it's on their iTax certificate).");
        if (await db.WithholdingCertificates.AnyAsync(w => w.Certificate == certificate && w.Kind == input.Kind)) throw new AppException("That certificate is already recorded.", 409);
        if (input.Kind == WithholdingKind.Vat && input.Amount > inv.Tax) throw new AppException($"Withholding VAT can't be more than the VAT on the invoice ({inv.Tax / 100m:N2}).");

        var date = input.Date ?? Clock.Today;
        var w = new WithholdingCertificate { SalesDocId = inv.Id, CustomerId = inv.CustomerId, Kind = input.Kind, Amount = input.Amount, Certificate = certificate, Date = date, CreatedBy = ctx.Actor };
        db.WithholdingCertificates.Add(w);
        await ledger.Post(date, $"{(input.Kind == WithholdingKind.Vat ? "VAT" : "Tax")} withheld on {inv.Number}, certificate {certificate}", "Withholding", w.Id,
            (input.Kind == WithholdingKind.Vat ? SystemAccounts.VatWithheld : SystemAccounts.TaxWithheld, input.Amount, 0), (SystemAccounts.Receivable, 0, input.Amount));
        inv.Paid += input.Amount;
        inv.Status = inv.Balance == 0 ? SalesDocStatus.Paid : SalesDocStatus.PartPaid;
        await db.SaveChangesAsync();
        return w;
    }

    /// <summary>The usual withholding VAT on an invoice: 2% of its taxable value (16% lines).</summary>
    public static long SuggestedVat(SalesDoc inv) =>
        (long)Math.Round(inv.Lines.Where(l => l.TaxType == TaxType.B_Standard16).Sum(l => l.Net) * 0.02m, MidpointRounding.AwayFromZero);
}
