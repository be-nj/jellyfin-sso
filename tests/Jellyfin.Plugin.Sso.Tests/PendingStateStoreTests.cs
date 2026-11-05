using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The pending state carries the CSRF token, the nonce and the PKCE verifier between the
/// authorize redirect and the callback.
/// </summary>
public sealed class PendingStateStoreTests
{
    private readonly PendingStateStore _store = new(TimeSpan.FromMinutes(10));

    /// <summary>A browser id of the shape the cookie carries: 32 bytes, url-safe base64.</summary>
    private const string Browser = "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWY";
    private const string OtherBrowser = "MDEyMzQ1Njc4OWFiY2RlZmdoaWprbG1ub3BxcnN0dXY";

    [Fact]
    public void An_issued_state_can_be_consumed_once()
    {
        var (state, pending) = _store.Issue("provider-1", "https://jf.example.com/sso/oidc/callback/provider-1", Browser);

        var consumed = _store.Consume(state, Browser);

        Assert.NotNull(consumed);
        Assert.Equal(pending.Nonce, consumed.Nonce);
        Assert.Equal(pending.CodeVerifier, consumed.CodeVerifier);
        Assert.Null(_store.Consume(state, Browser));
    }

    [Fact]
    public void An_unknown_state_is_not_accepted()
    {
        _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        Assert.Null(_store.Consume("forged-state", Browser));
    }

    [Fact]
    public void State_nonce_and_verifier_are_distinct_per_login()
    {
        var (firstState, first) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);
        var (secondState, second) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        Assert.NotEqual(firstState, secondState);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        // The three values must not be derived from one another.
        Assert.NotEqual(firstState, first.Nonce);
        Assert.NotEqual(firstState, first.CodeVerifier);
    }

    [Fact]
    public void The_quick_connect_code_is_carried_through_the_login()
    {
        var (state, _) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser, "ABC123");

        Assert.Equal("ABC123", _store.Consume(state, Browser)!.QuickConnectCode);
    }

    [Fact]
    public void Issued_values_are_url_safe_base64()
    {
        var (state, pending) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        foreach (var value in new[] { state, pending.Nonce, pending.CodeVerifier })
        {
            Assert.DoesNotContain('+', value);
            Assert.DoesNotContain('/', value);
            Assert.DoesNotContain('=', value);
        }
    }

    [Fact]
    public void The_code_challenge_matches_the_RFC_7636_S256_example()
    {
        // RFC 7636, appendix B: the reference verifier and its expected challenge.
        const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string Expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        Assert.Equal(Expected, PendingStateStore.CodeChallenge(Verifier));
    }

    /// <summary>
    /// At the cap the store keeps what it has and refuses what is new. It used to drop the
    /// oldest, and every entry has the same ten-minute life — so expiry order is arrival
    /// order and the oldest is the person still typing their password at the provider.
    /// Anybody can start a login, which made that a way to throw a chosen person out of a
    /// flow they were in the middle of, at a chosen moment, looking like their own
    /// mistake. A flood now denies only logins that have not started.
    /// </summary>
    [Fact]
    public void A_full_store_keeps_the_logins_it_has_and_refuses_new_ones()
    {
        var store = new PendingStateStore(TimeSpan.FromMinutes(10));

        var (first, _) = store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        for (var i = 1; i < PendingStateStore.MaxPending; i++)
        {
            store.Issue("provider-1", "https://jf.example.com/callback", OtherBrowser);
        }

        Assert.Throws<PendingStateStore.TooManyPendingLoginsException>(
            () => store.Issue("provider-1", "https://jf.example.com/callback", OtherBrowser));

        // The login that was already in flight is still there, which is the point.
        Assert.NotNull(store.Consume(first, Browser));
    }

    /// <summary>Room freed by expiry is room again; the refusal is not permanent.</summary>
    [Fact]
    public void A_store_that_has_emptied_accepts_logins_again()
    {
        var store = new PendingStateStore(TimeSpan.FromMilliseconds(1));

        for (var i = 0; i < PendingStateStore.MaxPending; i++)
        {
            store.Issue("provider-1", "https://jf.example.com/callback", Browser);
        }

        Thread.Sleep(20);

        var (state, _) = store.Issue("provider-1", "https://jf.example.com/callback", Browser);
        Assert.NotNull(store.Consume(state, Browser));
    }

    /// <summary>
    /// The state travels through the identity provider, so holding one proves nothing about
    /// who started the login. Without this check an attacker could start a flow, keep the
    /// callback URL and have somebody else finish it — in the linking flow that attaches
    /// their identity to the attacker's account.
    /// </summary>
    [Fact]
    public void A_state_presented_by_another_browser_is_refused()
    {
        var (state, _) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        Assert.Null(_store.Consume(state, OtherBrowser));
    }

    [Fact]
    public void A_state_presented_without_a_browser_id_is_refused()
    {
        var (state, _) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        Assert.Null(_store.Consume(state, null));
    }

    /// <summary>Spent either way: a wrong browser must not leave the state for a retry.</summary>
    [Fact]
    public void A_state_refused_for_the_wrong_browser_cannot_be_used_by_the_right_one()
    {
        var (state, _) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        Assert.Null(_store.Consume(state, OtherBrowser));
        Assert.Null(_store.Consume(state, Browser));
    }

    /// <summary>
    /// Two logins in two tabs share one cookie, so the id has to be reused rather than
    /// regenerated — otherwise starting the second login would invalidate the first.
    /// </summary>
    [Fact]
    public void A_browser_that_already_has_an_id_keeps_it()
    {
        Assert.Equal(Browser, PendingStateStore.BrowserId(Browser));

        var (firstState, _) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);
        var (secondState, _) = _store.Issue("provider-1", "https://jf.example.com/callback", Browser);

        Assert.NotNull(_store.Consume(secondState, Browser));
        Assert.NotNull(_store.Consume(firstState, Browser));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWY=")]
    [InlineData("QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZS+")]
    public void A_cookie_that_is_not_a_browser_id_yields_a_fresh_one(string? cookie)
    {
        var generated = PendingStateStore.BrowserId(cookie);

        Assert.NotEqual(cookie, generated);
        Assert.True(PendingStateStore.WellFormedBrowserId(generated));
    }
}
