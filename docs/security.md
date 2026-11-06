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
  to the plugin's configuration file — the API refuses a value that is not one of those
  two forms, so this is a check and not a convention
  ([how](configuration.md#confidential-clients)).
- Full ID-token validation: JWKS signature with the acceptable algorithms named and
  asymmetric, `iss`, `aud` — and `azp` where a token names several audiences — `exp`,
  `iat` and `nonce`. Where claims come from the userinfo endpoint, its `sub` has to match
  the ID token's. Each rejection has its own test.
- `iat` is required on both tokens, and the two rules differ because the tokens do. On an
  ID token it is a required claim that must not claim to come from the future, and how old
  the token may be is left to `exp`; on a back-channel logout token, which carries `iat`
  and only optionally `exp`, freshness is the replay control and five minutes is the limit.
  The ID token's half of that was missing until the conformance suite asked for it
  ([the run](reviews/2026-11-06-oidc-conformance.md)).
- Only keys the provider published for signatures are accepted as signing keys. A key set
  routinely carries encryption keys as well, and `use` is what says which is which
  (RFC 7517 §4.2); a key that says nothing is still accepted, because the field is
  optional.
- A login can only be finished in the browser that started it. The `state` makes a
  callback single-use and pins it to one provider; the browser is established separately,
  by a cookie minted when the login starts and compared when the callback arrives — a
  `state` alone is something an attacker can hold and hand to somebody else.
- The one-time secret that completes a login is random, single-use, expires after 90
  seconds and is never written to disk. It does reach the browser, in the body of the
  page that spends it.
- Identity is matched by `(provider, sub)`, never by username
  ([why](linking.md#identity-matching-and-the-idp-subject)).
- Unlinking an account ends its sessions, whether the person does it themselves or an
  administrator does it on the Links tab. Back-channel logout finds an account through its
  link and nothing else, so a link removed without that would leave tokens nobody can end.
- What the plugin fetches from the provider is fetched from the provider: the discovery
  document's keys, token, userinfo and end-session endpoints have to be on the authority's
  own host, and redirects are not followed, so none of those calls can be moved afterwards.
- Names and group names from a token reach the log on one line, with control characters
  replaced. They are the one class of logged value that cannot be mapped to a closed set,
  and a name carrying a newline would otherwise write a line of its own into the log.
