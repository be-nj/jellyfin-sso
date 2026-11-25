using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Where this server appears to be, seen from the browser. Jellyfin usually sits behind a
/// reverse proxy and sometimes under a sub-path, and every address the plugin hands out —
/// the redirect URI it registers, the links in the pages it renders — has to match what
/// the browser actually asked for, character for character.
///
/// The values come from request headers, which anyone can set: a request carrying
/// `X-Forwarded-Prefix: https://example.invalid` would otherwise turn a link back into
/// Jellyfin into a link to somebody else's site. So both are checked against what they
/// are allowed to look like, and anything else is dropped rather than repaired.
///
/// Worth knowing when reasoning about these: the plugin reads the forwarded headers
/// itself rather than through Jellyfin's known-proxy handling, so they are accepted from
/// whoever sent the request. What they are allowed to contain is the whole of the
/// defence, which is why that shape is narrow and why the pages escape these values again
/// for the context they land in.
/// </summary>
public static partial class ForwardedPaths
{
    /// <summary>
    /// A sub-path: slash-separated, no scheme, no host, no backslashes. The colon is left
    /// out deliberately — a path may legally contain one, and allowing it would let
    /// "https://example.invalid" through as a path once a leading slash is added.
    ///
    /// The sub-delimiters a path may legally carry are left out too, and the apostrophe is
    /// the reason: this value is also written into JavaScript string literals in the pages
    /// the plugin serves, where an apostrophe ends the string and what follows is code.
    /// The pages now escape it for that context as well, so this is the belt and that is
    /// the braces. A server published under a path that needs one of those characters can
    /// percent-encode it, which the percent sign still allows.
    ///
    /// No empty segment anywhere. At the start "//" would make the rest a host; in the
    /// middle it is only never a path a server is published under, and the rule used to
    /// stop at the start while its test asserted the whole (#296).
    /// </summary>
    [GeneratedRegex(@"^(?!.*//)/[A-Za-z0-9._~%/-]*\z")]
    private static partial Regex SafePrefix { get; }

    /// <summary>
    /// A host, with an optional port — or an IPv6 address in brackets. Anchored with \z and not
    /// $, which in .NET also matches before a final line feed (#327).
    /// </summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9._-]+|\[[0-9A-Fa-f:.]+\])(?::[0-9]{1,5})?\z")]
    private static partial Regex SafeHost { get; }

    /// <summary>
    /// The sub-path, normalised to either "" or "/something" without a trailing slash.
    /// A proxy's X-Forwarded-Prefix wins over Jellyfin's own base path, because only the
    /// proxy knows a prefix it strips before forwarding — but only if it is a path.
    /// </summary>
    public static string Prefix(string? forwardedPrefix, string? pathBase)
    {
        var candidate = Clean(forwardedPrefix);
        return candidate.Length > 0 ? candidate : Clean(pathBase);
    }

    /// <summary>The externally visible origin plus sub-path, with no trailing slash.</summary>
    public static string BaseUri(string? scheme, string? host, string prefix)
        => $"{scheme}://{host}{prefix}";

    /// <summary>
    /// The host to build addresses from: the forwarded one when it looks like a host,
    /// otherwise the one the request arrived on.
    /// </summary>
    public static string Host(string? forwardedHost, string requestHost)
        => forwardedHost is not null && SafeHost.IsMatch(forwardedHost) && IsHostAndPort(forwardedHost)
            ? forwardedHost
            : requestHost;

    /// <summary>
    /// Whether a host with an optional port is one System.Uri recognises as a name or an address,
    /// and the port, when there is one, is a port: 1 to 65535. The pattern above only says what
    /// characters it may hold; ".", "a..b" and "host:99999" pass it and are not addresses.
    /// </summary>
    internal static bool IsHostAndPort(string authority)
    {
        string host = authority;
        string? port = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = authority[..(close + 1)];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                port = rest[1..];
            }
        }
        else if (authority.LastIndexOf(':') is var colon and >= 0)
        {
            host = authority[..colon];
            port = authority[(colon + 1)..];
        }

        if (port is not null
            && (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number is < 1 or > 65535))
        {
            return false;
        }

        return Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    /// <summary>
    /// The scheme to build addresses from. Only the two the web speaks; anything else is
    /// somebody trying to steer a link somewhere else.
    /// </summary>
    public static string Scheme(string? forwardedScheme, string requestScheme)
        => forwardedScheme is "http" or "https" ? forwardedScheme : requestScheme;

    private static string Clean(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        if (!raw.StartsWith('/'))
        {
            raw = "/" + raw;
        }

        raw = raw.TrimEnd('/');
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        return SafePrefix.IsMatch(raw) ? raw : string.Empty;
    }
}
