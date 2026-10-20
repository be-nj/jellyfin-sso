using Duende.IdentityModel.Client;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Web;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Controllers;

[ApiController]
[Route("sso")]
public sealed class SsoController : ControllerBase
{
    private readonly PendingStateStore _states;
    private readonly OidcClientFactory _oidc;
    private readonly ClaimsExtractor _claims;
    private readonly UserProvisioner _provisioner;
    private readonly PermissionSync _permissions;
    private readonly ProfilePictureSync _pictures;
    private readonly SessionRevoker _revoker;
    private readonly EphemeralSecretStore _secrets;
    private readonly QuickConnectApprovalStore _approvals;
    private readonly ProviderLogos _logos;
    private readonly IQuickConnect _quickConnect;
    private readonly MediaBrowser.Controller.Library.IUserManager _userManager;
    private readonly ILogger<SsoController> _logger;

    public SsoController(
        PendingStateStore states,
        OidcClientFactory oidc,
        ClaimsExtractor claims,
        UserProvisioner provisioner,
        PermissionSync permissions,
        ProfilePictureSync pictures,
        SessionRevoker revoker,
        EphemeralSecretStore secrets,
        QuickConnectApprovalStore approvals,
        ProviderLogos logos,
        IQuickConnect quickConnect,
        MediaBrowser.Controller.Library.IUserManager userManager,
        ILogger<SsoController> logger)
    {
        _states = states;
        _oidc = oidc;
        _claims = claims;
        _provisioner = provisioner;
        _permissions = permissions;
        _pictures = pictures;
        _revoker = revoker;
        _secrets = secrets;
        _approvals = approvals;
        _logos = logos;
        _quickConnect = quickConnect;
        _userManager = userManager;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Web login path: browser navigates here, gets redirected to IdP
    // ──────────────────────────────────────────────────────────────────────────

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

        return Content(SsoPages.LoginButtonScript(BasePath(), providers), "application/javascript");
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
        var provider = GetEnabledProvider(providerId);
        if (provider is null)
        {
            return NotFoundText("No such provider, or it is not enabled.");
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
    /// The mark on a provider's login button. Served from here rather than from a CDN so
    /// that a login page on a LAN without internet still renders it, and so that nobody
    /// outside learns who visits the login page.
    /// </summary>
    [HttpGet("icon/{slug}")]
    [AllowAnonymous]
    public IActionResult Icon(string slug)
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(ProviderIcons.Svg(slug), "image/svg+xml");
    }

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
            return ProviderPicker(enabled, queryString: $"?code={Uri.EscapeDataString(code)}");

        return await StartWithQuickConnect(enabled[0].Id, code, ct).ConfigureAwait(false);
    }

    [HttpGet("oidc/start/{providerId}")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> Start(string providerId, CancellationToken ct)
    {
        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFoundText("No such provider, or it is not enabled.");

        return Redirect(await AuthorizeUrlAsync(provider, ct).ConfigureAwait(false));
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
            return BadRequestText("Quick Connect is disabled on this server.");

        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFoundText("No such provider, or it is not enabled.");

        return Redirect(await AuthorizeUrlAsync(provider, ct, quickConnectCode: code)
            .ConfigureAwait(false));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Logout: end the session at the identity provider too
    // ──────────────────────────────────────────────────────────────────────────

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
        // Local by construction: BasePath is either empty or a validated path, so this
        // is a path and never an address somewhere else. LocalRedirect enforces that.
        var home = $"{BasePath()}/web/index.html";
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.SignOutOfIdentityProvider)
            return LocalRedirect(home);

        var provider = providerId is null
            ? EnabledProviders().FirstOrDefault()
            : GetEnabledProvider(providerId);

        if (provider is null)
            return LocalRedirect(home);

        try
        {
            var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(disco.EndSessionEndpoint))
            {
                _logger.LogInformation(
                    "Provider {Id} advertises no end_session_endpoint; skipping provider logout.",
                    KnownProviderId(provider.Id));
                return LocalRedirect(home);
            }

            var returnTo = $"{ExternalBaseUri()}{home}";
            var url = new RequestUrl(disco.EndSessionEndpoint)
                .Create(new Parameters
                {
                    { "client_id", provider.ClientId },
                    { "post_logout_redirect_uri", returnTo },
                });

            return Redirect(url);
        }
        catch (InvalidOperationException ex)
        {
            // A provider that cannot be reached must not keep the user from logging out.
            _logger.LogWarning(ex, "Provider logout failed for {Id}", KnownProviderId(provider.Id));
            return LocalRedirect(home);
        }
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

        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFoundText("No such provider, or it is not enabled.");

