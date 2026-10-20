using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// The ID token is the only evidence the plugin has that a login is genuine, so every
/// rejection path gets its own test. A regression here hands out Jellyfin accounts.
/// </summary>
public sealed class IdTokenValidationTests : IDisposable
{
    private readonly IdTokenFactory _idp = new();
    private readonly ProviderConfig _provider = new()
    {
        Name = "Test",
        Authority = IdTokenFactory.Issuer,
        ClientId = IdTokenFactory.Audience,
        GroupsClaim = "groups",
    };

    private (string Sub, string Username, List<string> Groups, string? Picture) Validate(
        string idToken, string expectedNonce = "test-nonce", SecurityKey? key = null)
        => ClaimsExtractor.ValidateIdToken(
            _provider,
            IdTokenFactory.Issuer,
            [key ?? _idp.SigningKey],
            idToken,
            expectedNonce);

    [Fact]
    public void A_valid_token_yields_sub_username_and_groups()
    {
        var token = _idp.Mint(sub: "ben", preferredUsername: "ben", groups: ["media", "admins"]);

        var (sub, username, groups, _) = Validate(token);

        Assert.Equal("ben", sub);
        Assert.Equal("ben", username);
        Assert.Equal(["media", "admins"], groups);
    }

    [Fact]
    public void A_token_signed_by_an_untrusted_key_is_rejected()
    {
        using var attacker = new IdTokenFactory("attacker-key");
        var forged = attacker.Mint();

        // Validated against the real provider's key, not the attacker's.
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(forged));
    }

    [Fact]
    public void An_unsigned_token_is_rejected()
    {
        // alg=none: the classic JWT downgrade attempt.
        var unsigned = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0."
            + "eyJzdWIiOiJiZW4iLCJub25jZSI6InRlc3Qtbm9uY2UiLCJpc3MiOiJodHRwczovL2lkcC5leGFtcGxlLmNvbS9hcHBsaWNhdGlvbi9vL2plbGx5ZmluIiwiYXVkIjoiamVsbHlmaW4tY2xpZW50LWlkIn0.";

        Assert.ThrowsAny<Exception>(() => Validate(unsigned));
    }

    [Fact]
    public void A_token_for_another_client_is_rejected()
    {
        var token = _idp.Mint(audience: "some-other-app");

        Assert.ThrowsAny<SecurityTokenInvalidAudienceException>(() => Validate(token));
    }

    [Fact]
    public void A_token_from_another_issuer_is_rejected()
    {
        var token = _idp.Mint(issuer: "https://evil.example.com/");

        Assert.ThrowsAny<SecurityTokenInvalidIssuerException>(() => Validate(token));
    }

    [Fact]
    public void An_expired_token_is_rejected()
    {
        // Beyond the 60 second clock skew the validator allows.
        var token = _idp.Mint(lifetime: TimeSpan.FromMinutes(-5));

        Assert.ThrowsAny<SecurityTokenExpiredException>(() => Validate(token));
    }

    [Fact]
    public void A_replayed_token_from_another_login_is_rejected()
    {
        var token = _idp.Mint(nonce: "nonce-of-a-different-login");

        var ex = Assert.Throws<SecurityTokenException>(() => Validate(token, expectedNonce: "test-nonce"));
        Assert.Contains("nonce", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_token_without_a_nonce_is_rejected()
    {
        var token = _idp.Mint(nonce: null);

        Assert.Throws<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void A_token_without_a_sub_is_rejected()
    {
        var token = _idp.Mint(sub: null!);

        var ex = Assert.Throws<SecurityTokenException>(() => Validate(token));
        Assert.Contains("sub", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_username_falls_back_to_the_sub_when_the_idp_sends_no_preferred_username()
    {
        var token = _idp.Mint(sub: "90b1e0f2", preferredUsername: null);

        var (sub, username, _, _) = Validate(token);

        Assert.Equal("90b1e0f2", sub);
        Assert.Equal("90b1e0f2", username);
    }

    [Fact]
    public void The_username_falls_back_to_the_name_claim_before_the_sub()
    {
        var token = _idp.Mint(sub: "90b1e0f2", preferredUsername: null, name: "Ben Beckmann");

        var (_, username, _, _) = Validate(token);

        Assert.Equal("Ben Beckmann", username);
    }

    [Fact]
    public void A_preferred_username_wins_over_the_name_claim()
    {
        var token = _idp.Mint(sub: "90b1e0f2", preferredUsername: "ben", name: "Ben Beckmann");

        var (_, username, _, _) = Validate(token);

        Assert.Equal("ben", username);
    }

    [Fact]
    public void Claims_under_a_different_name_are_not_read_as_groups()
    {
        // The provider is configured for "groups"; a "roles" claim must not leak in,
        // otherwise an IdP-side rename would silently grant library access.
        var token = _idp.Mint(groups: ["admins"], groupsClaim: "roles");

        var (_, _, groups, _) = Validate(token);

        Assert.Empty(groups);
    }

    [Fact]
    public void A_configured_groups_claim_name_is_honoured()
    {
        _provider.GroupsClaim = "jellyfin_roles";
        var token = _idp.Mint(groups: ["media"], groupsClaim: "jellyfin_roles");

        var (_, _, groups, _) = Validate(token);

        Assert.Equal(["media"], groups);
    }

    public void Dispose() => _idp.Dispose();
}
