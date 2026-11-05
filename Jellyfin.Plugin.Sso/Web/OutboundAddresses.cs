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
    public static async Task<bool> AllowedAsync(
        Uri target,
        Uri? authority,
        bool allowPrivateNetworks,
        CancellationToken ct = default)
    {
        if (authority is not null
            && string.Equals(target.Host, authority.Host, StringComparison.OrdinalIgnoreCase))
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
            return true;
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
