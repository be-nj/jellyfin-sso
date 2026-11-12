# Requirements and versions

[Documentation index](README.md) · [README](../README.md)

## Jellyfin version

Dashboard → **About**, or `http://<your-jellyfin>/System/Info/Public`.

| Jellyfin | Plugin |
|---|---|
| 12.x, with a minimum ABI of 12.0 | 1.12.6 and newer; tested versions below |
| 12.2 only | 1.12.2 – 1.12.5 |
| 12.1 only | 1.12.0 – 1.12.1 |
| 11.x and older | none — upgrade Jellyfin first |

A build's `targetAbi` is a **minimum**: 1.12.6 and newer are compiled against Jellyfin
12.0. It is not a guarantee of compatibility with every future release. Jellyfin hides
builds whose minimum is higher than the server; if the entry is missing, also check the
repository URL, reachability and server logs.

The configured checks cover the range in two passes. For code changes, the pull-request
workflow runs the regular end-to-end tier against the oldest supported server
and the two newer versions pinned in `jellyfin-versions.json` — the ends of the range, where the two
things that break live: an API the oldest server does not have, and a web client that has
moved the login page. Once a week the same suite runs against **every** released 12.x and
against `:latest`, where a failure opens an issue instead of blocking a merge.

So a version in the middle of the range is tested weekly rather than per change. The
versions in the first pass are listed in
[`jellyfin-versions.json`](https://github.com/be-nj/jellyfin-sso/blob/main/jellyfin-versions.json).

**Pin the Jellyfin image tag** (`jellyfin/jellyfin:12.4`, not `latest`) and upgrade
deliberately. A Jellyfin release that breaks the plugin API arrives without warning, and
your local administrator is the way in until a matching build exists. Back the
configuration up before you do: a minor upgrade migrates the library database, so the tag
you pinned is not a way back once it has run — [If something goes wrong](recovery.md) has
the two commands. Upgrading the server is its own evening's work —
[container documentation](https://jellyfin.org/docs/general/installation/container/) —
and not one to combine with adding an authentication plugin.

## One address for Jellyfin, used everywhere

The redirect URI is built from the address you have the dashboard open on, so use the one
your household uses: `https://jellyfin.example.com` or `http://192.168.1.10:8096`.

**LAN-only works.** Everything in these documents applies. What you give up is privacy on
your own network: access tokens travel unencrypted. HTTPS through a reverse proxy (Caddy,
Traefik, nginx) fixes that and is worth doing eventually.

Behind a reverse proxy the addresses work without extra configuration — the plugin
honours `X-Forwarded-Proto`, `X-Forwarded-Host` and `X-Forwarded-Prefix`, and a server
published under a sub-path (Jellyfin's *Base URL*) keeps that sub-path in its redirect
URI.

One thing does need configuring, and it is Jellyfin's setting rather than the plugin's:
register the proxy under **Networking → Known proxies**. The login rate limit counts the
address a connection came from, and until Jellyfin trusts the proxy that address is the
*proxy* for everybody — so the limit becomes one shared bucket, and sixty requests from
anybody can keep the household out for the rest of the minute. The plugin deliberately
does not read `X-Forwarded-For` for this: a limit keyed on a header the caller writes is
a limit the caller resets at will.

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
