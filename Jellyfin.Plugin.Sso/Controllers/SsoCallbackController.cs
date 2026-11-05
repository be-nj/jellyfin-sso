using Microsoft.IdentityModel.Tokens;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Logging;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using Jellyfin.Plugin.Sso.Web;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sso.Controllers;

/// <summary>
/// Back from the identity provider, where the login is actually decided.
///
/// One endpoint receives every flow <see cref="SsoStartController"/> sent out, and what it
/// does depends on what the plugin wrote into the state it kept: finish a web login, ask
/// the person whether the device that printed a Quick Connect code may come in, or attach
/// the identity to an account that is already signed in. The second of those needs a
/// click, which is the other endpoint here.
/// </summary>
[ApiController]
[Route("sso")]
public sealed class SsoCallbackController : SsoFlowController
{
    private readonly LogoutSessionStore _logoutSessions;
    private readonly ClaimsExtractor _claims;
    private readonly UserProvisioner _provisioner;
    private readonly PermissionSync _permissions;
    private readonly ProfilePictureSync _pictures;
    private readonly EphemeralSecretStore _secrets;
    private readonly QuickConnectApprovalStore _approvals;
    private readonly IQuickConnect _quickConnect;
    private readonly ILogger<SsoCallbackController> _logger;

    public SsoCallbackController(
        PendingStateStore states,
        OidcClientFactory oidc,
        LogoutSessionStore logoutSessions,
        ClaimsExtractor claims,
        UserProvisioner provisioner,
        PermissionSync permissions,
        ProfilePictureSync pictures,
        EphemeralSecretStore secrets,
        QuickConnectApprovalStore approvals,
        IQuickConnect quickConnect,
        ILogger<SsoCallbackController> logger)
        : base(states, oidc)
    {
        _logoutSessions = logoutSessions;
        _claims = claims;
        _provisioner = provisioner;
        _permissions = permissions;
        _pictures = pictures;
        _secrets = secrets;
        _approvals = approvals;
        _quickConnect = quickConnect;
        _logger = logger;
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
            var errorCode = Loggable.OAuthErrorCode(error);
            _logger.LogWarning(
                "OIDC callback error for provider {Id}: {Error}",
                Loggable.KnownProviderId(providerId), errorCode);
            return PlainText.BadRequest($"IdP returned error: {errorCode}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return PlainText.BadRequest(
                "The provider returned neither an authorization code nor an error.");

        var pending = ConsumePendingLogin(state);
        if (pending is null)
        {
            return PlainText.BadRequest(
                "This login could not be matched to a state this browser started, or it "
                + "has expired. Please start again from the Jellyfin login page.");
        }

        var provider = EnabledProvider(providerId);
        if (provider is null || provider.Id != pending.ProviderId)
            return PlainText.BadRequest("Provider mismatch.");

        try
        {
            var signedIn = await IdentityFromProviderAsync(provider, pending, code, ct)
                .ConfigureAwait(false);

            if (signedIn is null)
            {
                return PlainText.BadRequest(
                    "The provider returned no ID token. Check that the 'openid' scope is "
                    + "allowed for this application.");
            }

            var identity = signedIn.Identity;

            // Self-service linking stops here: it attaches the identity to the account the
            // user was already signed into, and grants nothing by itself.
            if (pending.LinkUserId is { } linkUserId)
                return CompleteSelfServiceLink(provider, identity, linkUserId);

            WarnIfNoGroupsArrived(provider, identity);

            if (RefuseUnmatchedLogin(provider, identity) is { } refused)
                return refused;

            var (userId, username) = await _provisioner.ResolveAsync(provider, identity, ct)
                .ConfigureAwait(false);

            await _permissions.SyncAsync(userId, identity, ct).ConfigureAwait(false);
            await _pictures.SyncAsync(userId, identity.PictureUrl, provider, ct)
                .ConfigureAwait(false);

            if (pending.QuickConnectCode is not null)
                return AskToApproveQuickConnect(userId, username, pending.QuickConnectCode);

            RememberForLogout(provider, signedIn.IdentityToken);

            return CompleteWebLogin(username);
        }
        catch (InvalidOperationException ex)
        {
            // The message is for the operator, not the visitor: these carry the configured
            // authority, the name of an environment variable, and — on a server where the
            // secret reference was filled in by hand before this was validated — the
            // secret itself. The log is where that belongs.
            _logger.LogWarning(
                ex, "SSO login failed for provider {Id}", Loggable.KnownProviderId(providerId));
            return PlainText.BadRequest(
                "This provider is not configured correctly, so the login could not be "
                + "completed. The server log says what is wrong.");
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            // A token that does not validate is a refusal, not a server fault. Without
            // this the exception middleware answered 500 and put its message in the body.
            // ArgumentException belongs here too: a token the library will not even parse
            // — a registered claim carrying an object or an array — fails that way.
            _logger.LogWarning(
                "Rejected a token from provider {Id}: {Reason}",
                Loggable.KnownProviderId(providerId), ex.Message);
            return PlainText.BadRequest(
                "The provider's answer could not be verified, so the login was refused.");
        }
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
            return PlainText.BadRequest("This approval has expired. Start the login again.");
        }

