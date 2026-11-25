using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// Where this server is, as the browser making the current request sees it.
///
/// Three answers are needed all over the endpoints and all three come from the same
/// headers, so they are read once per request and passed around together: the sub-path
/// every link in a served page has to carry, the external base address a redirect URI is
/// built from, and whether the browser reached the server over HTTPS — which decides
/// what a cookie may be called.
/// </summary>
internal sealed class SsoAddress
{
    private SsoAddress(string basePath, string baseUri)
    {
        BasePath = basePath;
        BaseUri = baseUri;
        RequestBasePath = basePath;
    }

    private SsoAddress(SsoAddress configured, SsoAddress seen)
    {
        BasePath = configured.BasePath;
        BaseUri = configured.BaseUri;
        RequestBasePath = seen.BasePath;
        Elsewhere = !SameAddress(configured.BaseUri, seen.BaseUri);
    }

    /// <summary>
    /// The sub-path of the request itself, for what is fetched from the page the request
    /// serves: the login button's script and its links. With a public address configured
    /// this can differ from <see cref="BasePath"/> — the server opened at its LAN address,
    /// at the root, while the public one is under /jellyfin — and a script tag carrying
    /// the public prefix would then point at nothing.
    /// </summary>
    internal string RequestBasePath { get; }

    /// <summary>
    /// Whether a public address is configured and this request arrived somewhere else.
    /// A login cannot be finished from there: the provider sends the browser back to the
    /// public address, and a cookie set here does not travel with it. So a flow that
    /// starts elsewhere is sent to the public address before it starts; see
    /// <see cref="PublicAddressAttribute"/>.
    /// </summary>
    internal bool Elsewhere { get; }

    /// <summary>
    /// Whether the configuration holds a public address that is not one. It is not
    /// ignored: somebody set it so that the headers would not be trusted, and falling
    /// back to them without a word undoes that. A flow refuses to start instead.
    /// </summary>
    internal bool PublicAddressInvalid { get; private init; }

    /// <summary>
    /// The sub-path this server is published under, "" when it is at the root. Every link
    /// and fetch in the served pages is absolute, so each one has to carry it.
    /// </summary>
    internal string BasePath { get; }

    /// <summary>The server's externally visible base address, honouring proxy headers.</summary>
    internal string BaseUri { get; }

    /// <summary>
    /// Whether this request reached the server over HTTPS, as the browser sees it. Taken
    /// from the address the browser used rather than from the connection, because the
    /// deployment this is written for terminates TLS at a proxy.
    /// </summary>
    internal bool Secure => BaseUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static SsoAddress For(HttpRequest request)
        => For(request, Plugin.Instance?.Configuration.PublicAddress);

    /// <summary>
    /// The configured public address when there is one. The redirect URI, the cookies and
    /// the links in the pages then come from it alone, so a request cannot move the
    /// redirect URI to a host of its choosing. The request's own headers are still read,
    /// for two things that cannot leave this server: the prefix of what the page it
    /// serves fetches, and whether the browser has to be sent to the public address first.
    /// </summary>
    internal static SsoAddress For(HttpRequest request, string? publicAddress)
    {
        var seen = Seen(request);
        if (string.IsNullOrWhiteSpace(publicAddress))
        {
            return seen;
        }

        return Configured(publicAddress) is { } configured
            ? new SsoAddress(configured, seen)
            : new SsoAddress(seen.BasePath, seen.BaseUri) { PublicAddressInvalid = true };
    }

    /// <summary>Where the server is according to the request and its forwarded headers.</summary>
    private static SsoAddress Seen(HttpRequest request)
    {
        var basePath = ForwardedPaths.Prefix(
            request.Headers["X-Forwarded-Prefix"].FirstOrDefault(),
            request.PathBase.Value);

        var scheme = ForwardedPaths.Scheme(
            request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                ?? request.Headers["X-Forwarded-Scheme"].FirstOrDefault(),
            request.Scheme);

        var host = ForwardedPaths.Host(
            request.Headers["X-Forwarded-Host"].FirstOrDefault(), request.Host.ToString());

        // A server published under a sub-path ("BaseUrl" in Jellyfin's network settings,
        // or a proxy that keeps the prefix) must keep that prefix in its redirect URI,
        // or the provider sends the browser to a path this server does not serve.
        return new SsoAddress(basePath, ForwardedPaths.BaseUri(scheme, host, basePath));
    }

    /// <summary>A public address as an address, or null when it is empty or not one.</summary>
    internal static SsoAddress? Configured(string? address)
    {
        // Never throws: whatever is typed or stored, the answer is an address or null (#327).
        // System.Uri reads some hosts it then cannot convert, so the conversion is guarded too.
        try
        {
            return ConfiguredOrThrow(address);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static SsoAddress? ConfiguredOrThrow(string? address)
    {
        // Characters that draw nothing or are not text (a right-to-left override, a byte-order
        // mark, a zero-width space) are dropped by Uri from a host, so the address would read
        // back as another one than it was typed.
        if (address is null
            || address.Any(c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Control && !char.IsWhiteSpace(c)))
        {
            return null;
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0
            || uri.Port is < 1 or > 65535
            || Uri.CheckHostName(uri.Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var basePath = ForwardedPaths.Prefix(path, null);
        if (basePath != path)
        {
            return null;
        }

        // The host in the form a provider has it registered in: punycode for a name with
        // letters outside ASCII, which Authority would hand back as typed. IdnHost drops
        // the brackets of an IPv6 literal, so that one keeps Host.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        if (!ForwardedPaths.IsHostAndPort(authority))
        {
            return null;
        }

        return new SsoAddress(basePath, ForwardedPaths.BaseUri(uri.Scheme, authority, basePath));
    }

    /// <summary>
    /// Whether two base addresses are the same place: scheme, host and port without
    /// regard to case and with the default port spelled or not, the path as written.
    /// </summary>
    private static bool SameAddress(string one, string other)
        => Uri.TryCreate(one, UriKind.Absolute, out var a)
            && Uri.TryCreate(other, UriKind.Absolute, out var b)
            && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Where the provider is told to send the browser back to.</summary>
    internal string CallbackUri(string providerId) => $"{BaseUri}/sso/oidc/callback/{providerId}";

    /// <summary>
    /// The Jellyfin web client as a path on this server, for a redirect that has nowhere
    /// better to go. Local by construction: <see cref="BasePath"/> is either empty or a
    /// validated path, so this is a path and never an address somewhere else, which is
    /// what <c>LocalRedirect</c> insists on.
    /// </summary>
    internal string LocalWebClient => $"{BasePath}/web/index.html";

    /// <summary>
    /// The same page as an address another server can send a browser to, for a
    /// <c>post_logout_redirect_uri</c>.
    ///
    /// Not <see cref="BaseUri"/> plus <see cref="LocalWebClient"/>: that published the
    /// sub-path twice, so a server under /jellyfin asked the provider to return the
    /// browser to /jellyfin/jellyfin/web/..., which is not the registered redirect.
    /// Keycloak and Authentik refuse an unregistered one and abort the logout, leaving the
    /// user on a provider error page with their provider session alive — and the retained
    /// token has already been spent by then, so trying again does not help.
    /// </summary>
    internal string ExternalWebClient => $"{BaseUri}/web/index.html";
}
