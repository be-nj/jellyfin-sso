using System.Text.Json;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>
/// RFC 9207: the provider names itself in the redirect back, and the client checks that
/// it is the provider it sent the person to.
///
/// Each provider card has its own callback and its own state, which stops a code from one
/// provider being redeemed through another — as long as the honest provider sends codes
/// only to its own callback. A provider registered with a pattern (Keycloak's
/// <c>…/sso/oidc/callback/*</c>) sends them to every callback of this server, and then an
/// attacker running a second configured provider could send a visitor on to the honest
/// one and receive the honest provider's code and PKCE verifier at its own token endpoint
/// (#325). The <c>iss</c> parameter is what tells the two apart, and it is checked before
/// the code goes anywhere.
/// </summary>
public static class AuthorizationResponseIssuer
{
    /// <summary>The discovery member a provider uses to say it sends <c>iss</c>.</summary>
    public const string SupportedMember = "authorization_response_iss_parameter_supported";

    /// <summary>
    /// Why an authorization response must not be used, or null when it may be.
    /// </summary>
    /// <param name="iss">The <c>iss</c> query parameter the response came back with.</param>
    /// <param name="issuer">The issuer of the provider this login was started at.</param>
    /// <param name="discovery">That provider's discovery document.</param>
    public static string? Refusal(string? iss, string? issuer, JsonElement? discovery)
    {
        if (iss is not null)
        {
            return string.Equals(iss, issuer, StringComparison.Ordinal)
                ? null
                : "The authorization response names another issuer than the provider this "
                    + "login was started at; a code meant for one provider was delivered to "
                    + "another, so it was not redeemed.";
        }

        return SaysItSendsIss(discovery)
            ? "The provider says it names itself in every authorization response, and this "
                + "one carries no 'iss', so it was not redeemed."
            : null;
    }

    private static bool SaysItSendsIss(JsonElement? discovery)
        => discovery is { ValueKind: JsonValueKind.Object } document
            && document.TryGetProperty(SupportedMember, out var supported)
            && supported.ValueKind == JsonValueKind.True;
}
