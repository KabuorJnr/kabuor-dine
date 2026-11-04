// The Daraja app an M-Pesa prompt is sent with. A restaurant normally uses its own (Settings → Business & M-Pesa,
// sealed at rest). The Kabuor Dine demo restaurant uses the platform's, so someone trying the demo gets the real
// prompt on their phone: those keys come from the host's environment under the names Kabuor Builds uses
// (PLATFORM_MPESA_* first, then MPESA_*), so the same values work for both. They're never stored in the database
// or the code, and only the demo restaurant can use them.
namespace Kabuor.Erp.Payments;

public sealed record DarajaKeys(string Environment, string ConsumerKey, string ConsumerSecret, string Shortcode, string Passkey, string Till)
{
    /// <summary>A Buy Goods till (the shortcode is then the store number) rather than a paybill.</summary>
    public bool IsTill => Till.Length > 0;

    /// <summary>The number a customer pays to: the till, or the paybill.</summary>
    public string PayTo => Till.Length > 0 ? Till : Shortcode;

    /// <summary>The platform's own Daraja app, or null when the host hasn't set all of its keys.</summary>
    public static DarajaKeys? Platform(IConfiguration config)
    {
        string Value(params string[] keys)
        {
            foreach (var key in keys)
                if ((config[key] ?? "").Trim() is { Length: > 0 } value) return value;
            return "";
        }
        var consumerKey = Value("PLATFORM_MPESA_CONSUMER_KEY", "MPESA_CONSUMER_KEY");
        var consumerSecret = Value("PLATFORM_MPESA_CONSUMER_SECRET", "MPESA_CONSUMER_SECRET");
        var shortcode = Value("PLATFORM_MPESA_SHORTCODE", "MPESA_STORE_NUMBER", "MPESA_SHORTCODE");
        var passkey = Value("PLATFORM_MPESA_PASSKEY", "MPESA_PASSKEY");
        if (consumerKey.Length == 0 || consumerSecret.Length == 0 || shortcode.Length == 0 || passkey.Length == 0) return null;
        var environment = Value("PLATFORM_MPESA_ENV", "MPESA_ENVIRONMENT") == "production" ? "production" : "sandbox";
        return new DarajaKeys(environment, consumerKey, consumerSecret, shortcode, passkey, Value("PLATFORM_MPESA_TILL", "MPESA_TILL_NUMBER"));
    }
}
