# Authelia setup

Back to the [documentation index](README.md) · [README](../README.md)

In Authelia's `configuration.yml`, under `identity_providers.oidc.clients`:

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

Then, in the plugin's provider card:

- *Issuer URL*: Authelia's own base URL, for example `https://auth.example.com`.
- *Scopes*: `openid profile email groups` — Authelia only reports groups when the
  `groups` scope is asked for, which is why this is the one provider where the field is
  not left empty.
- *Fetch groups from userinfo endpoint*: **on**. Authelia keeps the groups out of the
  login token and answers with them at its userinfo endpoint instead.

Groups themselves come from the `groups` list in Authelia's user file.

Authelia also refuses a plain-HTTP redirect URI for public clients, so Jellyfin needs an
`https://` address (or a `localhost`-style name) for this provider to work at all.
