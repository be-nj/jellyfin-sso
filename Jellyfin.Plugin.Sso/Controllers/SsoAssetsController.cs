using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// What the login page fetches: the script that puts the button there, and the marks on
/// the buttons.
///
/// All of it is anonymous and all of it is served from here rather than from a CDN, so
/// that a login page on a LAN without internet still renders, and so that nobody outside
/// learns who visits it.
/// </summary>
[ApiController]
[Route("sso")]
public sealed class SsoAssetsController : SsoControllerBase
{
    private readonly ProviderLogos _logos;

    public SsoAssetsController(ProviderLogos logos)
    {
        _logos = logos;
    }

    /// <summary>
    /// The script that places the login button inside Jellyfin's login form. Served only
    /// when the administrator asked for it; otherwise it is an empty file, so a cached
    /// index.html cannot keep a button alive after the switch is turned off.
    /// </summary>
    [HttpGet("login-button.js")]
    [AllowAnonymous]
    public IActionResult LoginButtonScript()
    {
        var config = Plugin.Instance?.Configuration;
        var providers = config is { InjectLoginButtonIntoForm: true } ? EnabledProviders() : [];

        Response.Headers.CacheControl = "no-cache";

        if (providers.Count == 0)
        {
            return Content("/* SSO: the in-form login button is off. */", "application/javascript");
        }

        return Content(SsoPages.LoginButtonScript(At.BasePath, providers), "application/javascript");
    }

    /// <summary>
    /// The provider's own logo, when it has one the plugin can find, and the built-in
    /// mark otherwise. Fetched by the server and cached, so the login page itself never
    /// talks to the identity provider.
    /// </summary>
    [HttpGet("icon/provider/{providerId}")]
    [AllowAnonymous]
    public async Task<IActionResult> ProviderIcon(string providerId, CancellationToken ct)
    {
        var provider = EnabledProvider(providerId);
        if (provider is null)
        {
            return NoSuchProvider();
        }

        Response.Headers.CacheControl = "public, max-age=3600";

        var config = Plugin.Instance?.Configuration;
        if (config is { UseProviderLogos: true })
        {
            var logo = await _logos.GetAsync(provider, ct).ConfigureAwait(false);
            if (logo is not null)
            {
                return File(logo.Bytes, logo.ContentType);
            }
        }

        return Content(
            ProviderIcons.Svg(ProviderIcons.SlugFor(provider.Name, provider.Authority)),
            "image/svg+xml");
    }

    /// <summary>
    /// The mark on a provider's login button, by slug, for the pages that know which one
    /// they want.
    /// </summary>
    [HttpGet("icon/{slug}")]
    [AllowAnonymous]
    public IActionResult Icon(string slug)
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(ProviderIcons.Svg(slug), "image/svg+xml");
    }
}
