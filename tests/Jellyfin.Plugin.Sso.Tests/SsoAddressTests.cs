using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Every address the plugin hands out — the redirect URI it registers with a provider, the
/// post-logout address, the links in the pages it serves — is built from the headers a
/// reverse proxy sets. Getting one of them wrong does not degrade the login, it ends it:
/// a provider refuses a redirect URI it does not have on file.
/// </summary>
public sealed class SsoAddressTests
{
    private static SsoAddress From(
        string? prefix = null, string? proto = null, string? host = null, string pathBase = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("jellyfin.internal:8096");
        http.Request.PathBase = pathBase;

        if (prefix is not null)
            http.Request.Headers["X-Forwarded-Prefix"] = prefix;
        if (proto is not null)
            http.Request.Headers["X-Forwarded-Proto"] = proto;
        if (host is not null)
            http.Request.Headers["X-Forwarded-Host"] = host;

        return SsoAddress.For(http.Request);
    }

    [Fact]
    public void Without_a_proxy_the_request_itself_says_where_the_server_is()
    {
        var at = From();

        Assert.Equal(string.Empty, at.BasePath);
        Assert.Equal("http://jellyfin.internal:8096", at.BaseUri);
        Assert.False(at.Secure);
    }

    [Fact]
    public void A_proxy_that_terminates_TLS_makes_the_address_secure()
    {
        var at = From(proto: "https", host: "jf.example.com");

        Assert.Equal("https://jf.example.com", at.BaseUri);
        Assert.True(at.Secure);
    }

    [Fact]
    public void The_sub_path_is_carried_into_the_callback_the_provider_is_given()
    {
        var at = From(prefix: "/jellyfin", proto: "https", host: "jf.example.com");

        Assert.Equal("/jellyfin", at.BasePath);
        Assert.Equal(
            "https://jf.example.com/jellyfin/sso/oidc/callback/authentik",
            at.CallbackUri("authentik"));
    }

    /// <summary>
    /// The trap this type exists for: the external address already ends with the sub-path,
    /// so the two web-client addresses cannot be built from one another. A server under
    /// /jellyfin that published /jellyfin/jellyfin/web/… asked the provider for a redirect
    /// it does not have on file, and Keycloak and Authentik abort the logout over it.
    /// </summary>
    [Fact]
    public void The_sub_path_is_published_once_in_each_web_client_address()
    {
        var at = From(prefix: "/jellyfin", proto: "https", host: "jf.example.com");

        Assert.Equal("/jellyfin/web/index.html", at.LocalWebClient);
        Assert.Equal("https://jf.example.com/jellyfin/web/index.html", at.ExternalWebClient);
    }

    [Fact]
    public void Jellyfins_own_base_url_setting_counts_as_the_sub_path()
    {
        var at = From(pathBase: "/media");

        Assert.Equal("/media", at.BasePath);
        Assert.Equal("http://jellyfin.internal:8096/media", at.BaseUri);
    }
}
