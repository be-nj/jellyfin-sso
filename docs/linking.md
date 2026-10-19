# Linking accounts

Back to the [documentation index](README.md) · [README](../README.md)

New users are linked automatically at their first SSO login. Linking by hand is for
accounts that existed before SSO.

## Identity matching and the IdP subject

The plugin keys each Jellyfin user to an OIDC identity by `(provider, sub)` — **never by
username** (that is the source of the old plugin's admin-takeover bug). A user is matched
only via an explicit link; an unknown `sub` always becomes a new account. That is why the
first SSO login for an existing name is refused with *"Username 'ben' already exists"*
until an administrator links it.

### Authentik: choose a Subject mode

In **Applications → Providers → (your provider) → Advanced protocol settings → Subject
mode**, Authentik decides what `sub` contains:

| Subject mode | `sub` value | Notes |
|---|---|---|
| **Based on the User's hashed ID (default)** | opaque SHA256 hash | **Recommended.** Stable and safe; the value is only learnable from a login, which [manual linking](#manual-linking) covers. |
| Based on the User's username | the username | Easy to link by hand, and brittle: see the trade-offs below. |
| Based on the User's UUID | the user UUID | Stable; only readable via the API (`/api/v3/core/users/`). |

The username mode trades safety for convenience, which is why the setup guide recommends
leaving Authentik's default in place:

- **Renaming someone at the provider breaks their link**, because the `sub` changes with
  the name. They have to be linked again by hand.
- **Reusing a username is dangerous**: delete `ben`, create a different person as `ben`
  later, and they inherit the old account's link — including whatever it could see. Only
  pick this mode if usernames in your directory are never recycled.
- In return the subject *is* the username, so linking an existing account is typing a name
  instead of hunting for a hash.

People who are new to Jellyfin never need any of this: their account is created and linked
at their first login.

## Manual linking

Use the **Account Links** tab to connect an account that existed before SSO:

![The manual account link form](img/account-links.png)

- **Provider** + **Jellyfin user** are dropdowns.
- **Subject** is the identity's `sub` at the provider. With Authentik's *Subject mode:
  username* it is simply the username.
- With any other subject mode you cannot know the value in advance, so let the person try
  to log in once. The attempt is refused and Jellyfin's log (Dashboard → **Logs**, or
  `docker logs jellyfin`) carries the value you need:

  ```
  [WRN] Jellyfin.Plugin.Sso.Provisioning.UserProvisioner: SSO JIT blocked: username 'ben'
  already exists. To link manually: ProviderId=ab12… Sub=f81d4fae-7dec-11d0-a765-… JellyfinUserId=…
  ```

  Copy the `Sub=` value into the form. Repeat per person; for a household moving over from
  another plugin, plan an evening for it.

Keep one administrator unlinked, with a password, as your way back in.

## Self-service linking

With **Let users link their own account** enabled, signed-in users can link their own
account at **`/sso/link`**: the page reads the session the web client stored, starts the
OIDC flow for that user, and attaches the identity to their account. An identity that is
already linked to a different Jellyfin account is refused — only an administrator can move
a link.

Unlinking has no button for users yet. **An administrator removes any link under the
Account Links tab**, which is the practical route. There is an endpoint for a user's own
link, `DELETE /sso/link/self` with their Jellyfin token, but nothing in the web client
calls it — treat it as something you do for your household, not something they do.

Linking grants nothing by itself. Library access and the admin flag are applied on the
next SSO login, from the groups, as always.
