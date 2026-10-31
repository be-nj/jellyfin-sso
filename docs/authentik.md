# Authentik setup

[Documentation index](README.md) · [README](../README.md)

Written against Authentik 2026.8. In Authentik's vocabulary a **Provider** speaks the
protocol and an **Application** is what users see.

1. **Directory → Groups → Create**: one group per level of access, for example `media` and
   `jellyfin-admins`.
2. **Applications → Providers → Create → OAuth2/OpenID Provider**:
   - *Authorization flow*: `default-provider-authorization-implicit-consent` (with consent
     works too, users just get an extra click).
   - *Client type*: **Public** — no secret, the plugin uses PKCE.
   - *Redirect URIs*, **Strict**: the URI the plugin's provider card printed,
     `https://<your-jellyfin>/sso/oidc/callback/<providerId>`.
   - *Signing Key*: the self-signed certificate Authentik ships with. **Without one,
     Authentik issues a token the plugin cannot verify** and the login fails.
   - Scopes stay at `openid`, `profile`, `email`; Authentik's `profile` mapping already
     carries the groups.
3. **Applications → Applications → Create**: pick that provider and give it a slug. The
   slug decides the issuer URL, trailing slash included:

   ```
   https://<your-authentik>/application/o/<slug>/
   ```

4. **Who may log in** is decided here: bind a policy to the application unless every
   Authentik user should get a Jellyfin account. The plugin can hold the line from its
   side too — *Refuse logins that match no mapping* in
   [Safety](configuration.md#safety) — as a second lock rather than a replacement.

In the plugin: *Groups claim name* `groups`, *Scopes* empty, *Fetch groups from userinfo
endpoint* off.

**Leave *Subject mode* at its default.** The opaque id survives renames and cannot be
inherited by a recycled username; linking existing accounts then costs one value copied
out of the log. The alternative and its trade-offs:
[Linking accounts](linking.md#authentik-subject-mode).
