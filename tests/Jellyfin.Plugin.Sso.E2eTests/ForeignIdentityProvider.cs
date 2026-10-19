using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// Signs tokens with a key no configured provider published, while claiming to be one.
/// It stands for whoever else can reach the back-channel logout endpoint — which, the
/// endpoint being anonymous, is everyone on the network.
/// </summary>
internal sealed class ForeignIdentityProvider : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string MintLogoutToken(string sub, string clientId, string issuer)
    {
        var payload = new JwtPayload
        {
            { "iss", issuer },
            { "aud", clientId },
            { "sub", sub },
            { "jti", Guid.NewGuid().ToString("N") },
            { "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            {
                "events",
                new Dictionary<string, object>
                {
                    ["http://schemas.openid.net/event/backchannel-logout"]
                        = new Dictionary<string, object>(),
                }
            },
        };

        var key = new RsaSecurityKey(_rsa) { KeyId = "foreign-key" };
        var token = new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256)), payload);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => _rsa.Dispose();
}
