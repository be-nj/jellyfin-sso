using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// Mints ID tokens the way an identity provider would, so the plugin's validation rules
/// can be exercised without a live IdP. Each factory owns one RSA key pair; using two
/// factories produces a token signed by a key the validator does not trust.
/// </summary>
internal sealed class IdTokenFactory : IDisposable
{
    public const string Issuer = "https://idp.example.com/application/o/jellyfin";
    public const string Audience = "jellyfin-client-id";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;

    public IdTokenFactory(string keyId = "test-key")
    {
        _key = new RsaSecurityKey(_rsa) { KeyId = keyId };
    }

    /// <summary>The public key material a provider would publish in its JWKS.</summary>
    public SecurityKey SigningKey => _key;

    public string Mint(
        string sub = "ben",
        string? preferredUsername = "ben",
        string? nonce = "test-nonce",
        IEnumerable<string>? groups = null,
        string issuer = Issuer,
        string audience = Audience,
        TimeSpan? lifetime = null,
        string groupsClaim = "groups")
    {
        var claims = new List<Claim>();
        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }

        if (preferredUsername is not null)
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }

        if (nonce is not null)
        {
            claims.Add(new Claim("nonce", nonce));
        }

        foreach (var group in groups ?? [])
        {
            claims.Add(new Claim(groupsClaim, group));
        }

        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(5));
        // A negative lifetime mints an already-expired token; nbf has to stay before exp
        // or the token is rejected for an invalid lifetime instead of for being expired.
        var notBefore = expires.AddMinutes(-1) < now ? expires.AddMinutes(-1) : now.AddMinutes(-1);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => _rsa.Dispose();
}
