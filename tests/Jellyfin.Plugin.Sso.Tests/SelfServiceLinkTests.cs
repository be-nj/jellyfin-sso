using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Provisioning;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Linking grants nothing by itself, but it decides which Jellyfin account an identity
/// provider's user is — so the one answer that must never be "write it" is an identity
/// another account already holds.
/// </summary>
public sealed class SelfServiceLinkTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static PluginConfiguration With(params LinkConfig[] links)
    {
        var config = new PluginConfiguration();
        config.Links.AddRange(links);
        return config;
    }

    [Fact]
    public void An_unheld_identity_is_written()
    {
        var decision = SelfServiceLinks.Decide(With(), "authentik", "sub-1", Alice);

        Assert.Equal(SelfServiceLinkOutcome.Write, decision.Outcome);
        Assert.Null(decision.Owner);
    }

    [Fact]
    public void Linking_the_same_identity_to_the_same_account_twice_changes_nothing()
    {
        var config = With(new LinkConfig
        {
            ProviderId = "authentik", Sub = "sub-1", JellyfinUserId = Alice,
        });

        var decision = SelfServiceLinks.Decide(config, "authentik", "sub-1", Alice);

        Assert.Equal(SelfServiceLinkOutcome.AlreadyThisAccount, decision.Outcome);
    }

    [Fact]
    public void An_identity_another_account_holds_is_refused_and_the_holder_is_named()
    {
        var config = With(new LinkConfig
        {
            ProviderId = "authentik", Sub = "sub-1", JellyfinUserId = Bob,
        });

        var decision = SelfServiceLinks.Decide(config, "authentik", "sub-1", Alice);

        Assert.Equal(SelfServiceLinkOutcome.HeldByAnotherAccount, decision.Outcome);
        Assert.Equal(Bob, decision.Owner);
    }

    [Fact]
    public void One_identity_per_account_per_provider()
    {
        var config = With(new LinkConfig
        {
            ProviderId = "authentik", Sub = "sub-old", JellyfinUserId = Alice,
        });

        var decision = SelfServiceLinks.Decide(config, "authentik", "sub-new", Alice);

        Assert.Equal(SelfServiceLinkOutcome.AccountAlreadyLinked, decision.Outcome);
    }

    /// <summary>
    /// A link belongs to one provider. The same subject arriving from a second provider is
    /// a different person as far as this plugin is concerned.
    /// </summary>
    [Fact]
    public void A_link_at_one_provider_says_nothing_about_another()
    {
        var config = With(new LinkConfig
        {
            ProviderId = "authentik", Sub = "sub-1", JellyfinUserId = Bob,
        });

        var decision = SelfServiceLinks.Decide(config, "keycloak", "sub-1", Alice);

        Assert.Equal(SelfServiceLinkOutcome.Write, decision.Outcome);
    }
}
