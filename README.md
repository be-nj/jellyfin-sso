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

OpenID Connect single sign-on for Jellyfin — for households already running
[Authentik](docs/authentik.md), [Keycloak](docs/keycloak.md) or
[Authelia](docs/authelia.md).

- **Sign in through your provider.** PKCE public client, no secret stored.
- **Groups decide access** — libraries, Live TV, downloads, deletion, collections,
  subtitles, lyrics, parental rating, admin — reapplied on every login.
- **Accounts created at first login**, existing ones [linked explicitly](docs/linking.md).
- **Phones and TVs** via Jellyfin's [Quick Connect](docs/clients.md).
- **Profile pictures from the provider**, if you want them — off by default, because an
  avatar somebody chose here is theirs.
- **Several providers at once**, each with its own login button — plain, or painted in
  the provider's colour.
- **Optional single logout** that ends the provider's session too, and **back-channel
  logout** so a session ended at the provider ends in Jellyfin as well.
- **Lockout guards** — the last administrator is never demoted.
- **A limit on the anonymous endpoints**, so the parts of a login that cannot ask for a
  token are not free to hammer.

## What it looks like

<div align="center">

<img src="docs/img/login-flow.gif" alt="A login: Jellyfin's login page, the button, Authentik's form, the home screen" width="760">

</div>

Not a mock-up — a test run with a real Authentik in a container, recorded by `make demo`,
which fails if any step of the login does not happen.
[Sharper](docs/img/login-flow.mp4) · the same login through
**[Keycloak](docs/img/login-flow-keycloak.gif)** ([mp4](docs/img/login-flow-keycloak.mp4))
and **[Authelia](docs/img/login-flow-authelia.gif)** ([mp4](docs/img/login-flow-authelia.mp4)).

## How it is tested

Against real software, not mocks. Every push runs:

| | |
|---|---|
| **341 unit tests** | Token validation, permissions, lockout guards, markup, the served pages |
| **84 end-to-end tests** | Throwaway Jellyfin container, fake provider, real browser |
| **Three Jellyfin versions** | The same build against 12.0, 12.1 and 12.2 |
| **9 real-provider logins** | Authentik, Keycloak and Authelia, through their own forms |
| **Mutation testing** | Stryker breaks the code to see whether the tests notice: 70 %, floor 65 % |
| **CodeQL** | No open alerts |

The provider guides are the configuration those containers get. What the plugin trusts,
and which test holds each claim: [threat model](docs/threat-model.md).

## Compared with the other plugins

Four plugins do this job, and **your Jellyfin version decides two of them away**: this
one and K0lin's are for Jellyfin 12, the other two for 10.11.

