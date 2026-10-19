namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Where this server appears to be, seen from the browser. Jellyfin usually sits behind a
/// reverse proxy and sometimes under a sub-path, and every address the plugin hands out —
/// the redirect URI it registers, the links in the pages it renders — has to match what
/// the browser actually asked for, character for character.
/// </summary>
public static class ForwardedPaths
{
    /// <summary>
    /// The sub-path, normalised to either "" or "/something" without a trailing slash.
    /// A proxy's X-Forwarded-Prefix wins over Jellyfin's own base path, because only the
    /// proxy knows a prefix it strips before forwarding.
    /// </summary>
    public static string Prefix(string? forwardedPrefix, string? pathBase)
    {
        var raw = (forwardedPrefix ?? pathBase ?? string.Empty).Trim().TrimEnd('/');

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        return raw.StartsWith('/') ? raw : "/" + raw;
    }

    /// <summary>The externally visible origin plus sub-path, with no trailing slash.</summary>
    public static string BaseUri(string? scheme, string? host, string prefix)
        => $"{scheme}://{host}{prefix}";
}
