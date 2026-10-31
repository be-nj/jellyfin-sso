# Keycloak setup

[Documentation index](README.md) · [README](../README.md)

1. Create a client: **Client authentication off** (public), standard flow, PKCE method
   `S256`, redirect URI `https://<your-jellyfin>/sso/oidc/callback/<providerId>`.
2. Add a **Group Membership** mapper, claim name `groups`, **Full group path off** —
   otherwise the claim reads `/media` and your mapping has to spell it that way.
3. Leave the plugin's *Scopes* **empty**. Keycloak has no `groups` scope unless someone
   creates one, and an unknown scope fails the authorization with `invalid_scope`.
4. Issuer URL: `https://<your-keycloak>/realms/<realm>`.
5. **Who may log in** is decided here, not in Jellyfin: give the client a dedicated
   authorization flow, or restrict it to members of a group, unless every Keycloak user
   should get a Jellyfin account. The plugin can also hold the line from its side —
   *Refuse logins that match no mapping* in
   [Safety](configuration.md#safety) — which is worth switching on as a second lock
   rather than as a replacement.

Keycloak puts the groups in the ID token, so *Fetch groups from userinfo endpoint* stays
off.

Without the mapper from step 2, this plugin sees no groups at all. Keycloak does know the
user's realm roles, and does send them nested as `realm_access.roles` — but its built-in
*realm roles* mapper writes them into the **access token** only. *Add to ID token* is off
by default, and so is *Add to userinfo*, and those two are the only places this plugin
looks. The login still succeeds; the account simply arrives with no libraries, which is
the quietest way for a setup to be wrong.

Roles can stand in for groups, with one switch: **Client scopes → roles → realm roles →
Add to ID token**. `realm_access.roles` in *Groups claim name* then works, and
`resource_access.<client-id>.roles` the same way for a client's own roles, through that
mapper's own copy of the switch. The group mapper from step 2 is still the better setup:
realm roles carry Keycloak's own `default-roles-<realm>`, `offline_access` and
`uma_authorization` alongside yours, and a group is what a household actually thinks in.

Every warning on this page is walked by a test against a real Keycloak — the `groups`
scope being refused, the full path arriving as `/media`, a client with no mapper sending
nothing, and `realm_access.roles` resolving once the switch is on
([`RealIdentityProviderTests`](../tests/Jellyfin.Plugin.Sso.E2eTests/RealIdentityProviderTests.cs)).
