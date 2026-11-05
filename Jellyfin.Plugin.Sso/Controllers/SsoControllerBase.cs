using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// What every endpoint under /sso needs to know about the request it is answering.
///
/// The four controllers under this one are one feature cut into the four things it does:
/// serving the login button and its marks, starting and finishing a login, ending a
/// session, and letting somebody attach their own identity. They were a single
/// thousand-line class, which is readable only in the order it happens to be written in.
/// What they share is this: where the server is as the browser sees it, the two cookies
/// the flow sets, and which providers an operator has enabled.
/// </summary>
public abstract class SsoControllerBase : ControllerBase
{
    private SsoAddress? _at;
    private SsoCookies? _cookies;

    /// <summary>Where this server is, as the browser making this request sees it.</summary>
    private protected SsoAddress At => _at ??= SsoAddress.For(Request);

    /// <summary>The cookies the login flow sets, under the rules this deployment allows.</summary>
    private protected SsoCookies Cookies => _cookies ??= new SsoCookies(Request, Response, At);

    private protected static List<ProviderConfig> EnabledProviders()
        => Plugin.Instance?.Configuration.Providers.Where(p => p.Enabled).ToList() ?? [];

    private protected static ProviderConfig? EnabledProvider(string providerId)
        => Plugin.Instance?.Configuration.Providers
            .FirstOrDefault(p => p.Enabled &&
                string.Equals(p.Id, providerId, StringComparison.Ordinal));

    /// <summary>The answer when the provider named in the path is not one of ours.</summary>
    private protected static IActionResult NoSuchProvider()
        => PlainText.NotFound("No such provider, or it is not enabled.");

    /// <summary>
    /// The Jellyfin user behind the request. Jellyfin's own claim name is checked first,
    /// with the standard ones as a fallback so a server-side rename does not break linking.
    /// </summary>
    private protected Guid? CurrentUserId()
    {
        foreach (var claimType in new[]
                 {
                     "Jellyfin-UserId",
                     System.Security.Claims.ClaimTypes.NameIdentifier,
                     "sub",
                 })
        {
            if (Guid.TryParse(HttpContext.User.FindFirst(claimType)?.Value, out var id))
                return id;
        }

        return null;
    }
}
