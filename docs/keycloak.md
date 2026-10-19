# Keycloak setup

Back to the [documentation index](README.md) · [README](../README.md)

1. Create a client: **Client authentication off** (public), standard flow, PKCE method
   `S256`, redirect URI `https://<your-jellyfin>/sso/oidc/callback/<providerId>`.
2. Add a **Group Membership** mapper to the client, claim name `groups`, and switch
   **Full group path off** — otherwise the claim reads `/media` instead of `media` and your
   group mapping has to spell it that way.
3. Leave the plugin's *Scopes* field **empty**, which asks for `openid profile email`.
   Keycloak has **no** `groups` scope unless someone creates one, and requesting a scope it
   does not know fails the whole authorization with `invalid_scope` — so do not copy
   Authelia's scope line here.
4. The issuer URL is `https://<your-keycloak>/realms/<realm>`.

Keycloak sends the groups in the login token, so *Fetch groups from userinfo endpoint*
stays off.
