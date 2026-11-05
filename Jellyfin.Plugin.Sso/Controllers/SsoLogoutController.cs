using Duende.IdentityModel.Client;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Logging;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// Ending a session — from this side, and from the provider's.
///
/// Two endpoints that look unrelated and are the same subject. One is a navigation a
/// person makes when they press "sign out" and the plugin passes it on to the identity
/// provider; the other is the provider's own back end telling the plugin that a session
/// it issued is over. Both come down to the same question, which is whose session, and
/// both have to end in the session being gone rather than in an error page.
/// </summary>
[ApiController]
[Route("sso")]
public sealed class SsoLogoutController : SsoControllerBase
{
    private readonly LogoutSessionStore _logoutSessions;
    private readonly LogoutTokenLedger _logoutTokens;
    private readonly OidcClientFactory _oidc;
    private readonly SessionRevoker _revoker;
    private readonly ILogger<SsoLogoutController> _logger;

    public SsoLogoutController(
        LogoutSessionStore logoutSessions,
        LogoutTokenLedger logoutTokens,
        OidcClientFactory oidc,
        SessionRevoker revoker,
        ILogger<SsoLogoutController> logger)
    {
        _logoutSessions = logoutSessions;
        _logoutTokens = logoutTokens;
        _oidc = oidc;
        _revoker = revoker;
        _logger = logger;
    }

