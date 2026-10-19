using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Duende.IdentityModel.Client;
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
        var (sub, preferredUsername, groups) = ValidateIdToken(
            provider, disco.Issuer, OidcClientFactory.SigningKeysFrom(disco),
            idToken, expectedNonce);

        // Some providers — Authelia among them — keep the profile claims out of the ID
        // token, which left the JIT-created Jellyfin user named after the opaque subject.
        // Ask userinfo when the groups have to come from there anyway, or when the token
        // carried no name at all.
        var nameMissing = string.Equals(preferredUsername, sub, StringComparison.Ordinal);
        if (provider.GroupsFromUserInfo || nameMissing)
        {
            var userInfo = await _factory.GetUserInfoAsync(disco, accessToken, ct).ConfigureAwait(false);

            if (provider.GroupsFromUserInfo)
            {
                groups = ExtractGroupsFromElement(userInfo.Json, provider.GroupsClaim);
            }

            if (nameMissing)
            {
                preferredUsername =
                    StringClaim(userInfo.Json, "preferred_username")
                    ?? StringClaim(userInfo.Json, "name")
                    ?? preferredUsername;
            }
        }

        return new OidcIdentity(sub, preferredUsername, groups);
    }

    /// <summary>
    /// Validates signature, issuer, audience, lifetime and nonce, then reads the claims
    /// the plugin relies on. Internal so the validation rules can be tested without a
    /// live identity provider.
    /// </summary>
    internal static (string Sub, string Username, List<string> Groups) ValidateIdToken(
        ProviderConfig provider,
        string? issuer,
        IEnumerable<SecurityKey> signingKeys,
        string idToken,
        string expectedNonce)
    {
        var handler = new JwtSecurityTokenHandler();
        // Disable automatic claim type remapping so "sub", "nonce" etc. keep their
        // original names instead of being renamed to long WS-Security URN strings.
        handler.InboundClaimTypeMap.Clear();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
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

    /// <summary>
    /// Reads a string claim out of the userinfo response, if it is there. Internal because
    /// the userinfo path cannot be reached without a live provider, and what it reads
    /// decides what a JIT-created account is called.
    /// </summary>
    internal static string? StringClaim(JsonElement? nullableRoot, string name)
    {
        if (nullableRoot is not { } root || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// The groups out of a userinfo response. UserInfoResponse.Json is JsonElement? in
    /// IdentityModel, and a provider may answer with an array, a single string, or neither.
    /// Internal for the same reason as <see cref="StringClaim"/>.
    /// </summary>
    internal static List<string> ExtractGroupsFromElement(JsonElement? nullableRoot, string claimName)
    {
        if (nullableRoot is not { } root)
            return [];
        return ExtractGroupsFromElement(root, claimName);
    }

    private static List<string> ExtractGroupsFromElement(JsonElement root, string claimName)
    {
        // Anything but an object has no properties to ask for, and asking anyway throws.
        // A provider that answers with an array, a string or a number grants nothing,
        // like one that answers without the claim.
        if (root.ValueKind != JsonValueKind.Object)
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
