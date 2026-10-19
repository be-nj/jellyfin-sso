using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The fields of the policy that the group mappings decide, one at a time. Mutation
/// testing found these written but never read back: the suite stayed green with an
/// administrator's library list, the playback permissions and the parental rating
/// inverted.
/// </summary>
public sealed class PolicyDetailTests : IDisposable
{
    private static readonly Guid MoviesId = Guid.NewGuid();
    private static readonly Guid KidsId = Guid.NewGuid();

    private readonly PluginHarness _harness = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly PermissionSync _sync;
    private readonly Guid _userId = Guid.NewGuid();

    public PolicyDetailTests()
        => _sync = new PermissionSync(_userManager, NullLogger<PermissionSync>.Instance);

    private void Map(string group, Action<GroupMappingConfig> configure)
    {
        var mapping = new GroupMappingConfig { Group = group };
        configure(mapping);
        _harness.Configuration.GroupMappings.Add(mapping);
    }

    private UserPolicy Captured()
    {
        var call = _userManager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IUserManager.UpdatePolicyAsync));
        return (UserPolicy)call.GetArguments()[1]!;
    }

    [Fact]
    public async Task An_administrator_gets_every_library_and_no_explicit_list()
    {
        Map("admins", m => { m.IsAdministrator = true; m.LibraryIds = [MoviesId.ToString("N")]; });

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["admins"]));

        var policy = Captured();
        Assert.True(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);
    }

    [Fact]
    public async Task An_administrator_has_no_parental_limit_even_when_a_group_sets_one()
    {
        Map("admins", m => m.IsAdministrator = true);
        Map("kids", m => m.MaxParentalRating = 6);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["admins", "kids"]));

        Assert.Null(Captured().MaxParentalRating);
    }

    [Fact]
    public async Task Playback_is_allowed_for_every_synced_user()
    {
        Map("media", m => m.LibraryIds = [MoviesId.ToString("N")]);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["media"]));

        var policy = Captured();
        Assert.True(policy.EnableMediaPlayback);
        Assert.True(policy.EnableAudioPlaybackTranscoding);
        Assert.True(policy.EnableVideoPlaybackTranscoding);
        Assert.True(policy.EnablePlaybackRemuxing);
    }

    [Fact]
    public async Task One_group_granting_live_tv_is_enough()
    {
        Map("media", m => m.LibraryIds = [MoviesId.ToString("N")]);
        Map("tv", m => m.EnableLiveTvAccess = true);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["media", "tv"]));

        Assert.True(Captured().EnableLiveTvAccess);
    }

    [Fact]
    public async Task Live_tv_management_and_downloads_come_from_their_own_groups()
    {
        Map("tv-admins", m => m.EnableLiveTvManagement = true);
        Map("downloaders", m => m.EnableContentDownloading = true);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["tv-admins", "downloaders"]));

        var policy = Captured();
        Assert.True(policy.EnableLiveTvManagement);
        Assert.True(policy.EnableContentDownloading);
        Assert.False(policy.EnableLiveTvAccess);
    }

    [Fact]
    public async Task A_group_without_a_rating_lifts_the_limit_of_the_others()
    {
        Map("kids", m => m.MaxParentalRating = 6);
        Map("media", m => m.LibraryIds = [MoviesId.ToString("N")]);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["kids", "media"]));

        Assert.Null(Captured().MaxParentalRating);
    }

    [Fact]
    public async Task No_group_granting_live_tv_leaves_it_off()
    {
        Map("media", m => m.LibraryIds = [MoviesId.ToString("N")]);
        Map("other", m => m.LibraryIds = [KidsId.ToString("N")]);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["media", "other"]));

        var policy = Captured();
        Assert.False(policy.EnableLiveTvAccess);
        Assert.False(policy.EnableLiveTvManagement);
        Assert.False(policy.EnableContentDownloading);
    }

    [Fact]
    public async Task The_strictest_group_does_not_win_a_parental_rating()
    {
        Map("teens", m => m.MaxParentalRating = 12);
        Map("kids", m => m.MaxParentalRating = 6);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["teens", "kids"]));

        Assert.Equal(12, Captured().MaxParentalRating);
    }

    [Fact]
    public async Task A_synced_user_is_pinned_to_this_plugin_for_authentication()
    {
        Map("media", m => m.LibraryIds = [MoviesId.ToString("N")]);

        await _sync.SyncAsync(_userId, new OidcIdentity("sub-1", "ben", ["media"]));

        Assert.Equal(
            "Jellyfin.Plugin.Sso.Auth.SsoAuthenticationProvider",
            Captured().AuthenticationProviderId);
    }

    public void Dispose() => _harness.Dispose();
}
