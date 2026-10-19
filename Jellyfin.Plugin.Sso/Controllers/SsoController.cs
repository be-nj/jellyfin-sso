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
    /// Stable entry point. With one enabled provider it goes straight there; with several
    /// it asks which one. The URL never changes, so the login button can be static.
    /// </summary>
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

        var basePath = BasePath();
        var buttons = providers.Select(p => new
        {
            name = string.IsNullOrWhiteSpace(p.Name) ? "SSO" : p.Name,
            href = $"{basePath}/sso/oidc/start/{Uri.EscapeDataString(p.Id)}",
            icon = $"{basePath}/sso/icon/{ProviderIcons.SlugFor(p.Name, p.Authority)}",
        });

        var json = System.Text.Json.JsonSerializer.Serialize(buttons);

        // Written as plain ES5 so it runs in whatever a television calls a browser.
        var script = $$"""
            (function () {
              var buttons = {{json}};

              function form() {
                return document.querySelector('.loginForm')
                  || document.querySelector('form.login')
                  || document.querySelector('#loginPage form')
                  || document.querySelector('form');
              }

              function onLoginPage() {
                var hash = (location.hash || '').toLowerCase();
                return hash.indexOf('login') !== -1
                  || document.querySelector('input[type="password"]') !== null;
              }

              function place() {
                if (!onLoginPage() || document.querySelector('.sso-form-button')) return;
                var target = form();
                if (!target) return;

                buttons.forEach(function (button) {
                  var link = document.createElement('a');
                  link.className = 'raised block emby-button sso-form-button';
                  link.setAttribute('data-role', 'button');
                  link.href = button.href;
                  link.style.display = 'flex';
                  link.style.alignItems = 'center';
                  link.style.justifyContent = 'center';
                  link.style.gap = '.6em';

                  var mark = document.createElement('img');
                  mark.src = button.icon;
                  mark.alt = '';
                  mark.width = 20;
                  mark.height = 20;

                  var label = document.createElement('span');
                  label.textContent = 'Login with ' + button.name;

                  link.appendChild(mark);
                  link.appendChild(label);
                  target.appendChild(link);
                });
              }

              // jellyfin-web is a single page: the form arrives, and comes back, long
              // after this script runs.
              document.addEventListener('DOMContentLoaded', place);
              window.addEventListener('hashchange', function () { setTimeout(place, 200); });
              new MutationObserver(function () { place(); })
                .observe(document.documentElement, { childList: true, subtree: true });
              place();
            })();
            """;

        return Content(script, "application/javascript");
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

    [HttpGet("oidc/start")]
    [AllowAnonymous]
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
    public async Task<IActionResult> Start(string providerId, CancellationToken ct)
    {
        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFoundText("No such provider, or it is not enabled.");

        var redirectUri = BuildCallbackUri(providerId);
        var (state, pending) = _states.Issue(provider.Id, redirectUri);

        var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        var authorizeUrl = new RequestUrl(disco.AuthorizeEndpoint
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

        return Redirect(authorizeUrl);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Quick Connect path: native app initiates QC, browser completes OIDC here
    // ──────────────────────────────────────────────────────────────────────────

    [HttpGet("oidc/start/{providerId}/qc")]
    [AllowAnonymous]
    public async Task<IActionResult> StartWithQuickConnect(
        string providerId, [FromQuery] string code, CancellationToken ct)
    {
        if (!_quickConnect.IsEnabled)
            return BadRequestText("Quick Connect is disabled on this server.");

        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFoundText("No such provider, or it is not enabled.");

        var redirectUri = BuildCallbackUri(providerId);
        var (state, pending) = _states.Issue(provider.Id, redirectUri, quickConnectCode: code);

        var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        var authorizeUrl = new RequestUrl(disco.AuthorizeEndpoint
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

        return Redirect(authorizeUrl);
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

    // ──────────────────────────────────────────────────────────────────────────
    // Callback: IdP returns here with code+state
    // ──────────────────────────────────────────────────────────────────────────

    [HttpGet("oidc/callback/{providerId}")]
    [AllowAnonymous]
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

        var options = string.Join("\n", providers.Select(p =>
            $"<option value={JsonEscape(p.Id)}>{Html(p.Name)}</option>"));

        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/><title>Link SSO account</title>
            <style>
              body{font-family:system-ui,sans-serif;background:#1c1c1c;color:#ddd;display:flex;
                   align-items:center;justify-content:center;height:100vh;margin:0}
              .card{max-width:30em;padding:2em;line-height:1.5}
              select,button{font:inherit;padding:.5em;margin-top:.75em}
              #msg{color:#f88}
            </style>
            </head><body><div class="card">
            <h1>Link your SSO account</h1>
            <p>You are signed in to Jellyfin. Linking connects this account to your identity
               provider so you can sign in with it from now on.</p>
            <select id="provider">{{options}}</select>
            <button id="go" type="button">Continue to provider</button>
            <p id="msg"></p>
            <script>
            (function() {
              const msg = document.getElementById('msg');

              function token() {
                try {
                  const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
                  const server = (creds.Servers || [])[0];
                  return server && server.AccessToken;
                } catch (e) { return null; }
              }

              document.getElementById('go').addEventListener('click', async function() {
                const accessToken = token();
                if (!accessToken) {
                  msg.textContent = 'Please sign in to Jellyfin in this browser first.';
                  return;
                }
                msg.textContent = '';
                try {
                  const providerId = document.getElementById('provider').value;
                  const res = await fetch('{{BasePath()}}/sso/link/start?providerId='
                    + encodeURIComponent(providerId), {
                    method: 'POST',
                    headers: { 'Authorization': 'MediaBrowser Token="' + accessToken + '"' },
                  });
                  if (!res.ok) throw new Error(await res.text() || ('HTTP ' + res.status));
                  const data = await res.json();
                  window.location.href = data.AuthorizeUrl;
                } catch (e) {
                  msg.textContent = 'Could not start linking: ' + e.message;
                }
              });
            })();
            </script>
            </div></body></html>
            """;

        return Content(html, "text/html");
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

        var redirectUri = BuildCallbackUri(provider.Id);
        var (state, pending) = _states.Issue(provider.Id, redirectUri, linkUserId: userId);
        var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        var authorizeUrl = new RequestUrl(disco.AuthorizeEndpoint
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

    private Task<IActionResult> CompleteWebLoginAsync(string username, CancellationToken ct)
    {
        var secret = _secrets.Issue(username);

        // The callback page runs standalone (outside Jellyfin's SPA) so window.ApiClient
        // is unavailable. We authenticate via raw fetch, write the result into Jellyfin's
        // localStorage credential format, then redirect into the SPA.
        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/><title>SSO Login</title>
            <style>body{font-family:system-ui,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#1c1c1c;color:#ddd}</style>
            </head><body>
            <p id="msg">Completing login&hellip;</p>
            <script>
            (async function() {
              const msg = document.getElementById('msg');
              try {
                // crypto.randomUUID only exists in a secure context, so a server reached
                // over plain HTTP on a LAN address would fail here. Fall back to
                // getRandomValues, which has no such restriction.
                function uuid() {
                  if (window.crypto && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
                  const bytes = new Uint8Array(16);
                  if (window.crypto && typeof crypto.getRandomValues === 'function') {
                    crypto.getRandomValues(bytes);
                  } else {
                    for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
                  }
                  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
                  bytes[8] = (bytes[8] & 0x3f) | 0x80; // variant 1
                  let hex = '';
                  for (const b of bytes) hex += b.toString(16).padStart(2, '0');
                  return hex.slice(0, 8) + '-' + hex.slice(8, 12) + '-' + hex.slice(12, 16)
                    + '-' + hex.slice(16, 20) + '-' + hex.slice(20);
                }

                const deviceId = localStorage.getItem('_deviceId2') || uuid();
                localStorage.setItem('_deviceId2', deviceId);

                // Authenticate as the web client with its device id. Jellyfin keys sessions
                // by client + device id, so the token belongs to the same session the web
                // app later plays on, instead of a second session that shares the device id.
                const info = await fetch('{{BasePath()}}/System/Info/Public').then(function(r){ return r.json(); });
                // Rough match of the names jellyfin-web reports, so the device list stays readable.
                const ua = navigator.userAgent;
                const device = /Edg\//.test(ua) ? 'Edge Chromium'
                  : /Firefox\//.test(ua) ? 'Firefox'
                  : /Chrome\//.test(ua) ? 'Chrome'
                  : /Safari\//.test(ua) ? 'Safari'
                  : 'Web Browser';
                const authHeader = 'MediaBrowser Client="Jellyfin Web", Device="' + device + '", DeviceId="'
                  + deviceId + '", Version="' + info.Version + '"';

                const res = await fetch('{{BasePath()}}/Users/AuthenticateByName', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json', 'Authorization': authHeader },
                  body: JSON.stringify({ Username: {{JsonEscape(username)}}, Pw: {{JsonEscape(secret)}} })
                });

                if (!res.ok) throw new Error('Auth failed: HTTP ' + res.status);
                const data = await res.json();

                // Write credentials into localStorage in Jellyfin's expected format.
                const credsKey = 'jellyfin_credentials';
                let creds = { Servers: [] };
                try { creds = JSON.parse(localStorage.getItem(credsKey) || '{"Servers":[]}'); } catch(e) {}

                const serverEntry = {
                  Id: data.ServerId,
                  AccessToken: data.AccessToken,
                  UserId: data.User.Id,
                  UserName: data.User.Name,
                  // jellyfin-web resolves the API address from the entry itself; without
                  // an address for the chosen mode it throws "Must supply a serverAddress".
                  ManualAddress: window.location.origin,
                  LastConnectionMode: 2,
                  DateLastAccessed: Date.now(),
                };

                const idx = creds.Servers.findIndex(function(s){ return s.Id === data.ServerId; });
                if (idx >= 0) { creds.Servers[idx] = Object.assign(creds.Servers[idx], serverEntry); }
                else { creds.Servers.push(serverEntry); }

                localStorage.setItem(credsKey, JSON.stringify(creds));

                msg.textContent = 'Login successful — redirecting…';
                window.location.href = '{{BasePath()}}/web/index.html';
              } catch(e) {
                msg.textContent = 'SSO login failed: ' + e.message;
              }
            })();
            </script>
            </body></html>
            """;

        return Task.FromResult<IActionResult>(Content(html, "text/html"));
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

        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/><title>Approve this device</title>
            <style>
              body{font-family:system-ui,sans-serif;background:#1c1c1c;color:#ddd;display:flex;
                   align-items:center;justify-content:center;height:100vh;margin:0}
              .card{max-width:28em;padding:2em;line-height:1.5;text-align:center}
              .code{font-size:2em;letter-spacing:.2em;margin:.5em 0;font-family:ui-monospace,monospace}
              button{font:inherit;padding:.6em 1.4em;margin-top:1em;cursor:pointer}
              a{color:#9bf}
            </style>
            </head><body><div class="card">
            <h1>Approve this device?</h1>
            <p>Signed in as <strong>{{Html(username)}}</strong>. A device is waiting with this code:</p>
            <p class="code">{{Html(quickConnectCode)}}</p>
            <p>Approve only if this is the code your own device is showing.</p>
            <form method="post" action="{{BasePath()}}/sso/oidc/qc/approve">
              <input type="hidden" name="token" value="{{Html(token)}}"/>
              <button type="submit">Approve</button>
            </form>
            <p><a href="{{BasePath()}}/web/index.html">Cancel</a></p>
            </div></body></html>
            """;

        return Content(html, "text/html");
    }

    /// <summary>Approves the code the page just showed, once.</summary>
    [HttpPost("oidc/qc/approve")]
    [AllowAnonymous]
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

        var html = """
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/><title>Login approved</title>
            <style>
              body{font-family:system-ui,sans-serif;background:#1c1c1c;color:#ddd;display:flex;
                   align-items:center;justify-content:center;height:100vh;margin:0}
              .card{max-width:24em;padding:2em;text-align:center;line-height:1.5}
            </style>
            </head><body><div class="card">
            <h1>Login approved</h1>
            <p>You can close this tab and go back to the app.</p>
            </div></body></html>
            """;

        return Content(html, "text/html");
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
    {
        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/><title>SSO linking</title>
            <style>
              body{font-family:system-ui,sans-serif;background:#1c1c1c;color:#ddd;display:flex;
                   align-items:center;justify-content:center;height:100vh;margin:0}
              .card{max-width:30em;padding:2em;line-height:1.5;text-align:center}
              a{color:#9bf}
            </style>
            </head><body><div class="card">
            <h1>{{(success ? "Account linked" : "Linking failed")}}</h1>
            <p>{{Html(message)}}</p>
            <p><a href="{{BasePath()}}/web/index.html">Back to Jellyfin</a></p>
            </div></body></html>
            """;

        return Content(html, "text/html");
    }

    /// <summary>Lets the user choose when more than one provider is enabled.</summary>
    private IActionResult ProviderPicker(IReadOnlyList<ProviderConfig> providers, string queryString)
    {
        var buttons = string.Join("\n", providers.Select(p =>
            $"<a class=\"choice\" href=\"{BasePath()}/sso/oidc/start/{Uri.EscapeDataString(p.Id)}{queryString}\">"
            + $"{Html(p.Name)}</a>"));

        var html = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/><title>Choose a login provider</title>
            <style>
              body{font-family:system-ui,sans-serif;background:#1c1c1c;color:#ddd;display:flex;
                   align-items:center;justify-content:center;height:100vh;margin:0}
              .card{max-width:24em;padding:2em;text-align:center}
              .choice{display:block;margin:.5em 0;padding:.75em 1em;background:#2c2c2c;color:#ddd;
                      border-radius:.25em;text-decoration:none}
              .choice:hover{background:#3a3a3a}
            </style>
            </head><body><div class="card">
            <h1>Sign in with</h1>
            {{buttons}}
            </div></body></html>
            """;

        return Content(html, "text/html");
    }

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

    /// <summary>Minimal HTML escaping for the few values these pages print.</summary>
    private static string Html(string? value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

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

    private static string JsonEscape(string s)
        => System.Text.Json.JsonSerializer.Serialize(s);
}
