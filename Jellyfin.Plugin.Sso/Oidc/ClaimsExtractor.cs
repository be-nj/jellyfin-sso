using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Jellyfin.Plugin.Sso.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Oidc;

public sealed record OidcIdentity(
    string Sub,
    string PreferredUsername,
    IReadOnlyList<string> Groups,
    string? PictureUrl = null);

public sealed class ClaimsExtractor
{
    /// <summary>
    /// How far the provider's clock may be from this server's. Used both for the
    /// lifetime the library checks and for the issued-at time checked here, so the two
    /// cannot drift apart.
    /// </summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

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
        var (sub, preferredUsername, groups, picture) = ValidateIdToken(
            provider, disco.Issuer, OidcClientFactory.SigningKeysFrom(disco),
            idToken, expectedNonce);

        var wantsPicture = Plugin.Instance?.Configuration.SyncProfilePictures == true;

        // Some providers — Authelia among them — keep the profile claims out of the ID
        // token, which left the JIT-created Jellyfin user named after the opaque subject.
        // Ask userinfo when the groups have to come from there anyway, or when the token
        // carried no name at all.
        var nameMissing = string.Equals(preferredUsername, sub, StringComparison.Ordinal);
        var pictureMissing = wantsPicture && picture is null;

        // Groups and a name are worth failing a login over — without them the account is
        // wrong. A picture is not: a provider whose userinfo endpoint is absent or broken
        // signed people in before this feature existed and has to keep doing so.
        var required = provider.GroupsFromUserInfo || nameMissing;
        if (required || pictureMissing)
        {
            UserInfoResponse userInfo;
            try
            {
                userInfo = await _factory.GetUserInfoAsync(disco, accessToken, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!required)
            {
                return new OidcIdentity(sub, preferredUsername, groups, picture);
            }

            // OIDC Core §5.3.2: the sub in a userinfo response must be verified against
            // the one in the ID token, and where they differ the response must not be
            // used. The access token is this login's own, so a mismatch means the
            // provider answered about somebody else — and what is read from here decides
            // a user's groups, which is to say their libraries and their administrator
            // flag. A picture is not worth failing a login over, so a login that only
            // wanted one carries on without it.
            var userInfoSubject = StringClaim(userInfo.Json, "sub");
            if (!string.Equals(userInfoSubject, sub, StringComparison.Ordinal))
            {
                if (!required)
                {
                    return new OidcIdentity(sub, preferredUsername, groups, picture);
                }

                // Says what happened, which is not what this message used to say. "Its
                // claims were not used" describes the branch above, where the login goes
                // on without a picture; here the login is refused, and an operator
                // reading the old wording concluded somebody had signed in without their
                // groups and went looking for the fault in the group mapping.
                throw new SecurityTokenException(
                    "The userinfo endpoint answered about a different subject than the ID "
                    + "token names, so the login was refused. This login needed claims "
                    + "from userinfo, and a response about another subject cannot supply "
                    + "them.");
            }

            if (provider.GroupsFromUserInfo)
            {
                groups = ExtractGroupsFromElement(userInfo.Json, provider.GroupsClaim);
            }

            if (nameMissing)
            {
                preferredUsername =
                    Name(StringClaim(userInfo.Json, "preferred_username"))
                    ?? Name(StringClaim(userInfo.Json, "name"))
                    ?? preferredUsername;
            }

            if (pictureMissing)
            {
                // Authentik and Authelia keep the picture out of the ID token; Keycloak
                // sends it when the client asks for the profile scope.
                picture = First(ExtractGroupsFromElement(userInfo.Json, PictureClaim(provider)));
            }
        }

        return new OidcIdentity(sub, preferredUsername, groups, picture);
    }

    /// <summary>
    /// Validates signature, issuer, audience, lifetime and nonce, then reads the claims
    /// the plugin relies on. Internal so the validation rules can be tested without a
    /// live identity provider.
    /// </summary>
    internal static (string Sub, string Username, List<string> Groups, string? Picture) ValidateIdToken(
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
            ValidAlgorithms = OidcClientFactory.AsymmetricSigningAlgorithms,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
        };

        var principal = handler.ValidateToken(idToken, parameters, out _);

        var nonce = principal.FindFirst(OpenIdConnectParameterNames.Nonce)?.Value;
        if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            throw new SecurityTokenException("ID token nonce mismatch.");

        RequireThisClientIsTheParty(principal, provider.ClientId);
        RequireIssuedAt(principal, DateTimeOffset.UtcNow);

        // Blank, not merely absent. A provider whose subject comes from a mapper pointed
        // at an attribute that is empty for some users sends "sub": "", and an empty
        // string is a perfectly good dictionary key: the first such login would create
        // the link, and every later one would match it and sign in to that account.
        var sub = Blankless(PlainString(principal, "sub"))
            ?? throw new SecurityTokenException(
                "The ID token's 'sub' claim is missing, empty, or not a single value. The "
                + "subject is what this plugin matches an account on, so check which "
                + "attribute the provider maps to 'sub'.");
        var username = Name(PlainString(principal, "preferred_username"))
            ?? Name(PlainString(principal, "name"))
            ?? sub;

        // A picture is one string where groups are many, but both arrive the same way —
        // as a claim, or as a string inside a nested one — so they are read by the same
        // code and the picture takes the first thing that came back.
        var picture = First(GroupsFromClaims(principal, PictureClaim(provider)));

