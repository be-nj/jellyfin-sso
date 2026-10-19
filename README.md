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
  excluded, see [Safety](#plugin-configuration).)
- **Anyone your provider lets through gets a Jellyfin account.** The first SSO login
  creates the account automatically. The gate is on the provider's side: only people who
  may use the Jellyfin *application* there can get in. Without a matching group they get
  an account that sees no libraries.

---

## Requirements

- **Jellyfin 12.2.x.** The plugin is built against one server version and Jellyfin refuses
  builds that do not match; the catalog only offers you the right one.
- **HTTPS in front of Jellyfin.** The plugin works over plain `http://` on a LAN address,
  but then access tokens travel the network in the clear, and browsers restrict APIs the
  login page uses — one of which cost an earlier version its login. A reverse proxy with
  a certificate (Caddy, Traefik, nginx) is the right answer for anything beyond a test.
- **An identity provider you administer:** Authentik, Authelia or Keycloak. Anything that
  speaks OpenID Connect should work, those three are the ones that are tested.
- **Admin access to both**, and the public URL your household uses to reach Jellyfin.

---

## Quick start

Worked example with **Authentik**; Keycloak and Authelia differ only in step 3, see
[Identity provider setup](#identity-provider-setup). Budget half an hour the first time.

1. **Back up Jellyfin's config folder.** The one with `data/`, `plugins/` and `config/` in
   it. Everything below is undoable, but a copy costs nothing.

2. **Install the plugin.** Dashboard → **Plugins → Repositories → +**, name it
   `SSO Authentication`, URL:

   ```
   https://raw.githubusercontent.com/be-nj/jellyfin-sso/main/manifest.json
   ```

   Then **Plugins → Catalog → Authentication → SSO Authentication → Install**, and restart
   Jellyfin.

3. **Create a half-filled provider in the plugin** so it hands you an id. Dashboard →
   **Plugins → SSO Authentication → Providers → + Add Provider**. Give it a display name
   (`Authentik`), leave the rest empty for now, **Save**. The card now shows a login URL
   ending in a long id — that id is your `providerId`, and your redirect URI is:

   ```
   https://<your-jellyfin>/sso/oidc/callback/<providerId>
   ```

4. **Create the application at Authentik** with that redirect URI — the click-by-click is
   under [Authentik](#authentik). Note the **Client ID** and the application's **issuer
   URL**.

5. **Finish the provider in the plugin:** paste the issuer URL and client id into the same
   card, leave *Groups claim name* at `groups`, **Save**.

6. **Create a group in Authentik** (for example `media`), put yourself in it, then map it:
   **Group Mappings → + Add Mapping**, group name `media`, tick the libraries that group
   may watch, **Save**. Without a mapping people can sign in but see nothing.

7. **If you already have a Jellyfin account, link it now** — otherwise your first SSO
   login is refused with *"Username 'ben' already exists"*. That refusal is deliberate: an
   account is never handed to an identity just because the names match. See
   [Manual linking](#manual-linking-existing-local-accounts); with Authentik's *Subject
   mode: username* it is simply your username.

8. **Try it** in a private browser window: open `https://<your-jellyfin>/sso/oidc/start`.
   You should land at Authentik, sign in, and come back signed in to Jellyfin. For anyone
   without a Jellyfin account yet, this first login creates one.

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

**Everyone lost administrator rights.** They cannot all have: the last remaining
administrator is never demoted, by design. Log in as them, switch on *Never remove
administrator rights on login*, then fix the mapping.

**I want it gone again.** In Jellyfin: Dashboard → **Users** → each SSO-managed user →
set *Authentication Provider* back to the default and give them a password. Then remove
the plugin. Do it in that order — a user left pointing at a removed provider cannot log
in. Accounts, history and libraries survive all of it.

**Jellyfin updated itself and the plugin stopped working.** A plugin build matches one
server version. After a server upgrade, install the plugin build for the new version from
the catalog. Until you do, SSO logins may fail — which is, once more, why the local
administrator exists.

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
   `media` and `jellyfin-admins`. Add your own user to one of them.
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

Optional, and only if you intend to link existing Jellyfin accounts by hand: under the
provider's *Advanced protocol settings* you can set **Subject mode** to *Based on the
User's username*, which makes manual linking much easier. The trade-offs are in
[Identity matching](#identity-matching--the-idp-subject).

### Keycloak

1. Create a client: **Client authentication off** (public), standard flow, PKCE method
   `S256`, redirect URI `https://<your-jellyfin>/sso/oidc/callback/<providerId>`.
2. Add a **Group Membership** mapper to the client, claim name `groups`, and switch
   **Full group path off** — otherwise the claim reads `/media` instead of `media` and your
   group mapping has to spell it that way.
3. Leave the plugin's scopes at the default `openid profile email`. Keycloak has **no**
   `groups` scope unless someone creates one, and requesting a scope it does not know fails
   the whole authorization with `invalid_scope`.
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

Groups come from the `groups` list in Authelia's user file. Authelia keeps those claims
out of the login token, so in the plugin switch on **Fetch groups from userinfo endpoint**
for this provider. The issuer URL is Authelia's own base URL.

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

  Two settings have no field on this page because they are rare: the requested scopes
  (always `openid profile email` unless changed through the API) and the secret of a
  confidential client (`env:VAR` or `file:/path`, so the secret stays out of the plugin's
  configuration file).
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
- **Safety**:
  - **Never remove administrator rights on login** — keeps existing admins even when no
    admin group matches. Library access stays declarative.
  - **Excluded from sync** — users whose permissions you manage by hand. They can still
    log in via SSO; their policy is never touched.

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
refused — only an administrator can move a link. Users can unlink themselves again with
`DELETE /sso/link/self`.

Linking grants nothing by itself. Library access and the admin flag are applied on the
next SSO login, from the groups, as always.

### Manual linking (existing local accounts)

New users are linked automatically. Use **Manual Account Links** only to connect an
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

A server plugin cannot change what Jellyfin's own logout button does, so this last step
needs a snippet in the browser. If you have the **JavaScript Injector** plugin (a separate
community plugin that injects a script into Jellyfin's web client), add:

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
   provider, and the app logs in by itself.

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
| Based on the User's hashed ID (default) | opaque SHA256 hash | Stable, but invisible in the UI — only learnable from a login. |
| **Based on the User's username** | the username | Easy to link by hand, and the mode this README's examples assume. |
| Based on the User's UUID | the user UUID | Stable; only readable via the API (`/api/v3/core/users/`). |

Choosing the username mode trades safety for convenience:

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

- **PKCE public client** by default — no `client_secret` stored. For a confidential client,
  supply the secret via `env:VAR` or `file:/path`, never written into the plugin's own
  configuration file.
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
| `invalid_scope` from Keycloak | Keycloak has no `groups` scope unless you create one. Leave the plugin's scopes at the default `openid profile email`. |
| Login works, but the user sees **no libraries** | No group mapping matched. Check the group name in the mapping against what the provider sends; the names the plugin has seen are offered as autocomplete. |
| New accounts are named like `01HQ3M…` | The provider sends no `preferred_username`. The plugin falls back to the userinfo endpoint; if that is empty too, add the claim at the provider. |
| `SSO login failed: crypto.randomUUID is not a function` | A plugin build older than 1.12.1 on a server reached over plain HTTP. Update the plugin; current builds handle it. |
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
| **Issuer URL** | The provider's address for one application. Everything else is discovered from it; it has to answer at `<issuer>/.well-known/openid-configuration`, which you can simply open in a browser to check. |
| **Subject (`sub`)** | The provider's id for a person. Often an unreadable string, sometimes the username. The plugin remembers it so an account survives a rename. |
| **Client ID** | The name the provider knows your Jellyfin by. Not a secret. |
| **PKCE** | The mechanism that lets an application prove it started a login without holding a password. It is why no client secret is needed. |
| **Public client** | An application that holds no secret — anything running in a browser. The opposite is a confidential client. |
| **Declarative** | The group mapping is the whole truth: on every login it is written over whatever was set before, including by hand. |
| **Redirect URI** | The address the provider sends the browser back to after the login. It must be registered there, character for character. |
| **Userinfo endpoint** | A second address at the provider that answers with the same facts about a person. Some providers keep the groups only there. |
| **targetAbi** | The Jellyfin version a plugin build was made for. Jellyfin compares it with its own version and hides builds that do not match, which is why the catalog sometimes shows nothing. |

---

## Problems, questions, patches

Open an issue at <https://github.com/be-nj/jellyfin-sso/issues>. A Jellyfin log excerpt
around the failed login, your provider's name and the plugin version answer most questions
immediately.

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

## License

MIT, see [LICENSE](LICENSE).
