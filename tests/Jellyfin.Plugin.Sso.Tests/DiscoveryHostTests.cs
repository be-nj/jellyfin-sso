using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A discovery document is the provider's description of itself, and it decides where the
/// signing keys come from, where the client secret and the authorization code are sent,
/// and where a user's ID token goes as a logout hint. The paths are the provider's own
/// business — Authentik publishes most of its endpoints on a shared path while its issuer
/// is per-application — but a document that moves one of them to another host is not a
/// layout quirk. Both discovery checks used to be switched off to accommodate the first
/// thing, which gave away the second.
/// </summary>
public sealed class DiscoveryHostTests
{
    private const string Authority = "https://idp.example.com/application/o/jellyfin/";

    /// <summary>The endpoints in the shape Authentik publishes: shared paths, own host.</summary>
    private static (string, string?)[] Endpoints(
        string? jwks = "https://idp.example.com/application/o/jellyfin/jwks/",
        string? token = "https://idp.example.com/application/o/token/",
        string? userinfo = "https://idp.example.com/application/o/userinfo/",
        string? endSession = "https://idp.example.com/application/o/jellyfin/end-session/")
        =>
        [
            ("jwks_uri", jwks),
            ("token_endpoint", token),
            ("userinfo_endpoint", userinfo),
            ("end_session_endpoint", endSession),
        ];

    [Fact]
    public void Authentiks_shared_paths_on_its_own_host_are_accepted()
        => OidcClientFactory.RequireEndpointsOnKnownHosts(Authority, [], Endpoints());

    [Theory]
    [InlineData("jwks_uri")]
    [InlineData("token_endpoint")]
    [InlineData("userinfo_endpoint")]
    [InlineData("end_session_endpoint")]
    public void An_endpoint_on_another_host_is_refused(string moved)
    {
        const string Elsewhere = "https://attacker.example/x";

        var endpoints = moved switch
        {
            "jwks_uri" => Endpoints(jwks: Elsewhere),
            "token_endpoint" => Endpoints(token: Elsewhere),
            "userinfo_endpoint" => Endpoints(userinfo: Elsewhere),
            _ => Endpoints(endSession: Elsewhere),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts(Authority, [], endpoints));

        Assert.Contains(moved, ex.Message, StringComparison.Ordinal);
        Assert.Contains("another host", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A different path on the same host is still the same host. This check is about the
    /// host and says so; the path is deliberately not its business.
    /// </summary>
    [Fact]
    public void A_different_path_on_the_same_host_is_fine()
        => OidcClientFactory.RequireEndpointsOnKnownHosts(
            Authority, [], Endpoints(jwks: "https://idp.example.com/somewhere/else/jwks/"));

    /// <summary>An endpoint a provider does not publish is not a missing endpoint.</summary>
    [Fact]
    public void Endpoints_the_provider_omits_are_not_refused()
        => OidcClientFactory.RequireEndpointsOnKnownHosts(
            Authority, [], Endpoints(userinfo: null, endSession: null));

    /// <summary>A host that merely ends in the authority's name is another host.</summary>
    [Fact]
    public void A_lookalike_host_is_refused()
        => Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts(
                Authority, [], Endpoints(jwks: "https://evil-idp.example.com/jwks/")));

    // ─── Hosts an administrator named ─────────────────────────────────────────

    /// <summary>Google's document as published on 15 November 2026, issuer included.</summary>
    private const string Google = "https://accounts.google.com";

    private static readonly string[] GoogleHosts =
        ["www.googleapis.com", "oauth2.googleapis.com", "openidconnect.googleapis.com"];

    private static (string, string?)[] GoogleEndpoints(
        string jwks = "https://www.googleapis.com/oauth2/v3/certs")
        =>
        [
            ("jwks_uri", jwks),
            ("token_endpoint", "https://oauth2.googleapis.com/token"),
            ("userinfo_endpoint", "https://openidconnect.googleapis.com/v1/userinfo"),
            ("end_session_endpoint", null),
        ];

    /// <summary>
    /// The report this setting exists for: Google's issuer is accounts.google.com and
    /// nothing the plugin calls is served from there.
    /// </summary>
    [Fact]
    public void Googles_layout_is_accepted_once_its_hosts_are_named()
        => OidcClientFactory.RequireEndpointsOnKnownHosts(Google, GoogleHosts, GoogleEndpoints());

    /// <summary>
    /// One refusal names every host and the line to paste, so nobody finds Google's three
    /// hosts one failed login at a time.
    /// </summary>
    [Fact]
    public void A_refusal_names_every_host_and_the_setting_that_admits_them()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts(Google, [], GoogleEndpoints()));

