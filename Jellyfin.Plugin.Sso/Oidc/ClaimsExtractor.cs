using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using IdentityModel.Client;
using Jellyfin.Plugin.Sso.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Oidc;

public sealed record OidcIdentity(string Sub, string PreferredUsername, IReadOnlyList<string> Groups);

public sealed class ClaimsExtractor
{
    private readonly OidcClientFactory _factory;

    public ClaimsExtractor(OidcClientFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Validates the ID token and extracts sub, preferred_username, and group claims.
    /// When GroupsFromUserInfo is true, groups are fetched from the userinfo endpoint instead.
    /// </summary>
    public async Task<OidcIdentity> ExtractAsync(
        ProviderConfig provider,
        DiscoveryDocumentResponse disco,
        string idToken,
        string accessToken,
        string expectedNonce,
        CancellationToken ct = default)
    {
        var (sub, preferredUsername, groups) = ValidateIdToken(provider, disco, idToken, expectedNonce);

        if (provider.GroupsFromUserInfo)
        {
            var userInfo = await _factory.GetUserInfoAsync(disco, accessToken, ct).ConfigureAwait(false);
            groups = ExtractGroupsFromElement(userInfo.Json, provider.GroupsClaim);
        }

        return new OidcIdentity(sub, preferredUsername, groups);
    }

    private (string Sub, string Username, List<string> Groups) ValidateIdToken(
        ProviderConfig provider,
        DiscoveryDocumentResponse disco,
        string idToken,
        string expectedNonce)
    {
        var handler = new JwtSecurityTokenHandler();
        // Disable automatic claim type remapping so "sub", "nonce" etc. keep their
        // original names instead of being renamed to long WS-Security URN strings.
        handler.InboundClaimTypeMap.Clear();

        var signingKeys = disco.KeySet?.Keys?
            .Select(k => new JsonWebKey(JsonSerializer.Serialize(k)))
            .Cast<SecurityKey>()
            .ToList()
            ?? throw new SecurityTokenException("No signing keys in discovery document.");

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = disco.Issuer,
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = signingKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(60),
        };

        var principal = handler.ValidateToken(idToken, parameters, out _);

        var nonce = principal.FindFirst(OpenIdConnectParameterNames.Nonce)?.Value;
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new SecurityTokenException("ID token nonce mismatch.");

        var sub = principal.FindFirst("sub")?.Value
            ?? throw new SecurityTokenException("ID token missing 'sub' claim.");
        var username = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("name")?.Value
            ?? sub;

        var groups = principal.Claims
            .Where(c => c.Type == provider.GroupsClaim)
            .Select(c => c.Value)
            .ToList();

        return (sub, username, groups);
    }

    // UserInfoResponse.Json is JsonElement? in IdentityModel
    private static List<string> ExtractGroupsFromElement(JsonElement? nullableRoot, string claimName)
    {
        if (nullableRoot is not { } root)
            return [];
        return ExtractGroupsFromElement(root, claimName);
    }

    private static List<string> ExtractGroupsFromElement(JsonElement root, string claimName)
    {
        if (root.ValueKind == JsonValueKind.Undefined || root.ValueKind == JsonValueKind.Null)
            return [];

        if (!root.TryGetProperty(claimName, out var el))
            return [];

        if (el.ValueKind == JsonValueKind.Array)
            return el.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();

        if (el.ValueKind == JsonValueKind.String)
            return [el.GetString()!];

        return [];
    }
}
