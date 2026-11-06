# Documentation

The [README](../README.md) installs the plugin and walks the setup. The detail is here.

**Setting it up**

- [Requirements and versions](requirements.md) — which build fits which Jellyfin,
  addresses, reachability
- [Authentik](authentik.md) · [Keycloak](keycloak.md) · [Authelia](authelia.md) — the work
  on the provider's side
- [Tinyauth](tinyauth.md) — a fourth provider, guided but not in the test matrix, and what
  that costs
- [Configuration reference](configuration.md) — every field on the plugin page
- [Linking accounts](linking.md) — the IdP subject, manual and self-service linking

**Living with it**

- [Login button and logout](login-and-logout.md)
- [Phones, tablets and TVs](clients.md) — Quick Connect
- [Troubleshooting](troubleshooting.md) — errors and what they mean
- [If something goes wrong](recovery.md) — lockout, backup and restore, uninstalling
- [Security notes](security.md) · [Glossary](glossary.md)

**Behind the decisions**

- [Threat model](threat-model.md) — what the plugin trusts, and the test that holds each
  claim
- [Reviews](reviews/) — what each review found, and how it was done. The newest are the
  [conformance run](reviews/2026-11-06-oidc-conformance.md) — the OpenID Foundation suite
  implementing the other side of the protocol, the first check against the specification
  rather than against a reading of it — the [hardening pass](reviews/2026-11-05-hardening.md),
  the [outside review](reviews/2026-11-05-outside-review.md) — the first by something that
  did not write this plugin — and the
  [test practices audit](reviews/2026-11-05-test-practices.md)

- [Architecture decision records](adr/) — the authentication bridge, Quick Connect for
  native clients, IdP groups as the source of truth, no stored client secret
- [CONTEXT.md](../CONTEXT.md) — domain vocabulary
- [CONTRIBUTING.md](../CONTRIBUTING.md) — building, testing, releasing