        var authorized = await _quickConnect.AuthorizeRequest(pending.UserId, pending.Code)
            .ConfigureAwait(false);

        if (!authorized)
            return PlainText.BadRequest(
                "Quick Connect authorization failed. The code may have expired.");

        return Content(SsoPages.QuickConnectApproved(), "text/html");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Who the provider says this is, and the token it said it in — the token is kept
    /// because a later logout can prove with it which session is ending.
    /// </summary>
    private sealed record SignedIn(OidcIdentity Identity, string IdentityToken);

    /// <summary>
    /// Spends the authorization code and reads who the person is, or null when the
    /// provider sent no ID token to read it from.
    /// </summary>
    private async Task<SignedIn?> IdentityFromProviderAsync(
        ProviderConfig provider, PendingStateStore.PendingState pending, string code, CancellationToken ct)
    {
        var disco = await Oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        var tokens = await Oidc.ExchangeCodeAsync(
            provider, disco, code, pending.RedirectUri, pending.CodeVerifier, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(tokens.IdentityToken))
        {
            return null;
        }

        try
        {
            var identity = await _claims.ExtractAsync(
                provider, disco,
                tokens.IdentityToken, tokens.AccessToken ?? string.Empty,
                pending.Nonce, ct)
                .ConfigureAwait(false);

            return new SignedIn(identity, tokens.IdentityToken);
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // The provider signed this with a key the cached document does not carry,
            // which is what a key rotation looks like from here. The document is
            // cached for half an hour, so without this every login on the server
            // failed for the rest of that window and each failure had already spent
            // its pending state. Fetch once more and validate against what the
            // provider publishes now; a token signed by nobody still fails, which is
            // the point of trying exactly once.
            _logger.LogInformation(
                "A token from {Name} was signed by an unknown key; refetching its keys.",
                provider.Name);

            Oidc.Forget(provider);
            var fresh = await Oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

            var identity = await _claims.ExtractAsync(
                provider, fresh,
                tokens.IdentityToken, tokens.AccessToken ?? string.Empty,
                pending.Nonce, ct)
                .ConfigureAwait(false);

            return new SignedIn(identity, tokens.IdentityToken);
        }
    }

    /// <summary>
    /// Keeps this browser's ID token so a later logout can prove to the provider which
    /// session is ending, and hands the browser the opaque handle to it.
    ///
    /// Only while <c>SignOutOfIdentityProvider</c> is on. An ID token is somebody's
    /// identity, and the only thing this plugin ever does with a stored one is put it in
    /// an <c>id_token_hint</c>; on a server that never asks the provider to sign anybody
    /// out, keeping one per browser login for thirty days would be collecting what nobody
    /// is going to read.
    /// </summary>
    private void RememberForLogout(ProviderConfig provider, string identityToken)
    {
        if (Plugin.Instance?.Configuration.SignOutOfIdentityProvider != true)
        {
            return;
        }

        // Replaces whatever this browser had: one retained login per browser, and the
        // previous token stops being held the moment it stops being current.
        _logoutSessions.Consume(Cookies.Value(LogoutSessionStore.CookieName));
        Cookies.RememberLogout(_logoutSessions.Issue(provider, identityToken));
        Response.Headers.CacheControl = "no-store";
    }

    private IActionResult CompleteWebLogin(string username)
    {
        var secret = _secrets.Issue(username);

        // The secret is in this page's body. It is single-use and lives ninety seconds,
        // and the page spends it immediately — but a shared cache or a browser's disk
        // cache holding a live credential is not something to leave to chance. This used
        // to be set only as a side effect of remembering the logout token, which happens
        // only when the provider-logout switch is on.
        Response.Headers.CacheControl = "no-store";

        return Content(SsoPages.CompleteWebLogin(At.BasePath, username, secret), "text/html");
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

        // The approval token is in this page's body and is the whole authority for letting
        // the waiting device in, so the page must not be stored anywhere.
        Response.Headers.CacheControl = "no-store";

        // And it must not be shown inside somebody else's page. The approval is a click,
        // and a click can be stolen from a frame: an attacker can start Quick Connect,
        // put this page in a hidden frame under a decoy button, and have a visitor let
        // the attacker's device in. The plugin cannot rely on the deployment's proxy
        // adding this.
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";

        return Content(
            SsoPages.ApproveQuickConnect(At.BasePath, username, quickConnectCode, token),
            "text/html");
    }

