using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A logout the provider announces has to reach the right account and no other. The
/// identity is matched the same way a login matches it — by (provider, sub) — so the tests
/// here mirror the provisioning ones: never by name, never across providers.
/// </summary>
public sealed class SessionRevokerTests : IDisposable
{
    private readonly PluginHarness _harness = new();
    private readonly ISessionManager _sessions = Substitute.For<ISessionManager>();
    private readonly SessionRevoker _revoker;

    public SessionRevokerTests()
    {
        _revoker = new SessionRevoker(_sessions, NullLogger<SessionRevoker>.Instance);
    }

    private void Link(string providerId, string sub, Guid userId)
        => _harness.Configuration.Links.Add(new LinkConfig
        {
            ProviderId = providerId,
            Sub = sub,
            JellyfinUserId = userId,
        });

    [Fact]
    public async Task The_linked_accounts_tokens_are_revoked()
    {
        var userId = Guid.NewGuid();
        Link("provider-1", "ben", userId);

        var revoked = await _revoker.RevokeAsync("provider-1", "ben");

        Assert.Equal(1, revoked);
        await _sessions.Received(1).RevokeUserTokens(userId, null);
    }

    [Fact]
    public async Task An_identity_nobody_linked_revokes_nothing()
    {
        Link("provider-1", "ben", Guid.NewGuid());

        var revoked = await _revoker.RevokeAsync("provider-1", "stranger");

        Assert.Equal(0, revoked);
        await _sessions.DidNotReceiveWithAnyArgs().RevokeUserTokens(default, default);
    }

    [Fact]
    public async Task The_same_subject_at_another_provider_is_a_different_person()
    {
        Link("provider-2", "ben", Guid.NewGuid());

        var revoked = await _revoker.RevokeAsync("provider-1", "ben");

        Assert.Equal(0, revoked);
        await _sessions.DidNotReceiveWithAnyArgs().RevokeUserTokens(default, default);
    }

    [Fact]
    public async Task A_subject_is_matched_exactly_and_not_by_case()
    {
        Link("provider-1", "Ben", Guid.NewGuid());

        var revoked = await _revoker.RevokeAsync("provider-1", "ben");

        Assert.Equal(0, revoked);
    }

    [Fact]
    public async Task Two_accounts_linked_to_one_identity_are_both_signed_out()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Link("provider-1", "ben", first);
        Link("provider-1", "ben", second);

        var revoked = await _revoker.RevokeAsync("provider-1", "ben");

        Assert.Equal(2, revoked);
        await _sessions.Received(1).RevokeUserTokens(first, null);
        await _sessions.Received(1).RevokeUserTokens(second, null);
    }

    [Fact]
    public async Task A_duplicate_link_does_not_revoke_twice()
    {
        var userId = Guid.NewGuid();
        Link("provider-1", "ben", userId);
        Link("provider-1", "ben", userId);

        var revoked = await _revoker.RevokeAsync("provider-1", "ben");

        Assert.Equal(1, revoked);
        await _sessions.Received(1).RevokeUserTokens(userId, null);
    }

    public void Dispose() => _harness.Dispose();
}
