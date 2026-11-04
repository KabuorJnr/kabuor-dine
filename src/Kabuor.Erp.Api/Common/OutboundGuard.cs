// Webhook addresses are typed in by a business, and the server then calls them. Left open, that lets anyone with a
// Kabuor account make this server knock on its own private network (the database, other services, the cloud's
// metadata service) and read back which doors answer. Checking the typed text isn't enough: a public-looking name
// can resolve to 10.0.0.5, or switch to it after the first look. So the rule is applied to the address the
// connection actually goes to, every time, and redirects aren't followed.
using System.Net;
using System.Net.Sockets;

namespace Kabuor.Erp.Common;

public static class OutboundGuard
{
    /// <summary>True for an address on the public internet; false for this machine, private and carrier networks,
    /// link-local (cloud metadata), multicast and reserved ranges, in IPv4 and IPv6.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT, also used inside cloud networks
                || (b[0] == 169 && b[1] == 254)                  // link-local: 169.254.169.254 and 169.254.170.2 metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)     // includes Docker's 172.17.0.0/16
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224);                                 // multicast, reserved, broadcast
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b[0] & 0xfe) == 0xfc                          // unique local, fc00::/7
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)); // documentation, 2001:db8::/32
        return false;
    }

    /// <summary>Why a typed webhook address can't be used, or null when it can. Names are checked again, after DNS,
    /// each time something is sent.</summary>
    public static string? Problem(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return "Enter the full https:// address that should receive events.";
        if (!uri.IsDefaultPort && uri.Port is not (443 or 8443)) return "Use the normal https port (443) for webhooks.";
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) ? !IsPublic(ip)
            : host is "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal") || !host.Contains('.'))
            return "Webhook URL cannot target private or internal network addresses.";
        return null;
    }

    /// <summary>The connection for sending webhooks: resolves the name itself, refuses if any answer is internal,
    /// then connects to exactly the address it checked. No redirects.</summary>
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancel) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancel);
            if (addresses.Length == 0 || !addresses.All(IsPublic))
                throw new HttpRequestException("That address points inside a private network, so nothing was sent.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancel);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
