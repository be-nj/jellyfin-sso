using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Two logins at the same moment.
///
/// Every invariant this plugin advertises about accounts — the last administrator is never
/// demoted, one identity per account per provider — was written as a check followed by a
/// write, with nothing between them saying that no other login may run in between. The
/// suite had 677 tests and not one of them ran two things at once, so the checks held in
/// every test and in no particular order of events.
///
/// These tests are deliberately about the invariant and not about the interleaving: they
/// force the overlap, then ask what the server ended up with. A rendezvous with a timeout
/// does the forcing, so that once the overlap is impossible the test waits briefly, gives
/// up and still asserts the outcome.
/// </summary>
public sealed class ConcurrentAccountChangeTests : IDisposable
{
    private static readonly TimeSpan Rendezvous = TimeSpan.FromMilliseconds(500);

    private readonly PluginHarness _harness = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly RecordingLogger<PermissionSync> _log = new();

    private readonly SemaphoreSlim _second = new(0);
    private int _readers;

    private User Administrator(string name)
    {
        var user = new User(
            name,
            "Emby.Server.Implementations.Library.DefaultAuthenticationProvider",
            "Emby.Server.Implementations.Library.DefaultPasswordResetProvider");
        user.SetPermission(PermissionKind.IsAdministrator, true);
        _userManager.GetUserById(user.Id).Returns(user);
        return user;
    }

    /// <summary>
    /// The two administrators as the server would answer them, every time it is asked —
    /// and the first caller waits here for the second, so that both have read the list
    /// before either writes. Once the two cannot overlap, the wait times out and the
    /// second caller reads what the first wrote, which is the whole point of serialising.
    /// </summary>
    private User[] BothAdministrators(User first, User second)
    {
        if (Interlocked.Increment(ref _readers) == 1)
        {
            _second.Wait(Rendezvous);
        }
        else
        {
            _second.Release();
        }

        return [first, second];
    }

    /// <summary>
    /// With no administrator group mapped, both of the last two administrators are
    /// candidates for demotion, and each sees the other as the one that will be left. The
    /// guard the threat model advertises — "the last administrator is never demoted" —
    /// therefore held only as long as nobody logged in at the same time as somebody else.
    /// Demoting both locks everybody out of the server.
    /// </summary>
    [Fact]
    public async Task The_last_two_administrators_cannot_both_be_demoted_at_once()
    {
        var first = Administrator("first-admin");
        var second = Administrator("second-admin");

        _userManager.GetUsers().Returns(_ => BothAdministrators(first, second));

        // The server answers with the account's policy as it stands, and a write changes
        // what the next read sees — which is what makes a serialised second sync correct
        // rather than merely later.
        _userManager.GetUserDto(Arg.Any<User>()).Returns(call =>
        {
            var user = call.Arg<User>();
            return new UserDto
            {
                Policy = new UserPolicy
                {
                    IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator),
                },
            };
        });

        _userManager.UpdatePolicyAsync(Arg.Any<Guid>(), Arg.Any<UserPolicy>())
            .Returns(call =>
            {
                var id = call.Arg<Guid>();
                var policy = call.Arg<UserPolicy>();
                var user = id == first.Id ? first : second;
                user.SetPermission(PermissionKind.IsAdministrator, policy.IsAdministrator);
                return Task.CompletedTask;
            });

        // A mapping that grants libraries and no administrator rights: the group is matched,
        // so this is an ordinary login rather than a refusal, and nothing in it says admin.
        _harness.Configuration.GroupMappings.Add(new GroupMappingConfig { Group = "media" });

        var sync = new PermissionSync(_userManager, _log);
        var identity = new OidcIdentity("sub-1", "ben", ["media"]);

        await Task.WhenAll(
            Task.Run(() => sync.SyncAsync(first.Id, identity)),
            Task.Run(() => sync.SyncAsync(second.Id, identity)));

        Assert.True(
            first.HasPermission(PermissionKind.IsAdministrator)
            || second.HasPermission(PermissionKind.IsAdministrator),
            "both administrators were demoted, which locks everybody out of the server");
    }

    /// <summary>
    /// Two link flows finishing together, for one account and one provider, with different
    /// subjects. One of them has to lose: the plugin keeps one identity per account per
    /// provider, and two links mean the account an identity reaches depends on the order of
    /// a list.
    ///
    /// Forced rather than hoped for. The first version of this test started both callbacks
    /// from a barrier and ran two hundred rounds, and it passed against the broken code
    /// every time — the window between deciding and writing is a few instructions wide, and
    /// two threads simply do not land in it. So the test holds the configuration lock
    /// itself: both callbacks reach their decision while it is held, which is the state the
    /// bug needs, and releasing it lets them act on what they decided. A decision taken
    /// outside that lock is then visibly stale.
    /// </summary>
    [Fact]
    public async Task Two_link_callbacks_for_one_account_leave_one_link()
    {
        var account = Guid.NewGuid();
        Task<SelfServiceLinkDecision> first, second;

        lock (Plugin.ConfigurationLock)
        {
            first = Task.Run(() => SelfServiceLinks.Attach(
                _harness.Configuration, "authentik", "sub-a", account));
            second = Task.Run(() => SelfServiceLinks.Attach(
                _harness.Configuration, "authentik", "sub-b", account));

            // Sleep, not await: this is a lock, and both callbacks have to arrive while it
            // is still held.
            Thread.Sleep(200);
        }

        var decisions = await Task.WhenAll(first, second);

        Assert.Single(_harness.Configuration.Links);
        Assert.Equal(1, decisions.Count(d => d.Outcome == SelfServiceLinkOutcome.Write));
    }

    /// <summary>
    /// The same from the other side: one identity, two accounts asking for it. Only one may
    /// hold it, or the loser's Jellyfin account is reachable by somebody else's provider
    /// account.
    /// </summary>
    [Fact]
    public async Task Two_accounts_claiming_one_identity_leave_one_owner()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        Task<SelfServiceLinkDecision> first, second;

        lock (Plugin.ConfigurationLock)
        {
            first = Task.Run(() => SelfServiceLinks.Attach(
                _harness.Configuration, "authentik", "sub-contested", alice));
            second = Task.Run(() => SelfServiceLinks.Attach(
                _harness.Configuration, "authentik", "sub-contested", bob));

            Thread.Sleep(200);
        }

        var decisions = await Task.WhenAll(first, second);

        var link = Assert.Single(_harness.Configuration.Links);
        Assert.Contains(link.JellyfinUserId, new[] { alice, bob });
        Assert.Equal(1, decisions.Count(d => d.Outcome == SelfServiceLinkOutcome.Write));
    }

    public void Dispose()
    {
        _second.Dispose();
        _harness.Dispose();
    }
}
