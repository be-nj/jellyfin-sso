# Troubleshooting

[Documentation index](README.md) · [README](../README.md)

Jellyfin's log is the first place to look: every rejection the plugin makes is logged with
the provider and the reason. Dashboard → **Logs**, or `docker logs jellyfin`.

| What you see | What it means |
|---|---|
| Plugin shows **NotSupported** in the dashboard | The plugin was built for a different Jellyfin version. Install the build whose `targetAbi` matches your server — see [Requirements](requirements.md). |
| `invalid_scope` from Keycloak | Keycloak has no `groups` scope unless you create one. Empty the provider's *Scopes* field, which asks for `openid profile email`. |
| Login works, but the user sees **no libraries** | No group mapping matched. Check the group name in the mapping against what the provider sends; the names the plugin has seen are offered as autocomplete. |
| New accounts are named like `01HQ3M…` | The provider sends no `preferred_username`. The plugin tries `name`, then the userinfo endpoint, and falls back to the subject. Add one of the two claims at the provider. |
| `SSO login failed: crypto.randomUUID is not a function` | A plugin build older than 1.12.1 on a server reached over plain HTTP. Update the plugin; current builds work over HTTP, which is a privacy question, not a functional one. |
| `redirect_uri` mismatch at the provider | The provider's registered URI must match exactly, including scheme, host, port and the `providerId` at the end. |
| *"Username 'ben' already exists"* at the first login | Expected: an existing Jellyfin account is never handed to an identity by name. Link it, see [Linking accounts](linking.md#manual-linking). |
| Everyone lost administrator rights after a group rename | Turn on **Never remove administrator rights on login** while you fix the mapping. The last remaining administrator is never demoted. |
| *"This provider could not be reached or is not configured correctly"* | Press **Check saved settings** on the provider card: it shows what the server found, which is otherwise only in the log. |
| …and the check, or the log, says the discovery document **puts an endpoint on another host** | The provider serves its keys or its token endpoint from a host other than the issuer's — Google does. If that is the provider's documented layout, put the hosts that message ends with into **Further endpoint hosts** on the provider card; see [Configuration](configuration.md). If it is not, the document is wrong and the refusal is the point. |
| *"This server has no account for this sign-in and does not create one at this provider"* | **Create an account at first login** is off on the provider card, which is how a newly added provider starts. Turn it on if your provider decides who may use Jellyfin; otherwise link the person: the attempt is now listed on the **Account Links** tab, see [Linking accounts](linking.md#manual-linking). Somebody with a Jellyfin password can link themselves at the address the message names. |
| Authentik sends you back with an error right after signing in | The provider has no **Signing Key** selected. Authentik then issues a token the plugin cannot verify. |
| The plugin disappeared or shows **NotSupported** after a Jellyfin update | The build you have was made for the previous server version. Install the matching build from the catalog; log in as your local administrator meanwhile. |
| Back on Jellyfin's login page right after an SSO login, having reloaded at once | Jellyfin's web client checks its stored token each time it starts, and forgets the token if that one request fails — a reload in the first moment after it opens cancels it, and so does a dropped connection. That is the web client's behaviour on every start, with or without SSO; sign in again. The log shows no refusal, because the server never answered. |
| Opened at the LAN address, the SSO login ends at the public address | *Public address* is set. The provider sends the browser back to that address and to no other, so a login opened anywhere else is taken there before it starts. The browser is then signed in at the public address; the LAN address still takes a password. See [Public address](configuration.md#public-address). |
| `The public address in the SSO settings is not an address…` when starting a login | The stored *Public address* cannot be read as one — written by hand into the configuration file, or through Jellyfin's own plugin-configuration endpoint, which checks nothing. Sign in with a password and correct it or clear it on the provider tab. It is not ignored on purpose: it was set so that request headers would not decide where a login's code goes. |
| Nothing happens on the login page | The login button is injected into Jellyfin's branding. Check **Dashboard → General → Branding → Login Disclaimer**, or open `/sso/oidc/start` directly. |
| The catalog is empty after adding the repository | Your Jellyfin is older than any build in the repository. [Requirements](requirements.md) has the table. |

Locked out, lost libraries or want the plugin gone again:
[If something goes wrong](recovery.md).
