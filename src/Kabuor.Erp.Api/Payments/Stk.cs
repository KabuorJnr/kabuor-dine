// "Pay with M-Pesa" from an invoice: an STK prompt on the customer's phone, for the balance, with the invoice
// number as the account. When Safaricom confirms, the money is recorded against exactly that invoice, the
// same way a paybill payment is. Each company uses its own Daraja app (its keys are sealed at rest and never
// shown again); without keys the button explains how to get them instead of pretending to work.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Payments;

public sealed record StkSetupInput(string Environment, string Kind, string Shortcode, string? Till, string? ConsumerKey, string? ConsumerSecret, string? Passkey);
public sealed record StkRequestInput(string Phone, long? Amount);

/// <summary>Safaricom Daraja: sign in and send an STK prompt.</summary>
public static class Daraja
{
    public sealed record Push(string Environment, string ConsumerKey, string ConsumerSecret, string Shortcode, string Passkey, bool Till, string PartyB,
        string Phone, long Shillings, string CallbackUrl, string Account, string Description);

    static string Base(string env) => env == "production" ? "https://api.safaricom.co.ke" : "https://sandbox.safaricom.co.ke";

    /// <summary>Sends the prompt; returns Safaricom's CheckoutRequestID, or throws with a message a person can act on.</summary>
    public static async Task<string> Send(HttpClient http, Push p, ILogger log)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.ConsumerKey}:{p.ConsumerSecret}"));
        using var auth = new HttpRequestMessage(HttpMethod.Get, $"{Base(p.Environment)}/oauth/v1/generate?grant_type=client_credentials");
        auth.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var authRes = await http.SendAsync(auth);
        var token = authRes.IsSuccessStatusCode ? (await authRes.Content.ReadFromJsonAsync<JsonObject>())?["access_token"]?.GetValue<string>() : null;
        if (string.IsNullOrEmpty(token)) throw new AppException("M-Pesa didn't accept the Daraja keys. Check the consumer key and secret in Settings.", 502);

        var stamp = DateTime.UtcNow.AddHours(3).ToString("yyyyMMddHHmmss");
        var body = new JsonObject
        {
            ["BusinessShortCode"] = p.Shortcode,
            ["Password"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Shortcode + p.Passkey + stamp)),
            ["Timestamp"] = stamp,
            ["TransactionType"] = p.Till ? "CustomerBuyGoodsOnline" : "CustomerPayBillOnline",
            ["Amount"] = p.Shillings,
            ["PartyA"] = p.Phone,
            ["PartyB"] = p.PartyB,
            ["PhoneNumber"] = p.Phone,
            ["CallBackURL"] = p.CallbackUrl,
            ["AccountReference"] = p.Account.Length > 12 ? p.Account[..12] : p.Account,
            ["TransactionDesc"] = p.Description.Length > 13 ? p.Description[..13] : p.Description,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(p.Environment)}/mpesa/stkpush/v1/processrequest") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        var json = await res.Content.ReadFromJsonAsync<JsonObject>();
        if (!res.IsSuccessStatusCode || json?["ResponseCode"]?.GetValue<string>() != "0")
        {
            log.LogWarning("STK push refused: {Body}", json?.ToJsonString());
            var why = json?["errorMessage"]?.GetValue<string>() ?? json?["CustomerMessage"]?.GetValue<string>();
            throw new AppException($"M-Pesa didn't send the prompt{(why is null ? "" : $": {why}")}. Check the number and the shortcode.", 502);
        }
        return json["CheckoutRequestID"]!.GetValue<string>();
    }

    /// <summary>The parts of a stkCallback: (checkout id, paid?, receipt, amount in cents, phone, what Safaricom said).</summary>
    public static (string Checkout, bool Paid, string Receipt, long Amount, string Phone, string Note) Read(JsonObject body)
    {
        var cb = body["Body"]?["stkCallback"];
        var items = cb?["CallbackMetadata"]?["Item"]?.AsArray() ?? [];
        string Item(string name) => items.FirstOrDefault(i => i?["Name"]?.GetValue<string>() == name)?["Value"]?.ToString() ?? "";
        var amount = decimal.TryParse(Item("Amount"), System.Globalization.CultureInfo.InvariantCulture, out var a) ? (long)Math.Round(a * 100) : 0;
        return (cb?["CheckoutRequestID"]?.GetValue<string>() ?? "", cb?["ResultCode"]?.GetValue<int>() == 0, Item("MpesaReceiptNumber"), amount, Item("PhoneNumber"), cb?["ResultDesc"]?.GetValue<string>() ?? "");
    }
}

public sealed class StkService(ErpDb db, PaymentService payments, SecretBox box, IHttpClientFactory http, RequestContext ctx, ILogger<StkService> log)
{
    async Task<Company> Company() => await db.Companies.SingleAsync(c => c.Id == db.CompanyId);

    public async Task<object> Setup()
    {
        var c = await Company();
        return new { ready = Ready(c), environment = c.StkEnvironment, kind = c.StkTill.Length > 0 ? "till" : "paybill", shortcode = c.StkShortcode, till = c.StkTill, hasKeys = c.StkConsumerKey.Length > 0 && c.StkPasskey.Length > 0 };
    }

    static bool Ready(Company c) => c.StkShortcode.Length > 0 && c.StkConsumerKey.Length > 0 && c.StkConsumerSecret.Length > 0 && c.StkPasskey.Length > 0;

