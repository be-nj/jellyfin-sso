using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.Sso.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Oidc;

/// <summary>Who the identity provider says has signed out.</summary>
/// <param name="Sub">The subject whose sessions are over, when the token names one.</param>
/// <param name="Sid">
/// The provider's own session id. The plugin keeps no record of it, so it is carried here
/// for the log alone — a token that names only a session cannot be acted on.
/// </param>
public sealed record LogoutSubject(string? Sub, string? Sid);

/// <summary>
/// Checks a back-channel logout token (OpenID Connect Back-Channel Logout 1.0, §2.6).
/// The provider posts it to the plugin to say a session has ended; anybody on the network
/// can post to that endpoint, so the token is what makes the claim trustworthy and every
/// rule below is the difference between ending a session and letting a stranger end one.
/// </summary>
public static class LogoutTokenValidator
{
    /// <summary>The event a logout token has to carry to be one.</summary>
    public const string EventName = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>
    /// How old a logout token may be. The spec leaves <c>exp</c> optional, so the freshness
    /// of <c>iat</c> is what keeps a token captured once from ending a session again later.
    /// </summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Validates signature, issuer, audience, age and shape, and returns who the token
    /// says has signed out.
    /// </summary>
    /// <exception cref="SecurityTokenException">When the token is not a valid logout token.</exception>
    public static LogoutSubject Validate(
        ProviderConfig provider,
        string? issuer,
        IEnumerable<SecurityKey> signingKeys,
        string logoutToken,
        DateTimeOffset? now = null)
    {
        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = provider.ClientId,
            IssuerSigningKeys = signingKeys,
            // A logout token carries iat and only optionally exp, so the age is checked
            // below rather than here; an exp that is there is still honoured.
            RequireExpirationTime = false,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
        };

        var principal = handler.ValidateToken(logoutToken, parameters, out _);

        // "A nonce Claim MUST NOT be present." An ID token reused as a logout token would
        // otherwise end a session the user had just started.
        if (principal.FindFirst("nonce") is not null)
            throw new SecurityTokenException("A logout token must not carry a nonce.");

        RequireLogoutEvent(principal);
        RequireFreshIssuedAt(principal, now ?? DateTimeOffset.UtcNow);

        var sub = Trimmed(principal.FindFirst("sub")?.Value);
        var sid = Trimmed(principal.FindFirst("sid")?.Value);

        if (sub is null && sid is null)
            throw new SecurityTokenException("A logout token must carry 'sub', 'sid' or both.");

        return new LogoutSubject(sub, sid);
    }

    /// <summary>
    /// The <c>events</c> claim is what separates a logout token from any other token the
    /// same provider signed for the same audience.
    /// </summary>
    private static void RequireLogoutEvent(ClaimsPrincipal principal)
    {
        var events = principal.FindFirst("events")?.Value
            ?? throw new SecurityTokenException("A logout token must carry an 'events' claim.");

        try
        {
            using var document = JsonDocument.Parse(events);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(EventName, out _))
            {
                throw new SecurityTokenException(
                    "The 'events' claim does not name the back-channel logout event.");
            }
        }
        catch (JsonException)
        {
            throw new SecurityTokenException("The 'events' claim is not a JSON object.");
        }
    }

    private static void RequireFreshIssuedAt(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var raw = principal.FindFirst("iat")?.Value
            ?? throw new SecurityTokenException("A logout token must carry an 'iat' claim.");

        if (!long.TryParse(raw, out var seconds))
            throw new SecurityTokenException("The 'iat' claim is not a number.");

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);

        if (issuedAt > now + ClockSkew)
            throw new SecurityTokenException("The logout token was issued in the future.");

        if (issuedAt < now - MaxAge)
            throw new SecurityTokenException("The logout token is too old.");
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
