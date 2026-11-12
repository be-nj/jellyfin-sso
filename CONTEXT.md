# Jellyfin SSO Plugin

A Jellyfin plugin that authenticates users against an external OIDC/OAuth2 provider
(Authentik, Keycloak, Authelia) and derives per-library access from the user's IdP groups,
configurable by an admin in a dashboard UI.

## Language

**IdP** (Identity Provider):
The external OIDC/OAuth2 server that authenticates the user. Automated provider tests cover Authentik, Keycloak and Authelia.
_Avoid_: "SSO server", "OAuth server".

**Session Bridge**:
The mechanism that turns a validated OIDC login into a real Jellyfin access token.
For browser login, the plugin implements `IAuthenticationProvider` and supplies an
ephemeral secret to Jellyfin’s native username/password path. Quick Connect uses
Jellyfin’s device authorization API after an explicit approval.
_Avoid_: "token exchange" (that term belongs to the OIDC code-for-token step).

**Ephemeral Secret**:
A short-lived, single-use credential the plugin generates after a successful OIDC
login. The client presents it on Jellyfin's native auth path; the plugin's
`IAuthenticationProvider` validates it and lets Jellyfin mint the token.
Stored **in-memory only**, bound to the Jellyfin user ID and username, and valid for
90 seconds. Revocation invalidates unspent secrets and older pending logins. Losing
the store on restart means the pending login must be retried.

**JIT Provisioning** (Just-In-Time):
Creating a *brand new* Jellyfin user automatically on first OIDC login (unknown `sub`)
from token claims. JIT never adopts an existing Jellyfin account.

**Identity Anchor**:
The stable claim that keys an OIDC identity to a Jellyfin user — `sub` (Subject).
`sub` is unique per **Provider**, not global, so the **Link** key is `(provider_id, sub)`.
Username/email are display data only, never the key used to adopt an existing account
(matching by name is the 9p4 admin-takeover bug).

**Provider**:
A configured IdP entry. Multiple providers can be enabled simultaneously. The start
page selects the only enabled provider automatically, or presents a provider picker
when several are enabled.
Auth client: default is a **public client + PKCE** (no `client_secret` stored). If a
confidential client is required, the secret is supplied by env var / external file
reference — never written into plugin XML or shown in the dashboard.

**Link**:
An explicit association between an OIDC `sub` and a Jellyfin UserId. Created either by
JIT (for a new account) or explicitly for an existing account by an administrator or
the authenticated self-service linking flow. Existing accounts are never auto-matched.
All writers check uniqueness under the configuration lock when saving the link.

**Group**:
A group claim value from the IdP. The unit the admin maps to
library access. _Avoid_: "role" (9p4's term; we standardize on "group" since that is
what Authentik/Authelia emit).
Source: default claim `groups` read from the ID token. Claim paths and the "fetch from
userinfo" option are configurable per **Provider**. Provider configuration determines
where groups are available; the tested Authelia setup obtains them from userinfo.

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
- Phase 3 (1.12.14): deletion (`EnableContentDeletion`), collections
  (`EnableCollectionManagement`), subtitles (`EnableSubtitleManagement`), lyrics
  (`EnableLyricManagement`). Deletion is the one exception to "an administrator gets
  everything": it removes files, so only a group that grants it grants it.
UI: a table where each row maps one group (free-text + autocomplete from **Seen Groups**)
to a multiselect of libraries (referenced by stable GUID, shown by friendly name) plus an
admin toggle.

**Remote Images** (1.12.15):
The single place the plugin fetches a picture from an address it did not choose — the
provider's logo for the login button, and the profile picture from a claim. The rules for
doing that safely live there once: `http(s)` only, three redirects, an allow-list of image
types, a byte count kept while reading, a short timeout, and no exception but the caller's
own cancellation. Both callers had their own copy before, and the copies had drifted.

**Profile Picture Sync** (1.12.14, off by default):
The `picture` claim — configurable per provider, a dotted path like the groups claim —
is fetched after the permission sync and written through Jellyfin's own image pipeline.
State: one in-memory record per user of the address and hash last written, so the second
login fetches but does not rewrite. It is deliberately *not* in the configuration file:
it is a cache, not a setting, and it is allowed to be empty after a restart.

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
- Linking an *existing* Jellyfin account is either an administrator's action on the Links
  tab or the account holder's own, through **Self-Service Linking** — never something a
  login does by itself, because a login that adopted an account by name would be a way
  to walk into somebody else's
- A JIT username collision with an existing Jellyfin user → login rejected with an
  admin-facing message (never silent rename, never auto-adopt)
- **Web Login Path** and **Quick Connect Path** are the two ways a client reaches the bridge
- A **Group Mapping** associates one **Group** with a set of libraries
- **Declarative Sync** applies all matching **Group Mappings** on every login, overwriting
  Jellyfin's per-user access
- A user with no matching **Group Mapping** is still allowed in by default (JIT account
  created) but sees zero libraries until a mapping exists — the assumption being that the
  IdP gates who may reach the plugin at all. An operator who cannot make that assumption
  turns on *Refuse logins that match no mapping*, and then such a login is turned away
  before an account exists
- Removing a **Link** ends that account's Jellyfin sessions, whoever removes it.
  Back-channel logout finds an account through its link and nothing else, so a link
  removed without that leaves tokens the provider can no longer end and Jellyfin does not
  expire
- The bookkeeping half of a login — is this the last administrator, has this identity been
  linked, does this username exist — runs one login at a time. Each of those is a question
  followed by a change, and two logins overlapping there could both demote the last two
  administrators or write two links for one account

## Persistence

Persistent state (**Link** table, **Seen Groups**, **Group Mappings**, **Provider**
config) lives in the Jellyfin plugin config (`BasePlugin<TConfig>` XML) — automatic
serialization, free dashboard binding, no extra infra at homelab scale. **Ephemeral
Secrets** are in-memory only. No separate database.

## Build target

`Jellyfin.Controller` **12.0.0**, `net10.0`. The reference decides the `targetAbi` written
into the manifest, and Jellyfin hides a version whose `targetAbi` is newer than the server
— so the lowest version the plugin is built against is the widest audience it reaches.

Which servers prove that: `jellyfin-versions.json` holds the ones every pull request runs
against, and `scripts/check-jellyfin-versions.sh` fails if its oldest entry and this
reference disagree, because then either the catalog offers the plugin to a server nothing
tested or a job is spent on a server the catalog hides it from. The versions between the
oldest and the newest are covered by the weekly `Upstream` run, which tests every released
12.x and `:latest`.

Distribution: a `manifest.json` plus a versioned zip on this repository's GitHub releases,
which the dashboard installs and updates from. `jf.be-nj.net/m.json` is the same file under
a shorter name.

## Flagged ambiguities

- "token exchange" is overloaded: the OIDC authorization-code-for-ID-token step vs.
  the Jellyfin token minting. Reserved "token exchange" for the OIDC step;
  the Jellyfin side is the **Session Bridge**.
