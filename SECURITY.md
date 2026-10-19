# Security

This plugin sits between an identity provider and a Jellyfin server's user accounts, so a
defect here can mean someone reaching a library or an administrator flag that was never
meant for them. Reports are welcome and taken seriously.

## Reporting a vulnerability

Open a [private security advisory](https://github.com/be-nj/jellyfin-sso/security/advisories/new).
Use that rather than a public issue for anything that lets a person reach an account,
a library or an administrator flag that is not theirs.

What helps: the plugin version, the Jellyfin version, the identity provider and its
settings, and the shortest sequence that shows the problem. A log excerpt around the
failed or unexpected login usually carries the rest.

Expect a first answer within a week. A fix for anything that grants access goes out as a
release of its own rather than waiting for other work, and the advisory names what was
affected and from which version.

Anything that is not about access — a crash, a wrong button, a missing field — belongs in
a normal [issue](https://github.com/be-nj/jellyfin-sso/issues), in the open.

## What is in scope

- Everything under `Jellyfin.Plugin.Sso/`, including the pages the plugin renders itself.
- The release artifacts published from this repository.

Out of scope: Jellyfin itself, the identity providers, and a server configured against its
own documentation — a plugin cannot fix an identity provider that hands out groups to the
wrong people.

## What the plugin does on its side

- **No password and no client secret is stored.** The default client holds no secret at
  all (PKCE); a confidential client reads its secret from an environment variable or a
  file at login, and the configuration holds only the reference.
- **Every ID token is validated**: signature against the provider's JWKS, issuer,
  audience, lifetime, and the nonce this plugin issued. Each rejection has a test.
- **Identity is keyed by `(provider, subject)`, never by username**, so an account cannot
  be inherited by whoever registers the right name at the provider.
- **The last administrator is never demoted**, whatever the groups say.
- Every release is checked by signing in through real Authentik, Keycloak and Authelia
  containers in a browser, and CodeQL runs on every push.

The reasoning, the threat model and the test that holds each claim are in
[docs/threat-model.md](docs/threat-model.md).
