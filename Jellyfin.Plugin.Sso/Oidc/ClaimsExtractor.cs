using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

        return (sub, username, GroupsFromClaims(principal, provider.GroupsClaim));
    }

    /// <summary>
    /// The groups a validated token carries under <paramref name="claim"/>.
    ///
    /// The whole configured name is tried as a claim type first, because claim types
    /// legitimately contain dots — `urn:zitadel:iam:org:project:roles`, the
    /// `http://schemas.microsoft.com/ws/2008/06/identity/claims/role` family — and splitting
    /// those would break configurations that work today. Only when no claim goes by that
    /// name is it read as a path into a nested JSON claim, which is where Keycloak keeps its
    /// roles without a mapper (`realm_access.roles`) and where Entra ID keeps a client's
    /// (`resource_access.&lt;client&gt;.roles`).
    /// </summary>
    internal static List<string> GroupsFromClaims(ClaimsPrincipal principal, string claim)
    {
        var exact = principal.Claims
            .Where(c => c.Type == claim)
            .Select(c => c.Value)
            .ToList();

        if (exact.Count > 0 || !claim.Contains('.', StringComparison.Ordinal))
        {
            return exact;
        }

        // Longest head first, so a claim whose own name contains dots is still found
        // before the name is split further.
        foreach (var (head, tail) in SplitPoints(claim))
        {
            // Select is lazy, so a token carrying the claim twice is only parsed until one
            // of them holds the rest of the path.
            var nested = principal.Claims
                .Where(c => c.Type == head)
                .Select(c => GroupsFromJsonText(c.Value, tail))
                .FirstOrDefault(groups => groups.Count > 0);

            if (nested is not null)
            {
                return nested;
            }
        }

        return [];
    }

    /// <summary>
    /// A nested claim arrives as the JSON text the provider put in the token. Anything that
    /// is not JSON, or does not hold the rest of the path, grants nothing — like a token
    /// without the claim at all.
    /// </summary>
    private static List<string> GroupsFromJsonText(string text, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return GroupsAtPath(document.RootElement, path);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Every way of cutting a dotted path into a head and a tail, longest head first.</summary>
    private static IEnumerable<(string Head, string Tail)> SplitPoints(string path)
    {
        for (var i = path.LastIndexOf('.'); i > 0; i = path.LastIndexOf('.', i - 1))
        {
            yield return (path[..i], path[(i + 1)..]);
        }
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
        => GroupsAtPath(root, claimName);

    /// <summary>
    /// The groups at <paramref name="path"/> in a JSON object. The whole path is tried as a
    /// property name before it is read as a path, for the reason given on
    /// <see cref="GroupsFromClaims"/>: a provider may well call a property
    /// `urn:example:roles`.
    /// </summary>
    private static List<string> GroupsAtPath(JsonElement node, string path)
    {
        // Anything but an object has no properties to ask for, and asking anyway throws.
        // A provider that answers with an array, a string or a number grants nothing,
        // like one that answers without the claim.
        if (node.ValueKind != JsonValueKind.Object)
            return [];

        if (node.TryGetProperty(path, out var exact))
            return Strings(exact);

        foreach (var (head, tail) in SplitPoints(path))
        {
            if (node.TryGetProperty(head, out var child))
            {
                var nested = GroupsAtPath(child, tail);
                if (nested.Count > 0)
                {
                    return nested;
                }
            }
        }

        return [];
    }

    /// <summary>
    /// The group names a claim's value holds: an array of them, a single one as a string, or
    /// — for anything else a provider might send — none.
    /// </summary>
    private static List<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();

        if (value.ValueKind == JsonValueKind.String)
            return [value.GetString()!];

        return [];
    }
}
