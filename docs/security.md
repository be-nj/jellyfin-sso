# Security notes

[Documentation index](README.md) · [README](../README.md)

Two things that affect how you run this:

- **Revocation is not immediate.** Removing someone from a group at the provider changes
  nothing until their next login, and Jellyfin sessions last a long time. To cut someone
  off now, end their session under Dashboard → **Devices**, or delete the account.
- **No password and no client secret is stored.** The plugin never sees a password, and
  the default client holds no secret — PKCE proves it started the login.

The details:

- **PKCE public client** by default. A confidential client is supported, but its secret is
  set through the admin API as `env:VAR` or `file:/path` and read at login, never written
  to the plugin's configuration file
  ([how](configuration.md#confidential-clients)).
- Full ID-token validation: JWKS signature, `iss`, `aud`, `exp`/`iat`, `nonce`, plus
  `state` for CSRF. Each rejection has its own test.
- The one-time secret that completes a login is random, single-use, expires after 90
  seconds and never leaves memory.
- Identity is matched by `(provider, sub)`, never by username
  ([why](linking.md#identity-matching-and-the-idp-subject)).
