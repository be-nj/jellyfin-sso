# Default to a public OIDC client with PKCE; never store secrets in plugin config

Jellyfin has no secret vault: plugin config is plaintext XML on disk and is rendered to
any admin in the dashboard. So the default auth client is a **public client using PKCE**,
which needs no `client_secret` at all — eliminating the secret-storage problem.

Both Authentik and Authelia support public clients with PKCE, so this is viable for our
targets. If a deployment requires a confidential client, the `client_secret` is supplied
via environment variable or an external file reference; the config stores only the
reference, never the secret value. Storing the secret directly in plugin XML (the common
Jellyfin-plugin habit) is explicitly rejected.

## Considered Options

- **C (chosen, default):** Public client + PKCE, no secret.
- **B (chosen, fallback):** Confidential client, secret via env/file reference.
- **A (rejected):** Secret in plugin config XML — plaintext on disk and visible in the
  admin dashboard.
