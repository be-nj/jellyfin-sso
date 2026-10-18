using Jellyfin.Database.Implementations.Entities;
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
/// Provisioning decides which Jellyfin account an IdP identity gets. Matching the wrong
/// account here is the admin-takeover bug the plugin exists to avoid, so the tests pin
/// down that identities are matched by (provider, sub) and never by username.
/// </summary>
public sealed class UserProvisionerTests : IDisposable
{
    private readonly PluginHarness _harness = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly UserProvisioner _provisioner;
    private readonly ProviderConfig _provider = new() { Id = "provider-1", Name = "Test" };

    public UserProvisionerTests()
    {
        _provisioner = new UserProvisioner(_userManager, NullLogger<UserProvisioner>.Instance);
    }

    private static User UserNamed(string name)
        => new(name, "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");

    private static OidcIdentity Identity(string sub, string username, params string[] groups)
        => new(sub, username, groups);

    [Fact]
    public async Task An_existing_link_resolves_to_the_linked_user()
    {
        var linked = UserNamed("ben");
        _userManager.GetUserById(linked.Id).Returns(linked);
        _harness.Configuration.Links.Add(new LinkConfig
        {
            ProviderId = "provider-1",
            Sub = "ben",
            JellyfinUserId = linked.Id,
        });

        var (userId, username) = await _provisioner.ResolveAsync(_provider, Identity("ben", "ben"));

        Assert.Equal(linked.Id, userId);
        Assert.Equal("ben", username);
        await _userManager.DidNotReceiveWithAnyArgs().CreateUserAsync(default!);
    }

    [Fact]
    public async Task A_link_from_another_provider_does_not_match()
    {
        var linked = UserNamed("ben");
        _userManager.GetUserById(linked.Id).Returns(linked);
        _harness.Configuration.Links.Add(new LinkConfig
        {
            ProviderId = "some-other-provider",
            Sub = "ben",
            JellyfinUserId = linked.Id,
        });
        // No local account of that name, so provisioning may proceed with a new user.
        var created = UserNamed("ben");
        _userManager.CreateUserAsync("ben").Returns(created);

        var (userId, _) = await _provisioner.ResolveAsync(_provider, Identity("ben", "ben"));

        Assert.NotEqual(linked.Id, userId);
        Assert.Equal(created.Id, userId);
    }

    [Fact]
    public async Task An_unlinked_identity_never_adopts_an_existing_account_by_username()
    {
        var admin = UserNamed("admin");
        _userManager.GetUserByName("admin").Returns(admin);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _provisioner.ResolveAsync(_provider, Identity("attacker-sub", "admin")));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        await _userManager.DidNotReceiveWithAnyArgs().CreateUserAsync(default!);
        Assert.Empty(_harness.Configuration.Links);
    }

    [Fact]
    public async Task A_new_identity_is_created_linked_and_restricted_to_sso()
    {
        var created = UserNamed("newcomer");
        _userManager.CreateUserAsync("newcomer").Returns(created);

        var (userId, username) = await _provisioner.ResolveAsync(
            _provider, Identity("sub-123", "newcomer", "media"));

        Assert.Equal(created.Id, userId);
        Assert.Equal("newcomer", username);

        var policy = CapturedPolicy(created.Id);
        Assert.Equal(typeof(SsoAuthenticationProvider).FullName, policy.AuthenticationProviderId);
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);

        var link = Assert.Single(_harness.Configuration.Links);
        Assert.Equal("provider-1", link.ProviderId);
        Assert.Equal("sub-123", link.Sub);
        Assert.Equal(created.Id, link.JellyfinUserId);
    }

    [Fact]
    public async Task A_stale_link_is_dropped_and_the_identity_is_provisioned_again()
    {
        var goneUserId = Guid.NewGuid();
        _userManager.GetUserById(goneUserId).Returns((User?)null);
        _harness.Configuration.Links.Add(new LinkConfig
        {
            ProviderId = "provider-1",
            Sub = "ben",
            JellyfinUserId = goneUserId,
        });
        var created = UserNamed("ben");
        _userManager.CreateUserAsync("ben").Returns(created);

        var (userId, _) = await _provisioner.ResolveAsync(_provider, Identity("ben", "ben"));

        Assert.Equal(created.Id, userId);
        var link = Assert.Single(_harness.Configuration.Links);
        Assert.Equal(created.Id, link.JellyfinUserId);
    }

    [Fact]
    public async Task Groups_seen_at_login_are_remembered_for_the_admin_ui()
    {
        var created = UserNamed("newcomer");
        _userManager.CreateUserAsync("newcomer").Returns(created);
        _harness.Configuration.SeenGroups.Add("media");

        await _provisioner.ResolveAsync(_provider, Identity("sub-123", "newcomer", "media", "admins"));

        Assert.Equal(["media", "admins"], _harness.Configuration.SeenGroups);
    }

    private UserPolicy CapturedPolicy(Guid userId)
    {
        var calls = _userManager.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IUserManager.UpdatePolicyAsync))
            .Select(c => c.GetArguments())
            .Where(args => Equals(args[0], userId))
            .ToList();

        Assert.NotEmpty(calls);
        return Assert.IsType<UserPolicy>(calls[^1][1]);
    }

    public void Dispose() => _harness.Dispose();
}
