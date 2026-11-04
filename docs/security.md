# Security notes

[Documentation index](README.md) · [README](../README.md)

Two things that affect how you run this:

- **Revocation is not immediate.** Removing someone from a group at the provider changes
  nothing until their next login, and Jellyfin sessions last a long time. To cut someone
  off now, take the libraries away in Jellyfin's own user screen — the server enforces
  that on the next request, even mid-session, though an open app may show stale tiles
  until it reloads — and end their session under Dashboard → **Devices**, or delete the
  account. Note that the hand-edit lasts only until that person's next SSO login, when
  the group mapping is written over it again.
- **No password and no client secret is stored.** The plugin never sees a password, and
  the default client holds no secret — PKCE proves it started the login.

The details:

- **PKCE public client** by default. A confidential client is supported, but its secret is
  set through the admin API as `env:VAR` or `file:/path` and read at login, never written
  to the plugin's configuration file
  ([how](configuration.md#confidential-clients)).
- Full ID-token validation: JWKS signature, `iss`, `aud`, `exp`, `nonce`. `iat` is
  checked on the back-channel logout token, where freshness is the replay control. Each
  rejection has its own test.
- A login can only be finished in the browser that started it. The `state` makes a
  callback single-use and pins it to one provider; the browser is established separately,
  by a cookie minted when the login starts and compared when the callback arrives — a
  `state` alone is something an attacker can hold and hand to somebody else.
- The one-time secret that completes a login is random, single-use, expires after 90
  seconds and is never written to disk. It does reach the browser, in the body of the
  page that spends it.
- Identity is matched by `(provider, sub)`, never by username
  ([why](linking.md#identity-matching-and-the-idp-subject)).
