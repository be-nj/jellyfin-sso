using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Logging;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// Out to the identity provider.
///
/// Four ways in and one thing done with all of them. A browser can arrive at a stable
/// address that picks the provider for it, at a named provider, or at either of those
/// carrying a Quick Connect code a native app printed; each ends in the same redirect,
/// and they differ only in what the plugin writes into the state it keeps for the way
/// back. The way back is <see cref="SsoCallbackController"/>.
/// </summary>
[ApiController]
[Route("sso")]
public sealed class SsoStartController : SsoFlowController
{
    private readonly IQuickConnect _quickConnect;
    private readonly ILogger<SsoStartController> _logger;

    public SsoStartController(
        PendingStateStore states,
        OidcClientFactory oidc,
        IQuickConnect quickConnect,
        ILogger<SsoStartController> logger)
        : base(states, oidc)
    {
        _quickConnect = quickConnect;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Web login path: browser navigates here, gets redirected to IdP
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stable entry point. With one enabled provider it goes straight there; with several
    /// it asks which one. The URL never changes, so the login button can be static.
    /// </summary>
    [HttpGet("oidc/start")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> StartDefault(CancellationToken ct)
    {
        var enabled = EnabledProviders();
        if (enabled.Count == 0)
            return NotFound("No enabled SSO provider configured.");
        if (enabled.Count > 1)
            return ProviderPicker(enabled, queryString: string.Empty);

        return await Start(enabled[0].Id, ct).ConfigureAwait(false);
    }

    /// <summary>Same but for Quick Connect — stable entry point for native apps.</summary>
    [HttpGet("oidc/start/qc")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> StartDefaultQuickConnect(
        [FromQuery] string code, CancellationToken ct)
    {
        var enabled = EnabledProviders();
        if (enabled.Count == 0)
            return NotFound("No enabled SSO provider configured.");
        if (enabled.Count > 1)
            return ProviderPicker(
                enabled, queryString: $"?code={Uri.EscapeDataString(code)}", quickConnect: true);

        return await StartWithQuickConnect(enabled[0].Id, code, ct).ConfigureAwait(false);
    }

    [HttpGet("oidc/start/{providerId}")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> Start(string providerId, CancellationToken ct)
    {
        var provider = EnabledProvider(providerId);
        if (provider is null)
            return NoSuchProvider();

        return await StartAtProviderAsync(provider, ct).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Quick Connect path: native app initiates QC, browser completes OIDC here
    // ──────────────────────────────────────────────────────────────────────────

    [HttpGet("oidc/start/{providerId}/qc")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> StartWithQuickConnect(
        string providerId, [FromQuery] string code, CancellationToken ct)
    {
        if (!_quickConnect.IsEnabled)
            return PlainText.BadRequest("Quick Connect is disabled on this server.");

        var provider = EnabledProvider(providerId);
        if (provider is null)
            return NoSuchProvider();

        return await StartAtProviderAsync(provider, ct, quickConnectCode: code)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the browser to the provider, or says why it cannot.
    ///
    /// Building that address fetches the discovery document and checks it, and both can
    /// fail on a provider that is unreachable or configured wrongly. Nothing caught that
    /// here, so the login button answered a 500 — and Jellyfin's exception middleware puts
    /// the message in the body, which carries the configured authority to an anonymous
    /// caller. The callback has had this treatment since the secret leak; the start path
    /// had not.
    /// </summary>
    private async Task<IActionResult> StartAtProviderAsync(
        ProviderConfig provider, CancellationToken ct, string? quickConnectCode = null)
    {
        try
        {
            return Redirect(
                await AuthorizeUrlAsync(provider, ct, quickConnectCode).ConfigureAwait(false));
        }
        catch (PendingStateStore.TooManyPendingLoginsException)
        {
            // The store is full and keeps the logins already in it. Somebody else's flow
            // is not this request's to take.
            return PlainText.Unavailable(
                "Too many logins are in progress on this server right now. Please try "
                + "again in a moment.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex, "Could not start a login at provider {Id}",
                Loggable.KnownProviderId(provider.Id));
            return PlainText.BadRequest(
                "This provider could not be reached or is not configured correctly, so the "
                + "login could not be started. The server log says what is wrong.");
        }
    }

    /// <summary>Lets the user choose when more than one provider is enabled.</summary>
    private IActionResult ProviderPicker(
        IReadOnlyList<ProviderConfig> providers, string queryString, bool quickConnect = false)
        => Content(
            SsoPages.ProviderPicker(At.BasePath, providers, queryString, quickConnect),
            "text/html");
}
