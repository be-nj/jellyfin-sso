using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Scopes start out empty on purpose — XmlSerializer appends to a pre-filled list instead
/// of replacing it, which used to duplicate the defaults on every config load.
/// </summary>
public sealed class ProviderConfigTests
{
    [Fact]
    public void A_new_provider_starts_disabled_with_an_id_and_the_usual_groups_claim()
    {
        var provider = new ProviderConfig();

        // Disabled, because a provider is saved before it is finished: an enabled
        // half-provider puts a dead button on the household's login page.
        Assert.False(provider.Enabled);
        Assert.Equal(32, provider.Id.Length);
        Assert.NotEqual(new ProviderConfig().Id, provider.Id);
        Assert.Equal("groups", provider.GroupsClaim);
        Assert.False(provider.GroupsFromUserInfo);
        Assert.Equal(OidcClientType.Public, provider.ClientType);
        Assert.Null(provider.ConfidentialSecretRef);

        // The login button looks like Jellyfin's own until somebody asks for otherwise.
        Assert.False(provider.BrandedLoginButton);
        Assert.Equal(string.Empty, provider.ButtonColour);

        // On: the endpoint acts only on a token the provider signed, and a provider only
        // posts one once an administrator has registered the URL with it.
        Assert.True(provider.AcceptBackChannelLogout);
    }

    [Fact]
    public void A_new_configuration_manages_nobody_and_allows_self_service_linking()
    {
        var config = new PluginConfiguration();

        Assert.Empty(config.Providers);
        Assert.Empty(config.GroupMappings);
        Assert.Empty(config.Links);
        Assert.Empty(config.SeenGroups);
        Assert.Empty(config.SyncExcludedUserIds);
        Assert.False(config.PreserveAdminPermissions);
        Assert.False(config.SignOutOfIdentityProvider);
        Assert.True(config.AllowSelfServiceLinking);

        // The button is written into the branding, with the provider's own logo on it.
        Assert.True(config.AddLoginButtonToBranding);
        Assert.True(config.UseProviderLogos);

        // Off: it rewrites a response that belongs to Jellyfin.
        Assert.False(config.InjectLoginButtonIntoForm);

        Assert.Equal(500, PluginConfiguration.MaxSeenGroups);
    }

    [Fact]
    public void A_group_mapping_grants_nothing_until_it_is_filled_in()
    {
        var mapping = new GroupMappingConfig();

        Assert.Empty(mapping.Group);
        Assert.Empty(mapping.LibraryIds);
        Assert.False(mapping.IsAdministrator);
        Assert.False(mapping.EnableLiveTvAccess);
        Assert.False(mapping.EnableLiveTvManagement);
        Assert.False(mapping.EnableContentDownloading);
        Assert.Null(mapping.MaxParentalRating);
    }

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
