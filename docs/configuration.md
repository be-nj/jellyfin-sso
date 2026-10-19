# Configuration reference

[Documentation index](README.md) · [README](../README.md)

Dashboard → **Plugins → SSO Authentication**.

## Providers

One card per identity provider: *Display name*, *Enabled*, *Issuer URL* (must answer at
`<issuer>/.well-known/openid-configuration`), *Client ID*, *Groups claim name*, *Fetch
groups from userinfo endpoint*, *Scopes*. The card prints the provider id, the redirect
URI and the login URL.

![The provider card in the plugin's configuration page](img/provider-card.png)

Leave *Scopes* empty to request `openid profile email`. [Authelia](authelia.md) needs
`groups` added; [Keycloak](keycloak.md) rejects it.

A new provider is created **disabled**, and one with nothing but a display name saves — so
you can hand its redirect URI to the provider before the rest exists.

With several providers enabled, `/sso/oidc/start` asks which one; with one it goes
straight there.

### Confidential clients

The client secret has no field on the page, so it never reaches the plugin's configuration
file. The plugin stores a reference and reads the value at login: `env:JELLYFIN_SSO_SECRET`
for an environment variable on the Jellyfin container, `file:/run/secrets/sso` for a file.
Set it through the admin API with an API key from Dashboard → **API Keys → +**:

```bash
curl -X POST "https://<your-jellyfin>/sso/admin/config" \
  -H 'Authorization: MediaBrowser Token="<admin token>"' \
  -H 'Content-Type: application/json' \
  -d @config.json
# config.json is what GET /sso/admin/config returns, with two fields changed in the
# provider's entry: "ClientType": 1 and "ConfidentialSecretRef": "env:JELLYFIN_SSO_SECRET".
```

A public client with PKCE is the default and covers most setups.

## Sessions

- **Sign out of the identity provider too** — [Login and logout](login-and-logout.md).
- **Let users link their own account** — [Linking accounts](linking.md).

## Group Mappings

One card per group: *Group name*, *Libraries*, **Admin (all libraries)**, *Live TV
access*, *Live TV management*, *Downloads*, *Max parental rating* (empty = no limit).

![A group mapping with its libraries and permissions](img/group-mapping.png)

The group name must match the provider's spelling. The field autocompletes from groups
seen at a login, so the first one is typed blind; after a test login the log shows what
arrived:

```
SSO sync for user …: admin=False, libraries=1, groups=media
```

> Mappings are applied on **every login**, over anything set by hand in Jellyfin's user
> screen.

A user in several groups gets the union: every library of every matched group, each
permission any group grants, and the most permissive parental rating — a group without a
limit lifts the limit.

## Safety

Same tab as the group mappings:

- **Never remove administrator rights on login** — administrators keep the flag even when
  no admin group matches. Library access still comes from the groups.
- **Excluded from sync** — one checkbox per Jellyfin user. They can log in via SSO, but
  their permissions are never touched. Tick your local administrator.

> **Lockout guard:** the last remaining administrator is never demoted, whatever the
> groups say. A mistyped admin group costs a warning in the log, not your server.

## Account Links

Its own tab: the links between an identity and a Jellyfin account, and the form to add
one. See [Linking accounts](linking.md).
