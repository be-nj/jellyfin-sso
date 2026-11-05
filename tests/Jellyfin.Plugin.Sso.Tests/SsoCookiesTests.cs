using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The two cookies the login flow sets are handles, not credentials, but the rules they
/// are set under are what keeps a neighbouring host from planting either of them. The
/// __Host- prefix is the whole of that defence, and a cookie read under the wrong name
/// gives it away, so both are asserted here rather than left to an end-to-end login that
/// only ever runs on one scheme.
/// </summary>
public sealed class SsoCookiesTests
{
    private static (SsoCookies Cookies, HttpContext Http) For(bool secure, string prefix = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("jf.example.com");
        if (secure)
            http.Request.Headers["X-Forwarded-Proto"] = "https";
        if (prefix.Length > 0)
            http.Request.Headers["X-Forwarded-Prefix"] = prefix;

        var at = SsoAddress.For(http.Request);
        return (new SsoCookies(http.Request, http.Response, at), http);
    }

    [Fact]
    public void Over_https_the_name_carries_the_host_prefix()
    {
        Assert.Equal("__Host-SsoPendingLogin", SsoCookies.NameUnder(SsoCookies.PendingLogin, true));
    }

    [Fact]
    public void Over_plain_http_the_prefix_is_not_allowed_so_the_bare_name_is_used()
    {
        Assert.Equal("SsoPendingLogin", SsoCookies.NameUnder(SsoCookies.PendingLogin, false));
    }

    /// <summary>
    /// A cookie planted under the bare name by a sibling subdomain must not be read on a
    /// deployment that sets the prefixed one. Reading both names was the bug this holds
    /// shut: a planted pending cookie carrying the attacker's browser id would have let
    /// them finish their login in somebody else's browser.
    /// </summary>
    [Fact]
    public void Only_the_name_this_deployment_sets_is_read()
    {
        var (cookies, http) = For(secure: true);
        http.Request.Headers.Cookie = $"{SsoCookies.PendingLogin}=planted";

        Assert.Null(cookies.Value(SsoCookies.PendingLogin));
    }

    [Fact]
    public void The_cookie_this_deployment_sets_is_read()
    {
        var (cookies, http) = For(secure: true);
        http.Request.Headers.Cookie = $"__Host-{SsoCookies.PendingLogin}=mine";

        Assert.Equal("mine", cookies.Value(SsoCookies.PendingLogin));
    }

    [Fact]
    public void A_pending_login_cookie_survives_the_round_trip_through_the_provider()
    {
        var options = SsoCookies.PendingOptions(secure: true, basePath: "/jellyfin");

        // Lax, not Strict: the provider redirects the browser back here from its own site,
        // and Strict would withhold the cookie on exactly that navigation.
        Assert.Equal(SameSiteMode.Lax, options.SameSite);
        Assert.True(options.HttpOnly);
        Assert.True(options.Secure);
        Assert.Equal(TimeSpan.FromMinutes(15), options.MaxAge);
        // __Host- requires the root path, whatever sub-path the server is published under.
        Assert.Equal("/", options.Path);
    }

    [Fact]
    public void Without_the_prefix_the_path_is_what_limits_where_a_cookie_goes()
    {
        Assert.Equal(
            "/jellyfin/sso",
            SsoCookies.PendingOptions(secure: false, basePath: "/jellyfin").Path);
        Assert.Equal(
            "/jellyfin/sso/oidc",
            SsoCookies.LogoutOptions(secure: false, basePath: "/jellyfin").Path);
    }

    /// <summary>
    /// No MaxAge: the retained logout handle dies with the browser session, so closing the
    /// browser is itself a way to stop holding somebody's ID token.
    /// </summary>
    [Fact]
    public void The_logout_handle_lasts_as_long_as_the_browser_is_open()
    {
        var options = SsoCookies.LogoutOptions(secure: true, basePath: string.Empty);

        Assert.Null(options.MaxAge);
        Assert.Null(options.Expires);
        Assert.True(options.HttpOnly);
    }

    [Fact]
    public void Remembering_and_forgetting_use_the_same_name()
    {
        var (cookies, http) = For(secure: true);

        cookies.RememberLogout("handle");
        cookies.RememberPendingLogin("browser");

        var set = http.Response.Headers.SetCookie.ToString();
        Assert.Contains($"__Host-{LogoutSessionStore.CookieName}=handle", set);
        Assert.Contains($"__Host-{SsoCookies.PendingLogin}=browser", set);
    }
}
