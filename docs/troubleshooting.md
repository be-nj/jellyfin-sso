# Troubleshooting

[Documentation index](README.md) · [README](../README.md)

Jellyfin's log is the first place to look: every rejection the plugin makes is logged with
the provider and the reason. Dashboard → **Logs**, or `docker logs jellyfin`.

| What you see | What it means |
|---|---|
| Plugin shows **NotSupported** in the dashboard | The plugin was built for a different Jellyfin version. Install the build whose `targetAbi` matches your server — see [Requirements](requirements.md). |
| `invalid_scope` from Keycloak | Keycloak has no `groups` scope unless you create one. Empty the provider's *Scopes* field, which asks for `openid profile email`. |
| Login works, but the user sees **no libraries** | No group mapping matched. Check the group name in the mapping against what the provider sends; the names the plugin has seen are offered as autocomplete. |
| New accounts are named like `01HQ3M…` | The provider sends no `preferred_username`. The plugin falls back to the userinfo endpoint; if that is empty too, add the claim at the provider. |
| `SSO login failed: crypto.randomUUID is not a function` | A plugin build older than 1.12.1 on a server reached over plain HTTP. Update the plugin; current builds work over HTTP, which is a privacy question, not a functional one. |
| `redirect_uri` mismatch at the provider | The provider's registered URI must match exactly, including scheme, host, port and the `providerId` at the end. |
| *"Username 'ben' already exists"* at the first login | Expected: an existing Jellyfin account is never handed to an identity by name. Link it, see [Linking accounts](linking.md#manual-linking). |
| Everyone lost administrator rights after a group rename | Turn on **Never remove administrator rights on login** while you fix the mapping. The last remaining administrator is never demoted. |
| Authentik sends you back with an error right after signing in | The provider has no **Signing Key** selected. Authentik then issues a token the plugin cannot verify. |
| The plugin disappeared or shows **NotSupported** after a Jellyfin update | The build you have was made for the previous server version. Install the matching build from the catalog; log in as your local administrator meanwhile. |
| Nothing happens on the login page | The login button is injected into Jellyfin's branding. Check **Dashboard → General → Branding → Login Disclaimer**, or open `/sso/oidc/start` directly. |
| The catalog is empty after adding the repository | Your Jellyfin is older than any build in the repository. [Requirements](requirements.md) has the table. |

Locked out, lost libraries or want the plugin gone again:
[If something goes wrong](recovery.md).
