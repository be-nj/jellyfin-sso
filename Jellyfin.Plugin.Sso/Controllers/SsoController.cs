using IdentityModel.Client;
using Jellyfin.Plugin.Sso.Auth;
using Jellyfin.Plugin.Sso.Configuration;
using Jellyfin.Plugin.Sso.Oidc;
using Jellyfin.Plugin.Sso.Provisioning;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.AspNetCore.Authorization;
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
    private readonly IQuickConnect _quickConnect;
    private readonly ILogger<SsoController> _logger;

    public SsoController(
        PendingStateStore states,
        OidcClientFactory oidc,
        ClaimsExtractor claims,
        UserProvisioner provisioner,
        PermissionSync permissions,
        EphemeralSecretStore secrets,
        IQuickConnect quickConnect,
        ILogger<SsoController> logger)
    {
        _states = states;
        _oidc = oidc;
        _claims = claims;
        _provisioner = provisioner;
        _permissions = permissions;
        _secrets = secrets;
        _quickConnect = quickConnect;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Web login path: browser navigates here, gets redirected to IdP
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stable entry point — auto-picks the single enabled provider.
    /// Used by the JavaScript Injector button so the URL never changes.
    /// </summary>
    [HttpGet("oidc/start")]
    [AllowAnonymous]
    public async Task<IActionResult> StartDefault(CancellationToken ct)
    {
        var provider = Plugin.Instance?.Configuration.Providers.FirstOrDefault(p => p.Enabled);
        if (provider is null)
            return NotFound("No enabled SSO provider configured.");
        return await Start(provider.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Same but for Quick Connect — stable entry point for native apps.</summary>
    [HttpGet("oidc/start/qc")]
    [AllowAnonymous]
    public async Task<IActionResult> StartDefaultQuickConnect(
        [FromQuery] string code, CancellationToken ct)
    {
        var provider = Plugin.Instance?.Configuration.Providers.FirstOrDefault(p => p.Enabled);
        if (provider is null)
            return NotFound("No enabled SSO provider configured.");
        return await StartWithQuickConnect(provider.Id, code, ct).ConfigureAwait(false);
    }

    [HttpGet("oidc/start/{providerId}")]
    [AllowAnonymous]
    public async Task<IActionResult> Start(string providerId, CancellationToken ct)
    {
        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFound($"Provider '{providerId}' not found or not enabled.");

        var redirectUri = BuildCallbackUri(providerId);
        var (state, pending) = _states.Issue(provider.Id, redirectUri);

        var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        var authorizeUrl = new RequestUrl(disco.AuthorizeEndpoint
            ?? throw new InvalidOperationException("Discovery document has no authorize endpoint."))
            .CreateAuthorizeUrl(
                clientId: provider.ClientId,
                responseType: "code",
                scope: string.Join(' ', provider.Scopes),
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
            return BadRequest("Quick Connect is disabled on this server.");

        var provider = GetEnabledProvider(providerId);
        if (provider is null)
            return NotFound($"Provider '{providerId}' not found or not enabled.");

        var redirectUri = BuildCallbackUri(providerId);
        var (state, pending) = _states.Issue(provider.Id, redirectUri, quickConnectCode: code);

        var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

        var authorizeUrl = new RequestUrl(disco.AuthorizeEndpoint
            ?? throw new InvalidOperationException("Discovery document has no authorize endpoint."))
            .CreateAuthorizeUrl(
                clientId: provider.ClientId,
                responseType: "code",
                scope: string.Join(' ', provider.Scopes),
                redirectUri: redirectUri,
                state: state,
                nonce: pending.Nonce,
                codeChallenge: PendingStateStore.CodeChallenge(pending.CodeVerifier),
                codeChallengeMethod: "S256");

        return Redirect(authorizeUrl);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Callback: IdP returns here with code+state
    // ──────────────────────────────────────────────────────────────────────────

    [HttpGet("oidc/callback/{providerId}")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        string providerId,
        [FromQuery] string code,
        [FromQuery] string state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("OIDC callback error for provider {Id}: {Error}", providerId, error);
            return BadRequest($"IdP returned error: {error}");
        }

        var pending = _states.Consume(state);
        if (pending is null)
            return BadRequest("Invalid or expired state. Please try logging in again.");

        var provider = GetEnabledProvider(providerId);
        if (provider is null || provider.Id != pending.ProviderId)
            return BadRequest("Provider mismatch.");

        try
        {
            var disco = await _oidc.GetDiscoveryDocumentAsync(provider, ct).ConfigureAwait(false);

            var tokens = await _oidc.ExchangeCodeAsync(
                provider, disco, code, pending.RedirectUri, pending.CodeVerifier, ct)
                .ConfigureAwait(false);

            var identity = await _claims.ExtractAsync(
                provider, disco,
                tokens.IdentityToken!, tokens.AccessToken!,
                pending.Nonce, ct)
                .ConfigureAwait(false);

            var (userId, username) = await _provisioner.ResolveAsync(provider, identity, ct)
                .ConfigureAwait(false);

            await _permissions.SyncAsync(userId, identity, ct).ConfigureAwait(false);

            if (pending.QuickConnectCode is not null)
                return await CompleteQuickConnectAsync(userId, username, pending.QuickConnectCode, ct)
                    .ConfigureAwait(false);

            return await CompleteWebLoginAsync(username, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "SSO login failed for provider {Id}", providerId);
            return BadRequest(ex.Message);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Manual account linking (admin-only)
    // ──────────────────────────────────────────────────────────────────────────

    [HttpPost("admin/link")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult LinkAccount(
        [FromBody] LinkRequest req)
    {
        var config = Plugin.Instance!.Configuration;

        if (config.Links.Any(l =>
                l.ProviderId == req.ProviderId &&
                string.Equals(l.Sub, req.Sub, StringComparison.Ordinal)))
        {
            return Conflict("A link for this provider+sub already exists.");
        }

        config.Links.Add(new LinkConfig
        {
            ProviderId = req.ProviderId,
            Sub = req.Sub,
            JellyfinUserId = req.JellyfinUserId,
        });
        Plugin.Instance.SaveConfiguration();

        return Ok();
    }

    [HttpDelete("admin/link/{jellyfinUserId}")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult UnlinkAccount(Guid jellyfinUserId)
    {
        var config = Plugin.Instance!.Configuration;
        var removed = config.Links.RemoveAll(l => l.JellyfinUserId == jellyfinUserId);
        if (removed == 0)
            return NotFound("No link found for this user.");
        Plugin.Instance.SaveConfiguration();
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
                const deviceId = localStorage.getItem('_deviceId2') || crypto.randomUUID();
                localStorage.setItem('_deviceId2', deviceId);

                const authHeader = 'MediaBrowser Client="SSO Plugin", Device="Browser", DeviceId="'
                  + deviceId + '", Version="1.0.0.0"';

                const res = await fetch('/Users/AuthenticateByName', {
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
                window.location.href = '/web/index.html';
              } catch(e) {
                msg.textContent = 'SSO login failed: ' + e.message;
              }
            })();
            </script>
            </body></html>
            """;

        return Task.FromResult<IActionResult>(Content(html, "text/html"));
    }

    private async Task<IActionResult> CompleteQuickConnectAsync(
        Guid userId, string username, string quickConnectCode, CancellationToken ct)
    {
        var authorized = await _quickConnect.AuthorizeRequest(userId, quickConnectCode)
            .ConfigureAwait(false);

        if (!authorized)
            return BadRequest("Quick Connect authorization failed. The code may have expired.");

        var html = """
            <!DOCTYPE html><html><body>
            <script>
            document.body.innerText = 'Login approved! You can close this tab and return to your app.';
            </script>
            <p>Login approved. You can close this tab and return to your app.</p>
            </body></html>
            """;

        return Content(html, "text/html");
    }

    private static ProviderConfig? GetEnabledProvider(string providerId)
        => Plugin.Instance?.Configuration.Providers
            .FirstOrDefault(p => p.Enabled &&
                string.Equals(p.Id, providerId, StringComparison.Ordinal));

    private string BuildCallbackUri(string providerId)
    {
        var req = HttpContext.Request;

        // Honour reverse-proxy forwarded headers (Jellyfin runs behind HTTPS termination).
        var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault()
            ?? req.Headers["X-Forwarded-Scheme"].FirstOrDefault()
            ?? req.Scheme;

        var host = req.Headers["X-Forwarded-Host"].FirstOrDefault()
            ?? req.Host.ToString();

        return $"{scheme}://{host}/sso/oidc/callback/{providerId}";
    }

    private static string JsonEscape(string s)
        => System.Text.Json.JsonSerializer.Serialize(s);
}

public sealed class LinkRequest
{
    public required string ProviderId { get; set; }
    public required string Sub { get; set; }
    public required Guid JellyfinUserId { get; set; }
}
