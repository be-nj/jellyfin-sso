# Tinyauth setup

[Documentation index](README.md) · [README](../README.md)

Tinyauth is an authentication server for homelabs, and since v5 it can be an OpenID
Connect provider as well as a forward-auth middleware. It works with this plugin, with
caveats worth reading before you start — the first of them decides whether it is usable
for you at all.

This guide was written against **v5.2.0**, with a login walked end to end. It is not one
of the three providers CI runs on every change; see [what is tested](#what-is-tested).

## Before you start: groups

**Tinyauth's own username-and-password accounts have no groups.** Not an empty list — no
`groups` claim at all, in the ID token or at the userinfo endpoint, even when the `groups`
scope is requested and granted. Tinyauth fills that claim only for accounts that arrived
through LDAP or through an upstream OAuth provider.

That matters here more than it would elsewhere, because groups are how this plugin decides
what a person may see. A login with no groups succeeds and produces an account that can
see nothing, which looks like a plugin fault and is not one.

Two ways out, depending on which one you already have:

- **LDAP behind Tinyauth.** Point Tinyauth at an LDAP directory (LLDAP is the usual
  choice) and, by its documentation, group membership arrives in the `groups` claim like
  any other provider's. Then set the plugin's *Groups claim* to `groups` and configure it
  as the [Authentik](authentik.md) and [Keycloak](keycloak.md) guides describe. This route
  was not walked for this page — the tests behind it use local accounts — so treat it as
  the direction to go rather than a tested recipe.
- **Map usernames instead of groups.** Set the plugin's *Groups claim* to
  `preferred_username`. Each Tinyauth username then arrives as a group of one, and a
  mapping named after a person grants that person their libraries. It works, it is tested,
  and it does not scale: one mapping per account, by hand, every time somebody joins.

If neither fits, use one of the three providers this plugin is tested against.

## Tinyauth

The OIDC server only exists once a client is configured. Create one:

```sh
docker run -i -t --rm ghcr.io/tinyauthapp/tinyauth:v5 oidc create jellyfin
```

It prints a client id and a client secret once and cannot print them again.

```yaml
services:
  tinyauth:
    image: ghcr.io/tinyauthapp/tinyauth:v5
    environment:
      # Must be https, and must look like a domain. "localhost" is refused outright:
      # "invalid app url, must be in format subdomain.domain.tld or domain.tld".
      - TINYAUTH_APPURL=https://auth.example.com
      - TINYAUTH_AUTH_USERS=user:$$2a$$10$$…        # tinyauth user create
      - TINYAUTH_AUTH_TRUSTEDPROXIES=172.17.0.0/16
      - TINYAUTH_OIDC_CLIENTS_JELLYFIN_CLIENTID=<client id>
      - TINYAUTH_OIDC_CLIENTS_JELLYFIN_CLIENTSECRETFILE=/secrets/jellyfin
      - TINYAUTH_OIDC_CLIENTS_JELLYFIN_TRUSTEDREDIRECTURIS=https://<your-jellyfin>/sso/oidc/callback/<providerId>
      - TINYAUTH_OIDC_CLIENTS_JELLYFIN_NAME=Jellyfin
```

Three things about that:

- **Tinyauth has no TLS listener of its own**, and it refuses an issuer that is not
  `https`. It only runs behind a reverse proxy, which is how it is meant to be deployed
  anyway. The proxy must set `X-Forwarded-Host` and `X-Forwarded-Proto` itself rather than
  pass a client's through — Tinyauth reads them.
- **The redirect URI has to be listed literally.** Anything not in
  `TRUSTEDREDIRECTURIS` is refused at the authorize endpoint. Unlike Authelia, Tinyauth
  does not insist that it be `https`, so a Jellyfin on plain HTTP behind your own network
  works.
- **`CLIENTSECRETFILE` reads the secret from a file**, which keeps it out of your compose
  file and out of `docker inspect`. `CLIENTSECRET` takes the literal value if you would
  rather.

Names and email addresses for local accounts come from
`TINYAUTH_AUTH_USERATTRIBUTES_<user>_NAME` and `…_EMAIL`. There is no `groups` field among
those attributes, which is the limitation above.

## The plugin's provider card

- *Issuer URL*: Tinyauth's base URL, for example `https://auth.example.com`. Discovery
  lives at `/.well-known/openid-configuration` as usual; the token and userinfo endpoints
  are under `/api/oidc/`, which the plugin reads from the discovery document.
- *Client ID*: the one `oidc create` printed.
- *Scopes*: `openid profile email groups`.
- *Groups claim*: `preferred_username`, unless Tinyauth gets its users from LDAP — then
  `groups`. See [above](#before-you-start-groups).
- *Fetch groups from userinfo endpoint*: **off**. Tinyauth puts the profile claims in the
  ID token.

The plugin sends PKCE, Tinyauth verifies it — a wrong verifier is refused with
`invalid_grant` — even though its discovery document does not advertise support. Nothing
to configure.

Two settings have no field on that page, and this is the one provider in these guides that
needs them: **Tinyauth will not accept a public client.** Its token endpoint offers only
`client_secret_basic` and `client_secret_post`, so the plugin has to hold a secret. It
stores a reference rather than the value, and both fields are set through the admin API —
[Confidential clients](configuration.md#confidential-clients) has the request. For this
provider:

```json
"ClientType": 1,
"ConfidentialSecretRef": "file:/run/secrets/tinyauth"
```

`env:NAME` works too, for an environment variable on the Jellyfin container. Either way
the value must be the secret Tinyauth has; rotate it at the provider and the file, or
logins stop at the token exchange with nobody signed in and no account created.

## The first login asks permission

Tinyauth shows a consent page — *"Continue to Jellyfin?"*, with the scopes listed and an
**Authorize** button — the first time each account authorizes the client. It remembers the
answer, so it appears once per person and not again. Worth knowing before somebody tries
it on a television, where an unexpected page with two buttons is a worse surprise than on
a laptop; [Phones, tablets and TVs](clients.md) covers that route.

## Two more traps

**Renaming a user breaks the link.** Tinyauth derives the subject from the client id and
the username, so changing either produces a different `sub` for the same person. The
plugin links accounts by subject, which is the right thing to do everywhere else; here it
means a renamed Tinyauth user comes back as a stranger, and their next login either
collides with their old Jellyfin account by name or creates a second one. Recreating the
client id has the same effect for everybody at once. If you rename somebody, re-link them
by hand — [Linking accounts](linking.md) says how.

**Logout is one-way.** Tinyauth publishes no `end_session_endpoint` and sends no
back-channel logout. So *Sign out of the identity provider too* has nothing to use: it
does not fail, it simply ends the Jellyfin session and leaves the Tinyauth one alone. And
a logout at Tinyauth does not reach Jellyfin, whose tokens do not expire on their own.
Signing somebody out of Jellyfin is a thing you do in Jellyfin — see
[Login button and logout](login-and-logout.md) and, if you need it in a hurry,
[If something goes wrong](recovery.md).

## Who may log in

Tinyauth's access controls, as its own documentation describes them, apply to the apps it
proxies — they are keyed on a proxied app's domain or container labels, not on the OIDC
clients it issues tokens for. On that reading any account Tinyauth knows can complete a
login for Jellyfin, and the only gate is the plugin's own: *Refuse logins that match no
mapping* in [Safety](configuration.md#safety). With username mappings that is also what
keeps strangers out, because an account you have not written a mapping for matches
nothing. Like the LDAP route above, that comes from Tinyauth's documentation rather than
from a login walked here, so check it against your own setup before relying on it.

Leave it off while you are setting up, or a typo in a mapping locks everybody out at once.
[If something goes wrong](recovery.md) covers getting back in.

## What is tested

The suite runs every change against Authentik, Authelia and Keycloak. Tinyauth is in
`tests/Jellyfin.Plugin.Sso.E2eTests/TinyauthTests.cs` under the `Manual` category instead,
because its known limitation is the thing a test would mostly be asserting. What those
tests walk, so this page says only what was seen:

- a login as a confidential client, through the reverse proxy, with the secret read from a
  file in the container — and the member ending up with the library their username mapping
  names;
- the same for the administrator mapping, which really does grant administrator;
- the account being linked by subject rather than by name, which is what makes the rename
  trap above a trap rather than a disaster;
- the `groups` claim coming back empty for a local account, with the login succeeding and
  the account holding nothing — the limitation this page opens with, walked;
- a wrong client secret signing nobody in and creating no account;
- a sign-out answering normally with no `end_session_endpoint` to use.

Six tests, and the consent page is clicked on the way through — the first login each
account makes cannot be walked without it.

Run them with:

```sh
make e2e E2E_FILTER='--filter "FullyQualifiedName~TinyauthTests"'
```
