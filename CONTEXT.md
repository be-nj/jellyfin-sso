# Jellyfin SSO Plugin

A Jellyfin plugin that authenticates users against an external OIDC/OAuth2 provider
(Authentik, Authelia) and derives per-library access from the user's IdP groups,
configurable by an admin in a dashboard UI.

## Language

**IdP** (Identity Provider):
The external OIDC/OAuth2 server that authenticates the user. Targets: Authentik, Authelia.
_Avoid_: "SSO server", "OAuth server".

**Session Bridge**:
The mechanism that turns a validated OIDC login into a real Jellyfin access token.
Jellyfin only issues tokens via its native username+password path, so the plugin
bridges by implementing `IAuthenticationProvider` and feeding it an ephemeral secret.
_Avoid_: "token exchange" (that term belongs to the OIDC code-for-token step).

**Ephemeral Secret**:
A short-lived, single-use credential the plugin generates after a successful OIDC
login. The client presents it on Jellyfin's native auth path; the plugin's
`IAuthenticationProvider` validates it and lets Jellyfin mint the token.
Stored **in-memory only** (expires seconds after issue; lost-on-restart is fine —
a pending login just retries).

**JIT Provisioning** (Just-In-Time):
Creating a *brand new* Jellyfin user automatically on first OIDC login (unknown `sub`)
from token claims. JIT never adopts an existing Jellyfin account.

**Identity Anchor**:
The stable claim that keys an OIDC identity to a Jellyfin user — `sub` (Subject).
`sub` is unique per **Provider**, not global, so the **Link** key is `(provider_id, sub)`.
Username/email are display data only, never a matching key (matching by name is the 9p4
admin-takeover bug).

**Provider**:
A configured IdP entry (Authentik or Authelia). Config holds a *list* of named providers
but only one is enabled at a time for now; the single enabled one is auto-picked (no
provider-picker UI yet). Schema is list-shaped from day 1 so adding a second needs no
migration.
Auth client: default is a **public client + PKCE** (no `client_secret` stored). If a
confidential client is required, the secret is supplied by env var / external file
reference — never written into plugin XML or shown in the dashboard.

**Link**:
An explicit association between an OIDC `sub` and a Jellyfin UserId. Created either by
JIT (for a new account) or — for *existing* Jellyfin accounts — only by a manual admin
action. Existing accounts are never auto-matched.

**Group**:
A group claim value from the IdP (Authentik/Authelia). The unit the admin maps to
library access. _Avoid_: "role" (9p4's term; we standardize on "group" since that is
what Authentik/Authelia emit).
Source: default claim `groups` read from the ID token (works for both targets out of
box); claim name + a "fetch from userinfo" toggle are optionally overridable per
**Provider**.

**Seen Group**:
A **Group** string the plugin has observed in some login. Recorded so the admin UI can
autocomplete group names (the IdP's group list is otherwise unknown to Jellyfin).

**Group Mapping**:
The admin-configured rule associating a **Group** with a *bundle of permission flags*
(not just libraries). Data model is a bundle from day 1 so later phases need no schema
migration. Lives in plugin config, edited in the dashboard UI.
Derived permissions, phased:
- MVP: library access (`EnabledFolders`) + admin status (`IsAdministrator`).
- Phase 2: Live TV (access + manage), download/sync (`EnableContentDownloading`),
  parental rating (`MaxParentalRating`, for real kid accounts).
UI: a table where each row maps one group (free-text + autocomplete from **Seen Groups**)
to a multiselect of libraries (referenced by stable GUID, shown by friendly name) plus an
admin toggle.

**Declarative Sync**:
On every login the plugin computes the user's full library access from their current
**Groups** and *overwrites* Jellyfin's per-user access. IdP groups are the single source
of truth; manual changes in Jellyfin's user UI are reverted on next login.

**Web Login Path**:
Login from Jellyfin Web. The OIDC redirect lands back in the web app, JS reads the
state and presents the **Ephemeral Secret** to Jellyfin's native auth path directly.

**Quick Connect Path**:
Login from native apps (Android, iOS, TV, etc.). The app cannot receive the
**Ephemeral Secret** from the external browser directly, so it uses Jellyfin's
Quick Connect (browser approves a code, app polls for it). Requires Quick Connect
to be enabled server-side.

## Relationships

- A successful **OIDC** login produces one **Ephemeral Secret**
- The **Session Bridge** consumes the **Ephemeral Secret** to make Jellyfin mint a token
- First login (unknown `sub`) triggers **JIT Provisioning** of a new Jellyfin user
- A **Link** binds one **Identity Anchor** (`sub`) to one Jellyfin UserId
- Linking an *existing* Jellyfin account is a manual admin action only
- A JIT username collision with an existing Jellyfin user → login rejected with an
  admin-facing message (never silent rename, never auto-adopt)
- **Web Login Path** and **Quick Connect Path** are the two ways a client reaches the bridge
- A **Group Mapping** associates one **Group** with a set of libraries
- **Declarative Sync** applies all matching **Group Mappings** on every login, overwriting
  Jellyfin's per-user access
- A user with no matching **Group Mapping** is still allowed in (JIT account created) but
  sees zero libraries until a mapping exists. (Assumes the IdP gates who may reach the
  plugin at all.)

## Persistence

Persistent state (**Link** table, **Seen Groups**, **Group Mappings**, **Provider**
config) lives in the Jellyfin plugin config (`BasePlugin<TConfig>` XML) — automatic
serialization, free dashboard binding, no extra infra at homelab scale. **Ephemeral
Secrets** are in-memory only. No separate database.

## Build target

Pins to a single Jellyfin version (`Jellyfin.Controller` must match the server's install
version exactly or the plugin shows `NotSupported`). Target: **12.1.0** (the server on
`diana.home`), `net10.0`. Bump in lockstep with the server.

Distribution: manual DLL drop during dev; a `manifest.json` + versioned zip hosted on
gitea releases once stable (dashboard install/update).

## Flagged ambiguities

- "token exchange" is overloaded: the OIDC authorization-code-for-ID-token step vs.
  the Jellyfin token minting. Reserved "token exchange" for the OIDC step;
  the Jellyfin side is the **Session Bridge**.
