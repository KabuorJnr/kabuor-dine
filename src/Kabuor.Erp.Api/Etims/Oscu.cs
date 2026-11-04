// KRA eTIMS through an Online Sales Control Unit (OSCU), following KRA's OSCU specification v2.0:
//   /selectInitOsdcInfo   connect the device: returns the communication key (cmcKey) used on every call
//   /saveItem             register a product (each needs a KRA item code and classification)
//   /saveTrnsSalesOsdc    sign a sale or a credit note: returns the receipt number and signature
//   /selectTrnsPurchaseSalesList   sales suppliers filed against our PIN (to become bills)
//   /insertTrnsPurchase   confirm a purchase
// Amounts are in shillings with VAT included, grouped by tax band A (exempt), B (16%), C (0%), D (non-VAT), E (8%).
// Every call carries tin, bhfId and cmcKey as headers and in the body. resultCd "000" is success, "001" no data.
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Etims;

public sealed class EtimsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Talks to KRA. Knows nothing about invoices: it sends what it's given and reads KRA's answer.</summary>
public sealed class OscuApi(IHttpClientFactory http, IConfiguration config)
{
    public const string Sandbox = "https://etims-api-sbx.kra.go.ke/etims-api/";
    public const string Production = "https://etims-api.kra.go.ke/etims-api/";

    Uri Base(EtimsMode mode) => new(mode == EtimsMode.Live ? config["Etims:LiveUrl"] ?? Production : config["Etims:SandboxUrl"] ?? Sandbox);

    public async Task<JsonObject> Call(EtimsMode mode, string pin, string branch, string cmcKey, string path, JsonObject body, bool noDataOk = false)
    {
        body["tin"] = pin;
        body["bhfId"] = branch;
        if (cmcKey.Length > 0) body["cmcKey"] = cmcKey;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base(mode), path)) { Content = JsonContent.Create(body) };
        req.Headers.Add("tin", pin);
        req.Headers.Add("bhfId", branch);
        if (cmcKey.Length > 0) req.Headers.Add("cmcKey", cmcKey);
        HttpResponseMessage res;
        try { res = await http.CreateClient("kra-etims").SendAsync(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new EtimsException("894", "KRA eTIMS couldn't be reached. The invoice is kept and will be signed when you retry."); }
        using (res)
        {
            var json = await res.Content.ReadFromJsonAsync<JsonObject>().ConfigureAwait(false) ?? throw new EtimsException("899", "KRA eTIMS sent an empty answer.");
            var code = json["resultCd"]?.GetValue<string>() ?? "";
            if (code == "000" || (noDataOk && code == "001")) return json;
            throw new EtimsException(code, $"KRA eTIMS: {json["resultMsg"]?.GetValue<string>() ?? "refused"} ({code})");
        }
    }

    public static string When(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(3)).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    public static string Day(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    public static decimal Ksh(long cents) => cents / 100m;
}

/// <summary>Signs invoices with the company's eTIMS device; in Test mode (or with no device yet) uses test signatures.</summary>
public sealed class OscuEtimsClient(ErpDb db, OscuApi api, SecretBox box, RequestContext ctx) : IEtimsClient
{
    static readonly SandboxEtimsClient Test = new();
    public static string Band(TaxType t) => t switch { TaxType.A_Exempt => "A", TaxType.B_Standard16 => "B", TaxType.C_ZeroRated => "C", TaxType.D_NonVat => "D", _ => "E" };
    static readonly Dictionary<string, decimal> Rate = new() { ["A"] = 0, ["B"] = 16, ["C"] = 0, ["D"] = 0, ["E"] = 8 };

    async Task<EtimsDevice?> Device() => await db.EtimsDevices.SingleOrDefaultAsync();

    public async Task<EtimsReceipt> Sign(Company company, SalesDoc invoice)
    {
        var d = await Device();
        if (d is null || d.Mode == EtimsMode.Test) return await Test.Sign(company, invoice);
        return await Send(company, d, invoice, credit: false);
    }

    public async Task<EtimsReceipt?> CreditNote(Company company, SalesDoc invoice)
    {
        var d = await Device();
        if (d is null || d.Mode == EtimsMode.Test || invoice.EtimsStatus != EtimsStatus.Signed || invoice.EtimsInvcNo == 0) return null;
        return await Send(company, d, invoice, credit: true);
    }

