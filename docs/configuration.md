# Configuration reference

Back to the [documentation index](README.md) · [README](../README.md)

Dashboard → **Plugins → SSO Authentication**.

## Providers

One card per identity provider, with: *Display name*, *Enabled*, *Issuer URL* (must answer
at `<issuer>/.well-known/openid-configuration`), *Client ID*, *Groups claim name*, and
*Fetch groups from userinfo endpoint*. The card also prints the provider id, the redirect
URI and the login URL. With several enabled providers, `/sso/oidc/start` asks which one to
use; with one it goes straight there.

![The provider card in the plugin's configuration page](img/provider-card.png)

*Scopes* is usually left empty, which asks for `openid profile email`.
[Authelia](authelia.md) needs `groups` added; [Keycloak](keycloak.md) rejects it.

A new provider is created **disabled**, so nobody sees a login button that goes nowhere
yet, and a provider with nothing but a display name saves on purpose — that is how you get
an id to register at the provider before the rest exists.

### Confidential clients

A confidential client's secret is the one setting with no field, so that it never lands in
the plugin's configuration file. The plugin stores a *reference* and reads the value at
login time — `env:JELLYFIN_SSO_SECRET` for an environment variable you set on the Jellyfin
container, or `file:/run/secrets/sso` for a file. Setting it means one API call with an
administrator's token; `ConfidentialSecretRef` is the field on the provider, and
`ClientType` has to be `1`.

The token is an API key from Dashboard → **API Keys → +**:

```bash
curl -X POST "https://<your-jellyfin>/sso/admin/config" \
  -H 'Authorization: MediaBrowser Token="<admin token>"' \
  -H 'Content-Type: application/json' \
  -d @config.json
# config.json is what GET /sso/admin/config returns, with two fields changed inside the
# provider's entry in the "Providers" array: "ClientType": 1 and
# "ConfidentialSecretRef": "env:JELLYFIN_SSO_SECRET".
```

Most setups never need this: a public client with PKCE is the recommended shape and the
default.

## Sessions

- **Sign out of the identity provider too** — see [Login and logout](login-and-logout.md).
- **Let users link their own account** — see [Linking accounts](linking.md).

## Group Mappings

One card per group:

- Group name (autocompletes from groups seen at login).
- Libraries the group may access (checkboxes).
- **Admin (all libraries)** — grants Jellyfin admin and full library access.
- **Live TV access**, **Live TV management**, **Downloads**.
- **Max parental rating** — empty means no limit.

![A group mapping with its libraries and permissions](img/group-mapping.png)

Type the group name exactly as the provider spells it. The field autocompletes, but only
from groups the plugin has already seen at a login, so the first one is typed blind. After
a test login, Jellyfin's log shows what actually arrived:
`SSO sync for user …: admin=False, libraries=1, groups=media`.

> Mappings are applied **declaratively on every login**. Changing a user's library access
> in Jellyfin's own user UI will be overwritten on their next login.

A user in several groups gets the union: every library of every matched group, each
permission if any group grants it, and the **most permissive** parental rating — a group
without a limit lifts the limit.

## Safety

On the same tab as the group mappings:

- **Never remove administrator rights on login** — keeps existing administrators even when
  no admin group matches. Library access is still taken from the groups.
- **Excluded from sync** — a checkbox for every Jellyfin user. Tick the ones whose
  permissions you set by hand, starting with your local administrator. They can still log
  in via SSO; their policy is never touched.

> **Lockout guard:** the last remaining administrator is never demoted, whatever the
> groups say. A mistyped admin group therefore costs you a warning in the log, not access
> to your own server.

## Account Links

Its own tab: the manual links between an identity and a Jellyfin account, plus the form to
add one. See [Linking accounts](linking.md).
