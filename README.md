# Jellyfin SSO Authentication Plugin

Log in to Jellyfin with the account you already have at your identity provider — Authentik,
Authelia or Keycloak — and let the groups there decide who sees which libraries. Replaces
the deprecated [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).

- **Works with:** Jellyfin **12.2.x**. Each plugin build matches one server version.
- **Tested against:** Authentik, Keycloak and Authelia — every release is checked by
  signing in through all three for real, in a browser.
- **Install:** add one URL to Jellyfin's plugin repositories, no file copying.
- **Status:** in active development, running in the author's household.

**Tonight's path:** [What this changes](#what-this-changes-on-your-server) →
[Before you start](#before-you-start) → the eight steps of the [quick start](#quick-start).
Everything else — provider details, every configuration field, recovery, security — is in
[docs/](docs/README.md), including a [glossary](docs/glossary.md) in plain language for
*claim*, *scope*, *issuer* and *subject*.

---

## What this changes on your server

All of this can be undone — the [uninstall path](docs/recovery.md#uninstalling) leaves
your accounts and history intact. Read this list anyway, because two of these surprise
people.

- **Your existing accounts stay.** Nothing is deleted or renamed. Watch history,
  favourites and resume positions belong to the Jellyfin account and are untouched.
- **A user managed by SSO can no longer sign in with a Jellyfin password.** The first time
  someone logs in through the provider, the plugin switches that account to its own
  authentication method. Their old Jellyfin password stops working — on purpose, because
  the provider is now in charge.
- **Accounts that never log in through SSO are untouched** — your partner who only ever
  types a Jellyfin password keeps doing exactly that.
- **Keep one local administrator.** An account that never logs in through SSO keeps its
  password. That account is your way back in when the provider is down, when a Jellyfin
  upgrade outruns the plugin, or when a group mapping goes wrong. Do not skip this.
- **Library permissions become the provider's business.** Whatever your group mapping says
  is written to the user on *every* login. Changes you make by hand in Jellyfin's user
  screen are overwritten the next time that person signs in. (Individual users can be
  excluded, see [Safety](docs/configuration.md#safety).)
- **Anyone your provider lets through gets a Jellyfin account.** The first SSO login
  creates the account automatically. The gate is on the provider's side: only people who
  may use the Jellyfin *application* there can get in. Without a matching group they get
  an account that sees no libraries.

---

## Before you start

- **Jellyfin 12.2.x** — Dashboard → **About** tells you yours. Older servers need an older
  plugin build, and some have none; the table is in
  [Requirements](docs/requirements.md#jellyfin-version).
- **An identity provider you administer**: Authentik, Authelia or Keycloak. Anything that
  speaks OpenID Connect should work; those three are tested.
- **Admin access to both**, and the address your household uses to reach Jellyfin —
  public or LAN, whichever it is. Use that same address while setting this up, because the
  redirect URI is built from it. [Why, and what LAN-only
  costs](docs/requirements.md#one-address-for-jellyfin-used-everywhere).
- **Jellyfin itself must reach your provider** — the container, not your laptop. One
  command checks it, and a split-DNS household often fails here:
  [reachability check](docs/requirements.md#jellyfin-itself-must-reach-that-provider).

---

## Quick start

Worked example with **Authentik**. Keycloak and Authelia differ in steps 4 and 5 — see
[Keycloak](docs/keycloak.md) and [Authelia](docs/authelia.md). Budget half an hour the
first time.

1. **Back up Jellyfin's configuration — with the server stopped.** Copying its live
   databases while it runs gives you a backup that restores into a broken server. The two
   commands, for a bind mount and for a named volume, are under
   [Backup and restore](docs/recovery.md#backup-and-restore).

2. **Install the plugin.** Dashboard → **Plugins → Repositories → +**, name it
   `SSO Authentication`, URL:

   ```
   https://raw.githubusercontent.com/be-nj/jellyfin-sso/main/manifest.json
   ```

   Then **Plugins → Catalog → Authentication → SSO Authentication → Install**, and restart
   Jellyfin (`docker compose restart jellyfin`, or the Restart button in the dashboard). An
   empty catalog means your Jellyfin version has no matching build, not a broken URL — see
   [Requirements](docs/requirements.md#jellyfin-version).

3. **Create a half-filled provider in the plugin** so it hands you an id. Dashboard →
   **Plugins → SSO Authentication → Providers → + Add Provider**. Give it a display name
   (`Authentik`), leave everything else alone, **Save** — a provider with nothing but a
   name saves on purpose, and it is created **disabled** so nobody sees a login button
   that goes nowhere yet.

   ![The provider card in the plugin's configuration page](docs/img/provider-card.png)

   The card prints three values at the bottom: the **Provider ID**, the **Redirect URI**
   and the **Login URL**. Copy the redirect URI — that is the one your provider needs,
   complete, with no guessing about which part of the URL is the id. It is built from the
   address you are looking at the dashboard with, so open the dashboard on **the address
   your household actually uses** (`https://jellyfin.example.com`), not on
   `http://<server>:8096` — otherwise you register the wrong address and logins fail from
   everywhere else. If LAN is the only address you have, that is the right one here.

4. **Create the application at your provider** with that redirect URI. Click-by-click for
   [Authentik](docs/authentik.md) (public client, and **do not forget the Signing Key**),
   [Keycloak](docs/keycloak.md) or [Authelia](docs/authelia.md). Note the **Client ID** and
   the application's **issuer URL**, and leave Authentik's *Subject mode* at its default —
   the reasoning is in [Linking accounts](docs/linking.md#authentik-choose-a-subject-mode).

5. **Back in the plugin, finish the provider:** paste the issuer URL and client id into
   the same card, leave *Groups claim name* at `groups`, *Scopes* empty and *Fetch groups
   from userinfo endpoint* off, tick **Enabled**, **Save**. Saving is enough; no restart.
   ([Authelia](docs/authelia.md) is the exception on the last two.)

6. **Create a group at the provider** (for example `media`), put the test user from the
   next step in it, and map it **in the plugin** under **Group Mappings**: **+ Add
   Mapping**, group name `media`, tick the libraries that group may watch, **Save**.
   Without a mapping people can sign in but see nothing. Spell the group exactly as the
   provider does — [what the permissions
   mean](docs/configuration.md#group-mappings).

7. **Test with a throwaway account first, not your own.** Create a spare user at the
   provider, put it in the `media` group, and leave your own administrator account out of
   this for now. Open `https://<your-jellyfin>/sso/oidc/start` in a private window and sign
   in as the spare user. A person without a Jellyfin account gets one created here; that
   is the path you want to see working before you touch anything that matters.

8. **Only then link the accounts you already have.** An existing Jellyfin account is never
   handed to an identity just because the names match: the first SSO login for `ben` is
   refused with *"Username 'ben' already exists"* until an administrator links it —
   [how to link](docs/linking.md#manual-linking). Keep one administrator unlinked, with a
   password, as your way back in.

> **The warning worth repeating:** from now on, that user's library access is rewritten
> from their groups on every single login. Editing it in Jellyfin's user screen will not
> stick.

If it does not work, [Troubleshooting](docs/troubleshooting.md) lists the errors this
plugin actually produces and what each one means. Locked out?
[If something goes wrong](docs/recovery.md).

---

## Documentation

| | |
|---|---|
| [Requirements and versions](docs/requirements.md) | Which build fits which Jellyfin, addresses, reachability |
| [Authentik](docs/authentik.md) · [Keycloak](docs/keycloak.md) · [Authelia](docs/authelia.md) | The work on the provider's side |
| [Configuration reference](docs/configuration.md) | Every field on the plugin page |
| [Linking accounts](docs/linking.md) | The IdP subject, manual and self-service linking |
| [Login button and logout](docs/login-and-logout.md) | The branding button, signing out of the provider too |
| [Phones, tablets and TVs](docs/clients.md) | Quick Connect, and why there is no QR code yet |
| [Troubleshooting](docs/troubleshooting.md) | Errors and what they mean |
| [If something goes wrong](docs/recovery.md) | Lockout, backup and restore, uninstalling |
| [Security notes](docs/security.md) | What is stored, what is validated, what revocation does not do |
| [Glossary](docs/glossary.md) | *Claim*, *scope*, *issuer*, *subject* |

Installing by hand, the test suites and how releases are cut are in
[CONTRIBUTING.md](CONTRIBUTING.md). Design decisions are in [docs/adr/](docs/adr/), the
domain vocabulary in [CONTEXT.md](CONTEXT.md).

---

## Problems, questions, patches

Open an issue at <https://github.com/be-nj/jellyfin-sso/issues>. A Jellyfin log excerpt
around the failed login, your provider's name and the plugin version answer most questions
immediately.

---

## Roadmap

- Active revocation: group changes currently take effect at the next login, not at once.
- QR code login for TVs — needs a change in the Jellyfin client apps, see
  [Phones, tablets and TVs](docs/clients.md).
- Device-code grant (the standard way TVs log in), once Jellyfin's clients support it.

---

## License

MIT, see [LICENSE](LICENSE).
