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

    /// <summary>
    /// An empty subject used to be accepted, and an empty string is a perfectly good key:
    /// the first login with one created the link, and every later login with one matched
    /// it and landed in that first person's account — two legitimate users of the same
    /// provider sharing one Jellyfin account, including its administrator flag. The logout
    /// token validator has always treated blank as absent; the login side did not.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\u00a0")]
    public void A_token_whose_sub_is_blank_is_rejected(string sub)
    {
        var token = _idp.Mint(sub: sub);

        var ex = Assert.Throws<SecurityTokenException>(() => Validate(token));
        Assert.Contains("sub", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The message has to point at the cause: this is a provider mapping, not a bug.
        Assert.Contains("maps to", ex.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// The identity claims in shapes `UserInfoClaimsTests` has always treated as routine
    /// for the other transport: an array where a string belongs, an object, a number, the
    /// same claim twice. Mint could only emit strings, so none of this had ever reached
    /// the ID-token path — which is the asymmetry that let a blank subject through.
    ///
    /// What matters is that each one is refused or resolved deliberately, never that it
    /// throws some particular exception: a provider sending a strange shape must not end
    /// up signing somebody in as somebody else.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_subject_that_is_not_one_value_is_not_an_identity(bool asArray)
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["sub"] = asArray
            ? new[] { "ben", "mallory" }
            : new Dictionary<string, string> { ["id"] = "ben" };

        // The library refuses the token before this plugin sees it — a structure where a
        // registered claim belongs is not a token it will parse. The property is that no
        // identity comes out, not which exception says so, which is why this does not
        // pin the type.
        Assert.ThrowsAny<Exception>(() => Validate(_idp.MintRaw(payload)));
    }

    /// <summary>
    /// A number is a legitimate JSON value and some providers do send numeric subjects, so
    /// this one resolves rather than throwing — and it must resolve to the number.
    /// </summary>
    [Fact]
    public void A_numeric_subject_is_read_as_its_digits()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["sub"] = 90210;

        var (sub, _, _, _) = Validate(_idp.MintRaw(payload));

        Assert.Equal("90210", sub);
    }

    [Fact]
    public void A_username_that_is_an_object_does_not_become_the_username()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["preferred_username"] = new Dictionary<string, string> { ["given"] = "ben" };

        var (sub, username, _, _) = Validate(_idp.MintRaw(payload));

        // The subject stands in, because a structure is not a name. Before this, the
        // account was called {"given":"ben"} — the claim's JSON text, verbatim.
        Assert.Equal("ben", sub);
        Assert.Equal("ben", username);
    }

    /// <summary>
    /// A groups claim that is one string rather than an array. Some providers send a
    /// space-delimited list, and the plugin must not read it as a single group with a
    /// space in it — or, if it does, the mapping simply never matches, which is the safe
    /// direction. Either way, no group is invented.
    /// </summary>
    [Fact]
    public void A_space_delimited_groups_claim_does_not_invent_groups()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["groups"] = "media jellyfin-admins";

        var (_, _, groups, _) = Validate(_idp.MintRaw(payload));

        Assert.DoesNotContain("jellyfin-admins", groups);
    }

    /// <summary>
    /// An ID token with no issued-at time. OpenID Connect Core lists iat among the
    /// required claims (§2) and tells the client to validate it (§3.1.3.7, step 9);
    /// Microsoft's handler has no switch for it, so nothing refused one until the
    /// conformance suite pointed it out — it saw the plugin go on and call the userinfo
    /// endpoint with a token that had no iat.
    /// </summary>
    [Fact]
    public void A_token_with_no_issued_at_time_is_refused()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload.Remove("iat");

        var error = Assert.Throws<SecurityTokenException>(() => Validate(_idp.MintRaw(payload)));

        Assert.Contains("iat", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An issued-at time that is a word. The check written here never sees it: the
    /// library refuses to decode a payload whose iat is a string at all, with
    /// "IDX12723: Unable to decode the payload", and that is an ArgumentException rather
    /// than a token exception. It still ends as a refusal, because the callback makes
    /// one from both types — which is the property worth asserting, since it is the one
    /// that decides whether the visitor sees "refused" or a 500 with a library message
    /// in the body.
    /// </summary>
    [Fact]
    public void An_issued_at_time_that_is_not_a_number_is_refused()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["iat"] = "yesterday";

        var error = Record.Exception(() => Validate(_idp.MintRaw(payload)));

        Assert.NotNull(error);
        Assert.True(
            error is SecurityTokenException or ArgumentException,
            $"A bad iat has to fail as a refusal, not as {error.GetType().Name}.");
    }

    /// <summary>
    /// A token issued further in the future than the clock skew allows. Not a lifetime
    /// question — exp is still valid here — but a claim about the past that cannot be
    /// true, and the sort of thing a provider with a badly set clock or a replayed test
    /// fixture produces.
    /// </summary>
    [Fact]
    public void A_token_issued_in_the_future_is_refused()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["iat"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        var error = Assert.Throws<SecurityTokenException>(() => Validate(_idp.MintRaw(payload)));

        Assert.Contains("future", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token issued a minute into the future, which is inside the skew the lifetime
    /// check already allows. Refusing this would make the plugin stricter about iat than
    /// about exp, and a household server and its provider do not share a clock.
    /// </summary>
    [Fact]
    public void A_token_issued_within_the_clock_skew_is_accepted()
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["iat"] = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();

        var (sub, _, _, _) = Validate(_idp.MintRaw(payload));

        Assert.Equal("ben", sub);
    }

    /// <summary>
    /// An issued-at time outside the years a DateTimeOffset can hold. The same shape
    /// answered 500 on the back-channel logout endpoint before it was range-checked
    /// there, because ArgumentOutOfRangeException is not one of the types a refusal is
    /// made from. This is the login side of that rule.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    public void An_implausible_issued_at_time_is_refused_as_a_token_problem(long iat)
    {
        var payload = IdTokenFactory.ValidPayload();
        payload["iat"] = iat;

        Assert.Throws<SecurityTokenException>(() => Validate(_idp.MintRaw(payload)));
    }
}
