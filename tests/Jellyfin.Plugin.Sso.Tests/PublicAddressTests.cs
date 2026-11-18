using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A configured public address: what is built from it, what the request still decides,
/// and where a request that arrived somewhere else is sent.
/// </summary>
public sealed class PublicAddressTests
{
    private const string Public = "https://jellyfin.example.com/jellyfin";

    private static HttpRequest Request(
        string host, string scheme = "https", string? prefix = null, string path = "/sso/oidc/start",
        string query = "", string method = "GET")
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        http.Request.QueryString = new QueryString(query);
        if (prefix is not null)
        {
            http.Request.Headers["X-Forwarded-Prefix"] = prefix;
        }

        return http.Request;
    }

    // ─── Whether the setting is read at all ───────────────────────────────────

    /// <summary>
    /// Through the plugin's own configuration, which is the only way the endpoints ask.
    /// Every other test here hands the address in, and passed with the setting ignored.
    /// </summary>
    [Fact]
    public void The_address_saved_in_the_configuration_is_the_one_used()
    {
        using var harness = new PluginHarness();
        harness.Configuration.PublicAddress = Public;
        var request = Request("attacker.example");
        request.Headers["X-Forwarded-Host"] = "attacker.example";

        try
        {
            var at = SsoAddress.For(request);

            Assert.Equal($"{Public}/sso/oidc/callback/authentik", at.CallbackUri("authentik"));
        }
        finally
        {
            // The plugin instance outlives the harness, and every other test that asks
            // where the server is would be answered with this address.
            harness.Configuration.PublicAddress = string.Empty;
        }
    }

    // ─── The address itself ───────────────────────────────────────────────────

    [Theory]
    [InlineData("https://jellyfin.example.com", "https://jellyfin.example.com")]
    [InlineData("  https://Jellyfin.Example.com/jf/  ", "https://jellyfin.example.com/jf")]
    [InlineData("https://jellyfin.example.com:443", "https://jellyfin.example.com")]
    [InlineData("https://jellyfin.example.com//", "https://jellyfin.example.com")]
    [InlineData("https://jellyfin.example.com:8920", "https://jellyfin.example.com:8920")]
    [InlineData("http://192.168.1.10:8096", "http://192.168.1.10:8096")]
    [InlineData("http://[fd00::10]:8096/jf", "http://[fd00::10]:8096/jf")]
    // A provider has the punycode form on file; the name as typed would be refused there.
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    public void An_address_is_kept_in_the_form_a_provider_is_sent(string typed, string used)
        => Assert.Equal(used, SsoAddress.Configured(typed)!.BaseUri);

    [Theory]
    [InlineData("https://jellyfin.example.com/#top")]
    [InlineData("https://jellyfin.example.com/a//b")]
    [InlineData("//jellyfin.example.com")]
    [InlineData("javascript:alert(1)")]
    public void More_that_is_no_public_address(string address)
        => Assert.Null(SsoAddress.Configured(address));

    [Fact]
    public void A_plain_http_address_is_not_called_secure()
        => Assert.False(SsoAddress.Configured("http://192.168.1.10:8096")!.Secure);

    // ─── What the request still decides ───────────────────────────────────────

    /// <summary>
    /// The login button's script is fetched from the page the request serves. Opened at
    /// the LAN address, at the root, it has to be asked for at the root, although the
    /// public address is under a sub-path.
    /// </summary>
    [Fact]
    public void What_a_page_fetches_keeps_the_prefix_of_the_request_that_served_it()
    {
        var lan = SsoAddress.For(Request("192.168.1.10:8096", "http"), Public);
        Assert.Equal(string.Empty, lan.RequestBasePath);
        Assert.Equal("/jellyfin", lan.BasePath);

        var proxied = SsoAddress.For(Request("jellyfin.example.com", prefix: "/jellyfin"), Public);
        Assert.Equal("/jellyfin", proxied.RequestBasePath);
    }

    [Theory]
    [InlineData("jellyfin.example.com", "https", "/jellyfin", false)]
    [InlineData("JELLYFIN.example.com:443", "https", "/jellyfin", false)]
    [InlineData("192.168.1.10:8096", "http", null, true)]
    [InlineData("jellyfin.example.com", "http", "/jellyfin", true)]
    [InlineData("jellyfin.example.com", "https", null, true)]
    [InlineData("jellyfin.example.com:8920", "https", "/jellyfin", true)]
    [InlineData("other.example.com", "https", "/jellyfin", true)]
    public void A_request_is_elsewhere_unless_scheme_host_port_and_prefix_are_the_public_ones(
        string host, string scheme, string? prefix, bool elsewhere)
        => Assert.Equal(elsewhere, SsoAddress.For(Request(host, scheme, prefix), Public).Elsewhere);

    [Fact]
    public void Without_a_public_address_no_request_is_elsewhere()
        => Assert.False(SsoAddress.For(Request("192.168.1.10:8096", "http"), string.Empty).Elsewhere);

    // ─── Where a request that arrived elsewhere is sent ───────────────────────

    [Fact]
    public void A_start_elsewhere_is_sent_to_the_public_address_with_its_path_and_query()
    {
        var request = Request(
            "192.168.1.10:8096", "http", path: "/sso/oidc/start/authentik/qc", query: "?code=123456");

        var target = PublicAddressAttribute.Redirect(SsoAddress.For(request, Public), request);

        Assert.Equal($"{Public}/sso/oidc/start/authentik/qc?code=123456&at=public", target);
    }

    /// <summary>
    /// The target is the configured address, whatever the request claims: nothing an
    /// attacker can put in a header reaches it.
    /// </summary>
    [Fact]
    public void A_forged_host_does_not_reach_the_redirect()
    {
        var request = Request("attacker.example");
        request.Headers["X-Forwarded-Host"] = "attacker.example";
        request.Headers["X-Forwarded-Prefix"] = "/evil";

        var target = PublicAddressAttribute.Redirect(SsoAddress.For(request, Public), request);

        Assert.Equal($"{Public}/sso/oidc/start?at=public", target);
    }

    /// <summary>
    /// A proxy that does not pass the public host on makes the redirected request look
    /// as if it arrived elsewhere too. It is served where it is, or the browser would be
    /// sent round for ever.
    /// </summary>
    [Fact]
    public void A_request_that_was_sent_here_already_is_not_sent_again()
    {
        var request = Request("jellyfin:8096", "http", query: "?at=public");

        Assert.Null(PublicAddressAttribute.Redirect(SsoAddress.For(request, Public), request));
    }

    [Fact]
    public void A_request_at_the_public_address_is_served_there()
    {
        var request = Request("jellyfin.example.com", prefix: "/jellyfin");

        Assert.Null(PublicAddressAttribute.Redirect(SsoAddress.For(request, Public), request));
    }

    [Fact]
    public void Only_a_page_a_browser_opens_is_redirected()
    {
        var request = Request("192.168.1.10:8096", "http", method: "POST");

        Assert.Null(PublicAddressAttribute.Redirect(SsoAddress.For(request, Public), request));
    }

    // ─── A value that is not an address ───────────────────────────────────────

    /// <summary>
    /// The plugin's own save refuses one, but the configuration is a file and Jellyfin has
    /// a generic endpoint that writes it. Falling back to the headers would undo, without
    /// a word, the one thing the setting is for.
    /// </summary>
    [Fact]
    public void A_stored_value_that_is_no_address_is_reported_and_not_ignored()
    {
        var at = SsoAddress.For(Request("jellyfin.example.com"), "jellyfin.example.com");

        Assert.True(at.PublicAddressInvalid);
        Assert.False(at.Elsewhere);
    }

    [Fact]
    public void An_empty_value_is_simply_not_set()
        => Assert.False(SsoAddress.For(Request("jellyfin.example.com"), "  ").PublicAddressInvalid);

    [Theory]
    [InlineData("jellyfin.example.com")]
    [InlineData("ftp://jellyfin.example.com")]
    [InlineData("https://user@jellyfin.example.com")]
    [InlineData("https://jellyfin.example.com/?next=x")]
    public void The_save_refuses_a_public_address_that_is_not_one(string address)
    {
        var problem = ConfigurationRules.Problem(new PluginConfiguration { PublicAddress = address });

        Assert.Contains("is not a public address", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://jellyfin.example.com/jellyfin")]
    public void The_save_takes_an_empty_one_and_a_real_one(string address)
        => Assert.Null(ConfigurationRules.Problem(new PluginConfiguration { PublicAddress = address }));
}
