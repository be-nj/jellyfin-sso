# Authentik setup

Back to the [documentation index](README.md) · [README](../README.md)

Written against Authentik 2026.8. Authentik's own vocabulary: a **Provider** speaks the
protocol, an **Application** is what users see and what access is granted on.

1. **Directory → Groups → Create**: one group per level of access you want, for example
   `media` and `jellyfin-admins`. Put the test user from the setup walkthrough into `media`; add
   yourself later, once linking is done.
2. **Applications → Providers → Create → OAuth2/OpenID Provider**:
   - *Name*: `Jellyfin`.
   - *Authorization flow*: `default-provider-authorization-implicit-consent` (the one
     without a consent screen; with consent works too, users just get an extra click).
   - *Client type*: **Public**. No client secret — the plugin uses PKCE instead.
   - *Client ID*: keep what Authentik generates, you will paste it into the plugin.
   - *Redirect URIs*: the URL the plugin's provider card printed, as **Strict**:
     `https://<your-jellyfin>/sso/oidc/callback/<providerId>`
   - *Signing Key*: pick the self-signed certificate Authentik ships with. **Without a
     signing key Authentik does not sign the token in a way the plugin accepts** and the
     login fails.
   - Leave the scopes at the three defaults (`openid`, `profile`, `email`). Authentik's
     default `profile` mapping already includes the user's groups, so there is nothing to
     build — this is where Authentik is kinder than Keycloak.
3. **Applications → Applications → Create**: name `Jellyfin`, pick the provider you just
   made, and give it a slug, for example `jellyfin`. The slug decides the issuer URL:

   ```
   https://<your-authentik>/application/o/<slug>/
   ```

   That URL, trailing slash and all, goes into the plugin's *Issuer URL* field.
4. **Who may log in** is decided here, not in the plugin: bind a policy to the application
   if you do not want every Authentik user to end up with a Jellyfin account.

In the plugin's provider card, leave *Groups claim name* at `groups`, *Scopes* empty and
*Fetch groups from userinfo endpoint* off.

## Leave *Subject mode* alone

Authentik's default gives every person an unreadable, permanent id, which is the right
thing: it survives renames and cannot be inherited by a recycled username. Linking your
existing accounts then costs one value copied out of the log, which
[Linking accounts](linking.md) walks through step by step.

The alternative, *Based on the User's username* under *Advanced protocol settings*, makes
linking a matter of typing a name — at the price of breaking every link when someone is
renamed, and handing an old account to anyone who gets a recycled username. Only take it
if usernames in your directory are never reused. The trade-offs are laid out in
[Identity matching](linking.md#identity-matching-and-the-idp-subject).
