<div align="center">

<img src="https://cdn.simpleicons.org/jellyfin/00A4DC" height="52" alt="Jellyfin">
&nbsp;&nbsp;
<img src="https://cdn.simpleicons.org/authentik/FD4B2D" height="52" alt="Authentik">
&nbsp;&nbsp;
<img src="https://cdn.simpleicons.org/keycloak/8A8A8A" height="52" alt="Keycloak">
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

Every release signs in through real Authentik, Keycloak and Authelia containers in a
browser, in CI.

## Install

Dashboard → **Plugins → Repositories → +**, name it `SSO Authentication`, URL:

```
https://jf.be-nj.net/m.json
```

Then **Plugins → Catalog → Authentication → SSO Authentication → Install**, and restart
Jellyfin. Servers that added `jf.be-nj.net/manifest.json` or the raw GitHub URL keep
working — all three serve the same file.

Needs **Jellyfin 12.2.x**. Each build targets one server version and Jellyfin hides the
rest, so an empty catalog means no build matches yours
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

   ![The provider card in the plugin's configuration page](docs/img/provider-card.png)

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
