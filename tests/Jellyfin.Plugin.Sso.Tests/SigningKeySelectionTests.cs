using System.Text.Json.Nodes;
using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Which of a provider's published keys a token may be signed with.
///
/// This helper had no test of its own until the OpenID Foundation conformance suite was
/// run against the plugin. Its provider publishes thirteen keys — two RSA and four
/// elliptic-curve signing keys, two encryption keys, and five deliberately unusable ones
/// — and every one of them was being offered to the token validator.
/// </summary>
public sealed class SigningKeySelectionTests
{
    private static string[] Published(params JsonObject[] keys)
        => keys.Select(k => k.ToJsonString()).ToArray();

    /// <summary>An RSA public key, as a provider publishes one.</summary>
    private static JsonObject RsaKey(string kid, string? use)
    {
        var key = new JsonObject
        {
            ["kty"] = "RSA",
            ["kid"] = kid,
            ["e"] = "AQAB",
            ["n"] = "sXchDaQebHnPiGvyDOAT4saGEUetSyo9MKLOoWFsueri23bOdgWp4Dy1Wl"
                + "UzewbgBHod5pcM9H95GQRV3JDXboIRROSBigeC5yjU1hGzHHyXss8UDpre"
                + "cbAYxknTcQkhslANGRUZmdTOQ5qTRsLAt6BTYuyvVRdhS8exSZEy_c4gs_"
                + "7svlJJQ4H9_NxsiIcvNDBCnKjbUdSzHOIdRtRO9DWlbIMKpQnQHRSD0kJ3"
                + "a3q3qUsAK4-5Fq1AgwKAKbWlMQh3VdbtLZLvCCjLcVnqMoFyeMGcnbNhsk"
                + "2UJHhvJgV2Zd3L9c0rcdrPSFLFbhLBvmw1RqnzBJZIBfWdRHCJaZQ",
        };
        if (use is not null)
            key["use"] = use;
        return key;
    }

    [Fact]
    public void A_key_marked_for_signatures_is_offered_to_the_validator()
    {
        var keys = OidcClientFactory.SigningKeysFrom(Published(RsaKey("sig-key", "sig")));

        var key = Assert.Single(keys);
        Assert.Equal("sig-key", ((JsonWebKey)key).Kid);
    }

    /// <summary>
    /// The "use" parameter is optional, and a provider that publishes one key often omits
    /// it. Refusing those would lock out working deployments for the sake of a parameter
    /// nobody is required to send.
    /// </summary>
    [Fact]
    public void A_key_that_says_nothing_about_its_use_is_still_offered()
    {
        var keys = OidcClientFactory.SigningKeysFrom(Published(RsaKey("quiet-key", use: null)));

        Assert.Single(keys);
    }

    /// <summary>
    /// RFC 7517 §4.2. An encryption key is not something a token may be signed with, and
    /// the provider said as much by publishing it that way.
    /// </summary>
    [Fact]
    public void A_key_published_for_encryption_is_not_offered()
    {
        var keys = OidcClientFactory.SigningKeysFrom(
            Published(RsaKey("sig-key", "sig"), RsaKey("enc-key", "enc")));

        var key = Assert.Single(keys);
        Assert.Equal("sig-key", ((JsonWebKey)key).Kid);
    }

    /// <summary>
    /// A key set with keys in it, none of them for signatures. There is nothing to check a
    /// token against, which is the same answer as an empty set — and it has to be the
    /// refusal, not an empty list that makes every signature fail somewhere further on
    /// with a less useful message.
    /// </summary>
    [Fact]
    public void A_key_set_with_only_encryption_keys_is_refused()
    {
        var published = Published(RsaKey("enc-key", "enc"));

        var error = Assert.Throws<SecurityTokenException>(
            () => OidcClientFactory.SigningKeysFrom(published));

        Assert.Contains("No signing keys", error.Message, StringComparison.Ordinal);
    }
}
