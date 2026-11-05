using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Unlinking has to survive its own failure.
///
/// Back-channel logout finds an account through its link and nothing else, so the order of
/// the two steps decides what a failure leaves behind. Removing the link first and then
/// revoking meant a revocation that threw — a database that is busy, or a client that
/// closed the connection and cancelled the request — left a session nothing could end: the
/// provider cannot reach it, the plugin cannot find it, and Jellyfin's tokens do not
/// expire. The retry answered "no link found".
///
/// Nothing in the suite had ever made a write fail. One substitute in five hundred tests
/// threw, on a read.
/// </summary>
public sealed class UnlinkingTests : IDisposable
{
    private static readonly Guid Account = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly PluginHarness _harness = new();
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly Unlinking _unlinking;

    public UnlinkingTests()
    {
        _unlinking = new Unlinking(
            new SessionRevoker(_sessions, NullLogger<SessionRevoker>.Instance),
            NullLogger.Instance);

        _harness.Configuration.Links.Add(new LinkConfig
        {
            ProviderId = "authentik",
            Sub = "sub-1",
            JellyfinUserId = Account,
        });
    }

    [Fact]
    public async Task An_unlink_removes_the_link_and_ends_the_sessions()
    {
        Assert.True(await _unlinking.RemoveAsync(Account, "authentik", default));

        Assert.Empty(_harness.Configuration.Links);
        await _sessions.Received().RevokeUserTokens(Account, null);
    }

    /// <summary>
    /// The sessions are ended once before the link goes and once after, so a login that
    /// completed in between does not leave a token behind the removal.
    /// </summary>
    [Fact]
    public async Task The_sessions_are_ended_on_both_sides_of_the_removal()
    {
        await _unlinking.RemoveAsync(Account, "authentik", default);

        await _sessions.Received(2).RevokeUserTokens(Account, null);
    }

    /// <summary>
    /// The finding: a revocation that fails must leave something to retry. Before, the
    /// link was already gone by this point.
    /// </summary>
    [Fact]
    public async Task A_revocation_that_fails_leaves_the_link_in_place()
    {
        _sessions.RevokeUserTokens(Account, null).ThrowsAsync(new InvalidOperationException("busy"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _unlinking.RemoveAsync(Account, "authentik", default));

        Assert.Single(_harness.Configuration.Links);
    }

    [Fact]
    public async Task And_the_retry_finishes_what_the_failure_left()
    {
        var attempts = 0;
        _sessions.RevokeUserTokens(Account, null).Returns(_ =>
            ++attempts == 1
                ? Task.FromException(new InvalidOperationException("busy"))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _unlinking.RemoveAsync(Account, "authentik", default));
        Assert.Single(_harness.Configuration.Links);

        Assert.True(await _unlinking.RemoveAsync(Account, "authentik", default));
        Assert.Empty(_harness.Configuration.Links);
    }

    /// <summary>
    /// A client that closes the connection cancels the request's token, and that used to
    /// land between the removal and the revocation.
    /// </summary>
    [Fact]
    public async Task A_cancelled_request_leaves_the_link_in_place()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _unlinking.RemoveAsync(Account, "authentik", cancelled.Token));

        Assert.Single(_harness.Configuration.Links);
    }

    [Fact]
    public async Task An_account_with_no_such_link_is_left_alone()
    {
        Assert.False(await _unlinking.RemoveAsync(Account, "keycloak", default));

        Assert.Single(_harness.Configuration.Links);
        await _sessions.DidNotReceiveWithAnyArgs().RevokeUserTokens(default, default);
    }

    [Fact]
    public async Task An_administrators_unlink_takes_every_provider()
    {
        _harness.Configuration.Links.Add(new LinkConfig
        {
            ProviderId = "keycloak",
            Sub = "sub-2",
            JellyfinUserId = Account,
        });

        Assert.True(await _unlinking.RemoveAsync(Account, providerId: null, default));

        Assert.Empty(_harness.Configuration.Links);
    }

    public void Dispose() => _harness.Dispose();
}
