# Session Bridge via IAuthenticationProvider, not direct token minting

Jellyfin issues access tokens only through its native username+password auth path,
which has no concept of an OIDC redirect flow. We bridge by having a custom controller
run the OIDC flow, store an **Ephemeral Secret** on success, and implement
`IAuthenticationProvider` to validate that secret when the client logs in via Jellyfin's
native path — so **Jellyfin itself mints the token**.

We rejected minting tokens directly via `ISessionManager`/internal APIs: it is one step
shorter but binds us to unstable internals that break on Jellyfin upgrades. Keeping token
hoheit with Jellyfin is the durable choice.

## Considered Options

- **A (chosen):** `IAuthenticationProvider` + ephemeral secret bridge.
- **B (rejected):** Direct token minting via `ISessionManager`. Fragile across versions.
