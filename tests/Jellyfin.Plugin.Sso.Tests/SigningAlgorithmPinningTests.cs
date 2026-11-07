using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// A provider publishes several keys and signs with whichever it likes. These tests are
/// about the difference between accepting that and accepting what the provider was
/// configured to send.
///
/// The case comes from the OpenID Foundation conformance suite:
/// <c>oidcc-client-test-rp-backchannel-rpinitlogout-wrong-alg</c> registered RSA as its
/// client's signing algorithm, then signed the logout token ES256 with another key out of
/// the same published set, and the plugin ended the account's sessions. Both keys are the
/// provider's, so nothing here is a forgery defence — the keys handed to the validator in
/// every test below are the ones the token was signed with.
/// </summary>
public sealed class SigningAlgorithmPinningTests : IDisposable
{
    private readonly IdTokenFactory _idp = new();

    private static ProviderConfig Provider(params string[] algorithms) => new()
    {
        Name = "Test",
        Authority = IdTokenFactory.Issuer,
        ClientId = IdTokenFactory.Audience,
        GroupsClaim = "groups",
        SigningAlgorithms = [.. algorithms],
    };

    /// <summary>
    /// Both published keys, always. The provider holds both private halves, which is what
    /// makes this about configuration rather than about trust in a key.
    /// </summary>
    private (string Sub, string Username, List<string> Groups, string? Picture) Validate(
        ProviderConfig provider, string idToken)
        => ClaimsExtractor.ValidateIdToken(
            provider,
            IdTokenFactory.Issuer,
            [_idp.SigningKey, _idp.EllipticSigningKey],
            idToken,
            "test-nonce");

    private LogoutSubject ValidateLogout(ProviderConfig provider, string logoutToken)
        => LogoutTokenValidator.Validate(
            provider,
            IdTokenFactory.Issuer,
            [_idp.SigningKey, _idp.EllipticSigningKey],
            logoutToken);

    // ─── The ID token ─────────────────────────────────────────────────────────

    /// <summary>
    /// The default, and what every release before this setting existed did: any asymmetric
    /// algorithm the provider's keys support. Stated as a test because the setting would be
    /// worthless if switching it on were the only safe configuration.
    /// </summary>
    [Fact]
    public void A_provider_that_names_no_algorithm_accepts_either_of_its_keys()
    {
        var (sub, _, _, _) = Validate(
            Provider(), _idp.Mint(algorithm: SecurityAlgorithms.EcdsaSha256));

        Assert.Equal("ben", sub);
    }

    [Fact]
    public void A_provider_pinned_to_one_algorithm_refuses_a_token_signed_with_another()
    {
        var token = _idp.Mint(algorithm: SecurityAlgorithms.EcdsaSha256);

        Assert.ThrowsAny<SecurityTokenException>(
            () => Validate(Provider(SecurityAlgorithms.RsaSha256), token));
    }

    [Fact]
    public void A_provider_pinned_to_one_algorithm_still_accepts_that_one()
    {
        var (sub, _, _, _) = Validate(Provider(SecurityAlgorithms.RsaSha256), _idp.Mint());

        Assert.Equal("ben", sub);
    }

    [Fact]
    public void A_provider_may_name_several_algorithms()
    {
        var provider = Provider(SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256);

        Assert.Equal("ben", Validate(provider, _idp.Mint()).Sub);
        Assert.Equal(
            "ben",
            Validate(provider, _idp.Mint(algorithm: SecurityAlgorithms.EcdsaSha256)).Sub);
    }

