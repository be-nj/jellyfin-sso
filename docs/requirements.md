# Requirements and versions

[Documentation index](README.md) · [README](../README.md)

## Jellyfin version

Dashboard → **About**, or `http://<your-jellyfin>/System/Info/Public`.

| Jellyfin | Plugin |
|---|---|
| 12.0, 12.1, 12.2 | 1.12.6 and newer |
| 12.2 only | 1.12.2 – 1.12.5 |
| 12.1 only | 1.12.0 – 1.12.1 |
| 11.x and older | none — upgrade Jellyfin first |

A build's `targetAbi` is a **minimum**: 1.12.6 is compiled against Jellyfin 12.0 and runs
on every 12.x server from there. Jellyfin hides builds whose minimum is higher than the
server, which is why an empty catalog means your server is older than any build here, not
that the URL is wrong.

The range is measured rather than assumed: every push runs the whole end-to-end suite
three times, against Jellyfin 12.0, 12.1 and 12.2 containers, with the same build.

**Pin the Jellyfin image tag** (`jellyfin/jellyfin:12.2`, not `latest`) and upgrade
deliberately. A Jellyfin release that breaks the plugin API arrives without warning, and
your local administrator is the way in until a matching build exists. Upgrading the server
is its own evening's work —
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
