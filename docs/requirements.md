# Requirements and versions

[Documentation index](README.md) · [README](../README.md)

## Jellyfin version

Dashboard → **About**, or `http://<your-jellyfin>/System/Info/Public`. Each plugin build
is compiled against one server version; Jellyfin hides the rest:

| Jellyfin | Plugin |
|---|---|
| 12.2.x | 1.12.2 and newer |
| 12.1.x | 1.12.0 – 1.12.1 |
| 12.0.x, 11.x | none — this plugin started at 12.1 |
| 10.11.x and older | none — upgrade Jellyfin first |

An empty catalog after adding the repository means no build matches your server, not a
broken URL.

**Pin the Jellyfin image tag** (`jellyfin/jellyfin:12.2`, not `latest`) and upgrade
deliberately, checking for a matching plugin build first. If a Jellyfin release arrives
before the plugin does, your local administrator is the way in until it catches up.
Upgrading the server is its own evening's work —
[container documentation](https://jellyfin.org/docs/general/installation/container/) —
and not one to combine with adding an authentication plugin.

## One address for Jellyfin, used everywhere

The redirect URI is built from the address you have the dashboard open on, so use the one
your household uses: `https://jellyfin.example.com` or `http://192.168.1.10:8096`.

**LAN-only works.** Everything in these documents applies. What you give up is privacy on
your own network: access tokens travel unencrypted. HTTPS through a reverse proxy (Caddy,
Traefik, nginx) fixes that and is worth doing eventually.

Behind a reverse proxy nothing extra is needed — the plugin honours `X-Forwarded-Proto`,
`X-Forwarded-Host` and `X-Forwarded-Prefix`, and a server published under a sub-path
(Jellyfin's *Base URL*) keeps that sub-path in its redirect URI.

## An identity provider you administer

[Authentik](authentik.md), [Keycloak](keycloak.md) or [Authelia](authelia.md). Anything
speaking OpenID Connect should work; those three are tested.

## Jellyfin itself must reach that provider

The container fetches the provider's keys, not your laptop. In a split-DNS household
`https://auth.example.com` often resolves in your browser and not inside the container:

```bash
# "jellyfin" is the service name in your compose file; docker ps shows yours
docker compose exec jellyfin curl -sSf \
  https://auth.example.com/.well-known/openid-configuration >/dev/null && echo reachable
```

`reachable` means yes. "Could not resolve host" means the container's DNS cannot see your
provider — fix that first. ("executable file not found" means the check does not fit your
image, not that the provider is unreachable.)
