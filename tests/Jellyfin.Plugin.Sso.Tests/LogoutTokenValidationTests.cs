using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A back-channel logout endpoint has to be anonymous — the caller is the provider's back
/// end, not a browser with a Jellyfin token — so the logout token is the only thing
/// standing between a provider ending a session and anyone on the network ending one.
/// Each test here is one of the rules from OpenID Connect Back-Channel Logout 1.0 §2.6.
/// </summary>
public sealed class LogoutTokenValidationTests : IDisposable
{
    private readonly IdTokenFactory _provider = new();
    private readonly ProviderConfig _config = new()
    {
        Id = "provider-1",
        ClientId = IdTokenFactory.Audience,
    };

    private LogoutSubject Validate(string token, IdTokenFactory? signer = null)
        => LogoutTokenValidator.Validate(
            _config, IdTokenFactory.Issuer, [(signer ?? _provider).SigningKey], token);

    [Fact]
    public void A_well_formed_token_names_the_subject_that_signed_out()
    {
        var subject = Validate(_provider.MintLogoutToken(sub: "ben", sid: "session-7"));

        Assert.Equal("ben", subject.Sub);
        Assert.Equal("session-7", subject.Sid);
    }

    [Fact]
    public void A_token_without_an_expiry_is_still_accepted()
    {
        // The specification leaves exp optional; the age of iat is what bounds the token.
        var subject = Validate(_provider.MintLogoutToken(lifetime: null));

        Assert.Equal("ben", subject.Sub);
    }

    [Fact]
    public void A_token_signed_by_somebody_else_is_rejected()
    {
        using var stranger = new IdTokenFactory("stranger-key");

        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(stranger.MintLogoutToken(), signer: _provider));
    }

    [Fact]
    public void A_token_for_another_client_is_rejected()
    {
        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(audience: "some-other-client")));
    }

    [Fact]
    public void A_token_from_another_issuer_is_rejected()
    {
        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(issuer: "https://evil.example.com")));
    }

    [Fact]
    public void An_id_token_cannot_be_replayed_as_a_logout_token()
    {
        // The nonce is what gives it away: a logout token must not carry one.
        var ex = Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(nonce: "login-nonce")));

        Assert.Contains("nonce", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_without_the_logout_event_is_rejected()
    {
        var ex = Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(withEvents: false)));

        Assert.Contains("events", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_whose_event_is_a_different_one_is_rejected()
    {
        var other = new Dictionary<string, object>
        {
            ["http://schemas.openid.net/event/something-else"] = new Dictionary<string, object>(),
        };

        Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(events: other)));
    }

    [Fact]
    public void A_token_whose_events_claim_is_not_an_object_is_rejected()
    {
        Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(events: LogoutTokenValidator.EventName)));
    }

    [Fact]
    public void A_token_without_iat_is_rejected()
    {
        var ex = Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(withIssuedAt: false)));

        Assert.Contains("iat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_captured_earlier_cannot_be_replayed_later()
    {
        var ex = Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(age: LogoutTokenValidator.MaxAge
                + TimeSpan.FromMinutes(1))));

        Assert.Contains("too old", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_within_the_allowed_age_is_accepted()
    {
        var subject = Validate(_provider.MintLogoutToken(
            age: LogoutTokenValidator.MaxAge - TimeSpan.FromMinutes(1)));

        Assert.Equal("ben", subject.Sub);
    }

    [Fact]
    public void A_token_issued_in_the_future_is_rejected()
    {
        var ex = Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(age: TimeSpan.FromMinutes(-10))));

        Assert.Contains("future", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_that_names_neither_a_subject_nor_a_session_is_rejected()
    {
        Assert.Throws<SecurityTokenException>(
            () => Validate(_provider.MintLogoutToken(sub: null, sid: null)));
    }

    [Fact]
    public void A_token_that_names_only_a_session_is_accepted_but_carries_no_subject()
    {
        var subject = Validate(_provider.MintLogoutToken(sub: null, sid: "session-7"));

        Assert.Null(subject.Sub);
        Assert.Equal("session-7", subject.Sid);
    }

    [Fact]
    public void A_blank_subject_counts_as_no_subject()
    {
        var subject = Validate(_provider.MintLogoutToken(sub: "   ", sid: "session-7"));

        Assert.Null(subject.Sub);
    }

    public void Dispose() => _provider.Dispose();
}
