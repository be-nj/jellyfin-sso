using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Library access is declarative: whatever the IdP groups say at login becomes the user's
/// policy, including the removal of access they had before. These tests cover both
/// directions, since a sync that only ever grants would never revoke anything.
/// </summary>
public sealed class PermissionSyncTests : IDisposable
{
    private static readonly Guid MoviesId = Guid.NewGuid();
    private static readonly Guid KidsId = Guid.NewGuid();

    private readonly PluginHarness _harness = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly RecordingLogger<PermissionSync> _log = new();
    private readonly PermissionSync _sync;
    private readonly Guid _userId = Guid.NewGuid();

    public PermissionSyncTests()
    {
        _sync = new PermissionSync(_userManager, _log);
    }

    private void Map(string group, bool admin, params Guid[] libraries)
        => _harness.Configuration.GroupMappings.Add(new GroupMappingConfig
        {
            Group = group,
            IsAdministrator = admin,
            LibraryIds = libraries.Select(id => id.ToString("N")).ToList(),
        });

    private static OidcIdentity Identity(params string[] groups) => new("sub-1", "ben", groups);

    /// <summary>Registers a Jellyfin user for the synced id, optionally already an admin.</summary>
    private User ExistingUser(bool admin, Guid? id = null)
    {
        var user = new User(
            "ben",
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        // Registered under both ids so a test can sync either the shared id or the entity's own.
        _userManager.GetUserById(id ?? _userId).Returns(user);
        _userManager.GetUserById(user.Id).Returns(user);
        return user;
    }

    private void OtherUsers(params User[] users) => _userManager.GetUsers().Returns(users);

    [Fact]
    public async Task Settings_this_plugin_does_not_own_survive_a_sync()
    {
        // An administrator set these in Jellyfin's own user screen. The plugin manages
        // libraries and group permissions, not the rest of the policy.
        var user = ExistingUser(admin: false);
        _userManager.GetUserDto(user).Returns(new MediaBrowser.Model.Dto.UserDto
        {
            Policy = new UserPolicy
            {
                BlockedTags = ["horror"],
                EnableRemoteAccess = false,
                MaxActiveSessions = 2,
                EnableUserPreferenceAccess = false,
                LoginAttemptsBeforeLockout = 7,
            },
        });

        Map("media", admin: false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        var policy = CapturedPolicy();
        Assert.Equal(["horror"], policy.BlockedTags);
        Assert.False(policy.EnableRemoteAccess);
        Assert.Equal(2, policy.MaxActiveSessions);
        Assert.False(policy.EnableUserPreferenceAccess);
        Assert.Equal(7, policy.LoginAttemptsBeforeLockout);

        // …while the fields the plugin does own are still written.
        Assert.Equal([MoviesId], policy.EnabledFolders);
        Assert.False(policy.EnableAllFolders);
    }

    [Fact]
    public async Task Live_tv_and_downloads_come_from_the_groups()
    {
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig
        {
            Group = "media",
            LibraryIds = [MoviesId.ToString("N")],
            EnableLiveTvAccess = true,
        });
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig
        {
            Group = "downloaders",
            EnableContentDownloading = true,
        });

        await _sync.SyncAsync(_userId, Identity("media", "downloaders"));

        var policy = CapturedPolicy();
        Assert.True(policy.EnableLiveTvAccess);
        Assert.True(policy.EnableContentDownloading);
        Assert.False(policy.EnableLiveTvManagement);
    }

    [Fact]
    public async Task An_admin_group_grants_live_tv_and_downloads_too()
    {
        Map("jellyfin-admins", true);

        await _sync.SyncAsync(_userId, Identity("jellyfin-admins"));

        var policy = CapturedPolicy();
        Assert.True(policy.EnableLiveTvAccess);
        Assert.True(policy.EnableLiveTvManagement);
        Assert.True(policy.EnableContentDownloading);
        Assert.Null(policy.MaxParentalRating);
    }

