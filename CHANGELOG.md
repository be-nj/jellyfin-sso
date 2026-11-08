# Changelog

What a server operator sees in **Plugins → Catalog → SSO Authentication**, one short
section per version. `scripts/manifest-changelogs.sh` copies these into `manifest.json`,
so this file is the catalog text and is reviewed like any other change.

Short on purpose. The reasoning behind a release belongs on its
[releases page](https://github.com/be-nj/jellyfin-sso/releases) and in
[docs/reviews/](docs/reviews/); what belongs here is enough to decide whether to install,
in the few lines somebody reads before clicking.

Issue and pull request numbers are given bare rather than as links: the catalog renders
Markdown, so a link per finding would cost more characters than the lines themselves.

## 1.12.20

- **Security.** Token checks tightened after the first run of the OpenID Foundation
  conformance suite: an ID token carrying no `iat` is refused, a key a provider published
  for encryption is no longer accepted as a signing key, and a refusal no longer names an
  outcome that did not happen (#216, #217, #220, #221).
- **New: _Signing algorithms_** on the provider card pins what a provider may sign a token
  with, on the login and the back-channel logout path alike (#219, #222).
- **Tests.** The end-to-end suite waits for the web client and the server log instead of
  guessing how long either takes, and the conformance harness attributes a refusal to
  whoever made it (#224, #225, #226).

Nothing to do on upgrade — _Signing algorithms_ is empty by default, which is what every
earlier release did.

## 1.12.19

- **Fixes you notice.** Quick Connect works again with more than one provider; the
  configuration page and a first login both work on a server published under a sub-path;
  unlinking ends sessions even when the first attempt fails; a logout signed with a
  just-rotated key is accepted; a slow profile-picture host no longer holds a login open; a
  bad library id in a group mapping no longer turns every login into a 500; a username with
  a leading space no longer creates a second account (#190, #193, #199–#205, #196, #207).
- **Hardening.** `azp` is checked, signature algorithms pinned to the asymmetric ones, the
  userinfo answer has to be about whoever logged in, the end-session endpoint cannot move
  to another host, the back channel no longer follows redirects, a token claim cannot forge
  a log line, an unreadable secret file is reported without its path, and the logout
  endpoint is behind the request limiter (#184–#195, #196).
- **Under load.** Two administrators signing in at the same moment can no longer both be
  demoted — which locked everybody out — and two link callbacks finishing together can no
  longer write two links for one account (#198, #204, #207).
- **Also.** A guide for Tinyauth as a fourth provider, and `SsoController` split from a
  thousand lines into six files (#181–#183).

## 1.12.18

- **Update from 1.12.17.** Two of that release's security fixes did not hold. The pending
  login cookie was read under the unprefixed name as well, which handed back the
  administrator escalation 1.12.17 claimed to close, and the logo fetch passed a flag that
  waived the address rules it had just been given — on the one path an anonymous caller can
  read (#153–#168).
- **Fixes.** Pressing Save could resurrect a deleted link, or erase one made while the page
  was open; the back-channel logout spent its token before the sessions were gone; a key
  rotation stopped every login for half an hour with a 500; a flood of logins could evict a
  chosen person's session.
- **Release pipeline.** A pull request could decide whether it was tested; two workflows
  spliced text somebody else chose into a shell; the release ZIP was built from `main` while
  its attestation named the tag; nothing checked that the manifest's URLs point at this
  repository or that its checksums match.

## 1.12.17

A security release. A review of the whole plugin found thirteen defects, all fixed.

- **Any account holder could make themselves an administrator.** A pending login was not
  tied to the browser that began it; a cookie binds the flow now (#126–#138).
- **Also fixed.** The client secret was readable by anonymous callers, two server-side
  request forgeries returned their response, a blank `sub` collapsed users into one
  account, a logout token could be replayed, and the approval and logout pages could be
  framed or triggered from any site.
- **Four behaviour changes, none needing action on an ordinary setup.**
  `DELETE /sso/link/self` now requires `?providerId=…`; profile pictures are no longer
  fetched from private addresses, which _Allow profile pictures from this network_ lifts; a
  discovery document must keep `jwks_uri`, `token_endpoint` and `userinfo_endpoint` on the
  authority's host; and a `ConfidentialSecretRef` must use `env:` or `file:`.

**If you pasted a literal client secret into `ConfidentialSecretRef`, rotate it** — it was
readable by anyone who could trigger a callback.

## 1.12.16

- **New: refuse a login that matches no group mapping.** Off by default. With it on,
  somebody in no mapped group is turned away instead of getting an account with no
  libraries. Users excluded from the group sync stay exempt, and local passwords are
  untouched (#114).
- **Single logout names the session that is ending**, so a provider that used to ask the
  user to confirm ends it instead. Nothing is kept while provider logout is off (#105).
- **The login button reaches the server from apps bundling their own web client**, such as
  Samsung's, once **Dashboard → Networking → Published server URIs** is set. Which app
  signs in which way is now written down in `docs/clients.md` (#116, #117, #119, #120).

Nothing to do on upgrade.
