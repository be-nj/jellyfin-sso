# Keycloak setup

[Documentation index](README.md) · [README](../README.md)

1. Create a client: **Client authentication off** (public), standard flow, PKCE method
   `S256`, redirect URI `https://<your-jellyfin>/sso/oidc/callback/<providerId>`.
2. Add a **Group Membership** mapper, claim name `groups`, **Full group path off** —
   otherwise the claim reads `/media` and your mapping has to spell it that way.
3. Leave the plugin's *Scopes* **empty**. Keycloak has no `groups` scope unless someone
   creates one, and an unknown scope fails the authorization with `invalid_scope`.
4. Issuer URL: `https://<your-keycloak>/realms/<realm>`.

Keycloak puts the groups in the ID token, so *Fetch groups from userinfo endpoint* stays
off.

Without the mapper from step 2, Keycloak still sends the realm roles — nested, as
`realm_access.roles`. Typing that path into *Groups claim name* works, and so does
`resource_access.<client-id>.roles` for a client's own roles. The mapper is still the
better setup: realm roles carry Keycloak's own `default-roles-<realm>`, `offline_access`
and `uma_authorization` alongside yours, and a group is what a household actually thinks
in.