    [Fact]
    public async Task Deletion_collections_subtitles_and_lyrics_come_from_the_groups()
    {
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig
        {
            Group = "librarians",
            EnableCollectionManagement = true,
            EnableSubtitleManagement = true,
        });
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig
        {
            Group = "karaoke",
            EnableLyricManagement = true,
        });

        await _sync.SyncAsync(_userId, Identity("librarians", "karaoke"));

        var policy = CapturedPolicy();
        Assert.True(policy.EnableCollectionManagement);
        Assert.True(policy.EnableSubtitleManagement);
        Assert.True(policy.EnableLyricManagement);

        // Nobody's group granted deletion, so nobody gets it.
        Assert.False(policy.EnableContentDeletion);
    }

    [Fact]
    public async Task A_group_that_no_longer_grants_deletion_takes_it_away_again()
    {
        var user = ExistingUser(admin: false);
        _userManager.GetUserDto(user).Returns(new MediaBrowser.Model.Dto.UserDto
        {
            Policy = new UserPolicy
            {
                EnableContentDeletion = true,
                EnableContentDeletionFromFolders = [MoviesId.ToString("N")],
            },
        });

        Map("media", admin: false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        var policy = CapturedPolicy();
        Assert.False(policy.EnableContentDeletion);
        // A folder list left behind would keep granting in those folders what the flag
        // just took away everywhere.
        Assert.Empty(policy.EnableContentDeletionFromFolders);
    }

    [Fact]
    public async Task An_admin_group_grants_the_management_rights_but_not_deletion()
    {
        Map("jellyfin-admins", true);

        await _sync.SyncAsync(_userId, Identity("jellyfin-admins"));

        var policy = CapturedPolicy();
        Assert.True(policy.EnableCollectionManagement);
        Assert.True(policy.EnableSubtitleManagement);
        Assert.True(policy.EnableLyricManagement);

        // Deletion removes files. Being an administrator in the groups does not hand it
        // out: Jellyfin's own user screen owns that, and an operator who left it off
        // meant it. A group that ticks "delete media" still grants it to anybody.
        Assert.False(policy.EnableContentDeletion);
    }

    [Fact]
    public async Task A_folder_level_deletion_list_survives_a_user_who_never_had_deletion()
    {
        var user = ExistingUser(admin: false);
        _userManager.GetUserDto(user).Returns(new MediaBrowser.Model.Dto.UserDto
        {
            Policy = new UserPolicy
            {
                EnableContentDeletion = false,
                EnableContentDeletionFromFolders = [MoviesId.ToString("N")],
            },
        });

        Map("media", admin: false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        // The plugin took nothing away here, so it leaves the list an administrator set.
        Assert.Equal([MoviesId.ToString("N")], CapturedPolicy().EnableContentDeletionFromFolders);
    }

    [Fact]
    public async Task The_most_permissive_parental_rating_of_the_matched_groups_wins()
    {
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig { Group = "kids", MaxParentalRating = 6 });
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig { Group = "teens", MaxParentalRating = 12 });

        await _sync.SyncAsync(_userId, Identity("kids", "teens"));

        Assert.Equal(12, CapturedPolicy().MaxParentalRating);
    }

    [Fact]
    public async Task A_group_without_a_parental_limit_lifts_the_limit()
    {
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig { Group = "kids", MaxParentalRating = 6 });
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig { Group = "media" });

        await _sync.SyncAsync(_userId, Identity("kids", "media"));

        Assert.Null(CapturedPolicy().MaxParentalRating);
    }

    [Fact]
    public async Task An_excluded_user_keeps_the_permissions_an_admin_gave_them()
    {
        Map("media", false, MoviesId);
        _harness.Configuration.SyncExcludedUserIds.Add(_userId);

        await _sync.SyncAsync(_userId, Identity("media", "a-new-group"));

        await _userManager.DidNotReceiveWithAnyArgs().UpdatePolicyAsync(default, default!);
        // Group names are still learned, so the admin UI can offer them.
        Assert.Contains("a-new-group", _harness.Configuration.SeenGroups);
    }

    [Fact]
    public async Task PreserveAdminPermissions_keeps_an_admin_that_no_group_grants()
    {
        Map("media", false, MoviesId);
        _harness.Configuration.PreserveAdminPermissions = true;
        ExistingUser(admin: true);
        OtherUsers(ExistingUser(admin: true, id: Guid.NewGuid()));

        await _sync.SyncAsync(_userId, Identity("media"));

        var policy = CapturedPolicy();
        Assert.True(policy.IsAdministrator);
        // A kept administrator is still an administrator, so they keep full library access.
        Assert.True(policy.EnableAllFolders);
    }

    [Fact]
    public async Task The_last_administrator_is_never_demoted()
    {
        // A typo in the admin group would otherwise lock everyone out of the server.
        Map("jellyfin-adminz", true);
        var admin = ExistingUser(admin: true);
        OtherUsers(admin);

        await _sync.SyncAsync(admin.Id, Identity("jellyfin-admins"));

        Assert.True(CapturedPolicy().IsAdministrator);
    }

    [Fact]
    public async Task An_admin_is_demoted_while_another_admin_remains()
    {
        Map("media", false, MoviesId);
        var user = ExistingUser(admin: true);
        var otherAdmin = new User(
            "root",
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");
        otherAdmin.SetPermission(PermissionKind.IsAdministrator, true);
        OtherUsers(user, otherAdmin);

        await _sync.SyncAsync(user.Id, Identity("media"));

        Assert.False(CapturedPolicy().IsAdministrator);
    }

    [Fact]
    public async Task A_mapped_group_grants_exactly_its_libraries()
    {
        Map("media", false, MoviesId);
        Map("kids", false, KidsId);

        await _sync.SyncAsync(_userId, Identity("media"));

        var policy = CapturedPolicy();
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableAllFolders);
        Assert.Equal([MoviesId], policy.EnabledFolders);
    }

    [Fact]
    public async Task Several_groups_are_merged_without_duplicates()
    {
        Map("media", false, MoviesId, KidsId);
        Map("kids", false, KidsId);

        await _sync.SyncAsync(_userId, Identity("media", "kids"));

        var policy = CapturedPolicy();
        Assert.Equal(2, policy.EnabledFolders.Length);
        Assert.Contains(MoviesId, policy.EnabledFolders);
        Assert.Contains(KidsId, policy.EnabledFolders);
    }

    [Fact]
    public async Task An_admin_group_grants_admin_and_every_library()
    {
        Map("jellyfin-admins", true);

        await _sync.SyncAsync(_userId, Identity("jellyfin-admins"));

        var policy = CapturedPolicy();
        Assert.True(policy.IsAdministrator);
        Assert.True(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);
    }

    [Fact]
    public async Task Losing_every_group_revokes_access_and_admin()
    {
        Map("jellyfin-admins", true);
        Map("media", false, MoviesId);

        // The user kept their Jellyfin account but is in no mapped group any more.
        await _sync.SyncAsync(_userId, Identity("unrelated-group"));

        var policy = CapturedPolicy();
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);
    }

    [Fact]
    public async Task Group_names_are_matched_case_sensitively()
    {
        Map("Media", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        Assert.Empty(CapturedPolicy().EnabledFolders);
    }

    [Fact]
    public async Task A_mapping_that_differs_only_in_case_says_so_in_the_log()
    {
        Map("Media", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        Assert.Contains(
            _log.Warnings,
            line => line.Contains("'Media'", StringComparison.Ordinal)
                && line.Contains("'media'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mapping_that_names_only_the_last_part_of_a_path_says_so_in_the_log()
    {
        // What Keycloak sends with "Full group path" left on, against the mapping a
        // reader of its guide would have typed.
        Map("media", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("/media"));

        Assert.Empty(CapturedPolicy().EnabledFolders);
        Assert.Contains(
            _log.Warnings,
            line => line.Contains("'media'", StringComparison.Ordinal)
                && line.Contains("'/media'", StringComparison.Ordinal)
                && line.Contains("Full group path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deeper_path_is_the_same_near_miss()
    {
        Map("media", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("/household/media"));

        Assert.Contains(
            _log.Warnings,
            line => line.Contains("'/household/media'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_group_that_merely_ends_in_the_same_letters_is_not_a_near_miss()
    {
        Map("media", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("multimedia"));

        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task A_mapping_nobody_sent_is_not_worth_a_warning()
    {
        Map("downloaders", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task The_remembered_group_names_stop_at_the_cap()
    {
        _harness.Configuration.SeenGroups.AddRange(
            Enumerable.Range(0, PluginConfiguration.MaxSeenGroups).Select(i => $"group-{i}"));

        await _sync.SyncAsync(_userId, Identity("something-new"));

        Assert.Equal(PluginConfiguration.MaxSeenGroups, _harness.Configuration.SeenGroups.Count);
        Assert.DoesNotContain("something-new", _harness.Configuration.SeenGroups);
    }

    [Fact]
    public async Task Group_names_the_provider_sent_are_remembered_for_the_autocomplete()
    {
        await _sync.SyncAsync(_userId, Identity("media", "media", "downloaders"));

        Assert.Equal(["media", "downloaders"], _harness.Configuration.SeenGroups);
    }

    [Fact]
    public async Task Synced_users_stay_pinned_to_the_sso_provider_with_playback_allowed()
    {
        Map("media", false, MoviesId);

        await _sync.SyncAsync(_userId, Identity("media"));

        var policy = CapturedPolicy();
        Assert.Equal(typeof(SsoAuthenticationProvider).FullName, policy.AuthenticationProviderId);
        Assert.True(policy.EnableMediaPlayback);
        // Phase 2 fields: off until they are derived from group mappings.
        Assert.False(policy.EnableLiveTvAccess);
        Assert.False(policy.EnableContentDownloading);
    }

    [Fact]
    public async Task Groups_seen_at_login_are_remembered_once()
    {
        _harness.Configuration.SeenGroups.Add("media");

        await _sync.SyncAsync(_userId, Identity("media", "newcomers"));
        await _sync.SyncAsync(_userId, Identity("media", "newcomers"));

        Assert.Equal(["media", "newcomers"], _harness.Configuration.SeenGroups);
    }

    private UserPolicy CapturedPolicy()
    {
        var calls = _userManager.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IUserManager.UpdatePolicyAsync))
            .Select(c => c.GetArguments())
            .ToList();

        Assert.NotEmpty(calls);
        return Assert.IsType<UserPolicy>(calls[^1][1]);
    }

    public void Dispose() => _harness.Dispose();
}