    /// <summary>KRA must know a product before it's sold: register any not yet known.</summary>
    async Task<Dictionary<Guid, Item>> Register(EtimsDevice d, IEnumerable<SalesLine> lines)
    {
        var ids = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        foreach (var item in items.Values.Where(i => i.EtimsItemCode.Length == 0))
        {
            var cls = item.EtimsClassCode.Length > 0 ? item.EtimsClassCode : d.DefaultClassCode;
            if (cls.Length == 0) throw new EtimsException("910", $"Choose the KRA item classification for “{item.Name}” (Settings → Products) so it can be registered with eTIMS.");
            var type = item.Kind == ItemKind.Service ? "3" : item.TrackStock ? "2" : "2";
            var code = $"KE{type}NTU{++d.LastItemSeq:D7}";
            await api.Call(d.Mode, (await db.Companies.SingleAsync(c => c.Id == db.CompanyId)).KraPin, d.BranchId, box.Open(d.CmcKey), "saveItem", new JsonObject
            {
                ["itemCd"] = code, ["itemClsCd"] = cls, ["itemTyCd"] = type, ["itemNm"] = item.Name, ["itemStdNm"] = null, ["orgnNatCd"] = "KE",
                ["pkgUnitCd"] = "NT", ["qtyUnitCd"] = "U", ["taxTyCd"] = Band(item.TaxType), ["btchNo"] = null, ["bcd"] = item.Barcode.Length > 0 ? item.Barcode : item.Sku.Length > 0 ? item.Sku : null,
                ["dftPrc"] = OscuApi.Ksh(item.Price), ["addInfo"] = null, ["sftyQty"] = null, ["isrcAplcbYn"] = "N", ["useYn"] = "Y",
                ["regrId"] = Who(), ["regrNm"] = ctx.Actor, ["modrId"] = Who(), ["modrNm"] = ctx.Actor,
            });
            (item.EtimsItemCode, item.EtimsClassCode) = (code, cls);
        }
        return items;
    }

    string Who() => (ctx.UserId?.ToString("N") ?? "system")[..Math.Min(20, (ctx.UserId?.ToString("N") ?? "system").Length)];

    /// <summary>Lines typed in (not a product) are sent under one registered "general services" item.</summary>
    async Task<string> DefaultItem(EtimsDevice d, string pin)
    {
        if (d.DefaultItemCode.Length > 0) return d.DefaultItemCode;
        if (d.DefaultClassCode.Length == 0) throw new EtimsException("910", "Set the KRA classification for typed-in invoice lines (Settings → eTIMS), or pick products for every line.");
        var code = $"KE3NTU{++d.LastItemSeq:D7}";
        await api.Call(d.Mode, pin, d.BranchId, box.Open(d.CmcKey), "saveItem", new JsonObject
        {
            ["itemCd"] = code, ["itemClsCd"] = d.DefaultClassCode, ["itemTyCd"] = "3", ["itemNm"] = "General goods and services", ["itemStdNm"] = null, ["orgnNatCd"] = "KE",
            ["pkgUnitCd"] = "NT", ["qtyUnitCd"] = "U", ["taxTyCd"] = "B", ["btchNo"] = null, ["bcd"] = null, ["dftPrc"] = 0, ["addInfo"] = null, ["sftyQty"] = null,
            ["isrcAplcbYn"] = "N", ["useYn"] = "Y", ["regrId"] = Who(), ["regrNm"] = ctx.Actor, ["modrId"] = Who(), ["modrNm"] = ctx.Actor,
        });
        d.DefaultItemCode = code;
        return code;
    }