    /// <summary>
    /// The header a provider sends is compared exactly, so a configured name is matched
    /// without case and then used in the library's own spelling. Somebody typing the field
    /// by hand should not have to know that.
    /// </summary>
    [Theory]
    [InlineData("rs256")]
    [InlineData("Rs256")]
    [InlineData("  RS256  ")]
    public void A_configured_name_is_matched_without_case_or_surrounding_space(string configured)
    {
        var provider = Provider(configured);

        Assert.Equal("ben", Validate(provider, _idp.Mint()).Sub);
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(
            provider, _idp.Mint(algorithm: SecurityAlgorithms.EcdsaSha256)));
    }

    // ─── What the setting cannot be used for ──────────────────────────────────

    /// <summary>
    /// The setting narrows what is accepted and can never widen it. A configuration naming
    /// HS256 would otherwise undo the pinning the asymmetric set is there for, and a JWKS
    /// is a public document: its bytes would become the HMAC secret that decides whether a
    /// token is genuine.
    /// </summary>
    [Fact]
    public void A_symmetric_algorithm_cannot_be_configured_back_in()
    {
        var shared = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))
        {
            KeyId = "oct-key",
        };

        var hmacSigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(shared, SecurityAlgorithms.HmacSha256)),
            IdTokenFactory.ValidPayload()));

        var provider = Provider(SecurityAlgorithms.HmacSha256);

        Assert.Equal(
            OidcClientFactory.AsymmetricSigningAlgorithms,
            OidcClientFactory.SigningAlgorithmsFor(provider));

        Assert.ThrowsAny<SecurityTokenException>(() => ClaimsExtractor.ValidateIdToken(
            provider, IdTokenFactory.Issuer, [shared], hmacSigned, "test-nonce"));
    }

    /// <summary>
    /// The reason the fallback is the whole asymmetric set rather than an empty list: the
    /// library skips the algorithm check when <c>ValidAlgorithms</c> has no entries, so a
    /// field full of typos would accept more than the default, not less. Asserted against
    /// the library rather than described, because the fallback only matters if this is
    /// true.
    /// </summary>
    [Fact]
    public void An_empty_algorithm_list_would_let_the_library_accept_anything()
    {
        var shared = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))
        {
            KeyId = "oct-key",
        };

        var hmacSigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(shared, SecurityAlgorithms.HmacSha256)),
            IdTokenFactory.ValidPayload()));

        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();

        var principal = handler.ValidateToken(
            hmacSigned,
            new TokenValidationParameters
            {
                ValidIssuer = IdTokenFactory.Issuer,
                ValidAudience = IdTokenFactory.Audience,
                IssuerSigningKeys = [shared],
                ValidAlgorithms = [],
            },
            out _);

        Assert.Equal("ben", principal.FindFirst("sub")?.Value);
    }

    [Theory]
    [InlineData("RSA256")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_the_plugin_does_not_know_leaves_the_default_set_in_place(string configured)
    {
        Assert.Equal(
            OidcClientFactory.AsymmetricSigningAlgorithms,
            OidcClientFactory.SigningAlgorithmsFor(Provider(configured)));
    }

    [Fact]
    public void A_recognised_name_among_unrecognised_ones_is_the_one_that_counts()
    {
        Assert.Equal(
            [SecurityAlgorithms.RsaSha256],
            OidcClientFactory.SigningAlgorithmsFor(Provider("RSA256", "rs256", "none")));
    }

    [Fact]
    public void A_name_repeated_is_pinned_once()
    {
        Assert.Equal(
            [SecurityAlgorithms.RsaSha256],
            OidcClientFactory.SigningAlgorithmsFor(Provider("RS256", "rs256")));
    }

    // ─── The logout token ─────────────────────────────────────────────────────

    /// <summary>
    /// The module that found this. A logout token ends sessions, so the two paths have to
    /// be pinned together; the ID token check alone would have left the conformance
    /// failure exactly where it was.
    /// </summary>
    [Fact]
    public void A_logout_token_signed_with_an_unexpected_algorithm_is_refused()
    {
        var token = _idp.MintLogoutToken(algorithm: SecurityAlgorithms.EcdsaSha256);

        Assert.ThrowsAny<SecurityTokenException>(
            () => ValidateLogout(Provider(SecurityAlgorithms.RsaSha256), token));
    }

    [Fact]
    public void A_logout_token_signed_the_expected_way_still_ends_the_session()
    {
        var subject = ValidateLogout(
            Provider(SecurityAlgorithms.RsaSha256), _idp.MintLogoutToken());

        Assert.Equal("ben", subject.Sub);
    }

    [Fact]
    public void A_provider_that_names_no_algorithm_accepts_either_key_on_the_logout_path()
    {
        var subject = ValidateLogout(
            Provider(), _idp.MintLogoutToken(algorithm: SecurityAlgorithms.EcdsaSha256));

        Assert.Equal("ben", subject.Sub);
    }

    public void Dispose() => _idp.Dispose();
}
