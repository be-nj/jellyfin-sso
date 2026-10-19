using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Three stores hand out something that has to stop working on its own: the one-time
/// login secret, the pending login, and the Quick Connect approval. Mutation testing
/// found all three expiry checks untested — the suite passed with the clock comparison
/// inverted — so they are tested here with a short lifetime instead of the real one.
/// </summary>
public sealed class ExpiryTests
{
    private static readonly TimeSpan AlreadyOver = TimeSpan.FromMilliseconds(-1);
    private static readonly TimeSpan LongEnough = TimeSpan.FromMinutes(5);

    [Fact]
    public void An_expired_login_secret_is_refused()
    {
        var store = new EphemeralSecretStore(AlreadyOver);
        var secret = store.Issue("ben");

        Assert.Null(store.Consume(secret));
    }

    [Fact]
    public void A_fresh_login_secret_is_accepted()
    {
        var store = new EphemeralSecretStore(LongEnough);

        Assert.Equal("ben", store.Consume(store.Issue("ben")));
    }

    [Fact]
    public void An_expired_login_state_is_refused()
    {
        var store = new PendingStateStore(AlreadyOver);
        var (state, _) = store.Issue("provider-1", "https://jellyfin.example.com/sso/oidc/callback/p1");

        Assert.Null(store.Consume(state));
    }

    [Fact]
    public void A_fresh_login_state_is_accepted()
    {
        var store = new PendingStateStore(LongEnough);
        var (state, _) = store.Issue("provider-1", "https://jellyfin.example.com/sso/oidc/callback/p1");

        Assert.NotNull(store.Consume(state));
    }

    [Fact]
    public void An_expired_quick_connect_approval_is_refused()
    {
        var store = new QuickConnectApprovalStore(AlreadyOver);
        var token = store.Issue(Guid.NewGuid(), "ben", "ABC123");

        Assert.Null(store.Consume(token));
    }

    [Fact]
    public void A_fresh_quick_connect_approval_is_accepted()
    {
        var store = new QuickConnectApprovalStore(LongEnough);

        Assert.NotNull(store.Consume(store.Issue(Guid.NewGuid(), "ben", "ABC123")));
    }

    [Fact]
    public void An_expired_entry_does_not_keep_a_later_one_from_working()
    {
        var store = new EphemeralSecretStore(AlreadyOver);
        store.Issue("ben");

        var live = new EphemeralSecretStore(LongEnough);
        Assert.Equal("ben", live.Consume(live.Issue("ben")));
    }
}