    async Task<EtimsReceipt> Send(Company company, EtimsDevice d, SalesDoc inv, bool credit)
    {
        if (d.InitializedAt is null) throw new EtimsException("910", "Connect eTIMS first (Settings → eTIMS).");
        var items = await Register(d, inv.Lines);
        string? general = inv.Lines.Any(l => l.ItemId is null) ? await DefaultItem(d, company.KraPin) : null;
        var buyerPin = inv.Customer?.KraPin ?? "";
        var number = ++d.LastInvoiceNo;
        var bands = inv.Lines.GroupBy(l => Band(l.TaxType)).ToDictionary(g => g.Key, g => (total: g.Sum(l => l.Total), tax: g.Sum(l => l.Tax)));
        var body = new JsonObject
        {
            ["trdInvcNo"] = inv.Number, ["invcNo"] = number, ["orgInvcNo"] = credit ? inv.EtimsInvcNo : 0,
            ["custTin"] = buyerPin.Length > 0 ? buyerPin : null, ["custNm"] = inv.Customer?.Name,
            ["salesTyCd"] = "N", ["rcptTyCd"] = credit ? "R" : "S", ["pmtTyCd"] = inv.Paid >= inv.Total && inv.Total > 0 ? "01" : "02",
            ["salesSttsCd"] = credit ? "05" : "02", ["cfmDt"] = OscuApi.When(DateTimeOffset.UtcNow), ["salesDt"] = OscuApi.Day(inv.IssueDate),
            ["stockRlsDt"] = null, ["cnclReqDt"] = null, ["cnclDt"] = null,
            ["rfdDt"] = credit ? OscuApi.When(DateTimeOffset.UtcNow) : null, ["rfdRsnCd"] = credit ? "06" : null,
            ["totItemCnt"] = inv.Lines.Count,
            ["totTaxblAmt"] = OscuApi.Ksh(inv.Lines.Sum(l => l.Total)), ["totTaxAmt"] = OscuApi.Ksh(inv.Tax), ["totAmt"] = OscuApi.Ksh(inv.Total),
            ["prchrAcptcYn"] = "N", ["remark"] = credit ? $"Credit note for {inv.Number}" : null,
            ["regrId"] = Who(), ["regrNm"] = ctx.Actor, ["modrId"] = Who(), ["modrNm"] = ctx.Actor,
            ["receipt"] = new JsonObject
            {
                ["custTin"] = buyerPin.Length > 0 ? buyerPin : null, ["custMblNo"] = inv.Customer?.Phone is { Length: > 0 } ph ? $"0{ph[3..]}" : null,
                ["rcptPbctDt"] = OscuApi.When(DateTimeOffset.UtcNow), ["trdeNm"] = company.Name.Length > 20 ? company.Name[..20] : company.Name,
                ["adrs"] = company.County, ["topMsg"] = null, ["btmMsg"] = null, ["prchrAcptcYn"] = "N",
            },
        };
        foreach (var b in new[] { "A", "B", "C", "D", "E" })
        {
            var (total, tax) = bands.GetValueOrDefault(b);
            body[$"taxblAmt{b}"] = OscuApi.Ksh(total);
            body[$"taxRt{b}"] = Rate[b];
            body[$"taxAmt{b}"] = OscuApi.Ksh(tax);
        }
        var list = new JsonArray();
        var seq = 0;
        foreach (var l in inv.Lines)
        {
            var item = l.ItemId is { } id ? items.GetValueOrDefault(id) : null;
            list.Add(new JsonObject
            {
                ["itemSeq"] = ++seq, ["itemCd"] = item?.EtimsItemCode ?? general, ["itemClsCd"] = item?.EtimsClassCode ?? d.DefaultClassCode,
                ["itemNm"] = l.Description.Length > 200 ? l.Description[..200] : l.Description, ["bcd"] = item?.Barcode is { Length: > 0 } bc ? bc : item?.Sku is { Length: > 0 } sku ? sku : null,
                ["pkgUnitCd"] = "NT", ["pkg"] = l.Quantity, ["qtyUnitCd"] = "U", ["qty"] = l.Quantity, ["prc"] = OscuApi.Ksh(l.UnitPrice),
                ["splyAmt"] = OscuApi.Ksh(l.Total), ["dcRt"] = 0, ["dcAmt"] = 0,
                ["isrccCd"] = null, ["isrccNm"] = null, ["isrcRt"] = null, ["isrcAmt"] = null,
                ["taxTyCd"] = Band(l.TaxType), ["taxblAmt"] = OscuApi.Ksh(l.Total), ["taxAmt"] = OscuApi.Ksh(l.Tax), ["totAmt"] = OscuApi.Ksh(l.Total),
            });
        }
        body["itemList"] = list;

        JsonObject res;
        try { res = await api.Call(d.Mode, company.KraPin, d.BranchId, box.Open(d.CmcKey), "saveTrnsSalesOsdc", body); }
        catch { d.LastInvoiceNo--; throw; } // the number wasn't used: send it again next time
        var data = res["data"]!.AsObject();
        var rcptNo = data["curRcptNo"]?.ToString() ?? "";
        var sign = data["rcptSign"]?.GetValue<string>() ?? "";
        if (!credit) inv.EtimsInvcNo = number;
        // KRA's receipt check page takes PIN + branch + signature.
        var host = d.Mode == EtimsMode.Live ? "https://etims.kra.go.ke" : "https://etims-sbx.kra.go.ke";
        return new EtimsReceipt($"{d.SdcId}/{rcptNo}", sign, $"{host}/common/link/etims/receipt/indexEtimsReceiptData?Data={company.KraPin}{d.BranchId}{sign}");
    }
}
