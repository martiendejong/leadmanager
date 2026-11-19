using System.Net;
using System.Net.Sockets;

namespace LeadManager.Api.Services.Hunt;

/// <summary>SSRF guard: the hunt only ever fetches public websites, never internal addresses.</summary>
public static class UrlGuard
{
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 10) return false;                                  // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return false;                  // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return false;                  // link local, cloud metadata
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;    // carrier grade NAT
            if (b[0] == 0 || b[0] >= 224) return false;                    // this network, multicast, reserved
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;       // IETF protocol assignments
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return false;   // benchmarking
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                       // unique local fc00::/7
            if (b.Take(12).All(x => x == 0)) return false;                 // IPv4 compatible ::a.b.c.d (and ::)
            if (b.Take(8).All(x => x == 0) && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0) return false; // IPv4 translated ::ffff:0:a.b.c.d
            if (b[0] == 0x20 && b[1] == 0x02) return false;                // 6to4 2002::/16 embeds an IPv4 address
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return false; // NAT64 64:ff9b::/32 (covers /96 and the local-use /48)
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // Teredo 2001::/32
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // documentation 2001:db8::/32
            if (b[0] == 0x01 && b[1] == 0x00 && b.Skip(2).Take(6).All(x => x == 0)) return false; // discard-only 100::/64
            return true;
        }
        return false;
    }

    /// <summary>Syntax-level check before any network call: absolute http or https, no credentials, no IP literal in a private range.</summary>
    public static bool TryNormalize(string? raw, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (!raw.Contains("://")) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) || !IsPublicAddress(ip)) return false;
        }
        var host = u.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || (u.HostNameType == UriHostNameType.Dns && !host.Contains('.'))) return false;
        uri = u;
        return true;
    }

    /// <summary>
    /// The registrable part of a host: the last two labels, or three for second level suffixes such as co.uk. A redirect that leaves this
    /// domain is not followed, so facts are never attributed to a business from another site.
    /// </summary>
    public static string RegistrableDomain(string host)
    {
        var labels = host.ToLowerInvariant().Trim('.').Split('.');
        if (labels.Length <= 2) return string.Join('.', labels);
        var secondLevel = new[] { "co", "com", "org", "net", "gov", "ac", "edu" };
        var take = labels[^1].Length == 2 && secondLevel.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels.Skip(labels.Length - take));
    }

    public static string DomainOf(string? url)
    {
        if (!TryNormalize(url, out var u)) return "";
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }
}
