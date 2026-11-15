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

    // ─── A provider whose groups are not read ─────────────────────────────────

    /// <summary>
    /// The rule asks whether a login matches a group mapping. A provider that is set not
    /// to manage permissions — Google, which has no groups to send — would fail it at
    /// every login, so switching the rule on would switch that provider off. Its logins
    /// are decided by links and by whether it creates accounts instead.
    /// </summary>
    [Fact]
    public void The_rule_does_not_apply_to_a_provider_that_does_not_manage_permissions()
    {
        Config.RequireMappedGroup = true;
        Map("media");
        var google = new ProviderConfig { Id = "google", ManagePermissions = false };

        Assert.Null(LoginGate.Refusal(Config, Identity(), existingUserId: null, google));
    }

    /// <summary>And it still applies to every other provider on the same server.</summary>
    [Fact]
    public void The_rule_still_applies_to_a_provider_that_does_manage_them()
    {
        Config.RequireMappedGroup = true;
        Map("media");
        var authentik = new ProviderConfig { Id = "authentik" };

        Assert.NotNull(LoginGate.Refusal(Config, Identity(), existingUserId: null, authentik));
    }

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

    /// <summary>
    /// The switch refuses a login that matches *nothing*. Somebody who loses one of
    /// several mapped groups keeps the login and loses what that group granted, which is
    /// what the permission sync has always done — a distinction the documentation got
    /// wrong before it got tested.
    /// </summary>
    [Fact]
    public void Losing_one_of_several_mapped_groups_is_not_losing_the_login()
    {
        Config.RequireMappedGroup = true;
        Map("media");
        Map("kids");

        Assert.Null(Refusal(Identity("kids")));
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
