// Moving in: customers, suppliers, products (with stock on hand) and opening balances from a CSV saved from Excel,
// QuickBooks, Sage or Zoho. Column titles are recognised by their usual names in those systems. Every import is
// checked first (a preview with each row's problem), then saved in one go. Opening balances are booked against
// "Opening balance equity", never as sales, so this year's figures stay right:
//   customer owed:  Dr Money customers owe us   Cr Owner's equity     (an "OB-" invoice, open for payments)
//   supplier owed:  Dr Owner's equity           Cr Money we owe suppliers (an "OB-" bill)
using System.Globalization;
using Kabuor.Erp.Accounting;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Import;

public sealed record ImportRequest(string Csv, bool Commit, DateOnly? AsAt);
public sealed record ImportRow(int Line, string Name, string Status, string? Problem);

public sealed class ImportService(ErpDb db, Ledger ledger, Numbers numbers, InventoryService stock)
{
    static readonly Dictionary<string, string[]> Names = new()
    {
        ["name"] = ["name", "customer", "customer name", "display name", "company", "company name", "supplier", "supplier name", "vendor", "vendor name", "account name", "full name"],
        ["phone"] = ["phone", "phone number", "phone numbers", "mobile", "mobile number", "telephone", "tel", "cell"],
        ["email"] = ["email", "e-mail", "email address", "main email"],
        ["pin"] = ["kra pin", "pin", "tax reg no", "tax registration number", "tax number", "vat number", "tax id", "tin"],
        ["address"] = ["address", "billing address", "physical address", "street", "postal address"],
        ["balance"] = ["balance", "open balance", "opening balance", "amount owed", "balance due", "outstanding", "closing balance"],
        ["item"] = ["product/service name", "product", "item", "item name", "product name", "name", "description", "stock item"],
        ["sku"] = ["sku", "code", "item code", "product code", "barcode"],
        ["price"] = ["price", "sales price", "sales price/rate", "selling price", "rate", "unit price", "retail price"],
        ["cost"] = ["cost", "purchase cost", "unit cost", "average cost", "buying price"],
        ["qty"] = ["qty", "quantity", "quantity on hand", "qty on hand", "on hand", "stock", "stock on hand"],
        ["type"] = ["type", "item type", "product type"],
        ["tax"] = ["tax", "vat", "tax code", "sales tax", "vat rate", "tax rate"],
        ["unit"] = ["unit", "unit of measure", "uom"],
    };

    static Dictionary<string, int> Columns(string[] header, params string[] wanted)
    {
        var h = header.Select(c => c.Trim().ToLowerInvariant()).ToArray();
        var map = new Dictionary<string, int>();
        foreach (var w in wanted)
            foreach (var n in Names[w])
                if (Array.IndexOf(h, n) is var i and >= 0 && !map.ContainsValue(i)) { map[w] = i; break; }
        return map;
    }

    static (string[] Header, List<string[]> Rows) Read(string csv)
    {
        if ((csv ?? "").Length > 5_000_000) throw new AppException("That file is too big; split it.");
        var rows = StatementService.Csv(csv ?? "");
        if (rows.Count < 2) throw new AppException("The file needs a title row and at least one row.");
        return (rows[0], rows.Skip(1).ToList());
    }

