# Contributing

Everything about building, testing and releasing the plugin lives here, so the README can
stay about installing it and [docs/](docs/README.md) about running it.

## Layout

- Source: `Jellyfin.Plugin.Sso/`
- Config UI: `Configuration/configPage.html` + `configPage.js` (ES module loaded via
  `data-controller="__plugin/SSO Authentication.js"`).
- `make build` / `make publish` / `make deploy`.

### Tests

```bash
make test        # dotnet test tests/Jellyfin.Plugin.Sso.Tests -c Release --logger trx
```

Tests live under `tests/`; everything a run produces (binaries, obj, TRX results) goes to
`tests/runs/`, which is gitignored. They cover the security-relevant paths: ID token
validation (signature, issuer, audience, lifetime, nonce, `alg=none`), the ephemeral
secret bridge (single use, no empty password, no cross-user use), PKCE state handling, and
the provisioning and permission rules (never adopt an account by username, revoke access
when groups disappear).

### End-to-end tests

```bash
make e2e
```

A throwaway Jellyfin container is started from the image matching the pinned
`Jellyfin.Controller` version, with the **release ZIP** installed as a plugin — so the
artifact users download is the one under test. Alongside it runs an OIDC provider written
for this suite (a small ASP.NET host in the test process, serving HTTPS with a self-signed
certificate the container is made to trust) — real discovery, JWKS, PKCE code exchange and
RSA-signed tokens, with no login form, because the test says who logs in next instead of
typing it. The browser hop is driven by Playwright.

One tier up, the suite starts **real identity providers** in containers — Keycloak from a
realm file and Authelia from a generated config — and signs in through their own login
forms in the browser. That is the only way to prove the groups really arrive in the claims
and that the plugin copes with providers nobody here controls. These tests carry the trait
`Category=RealIdp` and run as their own CI job:

```bash
make e2e E2E_FILTER='--filter "Category=RealIdp"'
```

Both found real bugs the scripted provider could not: Keycloak rejects an unknown `groups`
scope, and Authelia keeps the profile claims out of the ID token, which used to leave the
new Jellyfin user named after the opaque subject.

Covered there: the plugin loads in a real server, the redirect and PKCE code exchange, JIT
provisioning, group-to-library permissions applied through Jellyfin's own user policy,
Jellyfin minting an access token for the ephemeral secret, the username-collision refusal,
and the localStorage handoff into the web client. The admin page is driven too — it is
plain JavaScript inside Jellyfin's dashboard, where only a browser notices a typo — so a
mapping edited in the UI is read back through the API. Screenshots and page dumps of
failed browser runs land in `tests/runs/playwright/`.

`make e2e` runs inside the .NET SDK image (no local SDK needed) and talks to the host's
Docker daemon; it needs Docker and about 1 GB for the Jellyfin image.

Both suites run on every push and pull request, and again before a release is published —
except when a change is Markdown, something under `docs/`, or `scripts/demo-clip.sh` and
nothing else, which `scripts/changed-code.sh` decides. The jobs still run and still
report; they skip their own expensive steps. Anything that cannot be read as a change —
a force push, a new branch, a filter that fell over — counts as code and runs everything.

Every one of those runs pins its containers — the Jellyfin tag follows the pinned
`Jellyfin.Controller`, and the three providers are pinned next to the `TestImages.Resolve`
calls that name them. That is on purpose: a provider shipping a breaking release on a
Tuesday must not turn that week's pull requests into a mystery. The price is that the
suite only proves the plugin worked with the versions it was written against.

So the pins are lifted once a week. The `Upstream` workflow runs the same tests against
the newest Jellyfin image and the newest Authentik, Keycloak and Authelia, on Monday at
05:00 UTC and on demand, and nothing in it is a required check. A failure opens an issue —
or comments on the one already open — with a link to the run, whose job summary lists the
digest of every image it pulled, because "the latest Keycloak broke it" is only useful
with the bytes named. The issue is not closed automatically: it is closed by whoever
established what changed, the same as any other finding here. The same thing locally:

```bash
E2E_KEYCLOAK_IMAGE=quay.io/keycloak/keycloak:latest \
  make e2e E2E_FILTER='--filter "Category=RealIdp"'
```

### Cutting a release

```bash
git tag v1.12.1 && git push origin v1.12.1
```

The `Release` workflow builds the ZIP, attaches it to a GitHub release and appends the
version to `manifest.json` on `main`, which is what servers poll. The `targetAbi` written
into the manifest is derived from the `Jellyfin.Controller` reference in the csproj, so a
Jellyfin bump automatically applies to the next release.

Tags must be four-part or three-part (`v1.12.1` becomes `1.12.1.0`) and must point at a
commit on `main`.

---

## Manual install while developing

Jellyfin loads a plugin from a folder in its config volume, by default
`/config/plugins/SSO Authentication_<version>/`. The folder needs the plugin DLL **and**
the dependencies Jellyfin does not ship:

```
Jellyfin.Plugin.Sso.dll
Duende.IdentityModel.dll
Microsoft.IdentityModel.Abstractions.dll
Microsoft.IdentityModel.JsonWebTokens.dll
Microsoft.IdentityModel.Logging.dll
Microsoft.IdentityModel.Protocols.dll
Microsoft.IdentityModel.Protocols.OpenIdConnect.dll
Microsoft.IdentityModel.Tokens.dll
System.IdentityModel.Tokens.Jwt.dll
```

