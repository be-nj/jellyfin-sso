using Jellyfin.Plugin.Sso.Oidc;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The pending state carries the CSRF token, the nonce and the PKCE verifier between the
/// authorize redirect and the callback.
/// </summary>
public sealed class PendingStateStoreTests
{
    private readonly PendingStateStore _store = new();

    [Fact]
    public void An_issued_state_can_be_consumed_once()
    {
        var (state, pending) = _store.Issue("provider-1", "https://jf.example.com/sso/oidc/callback/provider-1");

        var consumed = _store.Consume(state);

        Assert.NotNull(consumed);
        Assert.Equal(pending.Nonce, consumed.Nonce);
        Assert.Equal(pending.CodeVerifier, consumed.CodeVerifier);
        Assert.Null(_store.Consume(state));
    }

    [Fact]
    public void An_unknown_state_is_not_accepted()
    {
        _store.Issue("provider-1", "https://jf.example.com/callback");

        Assert.Null(_store.Consume("forged-state"));
    }

    [Fact]
    public void State_nonce_and_verifier_are_distinct_per_login()
    {
        var (firstState, first) = _store.Issue("provider-1", "https://jf.example.com/callback");
        var (secondState, second) = _store.Issue("provider-1", "https://jf.example.com/callback");

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
        var (state, _) = _store.Issue("provider-1", "https://jf.example.com/callback", "ABC123");

        Assert.Equal("ABC123", _store.Consume(state)!.QuickConnectCode);
    }

    [Fact]
    public void Issued_values_are_url_safe_base64()
    {
        var (state, pending) = _store.Issue("provider-1", "https://jf.example.com/callback");

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
}