        if (!provider.AcceptBackChannelLogout)
            return NotFoundText("Back-channel logout is turned off for this provider.");

        if (string.IsNullOrWhiteSpace(logoutToken))
            return BadRequestText("No logout_token in the request.");

        LogoutSubject subject;
        try
        {
            var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);
            subject = LogoutTokenValidator.Validate(
                provider, disco.Issuer, OidcClientFactory.SigningKeysFrom(disco), logoutToken);
        }
        catch (Microsoft.IdentityModel.Tokens.SecurityTokenException ex)
        {
            // The message describes the token, never quotes it.
            _logger.LogWarning(
                "Rejected a back-channel logout for provider {Id}: {Reason}",
                KnownProviderId(provider.Id), ex.Message);
            return BadRequestText("The logout token is not valid.");
        }
        catch (InvalidOperationException ex)
        {
            // Discovery failed: the provider is unreachable, so the token cannot be checked.
            _logger.LogWarning(
                ex, "Could not check a back-channel logout for provider {Id}",
                KnownProviderId(provider.Id));
            return BadRequestText("The logout token could not be checked.");
        }

        if (subject.Sub is null)
        {
            // A token that names only the provider's session id cannot be matched: the
            // plugin stores the subject, not the session.
            _logger.LogInformation(
                "Back-channel logout for provider {Id} named only a session id; nothing to do.",
                KnownProviderId(provider.Id));
            return Ok();
        }

        var revoked = await _revoker.RevokeAsync(provider.Id, subject.Sub, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Back-channel logout from provider {Id} ended the sessions of {Count} account(s).",
            KnownProviderId(provider.Id), revoked);

        return Ok();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Callback: IdP returns here with code+state
    // ──────────────────────────────────────────────────────────────────────────

    [HttpGet("oidc/callback/{providerId}")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> Callback(
        string providerId,
        // Nullable on purpose: a provider that reports an error sends no code, and model
        // validation would otherwise answer with a raw error blob before the error branch
        // below ever runs.
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            var errorCode = OAuthErrorCode(error);
            _logger.LogWarning(
                "OIDC callback error for provider {Id}: {Error}", KnownProviderId(providerId), errorCode);
            return BadRequestText($"IdP returned error: {errorCode}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return BadRequestText("The provider returned neither an authorization code nor an error.");

        var pending = _states.Consume(state);
        if (pending is null)
            return BadRequestText("Invalid or expired state. Please try logging in again.");

        var provider = GetEnabledProvider(providerId);
        if (provider is null || provider.Id != pending.ProviderId)
            return BadRequestText("Provider mismatch.");

        try
        {
            var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

            var tokens = await _oidc.ExchangeCodeAsync(
                provider, disco, code, pending.RedirectUri, pending.CodeVerifier, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(tokens.IdentityToken))
            {
                return BadRequestText(
                    "The provider returned no ID token. Check that the 'openid' scope is "
                    + "allowed for this application.");
            }

            var identity = await _claims.ExtractAsync(
                provider, disco,
                tokens.IdentityToken, tokens.AccessToken ?? string.Empty,
                pending.Nonce, ct)
                .ConfigureAwait(false);

            // Self-service linking stops here: it attaches the identity to the account the
            // user was already signed into, and grants nothing by itself.
            if (pending.LinkUserId is { } linkUserId)
                return CompleteSelfServiceLink(provider, identity, linkUserId);

            var (userId, username) = await _provisioner.ResolveAsync(provider, identity, ct)
                .ConfigureAwait(false);

            await _permissions.SyncAsync(userId, identity, ct).ConfigureAwait(false);
            await _pictures.SyncAsync(userId, identity.PictureUrl, ct).ConfigureAwait(false);

            if (pending.QuickConnectCode is not null)
                return AskToApproveQuickConnect(userId, username, pending.QuickConnectCode);

            return await CompleteWebLoginAsync(username, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SSO login failed for provider {Id}", KnownProviderId(providerId));
            return BadRequestText(ex.Message);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Self-service linking: a signed-in user attaches their own SSO identity
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The page a user opens to link their own account. It is anonymous because the browser
    /// cannot send Jellyfin's token on a navigation; the page reads the token the web client
    /// stored and calls <see cref="StartSelfServiceLink"/> with it.
    /// </summary>
    [HttpGet("link")]
    [AllowAnonymous]
    public IActionResult SelfServiceLinkPage()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AllowSelfServiceLinking)
            return NotFound("Self-service linking is disabled.");

        var providers = EnabledProviders();
        if (providers.Count == 0)
            return NotFound("No enabled SSO provider configured.");

        return Content(SsoPages.SelfServiceLink(BasePath(), providers), "text/html");
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
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AllowSelfServiceLinking)
            return NotFound("Self-service linking is disabled.");

        if (CurrentUserId() is not { } userId)
            return Unauthorized("No Jellyfin user in this request.");

        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFoundText("No such provider, or it is not enabled.");

        if (config.Links.Any(l => l.ProviderId == provider.Id && l.JellyfinUserId == userId))
            return Conflict("This account is already linked for that provider.");

        var authorizeUrl = await AuthorizeUrlAsync(provider, ct, linkUserId: userId)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Self-service link started by user {UserId} for provider {Id}",
            userId, KnownProviderId(provider.Id));

        return Ok(new { AuthorizeUrl = authorizeUrl });
    }

    /// <summary>Removes the signed-in user's own link.</summary>
    [HttpDelete("link/self")]
    [Authorize]
    public IActionResult RemoveOwnLink([FromQuery] string? providerId)
    {
        var config = Plugin.Instance!.Configuration;

        if (CurrentUserId() is not { } userId)
            return Unauthorized("No Jellyfin user in this request.");

        lock (Plugin.ConfigurationLock)
        {
            var removed = config.Links.RemoveAll(l =>
                l.JellyfinUserId == userId
                && (providerId is null || l.ProviderId == providerId));

            if (removed == 0)
                return NotFound("No link found for this account.");

            Plugin.Instance.SaveConfiguration();
        }

        return Ok();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues the state for a new flow and builds the provider's authorize URL for it.
    /// Every entry point — plain login, Quick Connect, self-service linking — differs only
    /// in what it writes into that state, so the request itself is built in one place.
    /// </summary>
    private async Task<string> AuthorizeUrlAsync(
        ProviderConfig provider,
        CancellationToken ct,
        string? quickConnectCode = null,
        Guid? linkUserId = null)
    {
        var redirectUri = BuildCallbackUri(provider.Id);
        var (state, pending) = _states.Issue(provider.Id, redirectUri, quickConnectCode, linkUserId);

        var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        return new RequestUrl(disco.AuthorizeEndpoint
            ?? throw new InvalidOperationException("Discovery document has no authorize endpoint."))
            .CreateAuthorizeUrl(
                clientId: provider.ClientId,
                responseType: "code",
                scope: string.Join(' ', provider.EffectiveScopes()),
                redirectUri: redirectUri,
                state: state,
                nonce: pending.Nonce,
                codeChallenge: PendingStateStore.CodeChallenge(pending.CodeVerifier),
                codeChallengeMethod: "S256");
    }

    private Task<IActionResult> CompleteWebLoginAsync(string username, CancellationToken ct)
    {
        var secret = _secrets.Issue(username);

        return Task.FromResult<IActionResult>(
            Content(SsoPages.CompleteWebLogin(BasePath(), username, secret), "text/html"));
    }

    /// <summary>
    /// Asks before approving. The login is finished at this point; what is left is the
    /// decision to let the device that printed this code in, and only the person in front
    /// of the browser can make it.
    /// </summary>
    private IActionResult AskToApproveQuickConnect(
        Guid userId, string username, string quickConnectCode)
    {
        var token = _approvals.Issue(userId, username, quickConnectCode);

        return Content(
            SsoPages.ApproveQuickConnect(BasePath(), username, quickConnectCode, token),
            "text/html");
    }

    /// <summary>Approves the code the page just showed, once.</summary>
    [HttpPost("oidc/qc/approve")]
    [AllowAnonymous]
    [ServiceFilter<RateLimitFilter>]
    public async Task<IActionResult> ApproveQuickConnect([FromForm] string? token)
    {
        var pending = _approvals.Consume(token);
        if (pending is null)
        {
            return BadRequestText("This approval has expired. Start the login again.");
        }

        return await CompleteQuickConnectAsync(pending.UserId, pending.Code).ConfigureAwait(false);
    }

    private async Task<IActionResult> CompleteQuickConnectAsync(Guid userId, string quickConnectCode)
    {
        var authorized = await _quickConnect.AuthorizeRequest(userId, quickConnectCode)
            .ConfigureAwait(false);

        if (!authorized)
            return BadRequestText("Quick Connect authorization failed. The code may have expired.");

        return Content(SsoPages.QuickConnectApproved(), "text/html");
    }

    /// <summary>Attaches the identity to the account the user is already signed into.</summary>
    private IActionResult CompleteSelfServiceLink(
        ProviderConfig provider, OidcIdentity identity, Guid linkUserId)
    {
        var config = Plugin.Instance!.Configuration;

        var existing = config.Links.FirstOrDefault(l =>
            l.ProviderId == provider.Id &&
            string.Equals(l.Sub, identity.Sub, StringComparison.Ordinal));

        if (existing is not null)
        {
            if (existing.JellyfinUserId == linkUserId)
                return LinkResultPage("This account is already linked.", success: true);

            // The identity belongs to somebody else's account; linking would hand it over.
            _logger.LogWarning(
                "Self-service link refused for user {UserId}: the identity is already linked "
                + "to user {OwnerId} on provider {Id}.",
                linkUserId, existing.JellyfinUserId, KnownProviderId(provider.Id));
            return LinkResultPage(
                "That identity is already linked to another Jellyfin account. "
                + "Ask an administrator if this is unexpected.",
                success: false);
        }

        if (config.Links.Any(l => l.ProviderId == provider.Id && l.JellyfinUserId == linkUserId))
            return LinkResultPage("This account is already linked for that provider.", success: true);

        lock (Plugin.ConfigurationLock)
        {
            config.Links.Add(new LinkConfig
            {
                ProviderId = provider.Id,
                Sub = identity.Sub,
                JellyfinUserId = linkUserId,
            });
            Plugin.Instance.SaveConfiguration();
        }

        _logger.LogInformation(
            "Self-service link created for user {UserId} on provider {Id}",
            linkUserId, KnownProviderId(provider.Id));

        return LinkResultPage(
            "Linked. Your library access is applied the next time you sign in with SSO.",
            success: true);
    }

    private IActionResult LinkResultPage(string message, bool success)
        => Content(SsoPages.LinkResult(BasePath(), message, success), "text/html");

    /// <summary>Lets the user choose when more than one provider is enabled.</summary>
    private IActionResult ProviderPicker(IReadOnlyList<ProviderConfig> providers, string queryString)
        => Content(SsoPages.ProviderPicker(BasePath(), providers, queryString), "text/html");

    private static List<ProviderConfig> EnabledProviders()
        => Plugin.Instance?.Configuration.Providers.Where(p => p.Enabled).ToList() ?? [];

    /// <summary>
    /// The Jellyfin user behind the request. Jellyfin's own claim name is checked first,
    /// with the standard ones as a fallback so a server-side rename does not break linking.
    /// </summary>
    private Guid? CurrentUserId()
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

    /// <summary>
    /// The sub-path this server is published under, "" when it is at the root. Every link
    /// and fetch in the pages below is absolute, so each one has to carry it.
    /// </summary>
    private string BasePath()
        => ForwardedPaths.Prefix(
            HttpContext.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault(),
            HttpContext.Request.PathBase.Value);

    /// <summary>The server's externally visible base address, honouring proxy headers.</summary>
    private string ExternalBaseUri()
    {
        var req = HttpContext.Request;
        var scheme = ForwardedPaths.Scheme(
            req.Headers["X-Forwarded-Proto"].FirstOrDefault()
                ?? req.Headers["X-Forwarded-Scheme"].FirstOrDefault(),
            req.Scheme);
        var host = ForwardedPaths.Host(
            req.Headers["X-Forwarded-Host"].FirstOrDefault(), req.Host.ToString());

        // A server published under a sub-path ("BaseUrl" in Jellyfin's network settings,
        // or a proxy that keeps the prefix) must keep that prefix in its redirect URI,
        // or the provider sends the browser to a path this server does not serve.
        return ForwardedPaths.BaseUri(scheme, host, BasePath());
    }

    /// <summary>
    /// A 400 the browser shows as written. The default BadRequest(string) serialises to a
    /// JSON string, which a person who lands on the callback URL reads with quotes around
    /// it.
    /// </summary>
    private static IActionResult NotFoundText(string message)
        => new ContentResult
        {
            StatusCode = StatusCodes.Status404NotFound,
            Content = message,
            ContentType = "text/plain",
        };

    private static IActionResult BadRequestText(string message)
        => new ContentResult
        {
            StatusCode = StatusCodes.Status400BadRequest,
            Content = message,
            ContentType = "text/plain",
        };

    private static ProviderConfig? GetEnabledProvider(string providerId)
        => Plugin.Instance?.Configuration.Providers
            .FirstOrDefault(p => p.Enabled &&
                string.Equals(p.Id, providerId, StringComparison.Ordinal));

    // Honours reverse-proxy forwarded headers (Jellyfin runs behind HTTPS termination).
    private string BuildCallbackUri(string providerId)
        => $"{ExternalBaseUri()}/sso/oidc/callback/{providerId}";

    /// <summary>
    /// The id of the configured provider the request refers to, or "unknown". Logging the
    /// configured value rather than the query string keeps caller-controlled text — and
    /// the forged log lines it could carry — out of the log entirely.
    /// </summary>
    internal static string KnownProviderId(string providerId)
        => Plugin.Instance?.Configuration.Providers
            .FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.Ordinal))
            ?.Id
            ?? "unknown";

    /// <summary>
    /// Maps the IdP's error to the fixed set from RFC 6749 §4.1.2.1, so neither the log
    /// nor the response ever carries a string the caller chose.
    /// </summary>
    internal static string OAuthErrorCode(string? error) => error switch
    {
        "invalid_request" => "invalid_request",
        "unauthorized_client" => "unauthorized_client",
        "access_denied" => "access_denied",
        "unsupported_response_type" => "unsupported_response_type",
        "invalid_scope" => "invalid_scope",
        "server_error" => "server_error",
        "temporarily_unavailable" => "temporarily_unavailable",
        _ => "unrecognised_error",
    };
}
