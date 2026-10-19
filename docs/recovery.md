# If something goes wrong

[Documentation index](README.md) · [README](../README.md)

**The identity provider is down.** SSO logins fail until it is back; the plugin never sees
a password it could check instead. Existing sessions keep working, and your local
administrator can still log in. That is what it is for.

**I cannot get in at all.** Log in as that local administrator. If there is none left,
Jellyfin's own [administrator password reset](https://jellyfin.org/docs/general/administration/troubleshooting/#forgot-password)
works independently of this plugin.

**Everyone lost their libraries.** The mapping did not match what the provider sends. On
the plugin page, the *Group name* field offers the groups seen at login — the quickest way
to spot a typo or a rename.

**Almost everyone lost administrator rights.** The last remaining administrator is never
demoted, by design. Log in as them, switch on *Never remove administrator rights on login*
(**Group Mappings** tab, under *Safety*), then fix the mapping.

**Jellyfin updated itself and the plugin stopped working.** Install the plugin build for
the new server version. If there is none yet: sign in as the local administrator and wait,
or put the image tag back (`jellyfin/jellyfin:12.2`, not `latest`) and pull again. See
[Requirements](requirements.md).

**I am coming from the old 9p4 plugin.** Different plugin, not an update: own id, own
configuration, own links, nothing migrated. Disable the old one first, set this one up, then link existing accounts
([how](linking.md#manual-linking)) — running both at the same time makes it hard to tell
which one handled a login.

## Backup and restore

Back up **with the server stopped** — Jellyfin's live databases are in there, and a copy
taken while it runs restores into a broken server.

```bash
docker compose stop jellyfin
cp -a /path/you/mounted/at/config ./jellyfin-config-backup   # bind mount
# named volume instead of a folder? docker volume ls shows yours; compose prefixes
# it with the project name, so it is often <project>_jellyfin_config
docker run --rm -v jellyfin_config:/from -v "$PWD":/to alpine \
  tar czf /to/jellyfin-config-backup.tar.gz -C /from .
docker compose start jellyfin
```

Restore, bind mount:

```bash
docker compose stop jellyfin
mv /path/you/mounted/at/config /path/you/mounted/at/config.broken
cp -a ./jellyfin-config-backup /path/you/mounted/at/config
docker compose start jellyfin
```

Restore, named volume:

```bash
docker compose stop jellyfin
# same volume name as in the backup — check with docker volume ls first, this empties
# the target before unpacking
docker run --rm -v jellyfin_config:/to -v "$PWD":/from alpine \
  sh -c 'rm -rf /to/..?* /to/.[!.]* /to/* 2>/dev/null; tar xzf /from/jellyfin-config-backup.tar.gz -C /to'
docker compose start jellyfin
```

That returns the server to the evening before, plugin and all, minus anything watched
since. `cp -a` and `tar` keep ownership; permission complaints afterwards mean one of them
was replaced by something that does not.

## Uninstalling

Dashboard → **Users** → each SSO-managed user → set *Authentication Provider* back to the
default and give them a password. **Then** remove the plugin — a user left pointing at a
removed provider cannot log in. Accounts, history and libraries survive.