    /// <summary>
    /// A login that brought no groups while group mappings exist. Nothing fails — the
    /// account is created and arrives with no libraries, which is the setup mistake that
    /// takes longest to recognise, so the log says which settings decide it.
    ///
    /// Only when mappings exist: a server that maps no groups at all is not misconfigured,
    /// and a warning on every login of such an installation would be noise.
    /// </summary>
    private void WarnIfNoGroupsArrived(ProviderConfig provider, OidcIdentity identity)
    {
        if (identity.Groups.Count > 0
            || Plugin.Instance?.Configuration.GroupMappings.Count is null or 0)
        {
            return;
        }

        if (provider.GroupsFromUserInfo)
        {
            _logger.LogWarning(
                "No groups came back from the userinfo endpoint for provider {Id}, which "
                + "was asked for claim {Claim}. Check the claim name, and that the scopes "
                + "this provider needs for it are in the provider card.",
                Loggable.KnownProviderId(provider.Id), provider.GroupsClaim);
            return;
        }

        _logger.LogWarning(
            "No groups were read from ID token claim {Claim} for provider {Id}. "
            + "If this provider keeps groups in userinfo, enable "
            + "'Fetch groups from userinfo endpoint' in its provider card.",
            provider.GroupsClaim, Loggable.KnownProviderId(provider.Id));
    }

    /// <summary>
    /// The stricter rule an operator can switch on: a login that matches no group mapping
    /// is turned away rather than given an account with nothing in it. Asked before
    /// anything is created, so a refused login leaves no user, no link and no trace beyond
    /// the log.
    ///
    /// The answer to the browser names no group: the person in front of it cannot act on
    /// the detail, and the detail came from somebody else's token.
    /// </summary>
    private IActionResult? RefuseUnmatchedLogin(ProviderConfig provider, OidcIdentity identity)
    {
        var config = Plugin.Instance!.Configuration;
        var refusal = LoginGate.Refusal(
            config, identity, UserProvisioner.LinkedUserId(provider, identity));

        if (refusal is null)
        {
            return null;
        }

        _logger.LogWarning(
            "SSO login refused for provider {Id}: {Reason}. The server is set to require a "
            + "matching group mapping.",
            Loggable.KnownProviderId(provider.Id),
            refusal.Reason);

        foreach (var (mapping, arrived, reason) in refusal.NearMisses)
        {
            _logger.LogWarning(
                "SSO group mapping '{Mapping}' does not match the group '{Arrived}' the "
                + "provider sent: {Reason}, and matching is exact.",
                mapping,
                Loggable.OneLine(arrived),
                reason);
        }

        return PlainText.Forbidden(
            "This server signs in only accounts that match one of its group mappings, and "
            + "this login matched none. Ask whoever runs it to put your account in a group "
            + "the server knows.");
    }

    /// <summary>Attaches the identity to the account the user is already signed into.</summary>
    private IActionResult CompleteSelfServiceLink(
        ProviderConfig provider, OidcIdentity identity, Guid linkUserId)
    {
        var config = Plugin.Instance!.Configuration;
        var decision = SelfServiceLinks.Decide(config, provider.Id, identity.Sub, linkUserId);

        switch (decision.Outcome)
        {
            case SelfServiceLinkOutcome.AlreadyThisAccount:
                return LinkResultPage("This account is already linked.", success: true);

            case SelfServiceLinkOutcome.HeldByAnotherAccount:
                // The identity belongs to somebody else's account; linking would hand it over.
                _logger.LogWarning(
                    "Self-service link refused for user {UserId}: the identity is already "
                    + "linked to user {OwnerId} on provider {Id}.",
                    linkUserId, decision.Owner, Loggable.KnownProviderId(provider.Id));
                return LinkResultPage(
                    "That identity is already linked to another Jellyfin account. "
                    + "Ask an administrator if this is unexpected.",
                    success: false);

            case SelfServiceLinkOutcome.AccountAlreadyLinked:
                return LinkResultPage(
                    "This account is already linked for that provider.", success: true);

            default:
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
                    linkUserId, Loggable.KnownProviderId(provider.Id));

                return LinkResultPage(
                    "Linked. Your library access is applied the next time you sign in with SSO.",
                    success: true);
        }
    }

    private IActionResult LinkResultPage(string message, bool success)
        => Content(SsoPages.LinkResult(At.BasePath, message, success), "text/html");
}
