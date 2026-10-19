# Requirements and versions

Back to the [documentation index](README.md) · [README](../README.md)

## Jellyfin version

Check yours under Dashboard → **About**, or open
`http://<your-jellyfin>/System/Info/Public` in a browser — the `Version` field is the
number that matters. Each plugin build is compiled against one server version and
Jellyfin refuses the others, so the catalog shows you only what fits:

| Jellyfin | Plugin |
|---|---|
| 12.2.x | 1.12.2 and newer |
| 12.1.x | 1.12.0 – 1.12.1 |
| 12.0.x, 11.x | none yet — this plugin started at 12.1 |
| 10.11.x and older | none — upgrade Jellyfin first |

**If the catalog shows nothing after you add the repository, your Jellyfin is older than
any build here** — that is the version gate at work, not a broken URL.

Upgrading Jellyfin itself is outside this plugin, and worth its own evening: take the
backup from step 1 of the [quick start](../README.md#quick-start), change the image tag to
`jellyfin/jellyfin:12.2`, pull and start, check that your libraries and users are intact
— Jellyfin's [container documentation](https://jellyfin.org/docs/general/installation/container/)
covers the mechanics — and only then come back here. Do not upgrade the server and add an
authentication plugin on the same evening.

The practical consequence: **pin your Jellyfin image tag** instead of running `latest`,
upgrade deliberately, and check for a matching plugin build first. If a Jellyfin release
arrives before the plugin does, your local administrator is the way in until it catches
up.

## One address for Jellyfin, used everywhere

Whatever your household types to watch — `https://jellyfin.example.com` or
`http://192.168.1.10:8096` — use that same address when you open the dashboard to set
this up, because the redirect URI is built from it.

**LAN-only is a supported shape.** If you never reach Jellyfin from outside, put the LAN
address in and carry on; everything in these documents works. What you give up is privacy
on your own network: access tokens travel unencrypted, so anyone already inside can read
them. With HTTPS (a reverse proxy such as Caddy, Traefik or nginx) they cannot — worth
doing eventually, not a prerequisite for tonight.

Behind a reverse proxy nothing extra is needed: the address in your browser is the address
that ends up in the redirect URI, and the plugin honours the usual `X-Forwarded-Proto` and
`X-Forwarded-Host` headers when it builds the same URI server-side.

## An identity provider you administer

Authentik, Authelia or Keycloak. Anything that speaks OpenID Connect should work; those
three are the ones that are tested. Setup for each is in
[Authentik](authentik.md), [Keycloak](keycloak.md) and [Authelia](authelia.md).

## Jellyfin itself must reach that provider

Not your laptop — the Jellyfin container. It fetches the provider's keys on its own, and
in a split-DNS household `https://auth.example.com` often resolves on your laptop and not
inside the container. Check it before you start:

```bash
# "jellyfin" is the service name in your compose file; docker ps shows yours
docker compose exec jellyfin curl -sSf \
  https://auth.example.com/.well-known/openid-configuration >/dev/null && echo reachable
```

A wall of JSON or the word `reachable` means yes. "Could not resolve host" means the
container's DNS cannot see your provider — fix that first, the plugin cannot work around
it. ("executable file not found" would mean the check itself is wrong for your image, not
that the provider is unreachable.)