`scripts/package.sh <version>` builds exactly that set as a ZIP; `make deploy` copies it to
the server named in the `Makefile` and restarts the container.

If the DLL is dropped without its dependencies, Jellyfin logs
`Failed to load assembly … IdentityModel …` and deletes the plugin folder on the next
restart. Always deploy all of them together.

## How the documentation was checked

The documentation claims to be followable by someone who runs Jellyfin in Docker and does not
write code. That claim was tested, and it is worth knowing exactly how, because the method
has a ceiling.

Seven times, a reader with no knowledge of this project was given the documentation and
nothing else — no source, no other files — and asked to walk the setup in their head, name
the first line that stopped them, and rate it. At that point all of it was one long README,
since split into the README and [docs/](docs/README.md). Each round's findings were fixed
before the next. The ratings went 3, 8, 7, 7, 7, 7, 9; the last round reported no blockers.

Three of those rounds found defects in the **code**, not the text: the configuration page
dropped a provider saved with only a name (the state the documented first step produces),
a newly added provider was enabled immediately and put a dead login button on the
household's login page, and the requested scopes could only be set through the API while
the Authelia and Keycloak guides gave contradictory advice about them. A fourth found a
link that returned 404.

**The ceiling:** those readers were language models in a role, not people. No human
newcomer has read this document. If you are reading this and you are one, the four
questions worth answering are: could you do it tonight without asking anyone, where did
you stop, was it ever unclear whether you were in Jellyfin or at your provider, and which
sentence made you guess. Open an issue with the answers; that evidence is worth more than
another round of the above.

### Checking it against the code

Prose about authentication rots quietly, so the claims are read back against the source,
not from memory: routes against the controllers' attributes, field names and labels
against `configPage.html`/`.js` and `PluginConfiguration`, log lines against the string
that is actually logged, constants (the 90-second secret, the scopes, `ClientType=1`)
against the code that reads them, and every link, anchor and image path by script.

That pass has found defects in the code as often as in the text — most recently a redirect
URI built without the server's sub-path, which broke SSO for anyone running Jellyfin under
a *Base URL*.

### Checking a release against its source

A release ZIP is byte-reproducible: rebuild it from its tag and the checksum matches the
one in `manifest.json`. Verified for v1.12.7 — `de194730f1522e7fd239bb8f3f148105` both
from the workflow and from a rebuild here.

```bash
git clone --branch v1.12.7 https://github.com/be-nj/jellyfin-sso.git
cd jellyfin-sso
SOURCE_DATE_EPOCH=$(git log -1 --format=%ct) scripts/package.sh 1.12.7.0
md5sum dist/jellyfin-sso_1.12.7.0.zip
```

Two things will make that fail, and both cost an afternoon to find:

- **Build from a clone, not a `git worktree`.** In a worktree `.git` is a file, the SDK's
  source-path normalisation resolves differently, and the assembly comes out with
  different metadata — same source, different bytes.
- **Use the SDK from `global.json`** (10.0.402, `rollForward: disable`). A different patch
  level is a different compiler. The container used here is
  `mcr.microsoft.com/dotnet/sdk:10.0.402`.

The release also carries a provenance attestation, which answers the other half of the
question — that the published file came out of this repository's workflow:

```bash
gh attestation verify jellyfin-sso_1.12.7.0.zip --owner be-nj
```

### Issues

`scripts/check-review-audit.sh` keeps that record honest: it resolves every commit and
every issue the review document names, and CI runs it on each pull request.

Everything found gets an issue, while it is fresh: a defect noticed in passing, an idea
put off, a gap a test exposed. A fixed one is closed with the commit, what the cause
turned out to be, and the test that now covers it; deliberately deferred items live in one
open issue so the decision stays visible. The record is public on purpose — a plugin that
sits in front of a household's accounts should be judged on what its author found and
fixed, not on the absence of a list.

### Review policy

Anything touching the login, the token handling or account linking gets reviewed before it
is merged — by a second person where there is one, and otherwise by a deliberate second
pass with a different lens than the one that wrote it. Current practice, and what the last
round of it found, is in [docs/reviews/](docs/reviews/).

`main` takes no direct pushes, from anybody, including the maintainer and the release
workflow: every change arrives as a pull request that the test matrix has to pass, and
auto-merge lands it when it does. That is also why the release publishes its manifest
through a pull request rather than pushing it.

The project is open to co-maintainers, and the rule above is the part of it that does not
wait for one: no change to the authentication path goes in on the strength of a green test
run alone.

## Screenshots

The images in the README and under `docs/` come out of the running plugin, not out of a
screenshot tool:

```bash
make screenshots
```

That starts the e2e environment, opens the real configuration page in a browser and
writes `docs/img/*.png`. Regenerate them whenever the page changes; the test is tagged
`Category=Screenshots` and is excluded from ordinary test runs because it writes into the
repository.

## Porting to a new Jellyfin version

The plugin pins `Jellyfin.Controller` and `Jellyfin.Model` to one server version; Jellyfin
refuses to load a plugin built against a different one. After a server upgrade:

1. Bump both package references in `Jellyfin.Plugin.Sso.csproj`.
2. Build and fix what the compiler complains about.
3. Run both test suites — the e2e harness reads the pinned version and starts the matching
   server image, so it tests against the Jellyfin you are porting to.
4. Tag a release. `targetAbi` in the manifest follows the csproj automatically.
