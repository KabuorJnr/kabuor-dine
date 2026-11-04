// Is this request really from Safaricom? M-Pesa confirmations and prompt results arrive at a web address that carries
// the business's secret key, and the owner can see that address (they paste it into Safaricom's portal), so the key
// alone can't prove a payment is real: a business could post its own "confirmations" to inflate its figures. A payment
// counts as confirmed by Safaricom only when the callback came from one of Safaricom's published callback addresses.
// Behind the host's proxy, RemoteIpAddress is the real caller (Program.cs trusts only private-range proxies).
// The list is configuration (Mpesa:CallbackIps, comma-separated addresses or CIDR ranges); the default is Safaricom's
// published Daraja callback addresses. Check it against Safaricom's documentation when they announce changes.
using System.Net;

namespace Kabuor.Erp.Payments;

public static class SafaricomCallers
{
    public const string DefaultList =
        "196.201.214.200,196.201.214.206,196.201.213.114,196.201.214.207,196.201.214.208,196.201.213.44," +
        "196.201.212.127,196.201.212.138,196.201.212.129,196.201.212.136,196.201.212.74,196.201.212.69";

    public static bool IsSafaricom(IPAddress? ip, IConfiguration config) => IsIn(ip, config["Mpesa:CallbackIps"] ?? DefaultList);

    public static bool IsIn(IPAddress? ip, string list)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/') ? IPNetwork.TryParse(entry, out var net) && net.Contains(ip) : IPAddress.TryParse(entry, out var one) && one.Equals(ip))
                return true;
        }
        return false;
    }
}
