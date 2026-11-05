using Jellyfin.Plugin.Sso.Oidc;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// The two cookies the login flow sets, and the rules they are set under.
///
/// Both are handles rather than credentials — one names the browser a login was started
/// in, the other names a retained ID token — but the rules around them are what keeps a
/// neighbouring host from planting either one, so they live in a single place where a
/// test can read them back.
/// </summary>
internal sealed class SsoCookies
{
    /// <summary>
    /// Names the browser a login was started in. Not a credential: it grants nothing on its
    /// own and is only ever compared with the value stored beside the pending login.
    /// </summary>
    internal const string PendingLogin = "SsoPendingLogin";

    private readonly HttpRequest _request;
    private readonly HttpResponse _response;
    private readonly SsoAddress _at;

    internal SsoCookies(HttpRequest request, HttpResponse response, SsoAddress at)
    {
        _request = request;
        _response = response;
        _at = at;
    }

    /// <summary>
    /// The name to set a cookie under. Over HTTPS it carries the <c>__Host-</c> prefix,
    /// which a browser only accepts from a secure origin, without a Domain attribute and
    /// with Path=/ — and that is the point: a cookie with that prefix cannot be set for
    /// this host by a sibling subdomain under a shared registrable domain. Without it, a
    /// neighbour at other.example.com could plant a logout handle of their own and have
    /// somebody else's logout end the neighbour's provider session instead of their own.
    ///
    /// Over plain HTTP the prefix is not allowed, so the bare name is used and the
    /// protection is simply not available — which is true of cookies in general there.
    /// </summary>
    internal static string NameUnder(string bare, bool secure) => secure ? $"__Host-{bare}" : bare;

    internal static CookieOptions PendingOptions(bool secure, string basePath) => new()
    {
        HttpOnly = true,
        Secure = secure,
        // Lax, not Strict: the provider redirects the browser back here from its own site,
        // and Strict would withhold the cookie on exactly that navigation.
        SameSite = SameSiteMode.Lax,
        // __Host- requires the root path. Without the prefix the narrower path is kept,
        // because then it is the only thing limiting where the cookie goes.
        Path = secure ? "/" : $"{basePath}/sso",
        MaxAge = TimeSpan.FromMinutes(15),
        IsEssential = true,
    };

    internal static CookieOptions LogoutOptions(bool secure, string basePath) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        Path = secure ? "/" : $"{basePath}/sso/oidc",
        IsEssential = true,
    };

    /// <summary>
    /// The value under the one name this deployment sets, and no other.
    ///
    /// Reading both names undid the whole point of the prefix. The bare name is exactly
    /// what a sibling subdomain under a shared registrable domain can set for this host,
    /// and the fallback applied whenever the browser had no prefixed cookie — which is the
    /// normal state, since the logout cookie dies with the browser session and the pending
    /// one lasts fifteen minutes. A planted pending cookie carrying the attacker's own
    /// browser id would have let them finish their login in somebody else's browser, which
    /// is the attack the browser binding exists to stop.
    ///
    /// The price of reading one name is that a deployment switching between HTTP and HTTPS
    /// loses one login or one logout. That is the right price.
    /// </summary>
    internal string? Value(string bare) => _request.Cookies[NameUnder(bare, _at.Secure)];

    internal void RememberPendingLogin(string browserId)
        => _response.Cookies.Append(
            NameUnder(PendingLogin, _at.Secure),
            browserId,
            PendingOptions(_at.Secure, _at.BasePath));

    internal void RememberLogout(string handle)
        => _response.Cookies.Append(
            NameUnder(LogoutSessionStore.CookieName, _at.Secure),
            handle,
            LogoutOptions(_at.Secure, _at.BasePath));

    internal void ForgetLogout()
        => _response.Cookies.Delete(
            NameUnder(LogoutSessionStore.CookieName, _at.Secure),
            LogoutOptions(_at.Secure, _at.BasePath));
}
