# Security notes

Back to the [documentation index](README.md) · [README](../README.md)

In plain language first, for the two things that affect how you run this:

- **Taking someone's access away is not immediate.** Removing them from a group at the
  provider changes nothing until their next login, and Jellyfin sessions last a long time.
  To cut someone off now, also end their session in Jellyfin: Dashboard → **Devices**, or
  delete the account.
- **No password is stored anywhere by this plugin.** It never sees one. There is also no
  client secret to leak, because the plugin authenticates by proving it started the login
  (that is what PKCE does).

The details, for readers who want them:

- **PKCE public client** by default — no `client_secret` stored anywhere. A confidential
  client is supported but has no field on the configuration page: its secret is set
  through the admin API as `env:VAR` or `file:/path`, so the plugin reads it from the
  environment or a file at login time and it never lands in the plugin's configuration
  file. If you do not know you need this, you do not need it. See
  [Configuration](configuration.md#confidential-clients).
- Full ID-token validation: JWKS signature, `iss`, `aud`, `exp`/`iat`, `nonce`; `state`
  for CSRF. Every one of those rejections has its own test.
- The one-time secret that completes a login is random, single-use, expires in 90 seconds
  and never leaves memory.
- Identity is matched by `(provider, sub)`, never by username — that is what stops a
  stranger with the right username from inheriting an account. See
  [Linking accounts](linking.md#identity-matching-and-the-idp-subject).