| | **This one** | [K0lin](https://github.com/K0lin/jellyfin-plugin-sso) | [Ezeqielle](https://github.com/Ezeqielle/jellyfin-plugin-oidc) | [9p4](https://github.com/9p4/jellyfin-plugin-sso) |
|---|:---:|:---:|:---:|:---:|
| **Jellyfin** | 12 | 12 | 10.11 | 10.11 |
| *Signing in* | | | | |
| OpenID Connect | ● | ● | ● | ● |
| SAML | ○ | ● | ○ | ● |
| Several providers at once | ● | ● | ● | ● |
| Quick Connect, for phones and TVs | ● | ○ | ● | ○ |
| A button on the login page | ● | ○ | ● | ◐ |
| *Accounts* | | | | |
| Identified by the provider's subject | ● | ◐ | ○ | ◐ |
| Created at first login | ● | ● | ● | ● |
| An existing account linked on purpose | ● | ◐ | ○ | ◐ |
| Users can link their own account | ● | ○ | ○ | ○ |
| *What the groups decide* | | | | |
| Libraries | ● | ● | ● | ● |
| Live TV | ● | ● | ● | ● |
| Downloads | ● | ● | ○ | ○ |
| Deleting, collections, subtitles, lyrics | ● | ○ | ◐ | ○ |
| Parental rating | ● | ○ | ● | ○ |
| The last administrator is never demoted | ● | ◐ | ○ | ○ |
| Accounts kept out of the sync | ● | ○ | ○ | ○ |
| *Claims* | | | | |
| ID token checked against the provider's keys | ● | ● | ● | ● |
| A nested claim path (`realm_access.roles`) | ● | ○ | ● | ○ |
| Groups from the userinfo endpoint | ● | ● | ● | ● |
| *Sessions* | | | | |
| Signing out ends the provider's session | ● | ○ | ○ | ○ |
| Back-channel logout | ● | ○ | ○ | ○ |
| *The rest* | | | | |
| Rate limit on the anonymous endpoints | ● | ○ | ○ | ○ |
| Profile picture from the provider | ◐ | ○ | ● | ○ |

● has it · ◐ partly, optionally, or by hand · ○ not found in its sources

◐ in detail: K0lin and 9p4 match a name claim against a link table an administrator keeps;
9p4's button is a snippet you paste into the login disclaimer; K0lin's administrator guard
is a setting; Ezeqielle maps deletion, collections and subtitles but not lyrics; the
picture here is off until you switch it on.

### Where they come from

Not a feature question, and not a ranking — the same handful of numbers, taken the
same way for each of the four, this one included.

| | **This one** | K0lin | Ezeqielle | 9p4 |
|---|---|---|---|---|
| First commit | 2026-09-17 | 2026-06-12 | 2026-05-12 | 2022-01-10 |
| Latest release | v1.12.14 (2026-10-20) | v5.1.1 (2026-09-09) | v1.0.10 (2026-09-10) | v4.0.0.4 (2026-03-20), archived |
| Stars | 1 | 162 | 40 | 1,455 |
| Licence | MIT sources, GPLv3 binary | GPL-3.0 | GPL-3.0 | GPL-3.0 |
| Commits carrying an AI trailer | **100 of 108** (Claude Opus 4.6–5) | none | 33 of the last 100 (Claude Opus 4.6–5) | none |
| Automated tests | 341 unit, 84 end-to-end, 9 real-provider logins | 68 test cases | 16 test cases | none in the repository |
| Mutation testing | 70 %, floor 65 % | — | — | — |
| Static analysis | CodeQL, no open alerts | — | — | — |

### What that means for this one

- **Youngest and least used** — one star, one household. The others have users who are
  not their authors.
- **Almost all of it was written by an LLM**, Claude Opus 4.6 through 5: the code, the
  tests, the threat model, this table. "None" in that row means no trailer, not
  necessarily no AI.
- **Nobody outside has read it.** CodeQL, the test matrix and mutation testing stand in
  for a reviewer and are not the same thing, and `docs/reviews/` was written by the model
  that wrote the code. Counting approving pull-request reviews would say little: 9p4 has
  none either across 56, which measures push rights rather than reading.
- **No SAML**, and only Authentik, Keycloak and Authelia are set up and tested.

If that is disqualifying — it reasonably might be — K0lin's is the active fork on
Jellyfin 12.

Not a fork, by the way: 30 of this plugin's 1,092 significant lines also appear in 9p4's
sources, all `using` directives, Jellyfin CSS classes and one-line properties. Hence MIT
sources where the forks are GPL-3.0 ([the binary is GPLv3 either way](#license)).

## Install

Dashboard → **Plugins → Repositories → +**, name it `SSO Authentication`, URL:

```
https://raw.githubusercontent.com/be-nj/jellyfin-sso/main/manifest.json
```

Then **Plugins → Catalog → Authentication → SSO Authentication → Install**, and restart.

- Needs **Jellyfin 12.0 or newer** — an empty catalog means yours is older
  ([table](docs/requirements.md#jellyfin-version)).
- The same file is served from this repository itself, so you can read it before you
  trust it, and the release ZIP it points to carries
  [build provenance](https://github.com/be-nj/jellyfin-sso/attestations).
- Shorter to type, same file: `jf.be-nj.net/m.json`.
- Building it yourself: [CONTRIBUTING.md](CONTRIBUTING.md).

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

Authentik as the example; [Keycloak](docs/keycloak.md) and [Authelia](docs/authelia.md)
differ in steps 4 and 5. Half an hour the first time.

1. **Back up Jellyfin's config, server stopped** —
   [two commands](docs/recovery.md#backup-and-restore). A copy taken while it runs is not
   a backup.
2. **Install the plugin** ([above](#install)) and restart.
3. **Add a provider**, display name only, **Save**. Dashboard → **Plugins → SSO
   Authentication → Providers → + Add Provider**. It starts disabled and prints the
   **Redirect URI** you need next.

   <a href="docs/img/provider-card.png"><img src="docs/img/provider-card.png" alt="The provider card in the plugin's configuration page" width="440"></a>

   Use **the address your household uses** when you open the dashboard — the redirect URI
   is built from it ([why](docs/requirements.md#one-address-for-jellyfin-used-everywhere)).
4. **Create the application at your provider**, public client, with that redirect URI:
   [Authentik](docs/authentik.md) (needs a *Signing Key*), [Keycloak](docs/keycloak.md),
   [Authelia](docs/authelia.md). Note the **Client ID** and the **issuer URL**.
5. **Finish the provider card**: issuer URL, client id, **Enabled**, **Save**. No restart
   ([every field](docs/configuration.md#providers)).
6. **Map a group**: **Group Mappings → + Add Mapping**, group name exactly as the provider
   spells it, tick libraries, **Save**. No mapping means people sign in and see nothing
   ([permissions](docs/configuration.md#group-mappings)).
7. **Test with a throwaway account**, not your own: `/sso/oidc/start` in a private window,
   signed in as a spare user from that group. Their Jellyfin account appears on the spot.
8. **Link your existing accounts.** The first SSO login for a name that already exists is
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

- **Issues and patches:** <https://github.com/be-nj/jellyfin-sso/issues> — attach the
  Jellyfin log around the failed login, your provider and the plugin version.
- **Building, the test tiers, releases:** [CONTRIBUTING.md](CONTRIBUTING.md) ·
  **decisions:** [docs/adr/](docs/adr/) · **reviews:** [docs/reviews/](docs/reviews/)
- **Roadmap:** active revocation (today group changes apply at the next login); QR-code
  login and the device-code grant, both needing changes in the Jellyfin apps.

## License

The sources are [MIT](LICENSE).

The built plugin is GPLv3. It links `Jellyfin.Controller` and `Jellyfin.Model`, which
Jellyfin publishes under `GPL-3.0-only`, so the DLL in the release zip carries those
terms — as every Jellyfin plugin's does, whatever its own sources say. Copy the code
under MIT; pass the plugin on under GPLv3.
