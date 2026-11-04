// Which invoices a payment pays. Pure logic (no database) so every rule is tested.
// Auto-matches only on strong evidence; anything weaker becomes a suggestion a person confirms with one tap.
//   1. Account number is an invoice number ("INV-0012", "inv12", "12")  → that invoice              (auto)
//   2. Account number is a customer number ("C-0003", "c3")             → their invoices, oldest first (auto)
//   3. Paid from a customer's phone, exactly one open invoice for that amount → that invoice       (auto)
//   4. Paid from a customer's phone otherwise                           → their invoices, oldest first (suggest)
//   5. Exactly one open invoice anywhere for that exact amount          → that invoice              (suggest)
namespace Kabuor.Erp.Payments;

public sealed record OpenInvoice(Guid Id, string Number, Guid CustomerId, string CustomerNumber, string CustomerName, string CustomerPhone, long Balance, DateOnly DueDate);

public sealed record Allocation(Guid InvoiceId, string InvoiceNumber, long Amount);

public sealed record MatchResult(bool Auto, string Reason, IReadOnlyList<Allocation> Allocations)
{
    /// <summary>Not certain, but strong: paid from the customer's own phone (a same-amount guess is not).</summary>
    public bool Likely { get; init; }
    public static readonly MatchResult None = new(false, "", []);
    public long Total => Allocations.Sum(a => a.Amount);
}

public static class Matching
{
    public static MatchResult Find(long amount, string accountRef, string payerMsisdn, IReadOnlyList<OpenInvoice> open)
    {
        if (amount <= 0 || open.Count == 0) return MatchResult.None;
        var byDue = open.Where(i => i.Balance > 0).OrderBy(i => i.DueDate).ThenBy(i => i.Number).ToList();

        var invoice = byDue.FirstOrDefault(i => SameNumber(accountRef, i.Number, "INV"));
        if (invoice is not null) return new(true, $"Account number {accountRef.Trim()} is invoice {invoice.Number}", Fill(amount, [invoice]));

        var byCustomerNo = byDue.Where(i => SameNumber(accountRef, i.CustomerNumber, "C")).ToList();
        if (byCustomerNo.Count > 0) return new(true, $"Account number {accountRef.Trim()} is customer {byCustomerNo[0].CustomerName}", Fill(amount, byCustomerNo));

        if (!string.IsNullOrEmpty(payerMsisdn))
        {
            var theirs = byDue.Where(i => Common.Phones.ToMsisdn(i.CustomerPhone) == payerMsisdn).ToList();
            var exact = theirs.Where(i => i.Balance == amount).ToList();
            if (exact.Count == 1) return new(true, $"Paid from {Owner(exact[0].CustomerName)} phone, exactly the balance of {exact[0].Number}", Fill(amount, exact));
            if (theirs.Count > 0) return new(false, $"Paid from {Owner(theirs[0].CustomerName)} phone", Fill(amount, theirs)) { Likely = true };
        }

        var sameAmount = byDue.Where(i => i.Balance == amount).ToList();
        if (sameAmount.Count == 1) return new(false, $"Same amount as {sameAmount[0].Number} ({sameAmount[0].CustomerName})", Fill(amount, sameAmount));

        return MatchResult.None;
    }

    static string Owner(string name) => name.EndsWith('s') ? $"{name}’" : $"{name}’s";

    /// <summary>Pays invoices in order until the money runs out.</summary>
    static List<Allocation> Fill(long amount, IEnumerable<OpenInvoice> invoices)
    {
        var left = amount;
        var result = new List<Allocation>();
        foreach (var i in invoices)
        {
            if (left <= 0) break;
            var pay = Math.Min(left, i.Balance);
            result.Add(new Allocation(i.Id, i.Number, pay));
            left -= pay;
        }
        return result;
    }

    /// <summary>"inv 12", "INV-0012", "12" all match INV-0012. Bare digits only count when the ref has no other prefix.</summary>
    internal static bool SameNumber(string reference, string number, string prefix)
    {
        static string Clean(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var r = Clean(reference ?? "");
        var n = Clean(number);
        if (r.Length == 0 || !n.StartsWith(prefix)) return false;
        var refPrefix = new string(r.TakeWhile(char.IsLetter).ToArray());
        var refDigits = r[refPrefix.Length..];
        if (refDigits.Length == 0 || !refDigits.All(char.IsDigit)) return false;
        if (refPrefix.Length > 0 && refPrefix != prefix) return false;
        if (refPrefix.Length == 0 && prefix != "INV") return false; // bare "12" means an invoice, not a customer
        return int.TryParse(refDigits, out var a) && int.TryParse(n[prefix.Length..], out var b) && a == b;
    }
}
