<div align="center">

<img src="https://cdn.simpleicons.org/jellyfin/00A4DC" height="52" alt="Jellyfin">
&nbsp;&nbsp;
<img src="https://cdn.simpleicons.org/authentik/FD4B2D" height="52" alt="Authentik">
&nbsp;&nbsp;
<img src="https://cdn.simpleicons.org/keycloak/4D4D4D" height="52" alt="Keycloak">
&nbsp;&nbsp;
<img src="https://cdn.simpleicons.org/authelia/113155" height="52" alt="Authelia">

# Jellyfin SSO Authentication

**Log in to Jellyfin with the account you already have — and let your identity provider's
groups decide who sees which libraries.**

[![Tests](https://github.com/be-nj/jellyfin-sso/actions/workflows/test.yml/badge.svg)](https://github.com/be-nj/jellyfin-sso/actions/workflows/test.yml)
[![Release](https://img.shields.io/github/v/release/be-nj/jellyfin-sso?logo=github&color=success)](https://github.com/be-nj/jellyfin-sso/releases)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.2.x-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![License](https://img.shields.io/github/license/be-nj/jellyfin-sso?color=blue)](LICENSE)

[Install](#install) · [Set it up](#set-it-up) · [Documentation](docs/README.md) ·
[Troubleshooting](docs/troubleshooting.md)

</div>

---

OpenID Connect single sign-on for Jellyfin, for households that already run
[Authentik](docs/authentik.md), [Keycloak](docs/keycloak.md) or
[Authelia](docs/authelia.md). One account, one password policy, and library access that
follows the groups you already maintain. Replaces the deprecated
[9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).

## What it does

- **Sign in through your provider** — PKCE public client, no secret stored anywhere.
- **Groups decide libraries** — map a provider group to libraries, Live TV, downloads, a
  parental rating or admin rights; applied on every login.
- **Accounts created on first login**, and existing Jellyfin accounts
  [linked deliberately](docs/linking.md) — never by matching names, which is what made the
  old plugin's admin takeover possible.
- **Works on phones and TVs** through Jellyfin's [Quick Connect](docs/clients.md).
- **Several providers at once**, each with its own login button in Jellyfin's branding.
- **Single logout**, optionally ending the provider's session too.
- **Lockout guards built in** — the last administrator is never demoted, and users can be
  excluded from sync.

Tested the way you will use it: every release signs in through real Authentik, Keycloak
and Authelia containers in a browser, on top of 72 unit tests, in CI.

## Install

Dashboard → **Plugins → Repositories → +**, name it `SSO Authentication`, URL:

```
https://jf.be-nj.net/manifest.json
```

Then **Plugins → Catalog → Authentication → SSO Authentication → Install** and restart
Jellyfin. Updates arrive in the catalog like any other plugin.

Needs **Jellyfin 12.2.x** — each build matches one server version, and the catalog hides
the ones that do not fit, so an empty catalog means your server is older than any build
here ([version table](docs/requirements.md#jellyfin-version)). Building and installing by
hand: [CONTRIBUTING.md](CONTRIBUTING.md).

> ### Before you switch
>
> - **Existing accounts, history and favourites stay.** Nothing is deleted or renamed.
> - **An SSO user can no longer sign in with a Jellyfin password** — the provider is in
>   charge of that account from its first SSO login.
> - **Keep one local administrator** with a password. That is your way back in when the
>   provider is down or a mapping goes wrong.
> - **Library permissions become the provider's business**: the mapping is rewritten onto
>   the user on *every* login, over anything you set by hand
>   ([except excluded users](docs/configuration.md#safety)).
> - **Anyone your provider lets through gets an account** — gate it there, not here.
>
> All of it is reversible: [uninstall path](docs/recovery.md#uninstalling).

## Set it up

Worked example with Authentik; [Keycloak](docs/keycloak.md) and
[Authelia](docs/authelia.md) differ in steps 4 and 5. Half an hour the first time.

1. **Back up Jellyfin's config with the server stopped** —
   [two commands](docs/recovery.md#backup-and-restore). A copy taken while it runs
   restores into a broken server.
2. **Install the plugin** ([above](#install)) and restart.
3. **Add a provider in the plugin** with nothing but a display name, and **Save**.
   Dashboard → **Plugins → SSO Authentication → Providers → + Add Provider**. It is
   created disabled, and the card now prints the **Redirect URI** you need next.

   ![The provider card in the plugin's configuration page](docs/img/provider-card.png)

   Open the dashboard on **the address your household actually uses** — the redirect URI
   is built from it ([why](docs/requirements.md#one-address-for-jellyfin-used-everywhere)).
4. **Create the application at your provider** with that redirect URI, as a public client.
   Click-by-click: [Authentik](docs/authentik.md) (do not forget the *Signing Key*),
   [Keycloak](docs/keycloak.md), [Authelia](docs/authelia.md). Note the **Client ID** and
   the **issuer URL**.
5. **Finish the provider card**: issuer URL, client id, tick **Enabled**, **Save**. No
   restart. Field by field: [configuration reference](docs/configuration.md#providers).
6. **Map a group**: **Group Mappings → + Add Mapping**, the group name exactly as the
   provider spells it, tick the libraries, **Save**. Without a mapping people can sign in
   but see nothing ([what the permissions mean](docs/configuration.md#group-mappings)).
7. **Test with a throwaway account**, not your own: open
   `https://<your-jellyfin>/sso/oidc/start` in a private window and sign in as a spare
   user from that group. That account is created for them on the spot.
8. **Then link the accounts you already have** — the first SSO login for an existing name
   is refused on purpose, until an administrator links it
   ([how](docs/linking.md#manual-linking)). Leave one administrator unlinked.

Stuck? [Troubleshooting](docs/troubleshooting.md) lists the errors this plugin actually
produces. Locked out? [If something goes wrong](docs/recovery.md).

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

Issues and patches welcome: <https://github.com/be-nj/jellyfin-sso/issues>. A log excerpt
around the failed login, your provider and the plugin version answer most questions at
once. Building, the three test tiers and how releases are cut are in
[CONTRIBUTING.md](CONTRIBUTING.md); design decisions in [docs/adr/](docs/adr/).

**Roadmap:** active revocation (group changes currently apply at the next login), QR-code
login for TVs (needs a change in the Jellyfin apps), device-code grant once the clients
support it.

## License

[MIT](LICENSE).
