# Implementation Plan — Jellyfin SSO Plugin

Executable plan for the implementer (Sonnet). All design decisions are fixed in
[CONTEXT.md](CONTEXT.md) and [docs/adr/](docs/adr/). Do not re-litigate them; if a
decision blocks implementation, surface it rather than silently diverging.

Target: Jellyfin **10.11.10** on `diana.home`, `net9.0`. `Jellyfin.Controller` version
**must** equal the server version or the plugin loads as `NotSupported`.

---

## Research-before-code checklist (verify against the live SDK, do not assume)

The article warns SDK docs are sparse and APIs drift. Before writing each area, confirm
the exact current signatures in `Jellyfin.Controller` 10.11.10:

1. `IAuthenticationProvider` — method shape (`AuthenticateAsync`?), return type
   (`ProviderAuthenticationResult`?), and how a username maps to a result. This is the
   bridge's core contract.
2. `IPluginServiceRegistrator` — the current DI registration entry point for plugins.
3. Quick Connect server API surface (`IQuickConnect` / `QuickConnectManager`) — how to
   programmatically authorize a Quick Connect code from our callback.
4. `ILibraryManager` — enumerating top-level virtual folders/libraries and their stable
   GUIDs for the mapping UI.
5. `IUserManager` — create user, set `Policy.EnabledFolders`, `Policy.IsAdministrator`,
   `EnableAllFolders`; persist policy.
6. How a plugin serves/injects client-side JS into the Jellyfin web login page
   (this is the known-fiddly part — see Risks).
7. OIDC client library choice: `IdentityModel` (Duende) for discovery + PKCE + token
   endpoint, plus `Microsoft.IdentityModel.Tokens` / `System.IdentityModel.Tokens.Jwt`
   for ID-token validation. Confirm license + net9.0 compatibility.

Use Context7 / official sources for library APIs; read the jellyfin-plugin-template and
(read-only, for reference) the archived 9p4 plugin for the bridge pattern.

---

## Project layout

```
Jellyfin.Plugin.Sso/
├── Jellyfin.Plugin.Sso.csproj         # net9.0, Jellyfin.Controller 10.11.10
├── Plugin.cs                          # BasePlugin<PluginConfiguration>, IHasWebPages
├── PluginServiceRegistrator.cs        # DI wiring
├── Configuration/
│   ├── PluginConfiguration.cs         # providers[], groupMappings[], links[], seenGroups[]
│   ├── config.html                    # admin dashboard page
│   └── config.js                      # admin UI logic
├── Auth/
│   ├── SsoAuthenticationProvider.cs   # IAuthenticationProvider — validates ephemeral secret
│   └── EphemeralSecretStore.cs        # in-memory, TTL, single-use
├── Oidc/
│   ├── OidcClient.cs                  # discovery, PKCE, code exchange, token validation
│   └── ClaimsExtractor.cs             # groups from claim path (token) or userinfo
├── Provisioning/
│   ├── UserProvisioner.cs            # link lookup, JIT create, collision reject
│   └── PermissionSync.cs             # groups -> EnabledFolders + admin (declarative)
├── Controllers/
│   └── SsoController.cs               # /sso/oidc/start, /callback, quickconnect bridge, linking
└── Web/
    └── login-inject.js                # "Login with SSO" button injected into web login
```

Tests live under `tests/` (global rule), outputs to `tests/runs/` (gitignored).

---

## Data model (PluginConfiguration)

- `Provider`: `Id`, `Name`, `Enabled`, `Authority` (issuer URL for discovery),
  `ClientId`, `ClientType` (Public|Confidential), `ConfidentialSecretRef`
  (env var name or file path — **never** the literal secret), `Scopes`,
  `GroupsClaim` (default `groups`), `GroupsFromUserInfo` (bool).
- `GroupMapping`: `Group` (string), `LibraryIds` (GUID[]), `IsAdministrator` (bool).
  (Phase 2 adds LiveTv / download / parental fields to this same bundle.)
