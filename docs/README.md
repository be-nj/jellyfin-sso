# Documentation

The [README](../README.md) is the install path: what changes on your server, the
requirements in brief, and the eight steps of the quick start. Everything else lives here.

## Setting it up

- [Requirements and versions](requirements.md) — which plugin build fits which Jellyfin,
  the address to use, and the reachability check between Jellyfin and your provider.
- [Authentik](authentik.md) · [Keycloak](keycloak.md) · [Authelia](authelia.md) — the work
  on the provider's side, per provider.
- [Configuration reference](configuration.md) — every field on the plugin page: providers,
  group mappings, safety switches, confidential clients.
- [Linking accounts](linking.md) — the IdP subject, linking accounts that existed before
  SSO, and letting users link their own.

## Living with it

- [Login button and logout](login-and-logout.md) — the button in Jellyfin's branding, and
  signing out of the provider as well.
- [Phones, tablets and TVs](clients.md) — Quick Connect, and why there is no QR code yet.
- [Troubleshooting](troubleshooting.md) — the errors this plugin produces, and what each
  one means.
- [If something goes wrong](recovery.md) — lockout, backup and restore, uninstalling,
  coming from the old 9p4 plugin.
- [Security notes](security.md) — what is stored, what is validated, and what revocation
  does not do.
- [Glossary](glossary.md) — *claim*, *scope*, *issuer*, *subject* in plain language.

## Behind the decisions

- [Architecture decision records](adr/) — why the plugin bridges Jellyfin's
  authentication provider, why native clients go through Quick Connect, why the IdP's
  groups are the source of truth, and why the default client holds no secret.
- [CONTEXT.md](../CONTEXT.md) — the domain vocabulary.
- [CONTRIBUTING.md](../CONTRIBUTING.md) — building, testing, releasing.
