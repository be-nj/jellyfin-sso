using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Which addresses this plugin is willing to fetch from when somebody else chose the
/// address.
///
/// Two of the plugin's fetches take their target from a value the plugin did not write:
/// a provider's branding document names its logo, and a token's `picture` claim names an
/// avatar. On many identity providers that claim is an attribute the user can edit. A
/// request the server makes on their behalf reaches whatever the server can reach — the
/// container network, the host's own loopback, a link-local metadata service — and
/// because the bytes come back out again, as a login button anybody can load or as a
/// profile picture the user can download, it is a read of that network and not a blind
/// poke at it.
///
/// So an address that somebody else chose has to be one of two things: the provider's own
/// host, which the operator configured and already trusts, or an ordinary public address.
/// Everything else is refused unless the operator says otherwise, because a provider that
/// serves its pictures from a second machine on the LAN is a real setup and this cannot
/// tell that apart from a pivot.
/// </summary>
public static class OutboundAddresses
{
    /// <summary>
    /// Whether <paramref name="target"/> may be fetched. <paramref name="authority"/> is
    /// the provider's own address, whose host is allowed wherever it points.
    /// </summary>
    /// <summary>
    /// Carried on each request so the connect callback can judge the address it is about
    /// to use, rather than trusting a name lookup that happened a moment earlier.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<(Uri? Authority, bool AllowPrivate)> Rules =
        new("Jellyfin.Plugin.Sso.OutboundRules");

    /// <summary>
    /// Whether this address may be connected to. Called at connect time with the address
    /// the socket is about to use, which is what closes the gap between a name lookup and
    /// the connection it was supposed to authorise: a name with a short time-to-live can
    /// answer publicly to the check and privately to the connect.
    /// </summary>
    internal static bool AddressAllowed(
        IPAddress address, string host, Uri? authority, bool allowPrivateNetworks)
    {
        if (authority is not null
            && string.Equals(host, authority.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowPrivateNetworks || IsPublic(address);
    }

    public static async Task<bool> AllowedAsync(
        Uri target,
        Uri? authority,
        bool allowPrivateNetworks,
        CancellationToken ct = default)
    {
        // The provider's own address, and not merely its host: an operator trusts a
        // service, not every port on a machine. Comparing the host alone let a
        // user-editable picture claim of http://auth.home.lan:9200/_cat/indices through
        // on the one host nobody expected to reach this way.
        if (authority is not null
            && string.Equals(target.Host, authority.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Scheme, authority.Scheme, StringComparison.OrdinalIgnoreCase)
            && target.Port == authority.Port)
        {
            return true;
        }

        if (allowPrivateNetworks)
        {
            return true;
        }

        var addresses = await ResolveAsync(target.Host, ct).ConfigureAwait(false);

        // Nothing resolved: refuse. A name that does not resolve cannot be fetched anyway,
        // and answering "allowed" for it would mean the one case this is here to stop —
        // a name that resolves differently a moment later — got a free pass.
        return addresses.Count > 0 && addresses.TrueForAll(IsPublic);
    }

    /// <summary>
    /// An IPv6 address out on the internet. The five framework properties are not enough:
    /// a dual-stack network gives LAN hosts *global* unicast addresses — that is how SLAAC
    /// normally works — so "not loopback, not link-local, not unique-local" called the
    /// router's admin page and the neighbouring container public. Only the global unicast
    /// range is public, and the embedded-IPv4 forms are judged by the address they carry.
    /// </summary>
    private static bool IsPublicV6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        // 64:ff9b::/96 (NAT64) and 64:ff9b:1::/48 (local-use NAT64) carry an IPv4 address
        // in their low bits, and that address is the one that decides.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
        {
            return IsPublic(new IPAddress(bytes[12..16]));
        }

        // ::ffff:0:0/96 is handled before this by the caller's MapToIPv4; ::<v4> is the
        // deprecated IPv4-compatible form and reaches here still as IPv6.
        if (bytes[..12].All(b => b == 0) && !(bytes[12] == 0 && bytes[13] == 0))
        {
            return IsPublic(new IPAddress(bytes[12..16]));
        }

        // 2000::/3 is the global unicast range. Everything outside it — the unassigned
        // space, 100::/64 discard-only, the ORCHID ranges, documentation 2001:db8::/32 —
        // is not somewhere a provider's avatar legitimately lives.
        if ((bytes[0] & 0xE0) != 0x20)
        {
            return false;
        }

        // 2001:db8::/32 and 3fff::/20 are both documentation space, and both sit inside
        // 2000::/3, so the range check above does not catch them.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
        {
            return false;
        }

        if (bytes[0] == 0x3f && (bytes[1] & 0xf0) == 0xf0)
        {
            return false;
        }

        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return IsPublic(new IPAddress(bytes[2..6]));
        }

        return true;
    }

    /// <summary>Every address a host name stands for, or the literal it already is.</summary>
    private static async Task<List<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            return [.. await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// An address out on the internet rather than one that only means something from
    /// inside this host or this network.
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.IsIPv6UniqueLocal
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return IsPublicV6(address);
        }

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            // RFC 1918 private use, and 0.0.0.0/8 "this network".
            0 or 10 => false,
            // Carrier-grade NAT, and 127/8 is already covered by IsLoopback.
            100 => octets[1] < 64 || octets[1] > 127,
            127 => false,
            // Link-local, including the 169.254.169.254 metadata address.
            169 => octets[1] != 254,
            172 => octets[1] < 16 || octets[1] > 31,
            192 => !(octets[1] == 168 || (octets[1] == 0 && octets[2] == 0)),
            // Benchmarking, and the 203.0.113.0/24 documentation range is public enough.
            198 => octets[1] != 18 && octets[1] != 19,
            // Multicast and reserved.
            >= 224 => false,
            _ => true,
        };
    }
}
