# Google, by hand

A complete login against Google, run by a person. Google cannot be started in a container
and its login form is not something a script should drive, so this is the one provider
check with a click in it. Everything around the click is the harness's:
`GoogleLoginTests` starts a throwaway Jellyfin on port 8097, configures a Google provider
the way [the configuration reference](../../docs/configuration.md#providers) describes,
hands the client secret to the server as a file, and waits.

Not in CI, and not part of `make e2e`. What the first run found is written up in
[docs/google.md](../../docs/google.md).

## Once: a Google OAuth client

In the [Google Cloud console](https://console.cloud.google.com/), signed in with the
account that should own the client:

1. Create a project.
2. **Google Auth Platform → Branding**: an app name and a support address.
3. **Audience**: *External*, publishing status **Testing**, and the account you will sign
   in with added as a **test user**. In Testing only listed test users can sign in, which
   is the right setting for a household too.
4. **Clients → Create client → Web application**, with the authorized redirect URI

   ```
   http://localhost:8097/sso/oidc/callback/google
   ```

5. Put the client's id and secret into `tests/runs/google/client.env`:

   ```
   GOOGLE_CLIENT_ID=<value>
   GOOGLE_CLIENT_SECRET=<value>
   ```

`tests/runs/` is gitignored. The harness reads the file, writes the secret into the
throwaway server and prints neither value.

## Each run

```shell
make google
```

When `tests/runs/google/ready.json` appears, open the address it names —
`http://localhost:8097/sso/oidc/start/google` — in a browser on the same machine and sign
in to Google. The harness sees the link the plugin writes, records what arrived in
`tests/runs/google/result.json` and holds the server for ten more minutes, so a second
login can be tried; delete `ready.json` to stop it sooner.

`result.json` says whether the login arrived, the shape of the subject (not the subject),
the account name Google's claims produced, whether a profile picture was taken, the groups
seen, the account's policy, and the tail of the plugin's log.
