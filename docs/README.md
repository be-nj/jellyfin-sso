# Documentation

The [README](../README.md) installs the plugin and walks the setup. The detail is here.

**Setting it up**

- [Requirements and versions](requirements.md) — which build fits which Jellyfin,
  addresses, reachability
- [Authentik](authentik.md) · [Keycloak](keycloak.md) · [Authelia](authelia.md) — the work
  on the provider's side
- [Configuration reference](configuration.md) — every field on the plugin page
- [Linking accounts](linking.md) — the IdP subject, manual and self-service linking

**Living with it**

- [Login button and logout](login-and-logout.md)
- [Phones, tablets and TVs](clients.md) — Quick Connect
- [Troubleshooting](troubleshooting.md) — errors and what they mean
- [If something goes wrong](recovery.md) — lockout, backup and restore, uninstalling
- [Security notes](security.md) · [Glossary](glossary.md)

**Behind the decisions**

- [Architecture decision records](adr/) — the authentication bridge, Quick Connect for
  native clients, IdP groups as the source of truth, no stored client secret
- [CONTEXT.md](../CONTEXT.md) — domain vocabulary
- [CONTRIBUTING.md](../CONTRIBUTING.md) — building, testing, releasing
