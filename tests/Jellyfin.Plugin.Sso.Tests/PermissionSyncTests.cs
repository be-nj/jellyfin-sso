using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly PermissionSync _sync;
    private readonly Guid _userId = Guid.NewGuid();

    public PermissionSyncTests()
    {
        _sync = new PermissionSync(_userManager, NullLogger<PermissionSync>.Instance);
    }

    private void Map(string group, bool admin, params Guid[] libraries)
        => _harness.Configuration.GroupMappings.Add(new GroupMappingConfig
        {
            Group = group,
            IsAdministrator = admin,
            LibraryIds = libraries.Select(id => id.ToString("N")).ToList(),
        });

    private static OidcIdentity Identity(params string[] groups) => new("sub-1", "ben", groups);

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
