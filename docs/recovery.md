# If something goes wrong

Back to the [documentation index](README.md) · [README](../README.md)

**My identity provider is down.** SSO logins fail for as long as it is down — the plugin
has no way to check a password it never sees. Anyone already signed in stays signed in,
and your local administrator can still log in with a password. This is the reason for
keeping one.

**I cannot get in at all.** Log in as the local administrator you kept. If you no longer
have one, Jellyfin's own [password-reset for administrators](https://jellyfin.org/docs/general/administration/troubleshooting/#forgot-password)
works independently of this plugin: it is a file in Jellyfin's config folder, and the
plugin cannot block it.

**Everyone lost their libraries.** The group mapping did not match what the provider
sends. Open the plugin page: the *Group name* field offers the groups it has actually seen
at login, which is the quickest way to spot a typo or a renamed group.

**Almost everyone lost administrator rights.** Not everyone: the last remaining
administrator is never demoted, by design. Log in as them, switch on *Never remove
administrator rights on login* (plugin page, **Group Mappings** tab, under *Safety*), then
fix the mapping.

**Jellyfin updated itself and the plugin stopped working.** A plugin build matches one
server version. After a server upgrade, install the plugin build for the new version from
the catalog. If there is none yet, you have two ways out: sign in as the local
administrator and wait, or put the Jellyfin image tag back to the version that worked
(`image: jellyfin/jellyfin:12.2` rather than `latest`) and pull again. Pinning that tag in
the first place is what keeps this from happening at an inconvenient hour. The version
table is in [Requirements](requirements.md).

**I am coming from the old 9p4 plugin.** This is a different plugin, not an update: it
has its own id, its own configuration and its own account links. Nothing is migrated.
Disable the old plugin first, then set this one up, then link existing accounts (see
[Linking accounts](linking.md#manual-linking)). Running both at once is asking for
confusion.

## Backup and restore

The backup, taken **with the server stopped** — Jellyfin keeps live databases in there,
and copying them while it runs gives you a backup that restores into a broken server:

```bash
docker compose stop jellyfin
cp -a /path/you/mounted/at/config ./jellyfin-config-backup   # bind mount
# named volume instead of a folder? docker volume ls shows yours; compose prefixes
# it with the project name, so it is often <project>_jellyfin_config
docker run --rm -v jellyfin_config:/from -v "$PWD":/to alpine \
  tar czf /to/jellyfin-config-backup.tar.gz -C /from .
docker compose start jellyfin
```

The restore is its mirror image. Bind mount — the folder you copied:

```bash
docker compose stop jellyfin
mv /path/you/mounted/at/config /path/you/mounted/at/config.broken
cp -a ./jellyfin-config-backup /path/you/mounted/at/config
docker compose start jellyfin
```

Named volume — the tarball you made instead:

```bash
docker compose stop jellyfin
# same volume name as in the backup — check with docker volume ls before running this,
# the first thing it does is empty the target
docker run --rm -v jellyfin_config:/to -v "$PWD":/from alpine \
  sh -c 'rm -rf /to/..?* /to/.[!.]* /to/* 2>/dev/null; tar xzf /from/jellyfin-config-backup.tar.gz -C /to'
docker compose start jellyfin
```

That returns the server to the evening before, plugin and all — at the cost of anything
watched since. `cp -a` and `tar` both keep ownership; if Jellyfin complains about
permissions afterwards, that is what went wrong.

## Uninstalling

In Jellyfin: Dashboard → **Users** → each SSO-managed user → set *Authentication Provider*
back to the default and give them a password. Then remove the plugin. Do it in that order
— a user left pointing at a removed provider cannot log in. Accounts, history and
libraries survive all of it.
