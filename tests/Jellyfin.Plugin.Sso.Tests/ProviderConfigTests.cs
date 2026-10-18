using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Scopes start out empty on purpose — XmlSerializer appends to a pre-filled list instead
/// of replacing it, which used to duplicate the defaults on every config load.
/// </summary>
public sealed class ProviderConfigTests
{
    [Fact]
    public void An_unconfigured_provider_requests_the_default_scopes()
    {
        var provider = new ProviderConfig();

        Assert.Equal(["openid", "profile", "email"], provider.EffectiveScopes());
    }

    [Fact]
    public void Configured_scopes_replace_the_defaults()
    {
        var provider = new ProviderConfig { Scopes = ["openid", "groups"] };

        Assert.Equal(["openid", "groups"], provider.EffectiveScopes());
    }

    [Fact]
    public void Blank_and_duplicate_scopes_are_dropped()
    {
        var provider = new ProviderConfig { Scopes = ["openid", " openid ", "  ", "profile"] };

        Assert.Equal(["openid", "profile"], provider.EffectiveScopes());
    }

    [Fact]
    public void A_provider_defaults_to_a_public_client_without_a_secret()
    {
        var provider = new ProviderConfig();

        Assert.Equal(OidcClientType.Public, provider.ClientType);
        Assert.Null(provider.ConfidentialSecretRef);
        Assert.Equal("groups", provider.GroupsClaim);
    }
}
