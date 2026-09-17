# Jellyfin SSO Authentication Plugin

OIDC/OAuth2 single sign-on for Jellyfin with **group-based library access**, built for
**Authentik** and **Authelia**. Replaces the deprecated [9p4/jellyfin-plugin-sso](https://github.com/9p4/jellyfin-plugin-sso).

Users log in through your identity provider; their library access is derived from their
IdP groups and re-applied on every login. Admins configure the group → library mapping
in a native Jellyfin dashboard page.

- **Target:** Jellyfin **12.1.x**, `net10.0`. `Jellyfin.Controller` must match the server version exactly.
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
scripts/package.sh 1.1.0.0

# Build a plain publish output (includes the IdentityModel deps)
make publish

# Or build + deploy to the server in one step (see Makefile for host vars)
make deploy
```

The plugin needs its own DLL **plus** these non-bundled dependencies copied into the
plugin folder (the `Makefile`'s `deploy` target handles this):

```
Jellyfin.Plugin.Sso.dll
IdentityModel.dll
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

- **Enable SSO login**
- **Issuer URL (Authority)** — e.g. `https://auth.example.com/application/o/jellyfin`
  (must serve `/.well-known/openid-configuration`).
- **Client ID**
- **Groups claim name** — default `groups` (works for Authentik & Authelia).
- **Fetch groups from userinfo** — only if your IdP omits groups from the ID token.
- **Group → Library Mappings** — one card per group:
  - Group name (autocompletes from groups seen at login).
  - Libraries the group may access (checkboxes).
  - **Admin (all libraries)** — grants Jellyfin admin and full library access.

> Mappings are applied **declaratively on every login**. Changing a user's library access
> in Jellyfin's own user UI will be overwritten on their next login.

### Manual linking (existing local accounts)

New users are linked automatically. Use **Manual Account Links** only to connect an
account that existed before SSO:

- **Provider** + **Jellyfin user** are dropdowns.
- **Subject** is the user's `sub`. With username subject mode, just enter the username.
  Otherwise, have the user attempt one SSO login — the rejected-collision log line prints
  `Sub=…`.

---

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

Tests live under `tests/` with outputs in `tests/runs/` (gitignored).

### Cutting a release

```bash
git tag v1.1.0 && git push origin v1.1.0
```

The `Release` workflow builds the ZIP, attaches it to a GitHub release and appends the
version to `manifest.json` on `main`, which is what servers poll. The `targetAbi` written
into the manifest is derived from the `Jellyfin.Controller` reference in the csproj, so a
Jellyfin bump automatically applies to the next release.

Tags must be four-part or three-part (`v1.1.0` becomes `1.1.0.0`) and must point at a
commit on `main`.

---

## Roadmap

- Phase 2 group-derived permissions: Live TV, downloads, parental rating.
- Active revocation (background re-sync).
- RP-initiated logout.
- Per-user "exclude from sync" opt-out.
- Multiple simultaneously-enabled providers + provider picker.

## License

MIT, see [LICENSE](LICENSE).
