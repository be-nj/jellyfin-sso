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
        => OidcClientFactory.RequireEndpointsOnTheAuthoritysHost(Authority, Endpoints());

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
            () => OidcClientFactory.RequireEndpointsOnTheAuthoritysHost(Authority, endpoints));

        Assert.Contains(moved, ex.Message, StringComparison.Ordinal);
        Assert.Contains("another host", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A different path on the same host is still the same host. This check is about the
    /// host and says so; the path is deliberately not its business.
    /// </summary>
    [Fact]
    public void A_different_path_on_the_same_host_is_fine()
        => OidcClientFactory.RequireEndpointsOnTheAuthoritysHost(
            Authority, Endpoints(jwks: "https://idp.example.com/somewhere/else/jwks/"));

    /// <summary>An endpoint a provider does not publish is not a missing endpoint.</summary>
    [Fact]
    public void Endpoints_the_provider_omits_are_not_refused()
        => OidcClientFactory.RequireEndpointsOnTheAuthoritysHost(
            Authority, Endpoints(userinfo: null, endSession: null));

    /// <summary>A host that merely ends in the authority's name is another host.</summary>
    [Fact]
    public void A_lookalike_host_is_refused()
        => Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnTheAuthoritysHost(
                Authority, Endpoints(jwks: "https://evil-idp.example.com/jwks/")));

    [Fact]
    public void An_authority_that_is_not_a_url_is_refused_plainly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OidcClientFactory.RequireEndpointsOnTheAuthoritysHost("not-a-url", Endpoints()));

        Assert.Contains("absolute URL", ex.Message, StringComparison.Ordinal);
    }
}
