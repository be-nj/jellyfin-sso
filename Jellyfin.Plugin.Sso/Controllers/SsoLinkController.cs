using Jellyfin.Plugin.Sso.Logging;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// Self-service linking: a user who is already signed in to Jellyfin attaches their own
/// identity-provider account to it, so that the next SSO login lands in the account they
/// already have rather than a new one beside it.
///
/// Three endpoints, and the third one is the reason the first two are gated the way they
/// are: an operator who turns self-service linking off has said who manages links, and
/// that is them. The login that completes a link comes back to the callback in
/// <see cref="SsoLoginController"/>, because at the provider it is an ordinary login; what
/// makes it a link is what the plugin wrote into the state it kept.
/// </summary>
[ApiController]
[Route("sso")]
public sealed class SsoLinkController : SsoFlowController
{
    private readonly SessionRevoker _revoker;
    private readonly ILogger<SsoLinkController> _logger;

    public SsoLinkController(
        PendingStateStore states,
        OidcClientFactory oidc,
        SessionRevoker revoker,
        ILogger<SsoLinkController> logger)
        : base(states, oidc)
    {
        _revoker = revoker;
        _logger = logger;
    }

    /// <summary>
    /// The page a user opens to link their own account. It is anonymous because the browser
    /// cannot send Jellyfin's token on a navigation; the page reads the token the web client
    /// stored and calls <see cref="StartSelfServiceLink"/> with it.
    /// </summary>
    [HttpGet("link")]
    [AllowAnonymous]
    public IActionResult SelfServiceLinkPage()
    {
        if (!Allowed())
            return NotFound("Self-service linking is disabled.");

        var providers = EnabledProviders();
        if (providers.Count == 0)
            return NotFound("No enabled SSO provider configured.");

        return Content(SsoPages.SelfServiceLink(At.BasePath, providers), "text/html");
    }

    /// <summary>
    /// Begins the OIDC flow for the signed-in user and returns where to send the browser.
    /// Authorization is why this is a POST with the token in a header rather than a redirect.
    /// </summary>
    [HttpPost("link/start")]
    [Authorize]
    public async Task<IActionResult> StartSelfServiceLink(
        [FromQuery] string providerId, CancellationToken ct)
    {
        if (!Allowed())
            return NotFound("Self-service linking is disabled.");

        if (CurrentUserId() is not { } userId)
            return Unauthorized("No Jellyfin user in this request.");

        var provider = EnabledProvider(providerId);
        if (provider is null)
            return NoSuchProvider();

        var config = Plugin.Instance!.Configuration;
        if (config.Links.Any(l => l.ProviderId == provider.Id && l.JellyfinUserId == userId))
            return Conflict("This account is already linked for that provider.");

        string authorizeUrl;
        try
        {
            authorizeUrl = await AuthorizeUrlAsync(provider, ct, linkUserId: userId)
                .ConfigureAwait(false);
        }
        catch (PendingStateStore.TooManyPendingLoginsException)
        {
            return PlainText.Unavailable(
                "Too many logins are in progress on this server right now. Please try "
                + "again in a moment.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex, "Could not start a link at provider {Id}",
                Loggable.KnownProviderId(provider.Id));
            return PlainText.BadRequest(
                "This provider could not be reached or is not configured correctly.");
        }

        _logger.LogInformation(
            "Self-service link started by user {UserId} for provider {Id}",
            userId, Loggable.KnownProviderId(provider.Id));

        return Ok(new { AuthorizeUrl = authorizeUrl });
    }

    /// <summary>
    /// Removes the signed-in user's own link for one provider, and ends their sessions.
    ///
    /// The sessions have to go with it. Back-channel logout finds an account through its
    /// link and nothing else, so an account that unlinked itself kept tokens the identity
    /// provider could no longer end — disable the person at the provider and the plugin
    /// would revoke "0 account(s)" and answer 200, while their tokens, which Jellyfin does
    /// not expire, kept working. Unlinking now means signing out, which is both safe and
    /// what somebody pressing it would expect.
    /// </summary>
    [HttpDelete("link/self")]
    [Authorize]
    public async Task<IActionResult> RemoveOwnLink(
        [FromQuery] string providerId, CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;

        // Gated like the two endpoints above it, which it was not: an operator who turns
        // self-service linking off has said who manages links, and that is them.
        if (!config.AllowSelfServiceLinking)
            return NotFound("Self-service linking is disabled.");

        if (CurrentUserId() is not { } userId)
            return Unauthorized("No Jellyfin user in this request.");

        // Required rather than optional. Without it this removed every link the caller
        // had, which is a surprising amount of work for a missing query parameter.
        if (string.IsNullOrWhiteSpace(providerId))
            return PlainText.BadRequest("Name the provider to unlink, as ?providerId=…");

        var unlinked = await new Unlinking(_revoker, _logger)
            .RemoveAsync(userId, providerId, ct).ConfigureAwait(false);

        return unlinked
            ? Ok()
            : NotFound("No link found for this account and provider.");
    }

    /// <summary>Whether the operator has left self-service linking on.</summary>
    private static bool Allowed()
        => Plugin.Instance?.Configuration is { AllowSelfServiceLinking: true };
}
