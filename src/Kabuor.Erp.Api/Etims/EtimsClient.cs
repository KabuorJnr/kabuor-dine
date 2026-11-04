// KRA eTIMS: every invoice a VAT-registered business issues must be signed by KRA (control code + QR).
// Going live needs KRA certification of the integration (OSCU for always-online, VSCU for offline/bulk),
// developed against KRA's eTIMS sandbox. Until then the Sandbox client below stands in: it validates what KRA
// would reject and returns clearly marked test codes. The real OSCU client replaces it behind this interface.
using System.Security.Cryptography;
using System.Text;
using Kabuor.Erp.Domain;

namespace Kabuor.Erp.Etims;

public sealed record EtimsReceipt(string InvoiceNo, string ControlCode, string QrUrl);

public interface IEtimsClient
{
    Task<EtimsReceipt> Sign(Company company, SalesDoc invoice);
    /// <summary>Tells KRA a signed invoice is cancelled (a credit note); null when there's nothing to send.</summary>
    Task<EtimsReceipt?> CreditNote(Company company, SalesDoc invoice) => Task.FromResult<EtimsReceipt?>(null);
}

public sealed class SandboxEtimsClient : IEtimsClient
{
    public Task<EtimsReceipt> Sign(Company company, SalesDoc invoice)
    {
        if (!IsKraPin(company.KraPin)) throw new InvalidOperationException("Add your company's KRA PIN in Settings to sign invoices with eTIMS.");
        var buyerPin = invoice.Customer?.KraPin ?? "";
        if (buyerPin.Length > 0 && !IsKraPin(buyerPin)) throw new InvalidOperationException($"The customer's KRA PIN \"{buyerPin}\" doesn't look right (e.g. P051234567X).");
        if (invoice.Lines.Count == 0) throw new InvalidOperationException("An invoice needs at least one line.");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{company.KraPin}|{invoice.Number}|{invoice.Total}"));
        var control = "TEST-" + Convert.ToHexString(hash)[..16];
        var number = $"{company.KraPin}/{invoice.Number}";
        return Task.FromResult(new EtimsReceipt(number, control, $"https://etims-sbx.kra.go.ke/common/link/etims/receipt/indexEtimsReceiptData?Data={Uri.EscapeDataString(number)}"));
    }

    /// <summary>KRA PINs are a letter (A individual, P non-individual), 9 digits, a letter.</summary>
    public static bool IsKraPin(string pin) =>
        pin.Length == 11 && char.IsAsciiLetterUpper(pin[0]) && pin[1..10].All(char.IsAsciiDigit) && char.IsAsciiLetterUpper(pin[10]);
}
