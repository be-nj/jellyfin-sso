using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.Sso.Configuration;

namespace Jellyfin.Plugin.Sso.Web;

/// <summary>
/// The standalone pages the login flow serves: the provider picker, the page that finishes
/// a web login, the Quick Connect prompt, the self-service linking page and its result.
/// They run outside jellyfin-web's single page application, so each one carries its own
/// markup, style and script.
///
/// They live here rather than in the controller because they are the bulk of it and none of
/// them needs anything from a request beyond the handful of values passed in — which also
/// means each one can be asserted on without a server.
/// </summary>
internal static class SsoPages
{
    /// <summary>
    /// The script that places a login button per provider inside Jellyfin's own login form.
    /// Written as plain ES5 so it runs in whatever a television calls a browser.
    /// </summary>
    internal static string LoginButtonScript(string basePath, IReadOnlyList<ProviderConfig> providers)
    {
        var buttons = providers.Select(p => new
        {
            name = string.IsNullOrWhiteSpace(p.Name) ? "SSO" : p.Name,
            href = $"{basePath}/sso/oidc/start/{Uri.EscapeDataString(p.Id)}",
            icon = $"{basePath}/sso/icon/{ProviderIcons.SlugFor(p.Name, p.Authority)}",
        });

        var json = JsonSerializer.Serialize(buttons);

        return $$"""
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
    }

    /// <summary>Lets the user choose when more than one provider is enabled.</summary>
    internal static string ProviderPicker(
        string basePath, IReadOnlyList<ProviderConfig> providers, string queryString)
    {
        var buttons = string.Join("\n", providers.Select(p =>
            $"<a class=\"choice\" href=\"{basePath}/sso/oidc/start/{Uri.EscapeDataString(p.Id)}{queryString}\">"
            + $"{Html(p.Name)}</a>"));

        return Shell(
            "Choose a login provider",
            """
              .card{max-width:24em;text-align:center}
              .choice{display:block;margin:.5em 0;padding:.75em 1em;background:#2c2c2c;color:#ddd;
                      border-radius:.25em;text-decoration:none}
              .choice:hover{background:#3a3a3a}
            """,
            $"""
            <h1>Sign in with</h1>
            {buttons}
            """);
    }

    /// <summary>
    /// Finishes a web login in the browser. The page runs outside Jellyfin's single page
    /// application, so window.ApiClient is unavailable: it authenticates with a raw fetch,
    /// writes the result into Jellyfin's localStorage credential format, then redirects
    /// into the application.
    /// </summary>
    internal static string CompleteWebLogin(string basePath, string username, string secret)
        => Shell(
            "SSO Login",
            string.Empty,
            $$"""
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
                const info = await fetch('{{basePath}}/System/Info/Public').then(function(r){ return r.json(); });
                // Rough match of the names jellyfin-web reports, so the device list stays readable.
                const ua = navigator.userAgent;
                const device = /Edg\//.test(ua) ? 'Edge Chromium'
                  : /Firefox\//.test(ua) ? 'Firefox'
                  : /Chrome\//.test(ua) ? 'Chrome'
                  : /Safari\//.test(ua) ? 'Safari'
                  : 'Web Browser';
                const authHeader = 'MediaBrowser Client="Jellyfin Web", Device="' + device + '", DeviceId="'
                  + deviceId + '", Version="' + info.Version + '"';

                const res = await fetch('{{basePath}}/Users/AuthenticateByName', {
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
                window.location.href = '{{basePath}}/web/index.html';
              } catch(e) {
                msg.textContent = 'SSO login failed: ' + e.message;
              }
            })();
            </script>
            """,
            card: false);

    /// <summary>
    /// Asks before approving. The login is finished at this point; what is left is the
    /// decision to let the device that printed this code in, and only the person in front
    /// of the browser can make it.
    /// </summary>
    internal static string ApproveQuickConnect(
        string basePath, string username, string quickConnectCode, string token)
        => Shell(
            "Approve this device",
            """
              .card{max-width:28em;text-align:center}
              .code{font-size:2em;letter-spacing:.2em;margin:.5em 0;font-family:ui-monospace,monospace}
              button{font:inherit;padding:.6em 1.4em;margin-top:1em;cursor:pointer}
            """,
            $"""
            <h1>Approve this device?</h1>
            <p>Signed in as <strong>{Html(username)}</strong>. A device is waiting with this code:</p>
            <p class="code">{Html(quickConnectCode)}</p>
            <p>Approve only if this is the code your own device is showing.</p>
            <form method="post" action="{basePath}/sso/oidc/qc/approve">
              <input type="hidden" name="token" value="{Html(token)}"/>
              <button type="submit">Approve</button>
            </form>
            <p><a href="{basePath}/web/index.html">Cancel</a></p>
            """);

    /// <summary>What the browser shows once the waiting device has been let in.</summary>
    internal static string QuickConnectApproved()
        => Shell(
            "Login approved",
            "  .card{max-width:24em;text-align:center}",
            """
            <h1>Login approved</h1>
            <p>You can close this tab and go back to the app.</p>
            """);

    /// <summary>
    /// The page a user opens to link their own account. It is served anonymously because
    /// the browser cannot send Jellyfin's token on a navigation; the page reads the token
    /// the web client stored and posts it to the linking endpoint itself.
    /// </summary>
    internal static string SelfServiceLink(string basePath, IReadOnlyList<ProviderConfig> providers)
    {
        var options = string.Join("\n", providers.Select(p =>
            $"<option value={JsonEscape(p.Id)}>{Html(p.Name)}</option>"));

        return Shell(
            "Link SSO account",
            """
              select,button{font:inherit;padding:.5em;margin-top:.75em}
              #msg{color:#f88}
            """,
            $$"""
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
                  const res = await fetch('{{basePath}}/sso/link/start?providerId='
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
            """);
    }

    /// <summary>What linking says when it is over, either way.</summary>
    internal static string LinkResult(string basePath, string message, bool success)
        => Shell(
            "SSO linking",
            "  .card{text-align:center}",
            $"""
            <h1>{(success ? "Account linked" : "Linking failed")}</h1>
            <p>{Html(message)}</p>
            <p><a href="{basePath}/web/index.html">Back to Jellyfin</a></p>
            """);

    /// <summary>
    /// The frame every page above shares: dark like Jellyfin's own, centred, and no wider
    /// than a paragraph wants to be. <paramref name="extraCss"/> is appended after the
    /// shared rules, so a page can narrow its card or centre its text.
    /// </summary>
    private static string Shell(string title, string extraCss, string body, bool card = true)
    {
        var content = card ? $"<div class=\"card\">\n{body}\n</div>" : body;

        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width, initial-scale=1"/>
            <title>{{Html(title)}}</title>
            <style>
              body{font-family:system-ui,sans-serif;background:#1c1c1c;color:#ddd;display:flex;
                   align-items:center;justify-content:center;min-height:100vh;margin:0}
              .card{max-width:30em;padding:2em;line-height:1.5}
              a{color:#9bf}
            {{extraCss}}
            </style>
            </head><body>
            {{content}}
            </body></html>
            """;
    }

    /// <summary>Minimal HTML escaping for the few values these pages print.</summary>
    internal static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>A value as a JavaScript literal, quotes and escaping included.</summary>
    internal static string JsonEscape(string value) => JsonSerializer.Serialize(value);
}