    static long? Money(string s)
    {
        s = (s ?? "").Replace("KES", "", StringComparison.OrdinalIgnoreCase).Replace("KSh", "", StringComparison.OrdinalIgnoreCase).Replace(",", "").Trim();
        if (s.Length == 0) return null;
        var neg = s.StartsWith('(') || s.StartsWith('-');
        return decimal.TryParse(s.Trim('(', ')', '-'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d * 100) * (neg ? -1 : 1) : null;
    }

    static TaxType Tax(string s)
    {
        s = (s ?? "").ToLowerInvariant();
        if (s.Contains("exempt")) return TaxType.A_Exempt;
        if (s.Contains("zero") || s == "0" || s == "0%") return TaxType.C_ZeroRated;
        if (s.Contains('8')) return TaxType.E_Reduced8;
        if (s.Contains("non") || s.Contains("no vat") || s == "none") return TaxType.D_NonVat;
        return TaxType.B_Standard16;
    }

    // ---------- customers and suppliers ----------

    public async Task<object> Parties(bool suppliers, ImportRequest req)
    {
        var (header, rows) = Read(req.Csv);
        var c = Columns(header, "name", "phone", "email", "pin", "address", "balance");
        if (!c.ContainsKey("name")) throw new AppException("Couldn't find a Name column (e.g. Name, Customer, Display Name, Supplier).");
        var asAt = req.AsAt ?? Clock.Today;
        var existingNames = suppliers ? await db.Suppliers.Select(x => x.Name.ToLower()).ToListAsync() : await db.Customers.Select(x => x.Name.ToLower()).ToListAsync();
        var existingPhones = suppliers ? await db.Suppliers.Select(x => x.Phone).ToListAsync() : await db.Customers.Select(x => x.Phone).ToListAsync();
        var seen = new HashSet<string>();
        var result = new List<ImportRow>();
        long openingTotal = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            string At(string k) => c.TryGetValue(k, out var idx) && idx < rows[i].Length ? rows[i][idx].Trim() : "";
            var name = At("name");
            if (name.Length == 0) continue;
            var phone = Phones.ToMsisdn(At("phone")) ?? "";
            var pin = At("pin").ToUpperInvariant();
            string? problem = null;
            if (name.Length < 2) problem = "Name too short";
            else if (!seen.Add(name.ToLowerInvariant())) problem = "Appears twice in the file";
            else if (existingNames.Contains(name.ToLowerInvariant()) || (phone.Length > 0 && existingPhones.Contains(phone))) problem = "Already here (skipped)";
            else if (pin.Length > 0 && !Etims.SandboxEtimsClient.IsKraPin(pin)) problem = $"KRA PIN “{pin}” doesn't look right (left out)";
            var balance = Money(At("balance")) ?? 0;
            var skip = problem is not null && !problem.EndsWith("(left out)");
            result.Add(new ImportRow(i + 2, name, skip ? "skip" : "add", problem));
            if (skip || !req.Commit) { if (!skip && balance > 0) openingTotal += balance; continue; }
            if (balance > 0) openingTotal += balance;
            if (pin.Length > 0 && !Etims.SandboxEtimsClient.IsKraPin(pin)) pin = "";
            if (suppliers)
            {
                var s = new Supplier { Number = await numbers.Next("S"), Name = name, Phone = phone, Email = At("email"), KraPin = pin, Address = At("address") };
                db.Suppliers.Add(s);
                if (balance > 0)
                {
                    var bill = new PurchaseDoc { Kind = PurchaseKind.Bill, Number = await numbers.Next("OB"), SupplierId = s.Id, SupplierRef = "Opening balance", IssueDate = asAt, DueDate = asAt, Status = PurchaseStatus.Open, Net = balance, Total = balance, Notes = $"Opening balance as at {asAt:d MMM yyyy}", Opening = true };
                    bill.Lines.Add(new PurchaseLine { Description = "Opening balance", Quantity = 1, UnitCost = balance, TaxType = TaxType.D_NonVat, Net = balance, Total = balance });
                    db.PurchaseDocs.Add(bill);
                    await ledger.Post(asAt, $"Opening balance owed to {name}", "OpeningBalance", bill.Id, (SystemAccounts.Equity, balance, 0), (SystemAccounts.Payable, 0, balance));
                }
            }
            else
            {
                var cu = new Customer { Number = await numbers.Next("C"), Name = name, Phone = phone, Email = At("email"), KraPin = pin, Address = At("address") };
                db.Customers.Add(cu);
                if (balance > 0)
                {
                    var inv = new SalesDoc { Kind = SalesDocKind.Invoice, Number = await numbers.Next("OB"), CustomerId = cu.Id, IssueDate = asAt, DueDate = asAt, Status = SalesDocStatus.Sent, Net = balance, Total = balance, Notes = $"Opening balance as at {asAt:d MMM yyyy}", EtimsStatus = EtimsStatus.NotRequired, Opening = true };
                    inv.Lines.Add(new SalesLine { Description = "Opening balance", Quantity = 1, UnitPrice = balance, TaxType = TaxType.D_NonVat, Net = balance, Total = balance });
                    db.SalesDocs.Add(inv);
                    await ledger.Post(asAt, $"Opening balance owed by {name}", "OpeningBalance", inv.Id, (SystemAccounts.Receivable, balance, 0), (SystemAccounts.Equity, 0, balance));
                }
            }
        }
        if (req.Commit) await db.SaveChangesAsync();
        return new { saved = req.Commit, adding = result.Count(r => r.Status == "add"), skipping = result.Count(r => r.Status == "skip"), openingBalances = openingTotal, rows = result.Take(500) };
    }