        Assert.Contains("jwks_uri on www.googleapis.com", ex.Message, StringComparison.Ordinal);
        Assert.Contains("token_endpoint on oauth2.googleapis.com", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Further endpoint hosts", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "www.googleapis.com oauth2.googleapis.com openidconnect.googleapis.com",
            ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Naming some hosts admits those hosts. The one left out is still refused.</summary>
    [Fact]
    public void A_host_left_off_the_list_is_still_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts(
                Google, ["www.googleapis.com", "oauth2.googleapis.com"], GoogleEndpoints()));

        Assert.Contains("userinfo_endpoint on openidconnect.googleapis.com", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("jwks_uri", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list names hosts, not families of them: neither a name that ends in a listed
    /// one nor a name that begins with it is on the list.
    /// </summary>
    [Theory]
    [InlineData("https://evil-www.googleapis.com/certs")]
    [InlineData("https://www.googleapis.com.attacker.example/certs")]
    [InlineData("https://googleapis.com/certs")]
    [InlineData("https://www.googleapis.com:8443/certs")]
    public void A_lookalike_of_a_named_host_is_refused(string jwks)
        => Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts(
                Google, GoogleHosts, GoogleEndpoints(jwks)));

    /// <summary>A port is part of the name where the endpoint uses one.</summary>
    [Fact]
    public void A_named_host_with_a_port_admits_that_port()
        => OidcClientFactory.RequireEndpointsOnKnownHosts(
            Google,
            [.. GoogleHosts, "WWW.googleapis.com:8443"],
            GoogleEndpoints("https://www.googleapis.com:8443/certs"));

    [Theory]
    [InlineData("www.googleapis.com", "www.googleapis.com")]
    [InlineData("  WWW.GoogleAPIs.com ", "www.googleapis.com")]
    [InlineData("keys.example:8443", "keys.example:8443")]
    [InlineData("keys.example:443", "keys.example")]
    [InlineData("192.0.2.7", "192.0.2.7")]
    [InlineData("[2001:db8::7]:8443", "[2001:db8::7]:8443")]
    public void A_host_name_is_read_as_one(string entry, string expected)
        => Assert.Equal(expected, OidcClientFactory.EndpointHost(entry));

    /// <summary>
    /// What the field does not do. A wildcard or a scheme would be a way to write
    /// "anywhere", and the rule this list loosens is the one that says "not anywhere".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("*.googleapis.com")]
    [InlineData("https://www.googleapis.com")]
    [InlineData("www.googleapis.com/oauth2")]
    [InlineData("user@www.googleapis.com")]
    [InlineData("www.googleapis.com oauth2.googleapis.com")]
    [InlineData("www.googleapis.com:port")]
    [InlineData(":8443")]
    public void Anything_else_is_not_a_host_name(string entry)
        => Assert.Null(OidcClientFactory.EndpointHost(entry));

    /// <summary>
    /// An entry that is not a host name admits nothing. A hand-edited configuration with a
    /// wildcard in it is therefore as strict as an empty one, not as loose as it reads.
    /// </summary>
    [Fact]
    public void An_entry_that_is_not_a_host_name_admits_nothing()
        => Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts(
                Google, ["*.googleapis.com", "*"], GoogleEndpoints()));

    [Fact]
    public void An_authority_that_is_not_a_url_is_refused_plainly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnKnownHosts("not-a-url", [], Endpoints()));

        Assert.Contains("absolute URL", ex.Message, StringComparison.Ordinal);
    }
}
