using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The switch that decides whether somebody in no mapped group gets an account with no
/// libraries, or no account at all. Every branch here either lets a person into a media
/// server or keeps them out, so each one is named.
/// </summary>
public sealed class LoginGateTests : IDisposable
{
    private readonly PluginHarness _harness = new();

    private PluginConfiguration Config => _harness.Configuration;

    private static OidcIdentity Identity(params string[] groups)
        => new("subject-1", "ben", [.. groups], null);

    private void Map(string group) => Config.GroupMappings.Add(new GroupMappingConfig { Group = group });

    private LoginRefusal? Refusal(OidcIdentity identity, Guid? existingUser = null)
        => LoginGate.Refusal(Config, identity, existingUser);

    // ─── Off, which is how it ships ───────────────────────────────────────────

    [Fact]
    public void A_login_in_no_mapped_group_is_allowed_while_the_switch_is_off()
    {
        Map("media");

        Assert.Null(Refusal(Identity("strangers")));
    }

    [Fact]
    public void Nothing_configured_at_all_changes_nothing()
        => Assert.Null(Refusal(Identity()));

    // ─── On ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_matching_group_still_gets_in()
    {
        Config.RequireMappedGroup = true;
        Map("media");

        Assert.Null(Refusal(Identity("media", "strangers")));
    }

    [Fact]
    public void A_login_in_no_mapped_group_is_refused()
    {
        Config.RequireMappedGroup = true;
        Map("media");

        var refusal = Refusal(Identity("strangers"));

        Assert.NotNull(refusal);
        Assert.Contains("matches a group mapping", refusal!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_login_with_no_groups_at_all_says_that_instead()
    {
        Config.RequireMappedGroup = true;
        Map("media");

        Assert.Equal("the provider sent no groups", Refusal(Identity())!.Reason);
    }

    /// <summary>
    /// The mistake this switch makes possible: on, with nothing to match. Refusing
    /// everybody is what it was asked to do, and the log says which half is missing —
    /// quietly letting everybody in instead is the kind of thing somebody would rely on
    /// at exactly the wrong moment.
    /// </summary>
    [Fact]
    public void Switched_on_with_no_mappings_at_all_refuses_everybody_and_says_why()
    {
        Config.RequireMappedGroup = true;

        var refusal = Refusal(Identity("media"));

        Assert.NotNull(refusal);
        Assert.Contains("no group mappings are configured", refusal!.Reason, StringComparison.Ordinal);
    }

    // ─── The near miss, which is the usual cause ──────────────────────────────

    [Theory]
    [InlineData("Media", "media", "case")]
    [InlineData("media", "/media", "Full group path")]
    [InlineData("media", "/household/media", "Full group path")]
    public void A_refusal_carries_what_nearly_matched(string mapping, string arrived, string expected)
    {
        Config.RequireMappedGroup = true;
        Map(mapping);

        var refusal = Refusal(Identity(arrived));

        var (gotMapping, gotArrived, reason) = Assert.Single(refusal!.NearMisses);
        Assert.Equal(mapping, gotMapping);
        Assert.Equal(arrived, gotArrived);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_with_nothing_in_common_is_refused_without_a_hint()
    {
        Config.RequireMappedGroup = true;
        Map("media");

        Assert.Empty(Refusal(Identity("multimedia"))!.NearMisses);
    }

    // ─── The way back in ──────────────────────────────────────────────────────

    [Fact]
    public void An_account_excluded_from_the_sync_is_also_excluded_from_this()
    {
        Config.RequireMappedGroup = true;
        Map("media");
        var ben = Guid.NewGuid();
        Config.SyncExcludedUserIds.Add(ben);

        Assert.Null(Refusal(Identity("strangers"), ben));
        // Somebody else in the same group is still turned away.
        Assert.NotNull(Refusal(Identity("strangers"), Guid.NewGuid()));
    }

    public void Dispose() => _harness.Dispose();
}
