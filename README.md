<div align="center">

<img src="docs/img/stack.svg" alt="Authentik, Keycloak and Authelia sign users in to Jellyfin" width="560">

# Jellyfin SSO Authentication

**Log in to Jellyfin with the account you already have — and let your identity provider's
groups decide who sees which libraries.**

[![Tests](https://github.com/be-nj/jellyfin-sso/actions/workflows/test.yml/badge.svg)](https://github.com/be-nj/jellyfin-sso/actions/workflows/test.yml)
[![Release](https://img.shields.io/github/v/release/be-nj/jellyfin-sso?logo=github&color=success)](https://github.com/be-nj/jellyfin-sso/releases)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.0%20%E2%80%93%2012.2-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![License](https://img.shields.io/github/license/be-nj/jellyfin-sso?color=blue)](LICENSE)

[Install](#install) · [Set it up](#set-it-up) · [Documentation](docs/README.md) ·
[Troubleshooting](docs/troubleshooting.md)

</div>

---

OpenID Connect single sign-on for Jellyfin, for households that already run
[Authentik](docs/authentik.md), [Keycloak](docs/keycloak.md) or
[Authelia](docs/authelia.md). Replaces the deprecated
[9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).

- **Sign in through your provider.** PKCE public client, no secret stored.
- **Groups decide access** — libraries, Live TV, downloads, parental rating, admin
  rights — reapplied on every login.
- **Accounts created at first login**; existing ones [linked explicitly](docs/linking.md),
  never matched by username.
- **Phones and TVs** via Jellyfin's [Quick Connect](docs/clients.md).
- **Several providers at once**, each with its own login button.
- **Optional single logout** that ends the provider's session too.
- **Lockout guards**: the last administrator is never demoted, and users can be excluded
  from sync.

## How it is tested

Authentication plugins fail in ways nobody notices until the wrong person is an
administrator, so this one is checked against running software rather than mocks. Every
push runs:

| | |
|---|---|
| **72 unit tests** | Token validation — signature, `iss`, `aud`, `exp`, `nonce`, `state` — permission resolution, the lockout guards, the login-button markup |
| **40 end-to-end tests** | A throwaway Jellyfin container with the packaged plugin, a fake provider and a real browser: logins, rejected callbacks, the configuration page, linking, sessions |
| **9 real-provider logins** | **Authentik**, **Keycloak** and **Authelia** in containers, each driven through its own login form in the browser, asserting the Jellyfin policy that comes out |
| **CodeQL** | On every push, currently zero alerts |

The provider guides in [docs/](docs/README.md) are the configuration the test suite
actually applies to those containers, and the screenshots are generated from the running
plugin — neither can quietly drift from what the code does.

## Compared with the other plugins

**This is not a fork.** It solves the same problem as the archived
[9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso) and its forks, but
shares no code with them: measured against 9p4's sources, 30 of this plugin's 1,092
significant lines also appear there, and all thirty are `using` directives, Jellyfin's own
CSS class names and one-line property declarations. That is why it can be MIT-licensed
while the others are GPL-3.0.

The differences that matter when one of these sits in front of your media:

- **Identity is keyed by `(provider, sub)`, never by username.** Matching on names is what
  let a stranger with the right username inherit an administrator account. Existing
  accounts are linked [explicitly](docs/linking.md).
- **The last administrator is never demoted**, and individual users can be excluded from
  sync — a typo in a group name costs a log line, not your server.
- **No client secret in the configuration file.** PKCE by default; a confidential client
  reads its secret from the environment or a file at login.
- **The supported Jellyfin range is tested, not claimed** — every push runs the whole
  end-to-end suite against 12.0, 12.1 and 12.2.
- **Self-service linking, several providers at once, RP-initiated logout** and Quick
  Connect for TVs.
- **No SAML**, deliberately — OIDC is what self-hosted providers speak.

## Install

Dashboard → **Plugins → Repositories → +**, name it `SSO Authentication`, URL:

```
https://jf.be-nj.net/manifest.json
```

Then **Plugins → Catalog → Authentication → SSO Authentication → Install**, and restart
Jellyfin. Typing it on a screen instead of pasting? `jf.be-nj.net/m.json` is the same
file.

Needs **Jellyfin 12.0 or newer**; every release is tested against 12.0, 12.1 and 12.2.
An empty catalog means your server is older than any build here
([table](docs/requirements.md#jellyfin-version)). Building by hand:
[CONTRIBUTING.md](CONTRIBUTING.md).

> ### Before you switch
>
> - **Existing accounts, history and favourites stay.**
> - **An SSO user can no longer sign in with a Jellyfin password.**
> - **Keep one local administrator** with a password — your way back in when the provider
>   is down.
> - **Permissions are rewritten from the groups on every login**, over anything you set by
>   hand ([unless the user is excluded](docs/configuration.md#safety)).
> - **Anyone your provider lets through gets an account.** Gate it there.
>
> Reversible: [uninstall path](docs/recovery.md#uninstalling).

## Set it up

Worked example with Authentik; [Keycloak](docs/keycloak.md) and
[Authelia](docs/authelia.md) differ in steps 4 and 5. Half an hour the first time.

1. **Back up Jellyfin's config with the server stopped** —
   [two commands](docs/recovery.md#backup-and-restore). A copy taken while it runs is not
   a backup.
2. **Install the plugin** ([above](#install)) and restart.
3. **Add a provider** with nothing but a display name, and **Save**. Dashboard →
   **Plugins → SSO Authentication → Providers → + Add Provider**. It is created disabled;
   the card now prints the **Redirect URI** for the next step.

   <a href="docs/img/provider-card.png"><img src="docs/img/provider-card.png" alt="The provider card in the plugin's configuration page" width="440"></a>

   Open the dashboard on **the address your household uses** — the redirect URI is built
   from it ([why](docs/requirements.md#one-address-for-jellyfin-used-everywhere)).
4. **Create the application at your provider**, public client, with that redirect URI:
   [Authentik](docs/authentik.md) (needs a *Signing Key*), [Keycloak](docs/keycloak.md),
   [Authelia](docs/authelia.md). Note the **Client ID** and the **issuer URL**.
5. **Finish the provider card**: issuer URL, client id, **Enabled**, **Save**. No restart
   ([every field](docs/configuration.md#providers)).
6. **Map a group**: **Group Mappings → + Add Mapping**, the group name exactly as the
   provider spells it, tick the libraries, **Save**. Without a mapping people sign in and
   see nothing ([permissions](docs/configuration.md#group-mappings)).
7. **Test with a throwaway account**, not your own: open
   `https://<your-jellyfin>/sso/oidc/start` in a private window and sign in as a spare
   user from that group. The Jellyfin account is created on the spot.
8. **Link the accounts you already have.** The first SSO login for an existing username is
   refused until an administrator links it ([how](docs/linking.md#manual-linking)). Leave
   one administrator unlinked.

Errors: [Troubleshooting](docs/troubleshooting.md). Locked out:
[If something goes wrong](docs/recovery.md).

## Documentation

| | |
|---|---|
| [Requirements and versions](docs/requirements.md) | Which build fits which Jellyfin, addresses, reachability |
| [Authentik](docs/authentik.md) · [Keycloak](docs/keycloak.md) · [Authelia](docs/authelia.md) | The work on the provider's side |
| [Configuration reference](docs/configuration.md) | Every field on the plugin page |
| [Linking accounts](docs/linking.md) | The IdP subject, manual and self-service linking |
| [Login button and logout](docs/login-and-logout.md) | Branding button, signing out of the provider too |
| [Phones, tablets and TVs](docs/clients.md) | Quick Connect, and why there is no QR code yet |
| [Troubleshooting](docs/troubleshooting.md) · [If something goes wrong](docs/recovery.md) | Errors, lockout, backup and restore, uninstalling |
| [Security notes](docs/security.md) · [Glossary](docs/glossary.md) | What is validated and stored; the vocabulary in plain words |

## Contributing

Issues and patches: <https://github.com/be-nj/jellyfin-sso/issues>. Attach the Jellyfin
log around the failed login, your provider and the plugin version. Building, the three
test tiers and releases: [CONTRIBUTING.md](CONTRIBUTING.md); decisions:
[docs/adr/](docs/adr/).

**Roadmap:** active revocation (group changes apply at the next login today), QR-code
login for TVs and the device-code grant — both need changes in the Jellyfin apps.

## License

[MIT](LICENSE).
