# Jellyfin SSO Authentication Plugin

Log in to Jellyfin with the account you already have at your identity provider — Authentik,
Authelia or Keycloak — and let the groups there decide who sees which libraries. Replaces
the deprecated [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).

- **Works with:** Jellyfin **12.2.x**. Each plugin build matches one server version.
- **Tested against:** Authentik, Keycloak and Authelia — every release is checked by
  signing in through all three for real, in a browser.
- **Install:** add one URL to Jellyfin's plugin repositories, no file copying.
- **Status:** in active development, running in the author's household.

New to the words in here — *claim*, *scope*, *issuer*, *subject*? There is a
[glossary](#glossary) at the bottom in plain language.

---

## What this changes on your server

All of this can be undone — there is an [uninstall path](#if-something-goes-wrong) that
leaves your accounts and history intact. Read it first anyway, because two of these
surprise people.

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
  excluded, see [Safety](#safety).)
- **Anyone your provider lets through gets a Jellyfin account.** The first SSO login
  creates the account automatically. The gate is on the provider's side: only people who
  may use the Jellyfin *application* there can get in. Without a matching group they get
  an account that sees no libraries.

---

## Requirements

- **Jellyfin 12.2.x.** Check yours under Dashboard → **About**, or open
  `http://<your-jellyfin>/System/Info/Public` in a browser — the `Version` field is the
  number that matters. Each plugin build is compiled against one server version and
  Jellyfin refuses the others, so the catalog shows you only what fits:

  | Jellyfin | Plugin |
  |---|---|
  | 12.2.x | 1.12.2 and newer |
  | 12.1.x | 1.12.0 – 1.12.1 |
  | 12.0.x, 11.x | none yet — this plugin started at 12.1 |
  | 10.11.x and older | none — upgrade Jellyfin first |

  Upgrading Jellyfin itself is outside this plugin, and worth its own evening: take the
  backup from step 1 of the [quick start](#quick-start), change the image tag to
  `jellyfin/jellyfin:12.2`, pull and start, check that your libraries and users are intact
  — Jellyfin's [container documentation](https://jellyfin.org/docs/general/installation/container/)
  covers the mechanics — and only then come back here. Do not upgrade the server and add
  an authentication plugin on the same evening. Older plugin builds stay in the repository, so a
  server that has not been upgraded keeps seeing its own version. **If the catalog shows nothing after you add the repository, your
  Jellyfin is older than any build here** — that is the hiding at work, not a broken URL. The practical consequence: **pin your Jellyfin image tag** instead
  of running `latest`, upgrade deliberately, and check here for a matching plugin build
  first. If a Jellyfin release arrives before the plugin does, your local administrator is
  the way in until it catches up.
- **One address for Jellyfin, used everywhere.** Whatever your household types to watch
  — `https://jellyfin.example.com` or `http://192.168.1.10:8096` — use that same address
  when you open the dashboard to set this up, because the redirect URI is built from it.

  **LAN-only is a supported shape.** If you never reach Jellyfin from outside, put the LAN
  address in and carry on; everything in this README works. What you give up is privacy on
  your own network: access tokens travel unencrypted, so anyone already inside can read
  them. With HTTPS (a reverse proxy such as Caddy, Traefik or nginx) they cannot — worth
  doing eventually, not a prerequisite for tonight.
- **An identity provider you administer:** Authentik, Authelia or Keycloak. Anything that
  speaks OpenID Connect should work, those three are the ones that are tested.
- **Jellyfin itself must be able to reach that provider.** Not your laptop — the Jellyfin
  container. It fetches the provider's keys on its own, and in a split-DNS household
  `https://auth.example.com` often resolves on your laptop and not inside the container.
  Check it before you start:

  ```bash
  # "jellyfin" is the service name in your compose file; docker ps shows yours
  docker compose exec jellyfin curl -sSf \
    https://auth.example.com/.well-known/openid-configuration >/dev/null && echo reachable
  ```

  A wall of JSON or the word `reachable` means yes. "Could not resolve host" means the
  container's DNS cannot see your provider — fix that first, the plugin cannot work around
  it. ("executable file not found" would mean the check itself is wrong for your image,
  not that the provider is unreachable.)
- **Admin access to both**, and the public URL your household uses to reach Jellyfin.

---

## Quick start

Worked example with **Authentik**. Keycloak and Authelia need different work at the
provider in step 4, and Authelia also needs an extra scope in step 5 and Keycloak a
setting that changes what you type in step 6 — all three are written out under
[Identity provider setup](#identity-provider-setup). Budget half an hour the first time.

1. **Back up Jellyfin's configuration — with the server stopped.** Jellyfin keeps live
   databases in there; copying them while it runs gives you a backup that restores into a
   broken server.

   ```bash
   docker compose stop jellyfin
   cp -a /path/you/mounted/at/config ./jellyfin-config-backup   # bind mount
   # named volume instead of a folder?
   docker run --rm -v jellyfin_config:/from -v "$PWD":/to alpine \
     tar czf /to/jellyfin-config-backup.tar.gz -C /from .
   docker compose start jellyfin
   ```

2. **Install the plugin.** Dashboard → **Plugins → Repositories → +**, name it
   `SSO Authentication`, URL:

   ```
   https://raw.githubusercontent.com/be-nj/jellyfin-sso/main/manifest.json
   ```

   Then **Plugins → Catalog → Authentication → SSO Authentication → Install**, and restart
   Jellyfin (`docker compose restart jellyfin`, or the Restart button in the dashboard). An empty catalog means your Jellyfin version has no matching build — see the
   table in [Requirements](#requirements).

3. **Create a half-filled provider in the plugin** so it hands you an id. Dashboard →
   **Plugins → SSO Authentication → Providers → + Add Provider**. Give it a display name
   (`Authentik`), leave everything else alone, **Save** — a provider with nothing but a
   name saves on purpose, and it is created **disabled** so nobody sees a login button
   that goes nowhere yet.

   The card now prints three values: the **Provider ID**, the **Redirect URI** and the
   **Login URL**. The redirect URI is built from the address you are looking at the
   dashboard with, so open the dashboard on the **public URL your household uses**
   (`https://jellyfin.example.com`), not on `http://<server>:8096` — otherwise you will
   register a LAN address at your provider and every outside login will fail. Copy the Redirect URI — that is the one your provider needs, complete,
   with no guessing about which part of the URL is the id. The login URL is what you open
   to test later; the provider id only matters if you build links yourself.

4. **Create the application at Authentik** with that redirect URI — the click-by-click is
   under [Authentik](#authentik), written against Authentik 2026.8. Note the **Client ID**
   and the application's **issuer URL**.

   Leave *Subject mode* at Authentik's default. It produces an unreadable id, which is the
   safe choice: it survives renames and cannot be inherited by a recycled username. The
   cost is that linking an existing Jellyfin account needs one value copied out of the log,
   which step 8 walks through. (The alternative, *Based on the User's username*, makes
   linking trivial and is what the [identity matching](#identity-matching--the-idp-subject)
   section discusses — take it only if usernames in your directory are never reused.)

5. **Back in the plugin, finish the provider:** paste the issuer URL and client id into
   the same card, leave *Groups claim name* at `groups`, *Scopes* empty and *Fetch groups
   from userinfo endpoint* off, tick **Enabled**, **Save**. Saving is enough; the server
   does not need another restart. (Authelia is the exception on two of those: see
   [Authelia](#authelia).)

6. **Create a group in Authentik** (for example `media`) and put the test user from the
   next step in it. Then map it **in the plugin**, under its **Group Mappings** tab:
   **+ Add Mapping**, group name `media`, tick the libraries that group may watch,
   **Save**. Without a mapping people can sign in but see nothing.

   Type the group name exactly as the provider spells it. The field autocompletes, but
   only from groups the plugin has already seen at a login, so the first one is typed
   blind. After the test login in step 7, Jellyfin's log shows what actually arrived:
   `SSO sync for user …: admin=False, libraries=1, groups=media`.

7. **Test with a throwaway account first, not your own.** Create a spare user at
   Authentik, put it in the `media` group from step 6, and leave your own Jellyfin
   administrator account out of all of this for now. Open `https://<your-jellyfin>/sso/oidc/start` in a private window
   and sign in as the spare user. A person without a Jellyfin account gets one created
   here; that is the path you want to see working before you touch anything that matters.

8. **Only then link the accounts you already have.** An existing Jellyfin account is never
   handed to an identity just because the names match: the first SSO login for `ben` is
   refused with *"Username 'ben' already exists"* until an administrator links it. How to
   do that — and where to find the subject when it is not the username — is under
   [Manual linking](#manual-linking-existing-local-accounts). Keep one administrator
   unlinked, with a password, as your way back in.

> **The warning worth repeating:** from now on, that user's library access is rewritten
> from their groups on every single login. Editing it in Jellyfin's user screen will not
> stick.

If it does not work, [Troubleshooting](#troubleshooting) lists the errors this plugin
actually produces and what each one means.

---

## If something goes wrong

**My identity provider is down.** SSO logins fail for as long as it is down — the plugin
has no way to check a password it never sees. Anyone already signed in stays signed in,
and your local administrator can still log in with a password. This is the reason for
keeping one.

**I cannot get in at all.** Log in as the local administrator you kept. If you no longer
have one, Jellyfin's own [password-reset for administrators](https://jellyfin.org/docs/general/administration/troubleshooting/#forgot-password)
works independently of this plugin: it is a file in Jellyfin's config folder, and the
plugin cannot block it.

**Everyone lost their libraries.** The group mapping did not match what the provider
sends. Open the plugin page: the *Group name* field offers the groups it has actually seen
at login, which is the quickest way to spot a typo or a renamed group.

**Almost everyone lost administrator rights.** Not everyone: the last remaining
administrator is never demoted, by design. Log in as them, switch on *Never remove
administrator rights on login* (plugin page, **Group Mappings** tab, under *Safety*), then
fix the mapping.

**I want my backup back.** Whichever way you took in step 1 of the quick start, the
restore is its mirror image.

Bind mount — the folder you copied:

```bash
docker compose stop jellyfin
mv /path/you/mounted/at/config /path/you/mounted/at/config.broken
cp -a ./jellyfin-config-backup /path/you/mounted/at/config
docker compose start jellyfin
```

Named volume — the tarball you made instead:

```bash
docker compose stop jellyfin
docker run --rm -v jellyfin_config:/to -v "$PWD":/from alpine \
  sh -c 'rm -rf /to/..?* /to/.[!.]* /to/* 2>/dev/null; tar xzf /from/jellyfin-config-backup.tar.gz -C /to'
docker compose start jellyfin
```

That returns the server to the evening before, plugin and all — at the cost of anything
watched since. `cp -a` and `tar` both keep ownership; if Jellyfin complains about
permissions afterwards, that is what went wrong.

**I want it gone again.** In Jellyfin: Dashboard → **Users** → each SSO-managed user →
set *Authentication Provider* back to the default and give them a password. Then remove
the plugin. Do it in that order — a user left pointing at a removed provider cannot log
in. Accounts, history and libraries survive all of it.

**Jellyfin updated itself and the plugin stopped working.** A plugin build matches one
server version. After a server upgrade, install the plugin build for the new version from
the catalog. If there is none yet, you have two ways out: sign in as the local
administrator and wait, or put the Jellyfin image tag back to the version that worked
(`image: jellyfin/jellyfin:12.2` rather than `latest`) and pull again. Pinning that tag in
the first place is what keeps this from happening at an inconvenient hour.

**I am coming from the old 9p4 plugin.** This is a different plugin, not an update: it
has its own id, its own configuration and its own account links. Nothing is migrated.
Disable the old plugin first, then set this one up, then link existing accounts (see
[Manual linking](#manual-linking-existing-local-accounts)). Running both at once is asking
for confusion.

---

## Installation

### From the plugin repository (recommended)

In the Jellyfin dashboard, go to **Plugins → Repositories → +** and add:

| Field | Value |
|---|---|
| Repository Name | `SSO Authentication` |
| Repository URL | `https://raw.githubusercontent.com/be-nj/jellyfin-sso/main/manifest.json` |

The plugin then appears under **Plugins → Catalog → Authentication**. Install it and
restart Jellyfin. Updates show up in the catalog like any other plugin.

Jellyfin only offers versions built for the server you are running, so a plugin for a
different Jellyfin release is never installed by accident.

Building and installing by hand is covered in [CONTRIBUTING.md](CONTRIBUTING.md).

---

## Identity provider setup

### Authentik

Authentik's own vocabulary: a **Provider** speaks the protocol, an **Application** is what
users see and what access is granted on.

1. **Directory → Groups → Create**: one group per level of access you want, for example
   `media` and `jellyfin-admins`. Put the test user from the quick start into `media`; add
   yourself later, once linking is done.
2. **Applications → Providers → Create → OAuth2/OpenID Provider**:
   - *Name*: `Jellyfin`.
   - *Authorization flow*: `default-provider-authorization-implicit-consent` (the one
     without a consent screen; with consent works too, users just get an extra click).
   - *Client type*: **Public**. No client secret — the plugin uses PKCE instead.
   - *Client ID*: keep what Authentik generates, you will paste it into the plugin.
   - *Redirect URIs*: the URL from step 3 of the quick start, as **Strict**:
     `https://<your-jellyfin>/sso/oidc/callback/<providerId>`
   - *Signing Key*: pick the self-signed certificate Authentik ships with. **Without a
     signing key Authentik does not sign the token in a way the plugin accepts** and the
     login fails.
   - Leave the scopes at the three defaults (`openid`, `profile`, `email`). Authentik's
     default `profile` mapping already includes the user's groups, so there is nothing to
     build — this is where Authentik is kinder than Keycloak.
3. **Applications → Applications → Create**: name `Jellyfin`, pick the provider you just
   made, and give it a slug, for example `jellyfin`. The slug decides the issuer URL:

   ```
   https://<your-authentik>/application/o/<slug>/
   ```

   That URL, trailing slash and all, goes into the plugin's *Issuer URL* field.
4. **Who may log in** is decided here, not in the plugin: bind a policy to the application
   if you do not want every Authentik user to end up with a Jellyfin account.

**Leave *Subject mode* alone.** Authentik's default gives every person an unreadable,
permanent id, which is the right thing: it survives renames and cannot be inherited by a
recycled username. Linking your existing accounts then costs one value copied out of the
log, which [Manual linking](#manual-linking-existing-local-accounts) walks through
step by step.

The alternative, *Based on the User's username* under *Advanced protocol settings*, makes
linking a matter of typing a name — at the price of breaking every link when someone is
renamed, and handing an old account to anyone who gets a recycled username. Only take it
if usernames in your directory are never reused. This README recommends the default.

### Keycloak

1. Create a client: **Client authentication off** (public), standard flow, PKCE method
   `S256`, redirect URI `https://<your-jellyfin>/sso/oidc/callback/<providerId>`.
2. Add a **Group Membership** mapper to the client, claim name `groups`, and switch
   **Full group path off** — otherwise the claim reads `/media` instead of `media` and your
   group mapping has to spell it that way.
3. Leave the plugin's *Scopes* field **empty**, which asks for `openid profile email`.
   Keycloak has **no** `groups` scope unless someone creates one, and requesting a scope it
   does not know fails the whole authorization with `invalid_scope` — so do not copy
   Authelia's scope line here.
4. The issuer URL is `https://<your-keycloak>/realms/<realm>`.

### Authelia

In Authelia's `configuration.yml`, under `identity_providers.oidc.clients`:

```yaml
- client_id: 'jellyfin'
  client_name: 'Jellyfin'
  public: true                     # no secret; the plugin uses PKCE
  require_pkce: true
  pkce_challenge_method: 'S256'
  authorization_policy: 'one_factor'
  redirect_uris:
    - 'https://<your-jellyfin>/sso/oidc/callback/<providerId>'
  scopes: ['openid', 'profile', 'email', 'groups']
  response_types: ['code']
  grant_types: ['authorization_code']
  token_endpoint_auth_method: 'none'
```

Then, in the plugin's provider card:

- *Issuer URL*: Authelia's own base URL, for example `https://auth.example.com`.
- *Scopes*: `openid profile email groups` — Authelia only reports groups when the
  `groups` scope is asked for, which is why this is the one provider where the field is
  not left empty.
- *Fetch groups from userinfo endpoint*: **on**. Authelia keeps the groups out of the
  login token and answers with them at its userinfo endpoint instead.

Groups themselves come from the `groups` list in Authelia's user file.

All three setups above are what the test suite configures when it starts these providers
in containers, so they are verified rather than remembered.

---

## Plugin configuration

Dashboard → **Plugins → SSO Authentication**:

- **Providers** — one card per identity provider, with: *Display name*, *Enabled*,
  *Issuer URL* (must answer at `<issuer>/.well-known/openid-configuration`), *Client ID*,
  *Groups claim name*, and *Fetch groups from userinfo endpoint*. The card also prints the
  login URL containing that provider's id. With several enabled providers,
  `/sso/oidc/start` asks which one to use; with one it goes straight there.

  *Scopes* is usually left empty, which asks for `openid profile email`. Authelia needs
  `groups` added; Keycloak rejects it.

  A confidential client's secret is the one setting with no field, so that it never lands
  in the plugin's configuration file. The plugin stores a *reference* and reads the value
  at login time — `env:JELLYFIN_SSO_SECRET` for an environment variable you set on the
  Jellyfin container, or `file:/run/secrets/sso` for a file. Setting it means one API call
  with an administrator's token; `ConfidentialSecretRef` is the field on the provider, and
  `ClientType` has to be `1`:

  The token is an API key from Dashboard → **API Keys → +**:

  ```bash
  curl -X POST "https://<your-jellyfin>/sso/admin/config" \
    -H 'Authorization: MediaBrowser Token="<admin token>"' \
    -H 'Content-Type: application/json' \
    -d @config.json
  # config.json is what GET /sso/admin/config returns, with two fields changed inside the
  # provider's entry in the "Providers" array: "ClientType": 1 and
  # "ConfidentialSecretRef": "env:JELLYFIN_SSO_SECRET".
  ```

  Most setups never need this: a public client with PKCE is the recommended shape and the
  default.
- **Sessions**:
  - **Sign out of the identity provider too** — see [Logout](#logout).
  - **Let users link their own account** — see
    [Self-service linking](#self-service-linking).
- **Group Mappings** — one card per group:
  - Group name (autocompletes from groups seen at login).
  - Libraries the group may access (checkboxes).
  - **Admin (all libraries)** — grants Jellyfin admin and full library access.
  - **Live TV access**, **Live TV management**, **Downloads**.
  - **Max parental rating** — empty means no limit.
- **Account Links** (its own tab) — the manual links between an identity and a Jellyfin
  account, plus the form to add one. See
  [Manual linking](#manual-linking-existing-local-accounts).
### Safety

On the same tab as the group mappings:

- **Never remove administrator rights on login** — keeps existing administrators even when
  no admin group matches. Library access is still taken from the groups.
- **Excluded from sync** — a checkbox for every Jellyfin user. Tick the ones whose
  permissions you set by hand, starting with your local administrator. They can still log
  in via SSO; their policy is never touched.

> Mappings are applied **declaratively on every login**. Changing a user's library access
> in Jellyfin's own user UI will be overwritten on their next login.

A user in several groups gets the union: every library of every matched group, each
permission if any group grants it, and the **most permissive** parental rating — a group
without a limit lifts the limit.

> **Lockout guard:** the last remaining administrator is never demoted, whatever the
> groups say. A mistyped admin group therefore costs you a warning in the log, not access
> to your own server.

### Self-service linking

Signed-in users can link their own account at **`/sso/link`**: the page reads the session
the web client stored, starts the OIDC flow for that user, and attaches the identity to
their account. An identity that is already linked to a different Jellyfin account is
refused — only an administrator can move a link.

Unlinking has no button for users yet. **An administrator removes any link under the
Account Links tab**, which is the practical route. There is an endpoint for a user's own
link, `DELETE /sso/link/self` with their Jellyfin token, but nothing in the web client
calls it — treat it as something you do for your household, not something they do.

Linking grants nothing by itself. Library access and the admin flag are applied on the
next SSO login, from the groups, as always.

### Manual linking (existing local accounts)

New users are linked automatically. Use the **Account Links** tab only to connect an
account that existed before SSO:

- **Provider** + **Jellyfin user** are dropdowns.
- **Subject** is the identity's `sub` at the provider. With Authentik's *Subject mode:
  username* it is simply the username.
- With any other subject mode you cannot know the value in advance, so let the person try
  to log in once. The attempt is refused and Jellyfin's log (Dashboard → **Logs**, or
  `docker logs jellyfin`) carries the value you need:

  ```
  [WRN] Jellyfin.Plugin.Sso.Provisioning.UserProvisioner: SSO JIT blocked: username 'ben'
  already exists. To link manually: ProviderId=ab12… Sub=f81d4fae-7dec-11d0-a765-… JellyfinUserId=…
  ```

  Copy the `Sub=` value into the form. Repeat per person; for a household moving over from
  another plugin, plan an evening for it.

---

## Logout

Jellyfin's own logout drops its token but leaves the identity provider session alone, so
the next login goes through without asking for credentials. With **Sign out of the
identity provider too** enabled, sending the browser to **`/sso/oidc/logout`** after
Jellyfin's logout ends the provider session as well and comes back to the web client.

**On its own, the switch changes nothing you can see.** It only decides what
`/sso/oidc/logout` does when something sends the browser there. A server plugin cannot
change Jellyfin's own logout button, so the last step needs a snippet in the browser,
which means the **JavaScript Injector** plugin — a separate community plugin, installed
from its own repository the same way this one was. With it:

```js
// Sends the browser on to the provider's logout once Jellyfin has signed the user out.
document.addEventListener('click', function (event) {
  if (event.target.closest('[data-action="logout"], .btnLogout')) {
    setTimeout(function () { window.location.href = '/sso/oidc/logout'; }, 500);
  }
});
```

Without the injector you can still visit `/sso/oidc/logout` yourself; the switch alone
changes nothing about Jellyfin's button.

If the provider advertises no `end_session_endpoint`, or cannot be reached, the user still
lands back in Jellyfin — a broken provider must never block a logout.

---

## Login button

- **Branding (built in):** the plugin adds one button per enabled provider to Jellyfin's
  *Login Disclaimer* whenever the configuration is saved. The buttons sit between
  `<!-- sso-login-buttons:start -->` and `:end`, and only that part is ever rewritten —
  anything else you put in the disclaimer stays. Disabling every provider removes the
  block and leaves your text alone.

  If Jellyfin is served under a sub-path, the links pick it up from the server's network
  configuration, so `/jellyfin/sso/oidc/start/<providerId>` works as well.
- **JavaScript Injector (a nicer button):** if you run that community plugin, this adds a
  button into the login form itself rather than below it:

  ```js
  // Puts an SSO button under Jellyfin's login form.
  document.addEventListener('DOMContentLoaded', function () {
    const form = document.querySelector('.loginForm, form.login');
    if (!form || document.getElementById('ssoButton')) return;
    const button = document.createElement('a');
    button.id = 'ssoButton';
    button.className = 'raised block emby-button';
    button.href = '/sso/oidc/start';   // asks which provider, if there are several
    button.textContent = 'Sign in with SSO';
    form.appendChild(button);
  });
  ```

---

## Phones, tablets and TVs

The Jellyfin apps cannot show a provider's login page, so they use Jellyfin's **Quick
Connect**: the app shows a six-character code, and an already signed-in browser approves
it.

With this plugin the browser does not even have to be signed in:

1. Enable Quick Connect in Jellyfin: Dashboard → **General → Quick Connect**.
2. In the app, choose *Quick Connect* and note the code.
3. On a phone or laptop, open
   `https://<your-jellyfin>/sso/oidc/start/qc?code=<the-code>`. You sign in at your
   provider, and the app logs in by itself. With several providers enabled this asks which
   one first, carrying the code along.

Once approved, the app holds a normal Jellyfin session and stays signed in like any other
login — this is a one-time dance per device, not a weekly ritual.

Yes, that URL is a mouthful, and it has to be done once per device and household member.
Showing the code as a **QR code** on the TV would fix it, and that has to happen in the
Jellyfin client apps themselves — a server plugin cannot draw on their screens. It is on
the [roadmap](#roadmap) as work to be done upstream, not something this plugin can solve
alone.

---

## Identity matching & the IdP "subject"

The plugin keys each Jellyfin user to an OIDC identity by `(provider, sub)` — **never by
username** (that is the source of the old plugin's admin-takeover bug). A user is matched
only via an explicit link; an unknown `sub` always becomes a new account.

### Authentik: choose a Subject mode

In **Applications → Providers → (your provider) → Advanced protocol settings → Subject
mode**, Authentik decides what `sub` contains:

| Subject mode | `sub` value | Notes |
|---|---|---|
| **Based on the User's hashed ID (default)** | opaque SHA256 hash | **Recommended.** Stable and safe; the value is only learnable from a login, which [Manual linking](#manual-linking-existing-local-accounts) covers. |
| Based on the User's username | the username | Easy to link by hand, and brittle: see the trade-offs below. |
| Based on the User's UUID | the user UUID | Stable; only readable via the API (`/api/v3/core/users/`). |

The username mode trades safety for convenience, which is why the setup guide recommends
leaving Authentik's default in place:

- **Renaming someone at the provider breaks their link**, because the `sub` changes with
  the name. They have to be linked again by hand.
- **Reusing a username is dangerous**: delete `ben`, create a different person as `ben`
  later, and they inherit the old account's link — including whatever it could see. Only
  pick this mode if usernames in your directory are never recycled.
- In return the subject *is* the username, so linking an existing account is typing a name
  instead of hunting for a hash.

If you prefer the stable choice, keep the hashed default and collect each existing user's
`sub` once from the rejected-collision log line (see
[Manual linking](#manual-linking-existing-local-accounts)). People who are new to Jellyfin
never need this: their account is created and linked at their first login.

---

## Security notes

In plain language first, for the two things that affect how you run this:

- **Taking someone's access away is not immediate.** Removing them from a group at the
  provider changes nothing until their next login, and Jellyfin sessions last a long time.
  To cut someone off now, also end their session in Jellyfin: Dashboard → **Devices**, or
  delete the account.
- **No password is stored anywhere by this plugin.** It never sees one. There is also no
  client secret to leak, because the plugin authenticates by proving it started the login
  (that is what PKCE does).

The details, for readers who want them:

- **PKCE public client** by default — no `client_secret` stored anywhere. A confidential
  client is supported but has no field on the configuration page: its secret is set
  through the admin API as `env:VAR` or `file:/path`, so the plugin reads it from the
  environment or a file at login time and it never lands in the plugin's configuration
  file. If you do not know you need this, you do not need it.
- Full ID-token validation: JWKS signature, `iss`, `aud`, `exp`/`iat`, `nonce`; `state`
  for CSRF. Every one of those rejections has its own test.
- The one-time secret that completes a login is random, single-use, expires in 90 seconds
  and never leaves memory.
- Identity is matched by `(provider, sub)`, never by username — that is what stops a
  stranger with the right username from inheriting an account.

---

## Troubleshooting

| What you see | What it means |
|---|---|
| Plugin shows **NotSupported** in the dashboard | The plugin was built for a different Jellyfin version. Install the build whose `targetAbi` matches your server. |
| `invalid_scope` from Keycloak | Keycloak has no `groups` scope unless you create one. Empty the provider's *Scopes* field, which asks for `openid profile email`. |
| Login works, but the user sees **no libraries** | No group mapping matched. Check the group name in the mapping against what the provider sends; the names the plugin has seen are offered as autocomplete. |
| New accounts are named like `01HQ3M…` | The provider sends no `preferred_username`. The plugin falls back to the userinfo endpoint; if that is empty too, add the claim at the provider. |
| `SSO login failed: crypto.randomUUID is not a function` | A plugin build older than 1.12.1 on a server reached over plain HTTP. Update the plugin; current builds work over HTTP, which is a privacy question, not a functional one. |
| `redirect_uri` mismatch at the provider | The provider's registered URI must match exactly, including scheme, host, port and the `providerId` at the end. |
| Everyone lost administrator rights after a group rename | Turn on **Never remove administrator rights on login** while you fix the mapping. The last remaining administrator is never demoted. |
| Authentik sends you back with an error right after signing in | The provider has no **Signing Key** selected. Authentik then issues a token the plugin cannot verify. |
| The plugin disappeared or shows **NotSupported** after a Jellyfin update | The build you have was made for the previous server version. Install the matching build from the catalog; log in as your local administrator meanwhile. |
| Nothing happens on the login page | The login button is injected into Jellyfin's branding. Check **Dashboard → General → Branding → Login Disclaimer**, or open `/sso/oidc/start` directly. |

Jellyfin's log is the first place to look: every rejection the plugin makes is logged with
the provider and the reason.

---

## Glossary

Only the words this README cannot avoid.

| Word | In plain language |
|---|---|
| **OIDC / OpenID Connect** | The protocol behind "log in with …". OAuth2 is the older standard it builds on. |
| **Identity provider (IdP)** | The service that holds the accounts and checks passwords: your Authentik, Authelia or Keycloak. |
| **Claim** | A single fact the provider states about a person — their username, their email, their groups. |
| **Scope** | A bundle of claims the application asks for. `profile` usually carries the name, `email` the address. |
| **Issuer URL** | The provider's address for one application. Everything else is discovered from it; it has to answer at `<issuer>/.well-known/openid-configuration` **from inside the Jellyfin container**, not only in your browser. |
| **Subject (`sub`)** | The provider's id for a person. Often an unreadable string; with some settings it is the username. The plugin links accounts by it, so an account survives a rename **as long as the subject is not the username** — see [Identity matching](#identity-matching--the-idp-subject). |
| **Client ID** | The name the provider knows your Jellyfin by. Not a secret. |
| **PKCE** | The mechanism that lets an application prove it started a login without holding a password. It is why no client secret is needed. |
| **Public client** | An application that holds no secret — anything running in a browser. The opposite is a confidential client. |
| **JIT** | "Just in time": the Jellyfin account is created at the first successful login rather than beforehand. The phrase shows up in the log line about a blocked login. |
| **Declarative** | The group mapping is the whole truth: on every login it is written over whatever was set before, including by hand. |
| **Redirect URI** | The address the provider sends the browser back to after the login. It must be registered there, character for character. |
| **Userinfo endpoint** | A second address at the provider that answers with the same facts about a person. Some providers keep the groups only there. |
| **targetAbi** | The Jellyfin version a plugin build was made for. Jellyfin compares it with its own version and hides builds that do not match, which is why the catalog sometimes shows nothing. |

---

## Problems, questions, patches

Open an issue at <https://github.com/be-nj/jellyfin-sso/issues>. A Jellyfin log excerpt
around the failed login, your provider's name and the plugin version answer most questions
immediately.

---

## Contributing

Build instructions, the test suites and how releases are cut live in
[CONTRIBUTING.md](CONTRIBUTING.md). Design decisions are in [docs/adr/](docs/adr/), the
domain vocabulary in [CONTEXT.md](CONTEXT.md).

---

## Roadmap

- Active revocation: group changes currently take effect at the next login, not at once.
- QR code login for TVs — needs a change in the Jellyfin client apps, see
  [Phones, tablets and TVs](#phones-tablets-and-tvs).
- Device-code grant (the standard way TVs log in), once Jellyfin's clients support it.

---

## License

MIT, see [LICENSE](LICENSE).
