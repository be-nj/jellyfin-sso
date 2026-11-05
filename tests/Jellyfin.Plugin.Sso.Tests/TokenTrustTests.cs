using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Two ways a token can be genuine and still not be this application's, both of them
/// about what the validator was told to accept rather than about the signature.
/// </summary>
public sealed class TokenTrustTests : IDisposable
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
        string idToken, SecurityKey? key = null)
        => ClaimsExtractor.ValidateIdToken(
            _provider, IdTokenFactory.Issuer, [key ?? _idp.SigningKey], idToken, "test-nonce");

    private static JwtPayload PayloadWith(params (string Claim, object Value)[] extra)
    {
        var payload = IdTokenFactory.ValidPayload();
        foreach (var (claim, value) in extra)
        {
            payload.Remove(claim);
            payload.Add(claim, value);
        }

        return payload;
    }

    // ─── Several audiences ────────────────────────────────────────────────────

    /// <summary>The ordinary token, which names one audience and no authorized party.</summary>
    [Fact]
    public void A_token_for_this_client_alone_needs_no_azp()
    {
        var (sub, _, _, _) = Validate(_idp.Mint());

        Assert.Equal("ben", sub);
    }

    /// <summary>
    /// A provider can be configured to add a second client's id to a token's audience —
    /// Keycloak does it with an audience mapper — and the audience check passes as soon as
    /// this client is one of the names. Such a token was issued for the other client, and
    /// OIDC Core §3.1.3.7 is how it says so: where there are several audiences, azp names
    /// the one it was issued for.
    /// </summary>
    [Fact]
    public void A_token_naming_several_audiences_without_azp_is_rejected()
    {
        var token = _idp.MintRaw(PayloadWith(
            ("aud", new[] { "another-application", IdTokenFactory.Audience })));

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void A_token_naming_several_audiences_for_another_party_is_rejected()
    {
        var token = _idp.MintRaw(PayloadWith(
            ("aud", new[] { "another-application", IdTokenFactory.Audience }),
            ("azp", "another-application")));

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(token));
    }

    [Fact]
    public void A_token_naming_several_audiences_for_this_party_is_accepted()
    {
        var token = _idp.MintRaw(PayloadWith(
            ("aud", new[] { "another-application", IdTokenFactory.Audience }),
            ("azp", IdTokenFactory.Audience)));

        var (sub, _, _, _) = Validate(token);

        Assert.Equal("ben", sub);
    }

    // ─── The algorithm ────────────────────────────────────────────────────────

    /// <summary>
    /// A JWKS is a public document. Left unpinned, the acceptable algorithms are whatever
    /// the library will take against the keys it was handed, so a symmetric entry in that
    /// document — which a provider has no reason to publish and every reason not to — made
    /// its own public bytes the HMAC secret that decides whether a token is genuine.
    /// </summary>
    [Fact]
    public void A_token_signed_with_a_symmetric_key_from_the_key_set_is_rejected()
    {
        var shared = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))
        {
            KeyId = "oct-key",
        };

        var hmacSigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(shared, SecurityAlgorithms.HmacSha256)),
            IdTokenFactory.ValidPayload()));

        // Handed the very key it was signed with, which is the whole point: the key is
        // public, so holding it proves nothing.
        Assert.ThrowsAny<SecurityTokenException>(() => Validate(hmacSigned, shared));
    }

    [Fact]
    public void A_token_signed_the_way_every_provider_signs_one_is_accepted()
    {
        Assert.Contains(SecurityAlgorithms.RsaSha256, OidcClientFactory.AsymmetricSigningAlgorithms);
        Assert.DoesNotContain(
            SecurityAlgorithms.HmacSha256, OidcClientFactory.AsymmetricSigningAlgorithms);

        var (sub, _, _, _) = Validate(_idp.Mint());

        Assert.Equal("ben", sub);
    }

    /// <summary>
    /// The logout path validates against the same key set and has to be pinned with it; a
    /// forged logout token ends somebody's sessions.
    /// </summary>
    [Fact]
    public void A_logout_token_signed_with_a_symmetric_key_is_rejected()
    {
        var shared = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))
        {
            KeyId = "oct-key",
        };

        var payload = new JwtPayload
        {
            { "iss", IdTokenFactory.Issuer },
            { "aud", IdTokenFactory.Audience },
            { "sub", "ben" },
            { "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            {
                "events", new Dictionary<string, object>
                {
                    [LogoutTokenValidator.EventName] = new Dictionary<string, object>(),
                }
            },
        };

        var hmacSigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(shared, SecurityAlgorithms.HmacSha256)),
            payload));

        Assert.ThrowsAny<SecurityTokenException>(() => LogoutTokenValidator.Validate(
            _provider, IdTokenFactory.Issuer, [shared], hmacSigned));
    }

    /// <summary>
    /// Asserted here rather than left implied: Encoding is referenced so the test file
    /// states the key is raw bytes, not a passphrase the library would stretch.
    /// </summary>
    [Fact]
    public void A_symmetric_key_made_from_a_passphrase_is_refused_the_same_way()
    {
        var shared = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-shared-secret-of-32-bytes-ok!!"));

        var hmacSigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(shared, SecurityAlgorithms.HmacSha256)),
            IdTokenFactory.ValidPayload()));

        Assert.ThrowsAny<SecurityTokenException>(() => Validate(hmacSigned, shared));
    }

    public void Dispose() => _idp.Dispose();
}
