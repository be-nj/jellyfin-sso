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
    }

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
