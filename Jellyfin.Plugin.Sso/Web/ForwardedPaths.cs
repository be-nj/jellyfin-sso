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
    /// </summary>
    [GeneratedRegex(@"^/(?!/)[A-Za-z0-9._~%/-]*$")]
    private static partial Regex SafePrefix { get; }

    /// <summary>A host, with an optional port — or an IPv6 address in brackets.</summary>
    [GeneratedRegex(@"^(?:[A-Za-z0-9._-]+|\[[0-9A-Fa-f:.]+\])(?::[0-9]{1,5})?$")]
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
        => forwardedHost is not null && SafeHost.IsMatch(forwardedHost) ? forwardedHost : requestHost;

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