    /// <summary>Saves the Daraja app details. Blank secrets keep the ones already saved.</summary>
    public async Task<object> Save(StkSetupInput input)
    {
        var c = await Company();
        var digits = (string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());
        c.StkEnvironment = input.Environment == "production" ? "production" : "sandbox";
        c.StkShortcode = digits(input.Shortcode);
        if (c.StkShortcode.Length is < 5 or > 7) throw new AppException("Enter the shortcode Safaricom gave you (5–7 digits): your paybill, or for a till the store number.");
        c.StkTill = input.Kind == "till" ? digits(input.Till) : "";
        if (input.Kind == "till" && c.StkTill.Length is < 5 or > 7) throw new AppException("Enter the till number customers pay to.");
        if (!string.IsNullOrWhiteSpace(input.ConsumerKey)) c.StkConsumerKey = input.ConsumerKey.Trim();
        if (!string.IsNullOrWhiteSpace(input.ConsumerSecret)) c.StkConsumerSecret = box.Seal(input.ConsumerSecret.Trim());
        if (!string.IsNullOrWhiteSpace(input.Passkey)) c.StkPasskey = box.Seal(input.Passkey.Trim());
        await db.SaveChangesAsync();
        return await Setup();
    }

    /// <summary>Sends the prompt for an invoice's balance (or part of it) to the customer's phone.</summary>
    public async Task<MpesaRequest> Request(Guid invoiceId, StkRequestInput input, string baseUrl)
    {
        var c = await Company();
        if (!Ready(c)) throw new AppException("To send M-Pesa prompts, add your Daraja app keys in Settings → Business & M-Pesa.");
        var doc = await db.SalesDocs.Include(d => d.Customer).SingleOrDefaultAsync(d => d.Id == invoiceId) ?? throw new AppException("Not found.", 404);
        if (doc.Kind != SalesDocKind.Invoice || doc.Status is not (SalesDocStatus.Sent or SalesDocStatus.PartPaid)) throw new AppException("Only an issued, unpaid invoice can be paid by prompt.");
        var balance = doc.Total - doc.Paid;
        var amount = input.Amount ?? balance;
        if (amount < 100 || amount > balance) throw new AppException($"Ask for between KSh 1 and the balance, {Money.Ksh(balance)}.");
        if (amount % 100 != 0) throw new AppException("M-Pesa takes whole shillings.");
        var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter the customer's Safaricom number (e.g. 0712 345 678).");
        var since = DateTimeOffset.UtcNow.AddMinutes(-2);
        // Compared in memory: SQLite can't order DateTimeOffset values.
        var pending = (await db.MpesaRequests.Where(r => r.SalesDocId == doc.Id && r.Status == MpesaRequestStatus.Pending).Select(r => r.CreatedAt).ToListAsync()).Any(t => t > since);
        if (pending) throw new AppException("A prompt was just sent for this invoice. Give the customer a minute to enter their PIN.", 409);

        var checkout = await Daraja.Send(http.CreateClient("daraja"), new Daraja.Push(c.StkEnvironment, c.StkConsumerKey, box.Open(c.StkConsumerSecret), c.StkShortcode, box.Open(c.StkPasskey),
            c.StkTill.Length > 0, c.StkTill.Length > 0 ? c.StkTill : c.StkShortcode, phone, amount / 100,
            $"{baseUrl.TrimEnd('/')}/api/mpesa/stk/{c.MpesaCallbackKey}", doc.Number, $"Pay {doc.Number}"), log);
        var r = new MpesaRequest { SalesDocId = doc.Id, Phone = phone, Amount = amount, CheckoutId = checkout, RequestedBy = ctx.Actor };
        db.MpesaRequests.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    public async Task<List<MpesaRequest>> ForInvoice(Guid invoiceId) =>
        (await db.MpesaRequests.Where(r => r.SalesDocId == invoiceId).ToListAsync()).OrderByDescending(r => r.CreatedAt).Take(10).ToList();

    /// <summary>Safaricom's answer. Paid: recorded against the invoice (once, even if Safaricom retries).</summary>
    public async Task Callback(Company company, JsonObject body, System.Net.IPAddress? callbackIp = null)
    {
        var (checkout, paid, receipt, amount, phone, note) = Daraja.Read(body);
        var r = await db.MpesaRequests.SingleOrDefaultAsync(x => x.CheckoutId == checkout);
        if (r is null || r.Status != MpesaRequestStatus.Pending) return;
        if (!paid)
        {
            (r.Status, r.Note) = (note.Contains("cancel", StringComparison.OrdinalIgnoreCase) ? MpesaRequestStatus.Cancelled : MpesaRequestStatus.Failed, note);
            await db.SaveChangesAsync();
            return;
        }
        var doc = await db.SalesDocs.SingleAsync(d => d.Id == r.SalesDocId);
        (r.Status, r.Receipt, r.PaidAt) = (MpesaRequestStatus.Paid, receipt, DateTimeOffset.UtcNow);
        if (!await db.Payments.AnyAsync(p => p.Reference == receipt.ToUpperInvariant()))
            await payments.Record(new PaymentInput(PaymentMethod.Mpesa, amount > 0 ? amount : r.Amount, DateTimeOffset.UtcNow, receipt, Phones.ToMsisdn(phone) ?? r.Phone, "", doc.Number, doc.CustomerId, doc.Id), "M-Pesa prompt", PaymentSource.MpesaPrompt, callbackIp);
        await db.SaveChangesAsync();
    }
}