- `Link`: `ProviderId`, `Sub`, `JellyfinUserId`. Created by JIT or manual admin action.
- `SeenGroups`: distinct group strings observed at login (autocomplete source).

---

## Auth flow (web path)

1. `GET /sso/oidc/start/{providerId}` → build PKCE challenge + `state` + `nonce`,
   stash verifier/state/nonce server-side keyed by `state`, 302 to IdP authorize.
2. IdP → `GET /sso/oidc/callback/{providerId}?code&state` → validate `state`, exchange
   code (PKCE) for tokens, validate ID token (signature via JWKS, `iss`, `aud`, `exp`,
   `nonce`). Extract `sub` + groups.
3. Resolve user via `UserProvisioner` (link lookup → JIT or collision-reject).
4. Run `PermissionSync` (declarative overwrite of EnabledFolders + admin).
5. Mint an **EphemeralSecret** bound to the resolved username; return HTML/JS that calls
   Jellyfin's native `AuthenticateByName(username, ephemeralSecret)`.
6. `SsoAuthenticationProvider.AuthenticateAsync` recognizes the secret (single-use,
   unexpired) → returns success → Jellyfin mints the real access token.

## Auth flow (native / Quick Connect path)

App initiates Quick Connect (gets a code) → user opens the SSO start URL in a browser,
completes OIDC → callback authorizes the pending Quick Connect code for the resolved
user → app's poll succeeds and receives a Jellyfin token. Confirm the exact
`IQuickConnect` authorize-by-code API in the research step.

---

## Milestones (each independently testable; stop-and-verify between)

- **M0 — Scaffold.** csproj + `Plugin.cs` + empty config page. Builds, DLL drops into
  `diana.home`, loads as Active (not NotSupported), shows in dashboard.
- **M1 — Web login end-to-end (no groups).** OIDC start/callback + PKCE + token
  validation + ephemeral-secret bridge + JIT create. A new IdP user logs in via web and
  gets a Jellyfin session. Username collision is rejected with a clear message.
- **M2 — Declarative permission sync.** `GroupMapping` → `EnabledFolders` + admin,
  overwritten every login. Hardcoded mapping first, then read from config.
- **M3 — Admin UI.** config.html/js: provider config, library-aware mapping table
  (group autocomplete from SeenGroups × library multiselect by GUID + admin toggle).
- **M4 — Manual account linking.** Admin links an existing Jellyfin user to a
  `(provider, sub)`.
- **M5 — Quick Connect native path.** Native app login via the Quick Connect bridge.
- **M6 — Login button injection + distribution.** "Login with SSO" on the web login
  page; gitea manifest.json + versioned zip for dashboard install/update.

## Phase 2 (after MVP ships)

LiveTv + download + parental-rating derivation (same mapping bundle), claim-path/userinfo
overrides exercised, active revocation re-sync, RP-initiated logout, per-user sync opt-out,
provider-picker UI for multiple enabled providers.

---

## Security must-dos (not optional, baked into M1)

- Validate `state` (CSRF) and `nonce` (replay) on every callback.
- Full ID-token validation: JWKS signature, `iss`, `aud`, `exp`/`iat`, `nonce`.
- Ephemeral secret: cryptographically random, single-use, short TTL (~60s), bound to the
  exact pending login; never logged.
- Never write a confidential `client_secret` into plugin XML or echo it in the dashboard
  (ADR-0004). Read it from env/file ref at runtime only.
- Never match identity by username (ADR — Identity Anchor is `(provider, sub)`).

## Risks / known-fiddly

- **Login-button injection** into Jellyfin web is poorly documented and historically
  hacky (custom-JS branding hook vs serving an injected script). Resolve early in M6;
  fallback is a documented bookmarkable `/sso/oidc/start/{provider}` URL.
- **Quick Connect authorize-by-code** API may differ from expectations — verify in
  research step 3 before committing to M5 design.
- **IAuthenticationProvider contract** is the linchpin; if its 10.11 shape differs from
  the 9p4-era pattern, the whole bridge adapts around it (research step 1).
