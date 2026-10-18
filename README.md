# Jellyfin SSO Authentication Plugin

OIDC/OAuth2 single sign-on for Jellyfin with **group-based library access**, built for
**Authentik** and **Authelia**. Replaces the deprecated [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).

Users log in through your identity provider; their library access is derived from their
IdP groups and re-applied on every login. Admins configure the group → library mapping
in a native Jellyfin dashboard page.

- **Target:** Jellyfin **12.1.x**, `net10.0`. `Jellyfin.Controller` must match the server version exactly.
- **Install:** add the [plugin repository](#from-the-plugin-repository-recommended) to your
  Jellyfin dashboard — no manual DLL copying.
- **Versioning:** the `1.12.x` line targets Jellyfin 12; compatibility itself is enforced
  by `targetAbi` in the manifest.
- **Status:** in active development.

---

## Features

- OIDC Authorization Code flow with **PKCE** (public client by default — no secret to store).
- **JIT provisioning** — first SSO login auto-creates the Jellyfin user.
- **Declarative group → library mapping** — IdP groups are the single source of truth;
  per-user access is overwritten on every login.
- **Admin status from groups** — a group can grant Jellyfin admin.
- **Native apps via Quick Connect.**
- **Manual account linking** for pre-existing local accounts.
- Native-looking config UI in the Jellyfin dashboard.
- **Several providers side by side** with a picker at a stable login URL.
- **Self-service linking** — users attach their own account; identities already linked
  elsewhere are refused.
- **RP-initiated logout** — optionally end the provider session together with Jellyfin's.
- Login button on the web login page via Jellyfin branding / JavaScript Injector.

---

## How it works

Jellyfin only issues access tokens through its native username+password path. This plugin
bridges OIDC to that path:

1. Browser hits `/sso/oidc/start` → redirected to the IdP (PKCE + `state` + `nonce`).
2. IdP redirects back to `/sso/oidc/callback/{providerId}` → the plugin validates the ID
   token (signature, issuer, audience, expiry, nonce), extracts `sub` + groups.
3. The user is resolved (existing link → that user; unknown `sub` → JIT-created user).
4. Library access + admin flag are synced from the user's groups.
5. The plugin mints a short-lived **ephemeral secret** and the browser completes login via
   Jellyfin's native auth path; an `IAuthenticationProvider` validates the secret so
   **Jellyfin itself issues the token**.

See [docs/adr/](docs/adr/) for the architectural decisions and [CONTEXT.md](CONTEXT.md)
for the domain glossary.

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
| **Based on the User's username** | the username | Easy to link manually; **this repo's deployment uses this.** |
| Based on the User's UUID | the user UUID | Stable; only readable via the API (`/api/v3/core/users/`). |

**This deployment uses "Based on the User's username".** Trade-offs of that choice:

- **Renaming a user in the IdP breaks their link** (the `sub` changes) → they must be
  re-linked. Acceptable here because usernames are LLDAP-managed and stable.
- **Username recycling is a risk**: deleting user `ben` and later creating a *different*
  person as `ben` would let them inherit the old account's link. Mitigated by the fact
  that only the admin can provision usernames (LLDAP-backed); don't recycle usernames.
- In return: `sub` == Jellyfin username == Authentik username, so manual linking is
  trivial and no UUID/hash hunting is needed.

If you prefer the stable choice, keep the hashed default and collect each existing user's
`sub` once from the rejected-collision log line (see *Manual linking* below). New users
never need this — they are linked automatically by JIT.

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

Jellyfin only offers versions whose `targetAbi` matches the running server, so a plugin
build for a different Jellyfin release is never installed by accident.

### Manual / development install

Jellyfin runs in Docker; the plugin folder lives in the config volume.

```bash
# Build a ZIP identical to the released one (dist/jellyfin-sso_<version>.zip)
scripts/package.sh 1.12.0.0

# Build a plain publish output (includes the IdentityModel deps)
make publish

# Or build + deploy to the server in one step (see Makefile for host vars)
make deploy
```

The plugin needs its own DLL **plus** these non-bundled dependencies copied into the
plugin folder (the `Makefile`'s `deploy` target handles this):

```
Jellyfin.Plugin.Sso.dll
Duende.IdentityModel.dll
Microsoft.IdentityModel.Abstractions.dll
Microsoft.IdentityModel.JsonWebTokens.dll
Microsoft.IdentityModel.Logging.dll
Microsoft.IdentityModel.Protocols.dll
Microsoft.IdentityModel.Protocols.OpenIdConnect.dll
Microsoft.IdentityModel.Tokens.dll
System.IdentityModel.Tokens.Jwt.dll
```

> If the DLL is dropped without its dependencies, Jellyfin logs
> `Failed to load assembly … IdentityModel …` and disables (and deletes) the plugin folder
> on the next restart. Always deploy all DLLs together.

After deploy, restart Jellyfin and confirm `Loaded plugin: SSO Authentication` in the logs.

---

## Identity provider setup

### Authentik

1. Create an **OAuth2/OIDC Provider**:
   - Client type: **Public** (PKCE; no client secret).
   - Redirect URI: `https://<your-jellyfin>/sso/oidc/callback/<providerId>`
     (or a wildcard `…/sso/oidc/callback/*`). The `providerId` is shown in the plugin
     config after the first save.
   - Scopes: `openid profile email`, plus a scope that emits the **`groups`** claim.
   - Subject mode: **Based on the User's username** (see above).
2. Create an **Application** bound to that provider.

### Authelia

Configure an OIDC client with PKCE (public client), the same redirect URI, and ensure the
`groups` claim is emitted.

---

## Plugin configuration

Dashboard → **Plugins → SSO Authentication**:

- **Providers** — one card per identity provider, each with its own display name, issuer
  URL (must serve `/.well-known/openid-configuration`), client id, groups claim name and
  an **Enabled** switch. With several enabled providers, `/sso/oidc/start` asks the user
  which one to use; with one it goes straight there.
- **Sessions**:
  - **Sign out of the identity provider too** — see *Logout* below.
  - **Let users link their own account** — see *Self-service linking* below.
- **Group → Library Mappings** — one card per group:
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
- **Subject** is the user's `sub`. With username subject mode, just enter the username.
  Otherwise, have the user attempt one SSO login — the rejected-collision log line prints
  `Sub=…`.

---

## Logout

Jellyfin's own logout drops its token but leaves the identity provider session alone, so
the next login goes through without asking for credentials. With **Sign out of the
identity provider too** enabled, sending the browser to **`/sso/oidc/logout`** after
Jellyfin's logout ends the provider session as well and comes back to the web client.

The plugin cannot hook Jellyfin's logout button from the server, so wire it up in the
JavaScript Injector alongside the login button:

```js
// After Jellyfin's own logout has completed.
window.location.href = '/sso/oidc/logout';
```

If the provider advertises no `end_session_endpoint`, or cannot be reached, the user still
lands back in Jellyfin — a broken provider must never block a logout.

## Login button

- **Branding (built in):** the plugin sets Jellyfin's *Login Disclaimer* to an SSO link
  when a provider is enabled.
- **JavaScript Injector (nicer button):** point its script at the stable login URL:

  ```js
  const SSO_AUTH_URL = 'https://<your-jellyfin>/sso/oidc/start';
  const PROVIDER = 'authentik';
  ```

Native apps: `/sso/oidc/start/qc?code=<quickConnectCode>` (requires Quick Connect enabled
server-side).

---

## Security notes

- **PKCE public client** by default — no `client_secret` stored. For a confidential client,
  supply the secret via `env:VAR` or `file:/path` (never written into plugin XML).
- Full ID-token validation: JWKS signature, `iss`, `aud`, `exp`/`iat`, `nonce`; `state`
  for CSRF.
- Ephemeral secrets are random, single-use, short-TTL, in-memory only.
- Identity is matched by `(provider, sub)`, never by username.
- Revocation latency: group changes take effect on the user's **next login** (no active
  re-sync in this version).

---

## Development

- Source: `Jellyfin.Plugin.Sso/`
- Config UI: `Configuration/configPage.html` + `configPage.js` (ES module loaded via
  `data-controller="__plugin/SSO Authentication.js"`).
- `make build` / `make publish` / `make deploy`.

### Tests

```bash
make test        # dotnet test tests/Jellyfin.Plugin.Sso.Tests -c Release --logger trx
```

Tests live under `tests/`; everything a run produces (binaries, obj, TRX results) goes to
`tests/runs/`, which is gitignored. They cover the security-relevant paths: ID token
validation (signature, issuer, audience, lifetime, nonce, `alg=none`), the ephemeral
secret bridge (single use, no empty password, no cross-user use), PKCE state handling, and
the provisioning and permission rules (never adopt an account by username, revoke access
when groups disappear).

### End-to-end tests

```bash
make e2e
```

A throwaway Jellyfin container is started from the image matching the pinned
`Jellyfin.Controller` version, with the **release ZIP** installed as a plugin — so the
artifact users download is the one under test. Alongside it runs a fake OIDC provider (a
small ASP.NET host in the test process, serving HTTPS with a self-signed certificate the
container is made to trust), and the browser hop is driven by Playwright.

Covered there: the plugin loads in a real server, the redirect and PKCE code exchange, JIT
provisioning, group-to-library permissions applied through Jellyfin's own user policy,
Jellyfin minting an access token for the ephemeral secret, the username-collision refusal,
and the localStorage handoff into the web client. The admin page is driven too — it is
plain JavaScript inside Jellyfin's dashboard, where only a browser notices a typo — so a
mapping edited in the UI is read back through the API. Screenshots and page dumps of
failed browser runs land in `tests/runs/playwright/`.

`make e2e` runs inside the .NET SDK image (no local SDK needed) and talks to the host's
Docker daemon; it needs Docker and about 1 GB for the Jellyfin image.

Both suites run on every push and pull request, and again before a release is published.

### Cutting a release

```bash
git tag v1.12.1 && git push origin v1.12.1
```

The `Release` workflow builds the ZIP, attaches it to a GitHub release and appends the
version to `manifest.json` on `main`, which is what servers poll. The `targetAbi` written
into the manifest is derived from the `Jellyfin.Controller` reference in the csproj, so a
Jellyfin bump automatically applies to the next release.

Tags must be four-part or three-part (`v1.12.1` becomes `1.12.1.0`) and must point at a
commit on `main`.

---

## Roadmap

- Active revocation (background re-sync).
- QR code login for TV and other keyboard-hostile clients (see below).
- Device-code grant (RFC 8628) once Jellyfin clients speak it.

## License

MIT, see [LICENSE](LICENSE).
