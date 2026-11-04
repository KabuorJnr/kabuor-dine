// Setting up the company's eTIMS connection, and the invoices suppliers sign naming this business as the buyer.
// KRA checks every expense in a tax return against eTIMS, so each supplier invoice becomes a bill (accept: it's
// confirmed back to KRA) or is rejected with a reason.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Purchases;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Etims;

public sealed record EtimsSetupInput(EtimsMode Mode, string? BranchId, string? DeviceSerial, string? DefaultClassCode);
public sealed record PurchaseLineView(string Name, decimal Quantity, long UnitPrice, string TaxBand, long Total, long Tax);

public sealed class EtimsService(ErpDb db, OscuApi api, SecretBox box, PurchaseService purchases, Numbers numbers, RequestContext ctx)
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    async Task<Company> Company() => await db.Companies.SingleAsync(c => c.Id == db.CompanyId);

    public async Task<object> Status()
    {
        var d = await db.EtimsDevices.SingleOrDefaultAsync();
        var waiting = await db.EtimsPurchases.CountAsync(p => p.Status == EtimsPurchaseStatus.New);
        var unsigned = await db.SalesDocs.CountAsync(s => s.EtimsStatus == EtimsStatus.Failed || s.EtimsStatus == EtimsStatus.Pending);
        var unclassified = await db.Items.CountAsync(i => i.Active && i.EtimsClassCode == "");
        return new
        {
            mode = d?.Mode ?? EtimsMode.Test, connected = d?.InitializedAt is not null, d?.BranchId, d?.DeviceSerial, d?.TaxpayerName, d?.BranchName, d?.SdcId,
            d?.InitializedAt, d?.DefaultClassCode, d?.LastError, purchasesWaiting = waiting, invoicesUnsigned = unsigned, productsWithoutClass = unclassified,
        };
    }

    /// <summary>Switches mode and, for Sandbox/Live, connects the device: KRA returns the taxpayer it belongs to and the key.</summary>
    public async Task<object> Setup(EtimsSetupInput input)
    {
        var company = await Company();
        var d = await db.EtimsDevices.SingleOrDefaultAsync() ?? db.EtimsDevices.Add(new EtimsDevice()).Entity;
        d.DefaultClassCode = new string((input.DefaultClassCode ?? d.DefaultClassCode).Where(char.IsDigit).ToArray());
        if (input.Mode == EtimsMode.Test)
        {
            d.Mode = EtimsMode.Test;
            await db.SaveChangesAsync();
            return await Status();
        }
        if (!SandboxEtimsClient.IsKraPin(company.KraPin)) throw new AppException("Add the company's KRA PIN in Settings first.");
        var serial = (input.DeviceSerial ?? "").Trim();
        if (serial.Length < 3) throw new AppException("Enter the device serial number KRA gave you when your OSCU application was approved.");
        var branch = string.IsNullOrWhiteSpace(input.BranchId) ? "00" : input.BranchId.Trim();
        var changed = d.Mode != input.Mode || d.DeviceSerial != serial || d.BranchId != branch;
        if (changed || d.InitializedAt is null)
        {
            JsonObject res;
            try
            {
                res = await api.Call(input.Mode, company.KraPin, branch, "", "selectInitOsdcInfo", new JsonObject { ["dvcSrlNo"] = serial });
            }
            catch (EtimsException e) when (e.Code == "902")
            {
                throw new AppException("KRA says this device is already set up elsewhere. Ask KRA to reset it, or use the key from the first setup.");
            }
            catch (EtimsException e) { throw new AppException(e.Message); }
            var info = res["data"]?["info"]?.AsObject() ?? throw new AppException("KRA didn't return the device details.");
            if (info["tin"]?.GetValue<string>() is { } tin && !tin.Equals(company.KraPin, StringComparison.OrdinalIgnoreCase))
                throw new AppException($"That device belongs to PIN {tin}, not {company.KraPin}.");
            (d.Mode, d.BranchId, d.DeviceSerial) = (input.Mode, branch, serial);
            d.CmcKey = box.Seal(info["cmcKey"]?.GetValue<string>() ?? throw new AppException("KRA didn't return a communication key."));
            d.SdcId = info["sdcId"]?.GetValue<string>() ?? info["sdicId"]?.GetValue<string>() ?? "";
            d.MrcNo = info["mrcNo"]?.GetValue<string>() ?? "";
            d.TaxpayerName = info["taxprNm"]?.GetValue<string>() ?? "";
            d.BranchName = info["bhfNm"]?.GetValue<string>() ?? "";
            (d.InitializedAt, d.LastError) = (DateTimeOffset.UtcNow, "");
            if (changed) (d.LastInvoiceNo, d.LastItemSeq, d.DefaultItemCode) = (0, 0, ""); // a new device starts its own numbering
            if (changed) await db.Items.Where(i => i.EtimsItemCode != "").ExecuteUpdateAsync(s => s.SetProperty(i => i.EtimsItemCode, ""));
        }
        await db.SaveChangesAsync();
        return await Status();
    }

    // ---------- supplier invoices ----------

    /// <summary>Fetches new sales suppliers signed against our PIN since the last check.</summary>
    public async Task<int> SyncPurchases()
    {
        var d = await db.EtimsDevices.SingleOrDefaultAsync();
        if (d is null || d.Mode == EtimsMode.Test || d.InitializedAt is null) throw new AppException("Connect eTIMS (Sandbox or Live) to receive supplier invoices.");
        var company = await Company();
        var now = OscuApi.When(DateTimeOffset.UtcNow);
        var res = await api.Call(d.Mode, company.KraPin, d.BranchId, box.Open(d.CmcKey), "selectTrnsPurchaseSalesList", new JsonObject { ["lastReqDt"] = d.LastPurchaseSync }, noDataOk: true);
        var list = res["data"]?["saleList"]?.AsArray() ?? [];
        var n = 0;
        foreach (var s in list.OfType<JsonObject>())
        {
            var pin = s["spplrTin"]?.GetValue<string>() ?? "";
            var no = s["spplrInvcNo"]?.GetValue<long>() ?? 0;
            if (await db.EtimsPurchases.AnyAsync(p => p.SupplierPin == pin && p.SupplierInvoiceNo == no)) continue;
            var lines = (s["itemList"]?.AsArray() ?? []).OfType<JsonObject>().Select(i => new PurchaseLineView(
                i["itemNm"]?.GetValue<string>() ?? "", i["qty"]?.GetValue<decimal>() ?? 1, Cents(i["prc"]), i["taxTyCd"]?.GetValue<string>() ?? "B", Cents(i["totAmt"]), Cents(i["taxAmt"]))).ToList();
            var date = DateOnly.TryParseExact(s["salesDt"]?.GetValue<string>() ?? "", "yyyyMMdd", out var dt) ? dt : Clock.Today;
            db.EtimsPurchases.Add(new EtimsPurchase
            {
                SupplierPin = pin, SupplierName = s["spplrNm"]?.GetValue<string>() ?? "", SupplierBranch = s["spplrBhfId"]?.GetValue<string>() ?? "00",
                SupplierInvoiceNo = no, Date = date, Total = Cents(s["totAmt"]), Tax = Cents(s["totTaxAmt"]), Lines = JsonSerializer.Serialize(lines, Web),
            });
            n++;
        }
        d.LastPurchaseSync = now;
        await db.SaveChangesAsync();
        if (n > 0) await Agents.AgentKick.Kick(db, "payables"); // new supplier invoices: the payables agent reads them now
        return n;
    }

    static long Cents(JsonNode? n) => n is null ? 0 : (long)Math.Round(n.GetValue<decimal>() * 100);

    public async Task<object> Purchases(string? status)
    {
        var q = db.EtimsPurchases.AsQueryable();
        var counts = new { waiting = await q.CountAsync(p => p.Status == EtimsPurchaseStatus.New), accepted = await q.CountAsync(p => p.Status == EtimsPurchaseStatus.Accepted), rejected = await q.CountAsync(p => p.Status == EtimsPurchaseStatus.Rejected) };
        q = status switch { "accepted" => q.Where(p => p.Status == EtimsPurchaseStatus.Accepted), "rejected" => q.Where(p => p.Status == EtimsPurchaseStatus.Rejected), _ => q.Where(p => p.Status == EtimsPurchaseStatus.New) };
        return new { counts, items = await q.OrderByDescending(p => p.Date).Take(200).ToListAsync() };
    }

    static TaxType Tax(string band) => band switch { "A" => TaxType.A_Exempt, "C" => TaxType.C_ZeroRated, "D" => TaxType.D_NonVat, "E" => TaxType.E_Reduced8, _ => TaxType.B_Standard16 };

    /// <summary>Turns the supplier's eTIMS invoice into a bill (finding or adding the supplier by PIN) and confirms it to KRA.</summary>
    public async Task<object> Accept(Guid id, Guid? accountId)
    {
        var p = await db.EtimsPurchases.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (p.Status != EtimsPurchaseStatus.New) throw new AppException("Already dealt with.");
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.KraPin == p.SupplierPin);
        if (supplier is null)
        {
            supplier = new Supplier { Number = await numbers.Next("S"), Name = p.SupplierName.Length > 0 ? p.SupplierName : p.SupplierPin, KraPin = p.SupplierPin };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();
        }
        var lines = JsonSerializer.Deserialize<List<PurchaseLineView>>(p.Lines, Web) ?? [];
        var account = accountId ?? (await db.Accounts.Where(a => a.SystemKey == Accounting.SystemAccounts.OtherExpenses).Select(a => (Guid?)a.Id).FirstOrDefaultAsync());
        var bill = await purchases.Create(new PurchaseInput(PurchaseKind.Bill, supplier.Id, p.Date, 30, $"eTIMS {p.SupplierInvoiceNo}", null, $"From {p.SupplierName}'s eTIMS invoice",
            lines.Select(l => new PurchaseLineInput(null, account, l.Name, l.Quantity, l.UnitPrice, Tax(l.TaxBand))).ToList()));
        await purchases.Post(bill.Id);

        // Confirm the purchase to KRA (live/sandbox), so it's declared on our side too.
        var d = await db.EtimsDevices.SingleOrDefaultAsync();
        if (d is { Mode: not EtimsMode.Test, InitializedAt: not null })
        {
            var company = await Company();
            var bands = lines.GroupBy(l => l.TaxBand).ToDictionary(g => g.Key, g => (total: g.Sum(l => l.Total), tax: g.Sum(l => l.Tax)));
            var body = new JsonObject
            {
                ["invcNo"] = ++d.LastPurchaseNo, ["orgInvcNo"] = 0, ["spplrTin"] = p.SupplierPin, ["spplrBhfId"] = p.SupplierBranch, ["spplrNm"] = p.SupplierName,
                ["spplrInvcNo"] = p.SupplierInvoiceNo, ["regTyCd"] = "A", ["pchsTyCd"] = "N", ["rcptTyCd"] = "P", ["pmtTyCd"] = "02", ["pchsSttsCd"] = "02",
                ["cfmDt"] = OscuApi.When(DateTimeOffset.UtcNow), ["pchsDt"] = OscuApi.Day(p.Date), ["wrhsDt"] = "", ["cnclReqDt"] = "", ["cnclDt"] = "", ["rfdDt"] = "",
                ["totItemCnt"] = lines.Count, ["totTaxblAmt"] = OscuApi.Ksh(p.Total), ["totTaxAmt"] = OscuApi.Ksh(p.Tax), ["totAmt"] = OscuApi.Ksh(p.Total), ["remark"] = null,
                ["regrId"] = "kabuor", ["regrNm"] = ctx.Actor, ["modrId"] = "kabuor", ["modrNm"] = ctx.Actor,
            };
            foreach (var b in new[] { "A", "B", "C", "D", "E" })
            {
                var (total, tax) = bands.GetValueOrDefault(b);
                (body[$"taxblAmt{b}"], body[$"taxRt{b}"], body[$"taxAmt{b}"]) = (OscuApi.Ksh(total), b == "B" ? 16 : b == "E" ? 8 : 0, OscuApi.Ksh(tax));
            }
            var seq = 0;
            body["itemList"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
            {
                ["itemSeq"] = ++seq, ["itemCd"] = null, ["itemClsCd"] = null, ["itemNm"] = l.Name, ["bcd"] = "", ["spplrItemClsCd"] = null, ["spplrItemCd"] = null, ["spplrItemNm"] = l.Name,
                ["pkgUnitCd"] = "NT", ["pkg"] = l.Quantity, ["qtyUnitCd"] = "U", ["qty"] = l.Quantity, ["prc"] = OscuApi.Ksh(l.UnitPrice), ["splyAmt"] = OscuApi.Ksh(l.Total),
                ["dcRt"] = 0, ["dcAmt"] = 0, ["taxblAmt"] = OscuApi.Ksh(l.Total), ["taxTyCd"] = l.TaxBand, ["taxAmt"] = OscuApi.Ksh(l.Tax), ["totAmt"] = OscuApi.Ksh(l.Total), ["itemExprDt"] = null,
            }).ToArray());
            try { await api.Call(d.Mode, company.KraPin, d.BranchId, box.Open(d.CmcKey), "insertTrnsPurchase", body); }
            catch (EtimsException e) { d.LastPurchaseNo--; p.Note = $"Bill made; KRA confirmation failed: {e.Message}"; }
        }
        (p.Status, p.BillId) = (EtimsPurchaseStatus.Accepted, bill.Id);
        await db.SaveChangesAsync();
        return new { bill = bill.Id, bill.Number };
    }

    public async Task<object> Reject(Guid id, string? reason)
    {
        var p = await db.EtimsPurchases.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);
        if (p.Status != EtimsPurchaseStatus.New) throw new AppException("Already dealt with.");
        if (string.IsNullOrWhiteSpace(reason)) throw new AppException("Say why (e.g. not our purchase, wrong amount): the supplier may need to issue a credit note.");
        (p.Status, p.Note) = (EtimsPurchaseStatus.Rejected, reason.Trim());
        await db.SaveChangesAsync();
        return new { p.Id, p.Status };
    }
}
