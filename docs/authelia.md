# Authelia setup

[Documentation index](README.md) · [README](../README.md)

In `configuration.yml`, under `identity_providers.oidc.clients`:

```yaml
- client_id: 'jellyfin'
  client_name: 'Jellyfin'
  public: true                     # no secret; the plugin uses PKCE
  require_pkce: true
  pkce_challenge_method: 'S256'
  authorization_policy: 'one_factor'
  redirect_uris:
    - 'https://<your-jellyfin>/sso/oidc/callback/<providerId>'
  scopes: ['openid', 'profile', 'email', 'groups']
  response_types: ['code']
  grant_types: ['authorization_code']
  token_endpoint_auth_method: 'none'
```

In the plugin's provider card:

- *Issuer URL*: Authelia's base URL, for example `https://auth.example.com`.
- *Scopes*: `openid profile email groups` — Authelia reports groups only when asked.
- *Fetch groups from userinfo endpoint*: **on**. Authelia keeps groups out of the ID token
  and answers with them at the userinfo endpoint.

Groups come from the `groups` list in Authelia's user file.

Authelia refuses plain-HTTP redirect URIs for public clients, so this provider needs
Jellyfin on `https://`.