        return (sub, username, GroupsFromClaims(principal, provider.GroupsClaim), picture);
    }

    /// <summary>
    /// A token whose <c>aud</c> names more than this client has to say which one it was
    /// issued for.
    ///
    /// The audience check above passes as soon as this client is one of the names in
    /// <c>aud</c>. A provider can be configured to put a second client's id in a token's
    /// audience — Keycloak does it through an audience mapper — and such a token was then
    /// accepted here as if it had been issued for this one. OIDC Core §3.1.3.7 covers this
    /// with <c>azp</c>: where there are several audiences, the authorized party must be
    /// this client.
    /// </summary>
    private static void RequireThisClientIsTheParty(ClaimsPrincipal principal, string clientId)
    {
        var audiences = principal.FindAll("aud").Take(2).Count();
        if (audiences < 2)
        {
            return;
        }

        var authorizedParty = PlainString(principal, "azp");
        if (!string.Equals(authorizedParty, clientId, StringComparison.Ordinal))
        {
            throw new SecurityTokenException(
                "The ID token names several audiences and its 'azp' claim does not name "
                + "this client, so it was not issued for this application.");
        }
    }

    /// <summary>
    /// Requires the <c>iat</c> claim, which the specification does and the library does
    /// not.
    ///
    /// OpenID Connect Core lists iat among an ID token's required claims (§2) and tells
    /// the client to validate it (§3.1.3.7, step 9). Microsoft's handler has
    /// RequireExpirationTime, RequireAudience and RequireSignedTokens, and nothing for
    /// this one, so a token with no iat at all validated here and the login went on to
    /// fetch claims from userinfo. The conformance suite caught it where five reviews had
    /// not: "Client has incorrectly called userinfo_endpoint after receiving an id_token
    /// with no iat claim."
    ///
    /// Only presence, shape and a token that claims to come from the future are checked.
    /// How old an ID token may be is what exp is for, and exp is already required — a
    /// maximum age here would refuse providers that legitimately issue long-lived tokens,
    /// which is a different rule and not one this plugin has any business inventing. The
    /// back-channel logout token is the other way round: it carries iat and only
    /// optionally exp, so there freshness is the whole point
    /// (<see cref="LogoutTokenValidator"/>).
    /// </summary>
    private static void RequireIssuedAt(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var raw = principal.FindFirst("iat")?.Value
            ?? throw new SecurityTokenException(
                "The ID token carries no 'iat' claim, which OpenID Connect requires.");

        if (!long.TryParse(raw, out var seconds))
            throw new SecurityTokenException("The ID token's 'iat' claim is not a number.");

        // Range-checked before it is converted, for the same reason as in the logout
        // token: FromUnixTimeSeconds throws ArgumentOutOfRangeException outside the
        // representable years, and that is not a type the callback turns into a refusal.
        if (seconds < DateTimeOffset.UnixEpoch.ToUnixTimeSeconds()
            || seconds > now.AddYears(1).ToUnixTimeSeconds())
        {
            throw new SecurityTokenException(
                "The ID token's 'iat' claim is not a plausible time.");
        }

        if (DateTimeOffset.FromUnixTimeSeconds(seconds) > now + ClockSkew)
            throw new SecurityTokenException("The ID token claims to have been issued in the future.");
    }

    private static string PictureClaim(ProviderConfig provider)
        => string.IsNullOrWhiteSpace(provider.PictureClaim) ? "picture" : provider.PictureClaim;

    private static string? First(List<string> values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

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
    /// <summary>
    /// A claim's value when it is one plain string, and null when it is anything else.
    ///
    /// A JSON object or array arrives here as its own JSON text — the library records the
    /// shape in the claim's value type and hands over the serialised form — so reading
    /// the value directly turned a provider sending
    /// <c>"preferred_username": {"given": "ben"}</c> into a Jellyfin account literally
    /// called <c>{"given":"ben"}</c>. These three claims name one person each; a
    /// structure is a misconfiguration, not a name.
    /// </summary>
    private static string? PlainString(ClaimsPrincipal principal, string claim)
    {
        var found = principal.FindFirst(claim);
        if (found is null)
        {
            return null;
        }

        return found.ValueType is JsonClaimValueTypes.Json or JsonClaimValueTypes.JsonArray
            or JsonClaimValueTypes.JsonNull
            ? null
            : found.Value;
    }

    /// <summary>
    /// Null for a value that is absent or only whitespace, and otherwise the value
    /// unchanged. The logout token validator has always treated a blank subject as no
    /// subject; this is the same rule on the login side, which is where it was missing.
    ///
    /// It does not trim, and the subject must not be trimmed: it is an opaque identifier
    /// this plugin compares byte for byte against a stored link, and quietly changing it
    /// would break every account whose provider pads it.
    /// </summary>
    private static string? Blankless(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// A name a Jellyfin account can be called, or null.
    ///
    /// Unlike the subject, this one is trimmed. The name is what an account is created
    /// under and what the collision check looks up, and the two have to agree: a provider
    /// sending " admin" found no collision with the existing "admin" — Jellyfin's own
    /// lookup does not pad — and then asked Jellyfin to create a second account whose
    /// name reads the same in every list it appears in.
    /// </summary>
    private static string? Name(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or ArgumentException)
        {
            // Not JSON, or text that cannot even be turned into UTF-8 — a lone surrogate
            // throws InvalidOperationException rather than JsonException. Either way the
            // claim grants nothing, which is what a claim nobody can read should do.
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