    /// <summary>
    /// Ends the session at the identity provider and comes back to the web client. Jellyfin
    /// has already dropped its own token by the time a client sends the browser here; this
    /// is the part Jellyfin cannot do, because only the plugin knows the provider.
    /// </summary>
    [HttpGet("oidc/logout")]
    [HttpGet("oidc/logout/{providerId}")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(string? providerId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        if (!StartedHere())
        {
            return LocalRedirect(At.LocalWebClient);
        }

        // Read and dropped before anything decides what this logout will do, on purpose:
        // somebody logging out should not leave their ID token held here, whether or not
        // the provider is told about it. The cost is that a logout which does not use the
        // hint — the switch off, or another provider named in the path — spends it anyway,
        // and the next login is what brings it back. That is the right way round: holding
        // the token longer would be the surprising half.
        var session = _logoutSessions.Consume(Cookies.Value(LogoutSessionStore.CookieName));
        Cookies.ForgetLogout();

        var home = At.LocalWebClient;
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.SignOutOfIdentityProvider)
            return LocalRedirect(home);

        providerId ??= session?.ProviderId;
        var provider = providerId is null
            ? EnabledProviders().FirstOrDefault()
            : EnabledProvider(providerId);

        if (provider is null)
            return LocalRedirect(home);

        try
        {
            var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(disco.EndSessionEndpoint))
            {
                _logger.LogInformation(
                    "Provider {Id} advertises no end_session_endpoint; skipping provider logout.",
                    Loggable.KnownProviderId(provider.Id));
                return LocalRedirect(home);
            }

            var parameters = new Parameters
            {
                { "client_id", provider.ClientId },
                { "post_logout_redirect_uri", At.ExternalWebClient },
            };
            if (session is not null && session.Matches(provider))
                parameters.Add("id_token_hint", session.IdentityToken);

            return Redirect(new RequestUrl(disco.EndSessionEndpoint).Create(parameters));
        }
        catch (InvalidOperationException ex)
        {
            // A provider that cannot be reached must not keep the user from logging out.
            _logger.LogWarning(
                ex, "Provider logout failed for {Id}", Loggable.KnownProviderId(provider.Id));
            return LocalRedirect(home);
        }
    }

    /// <summary>
    /// Whether the browser says this logout began on this site, and may therefore spend
    /// the retained token.
    ///
    /// A logout is a GET, because the documented way to reach it is a navigation the
    /// operator adds to Jellyfin's own logout. That makes it drivable from another site:
    /// window.location = ".../sso/oidc/logout" would sign the visitor out of their
    /// identity provider — every application in that session — and spend the token kept
    /// for their own logout. A confirmation step is what the spec suggests, and it would
    /// break the silent sign-out the switch exists for. The browser's own account of where
    /// the request came from costs nothing and does not: it is absent on old browsers,
    /// which then behave as before, and "cross-site" only where the navigation did not
    /// start here.
    ///
    /// Only this site's own navigations are honoured. Rejecting "cross-site" alone let a
    /// sibling subdomain through, because a page on evil.example.com navigating to
    /// jf.example.com sends "same-site" — and that is the same adversary the cookie prefix
    /// is written against. An absent header is an old browser, which gets the behaviour it
    /// always had.
    /// </summary>
    private bool StartedHere()
    {
        var from = Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        if (from is not { Length: > 0 }
            || string.Equals(from, "same-origin", StringComparison.Ordinal)
            || string.Equals(from, "none", StringComparison.Ordinal))
        {
            return true;
        }

        _logger.LogInformation(
            "Ignored a logout the browser reports as coming from {Site}; nothing was "
            + "signed out.",
            Loggable.NamedFetchSite(from));
        return false;
    }

    /// <summary>
    /// Where the identity provider says a session has ended (OpenID Connect Back-Channel
    /// Logout 1.0). Anonymous because the caller is the provider's own back end and has no
    /// Jellyfin token; the signed logout token is what authorises it, and nothing happens
    /// until that token checks out against the same JWKS a login is checked against.
    /// </summary>
    [HttpPost("oidc/backchannel-logout/{providerId}")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> BackChannelLogout(
        string providerId,
        [FromForm(Name = "logout_token")] string? logoutToken,
        CancellationToken ct)
    {
        // "The response MUST be cached by neither the client nor any intermediary."
        Response.Headers.CacheControl = "no-store";

        var provider = EnabledProvider(providerId);
        if (provider is null)
            return NoSuchProvider();

        if (!provider.AcceptBackChannelLogout)
            return PlainText.NotFound("Back-channel logout is turned off for this provider.");

        if (string.IsNullOrWhiteSpace(logoutToken))
            return PlainText.BadRequest("No logout_token in the request.");

        LogoutSubject subject;
        try
        {
            var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);
            subject = LogoutTokenValidator.Validate(
                provider, disco.Issuer, OidcClientFactory.SigningKeysFrom(disco), logoutToken);
        }
        catch (SecurityTokenException ex)
        {
            // The message describes the token, never quotes it.
            _logger.LogWarning(
                "Rejected a back-channel logout for provider {Id}: {Reason}",
                Loggable.KnownProviderId(provider.Id), ex.Message);
            return PlainText.BadRequest("The logout token is not valid.");
        }
        catch (InvalidOperationException ex)
        {
            // Discovery failed: the provider is unreachable, so the token cannot be checked.
            _logger.LogWarning(
                ex, "Could not check a back-channel logout for provider {Id}",
                Loggable.KnownProviderId(provider.Id));
            return PlainText.BadRequest("The logout token could not be checked.");
        }

        // Freshness is not a replay control on its own: the same token stays valid for as
        // long as its iat is young, and anybody who can read one in flight could otherwise
        // post it again for the rest of that window. Asked after validation, so an
        // unsigned or forged token cannot fill the ledger — and only asked, not recorded:
        // the record is written once the sessions are actually gone.
        if (_logoutTokens.AlreadyActedOn(provider.Id, subject.Jti, logoutToken))
        {
            _logger.LogWarning(
                "Rejected a back-channel logout for provider {Id}: this logout token has "
                + "already been acted on.",
                Loggable.KnownProviderId(provider.Id));
            return PlainText.BadRequest("This logout token has already been used.");
        }

        if (subject.Sub is null)
        {
            // A token that names only the provider's session id cannot be matched: the
            // plugin stores the subject, not the session.
            _logger.LogInformation(
                "Back-channel logout for provider {Id} named only a session id; nothing to do.",
                Loggable.KnownProviderId(provider.Id));
            return Ok();
        }

        int revoked;
        try
        {
            revoked = await _revoker.RevokeAsync(provider.Id, subject.Sub, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing is recorded, so the provider's retry is a retry and not a replay.
            // This endpoint exists to end sessions; refusing the second attempt because
            // the first one broke is the one direction it must not fail in.
            _logger.LogError(
                ex, "A back-channel logout from provider {Id} could not end the sessions "
                + "it named; the provider may retry.", Loggable.KnownProviderId(provider.Id));
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "The sessions could not be ended. Please retry.");
        }

        // Recorded now that the work is done. Recording it first meant a failure between
        // the two turned the provider's standards-compliant retry into a refused replay,
        // and the sessions the provider had ended lived on — Jellyfin's tokens do not
        // expire by themselves.
        _logoutTokens.Remember(provider.Id, subject.Jti, logoutToken);

        _logger.LogInformation(
            "Back-channel logout from provider {Id} ended the sessions of {Count} account(s).",
            Loggable.KnownProviderId(provider.Id), revoked);

        return Ok();
    }
}