    // ---------- products ----------

    public async Task<object> Items(ImportRequest req)
    {
        var (header, rows) = Read(req.Csv);
        var c = Columns(header, "item", "sku", "price", "cost", "qty", "type", "tax", "unit");
        if (!c.ContainsKey("item")) throw new AppException("Couldn't find a product name column (e.g. Name, Product/Service Name, Item).");
        var existing = await db.Items.Select(x => new { Name = x.Name.ToLower(), x.Sku }).ToListAsync();
        var seen = new HashSet<string>();
        var result = new List<ImportRow>();
        long stockValue = 0;
        var toOpen = new List<(Item Item, decimal Qty, long Cost)>();
        for (var i = 0; i < rows.Count; i++)
        {
            string At(string k) => c.TryGetValue(k, out var idx) && idx < rows[i].Length ? rows[i][idx].Trim() : "";
            var name = At("item");
            if (name.Length == 0) continue;
            var sku = At("sku");
            var price = Money(At("price"));
            var cost = Money(At("cost")) ?? 0;
            var qty = decimal.TryParse(At("qty").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : 0;
            var service = At("type").Contains("service", StringComparison.OrdinalIgnoreCase) || At("type").Contains("non-inventory", StringComparison.OrdinalIgnoreCase);
            string? problem = null;
            if (!seen.Add(name.ToLowerInvariant())) problem = "Appears twice in the file";
            else if (existing.Any(x => x.Name == name.ToLowerInvariant() || (sku.Length > 0 && x.Sku == sku))) problem = "Already here (skipped)";
            else if (price is null or < 0) problem = "No selling price";
            else if (qty < 0) problem = "Negative stock";
            result.Add(new ImportRow(i + 2, name, problem is null ? "add" : "skip", problem));
            if (problem is not null) continue;
            if (!service && qty > 0) stockValue += (long)Math.Round(qty * cost);
            if (!req.Commit) continue;
            var item = new Item { Name = name, Sku = sku, Price = price!.Value, TaxType = Tax(At("tax")), Kind = service ? ItemKind.Service : ItemKind.Product, TrackStock = !service && (qty > 0 || c.ContainsKey("qty")), Unit = At("unit") is { Length: > 0 } u ? u : "pc" };
            db.Items.Add(item);
            if (item.TrackStock && qty > 0) toOpen.Add((item, qty, cost));
        }
        if (req.Commit)
        {
            await db.SaveChangesAsync();
            foreach (var (item, qty, cost) in toOpen) await stock.Opening(item.Id, null, qty, cost);
        }
        return new { saved = req.Commit, adding = result.Count(r => r.Status == "add"), skipping = result.Count(r => r.Status == "skip"), stockValue, rows = result.Take(500) };
    }
}
