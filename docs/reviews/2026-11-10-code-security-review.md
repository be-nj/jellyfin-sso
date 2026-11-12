# Code and security review, 10–12 November 2026

[Documentation index](../README.md) · [Threat model](../threat-model.md) ·
[Security notes](../security.md) ·
[The conformance run before](2026-11-06-oidc-conformance.md) ·
[The conformance harness](../../tests/conformance/README.md)

Two passes, the second over the first. On 10 November an AI agent was asked to understand
the project, review it for security holes, fact-check the README and fix what it found;
on 11 November it ran the real-provider tier and the OpenID Foundation suite against its
fixes and corrected the conformance driver on the way. Which model that was is not
recorded. On 12 November Claude Opus 5.5 was asked to check all of that work before any
of it was committed, with three sub-agents reading the production code, the tests and the
documentation independently of each other.

Reviewed at `4061035`, plugin version `1.12.20.0`; fixes landed in `#PR`.

The first pass found seven things, three of them serious. The second found that the fix
for the most serious one had opened a new hole of its own, that one Jellyfin route minting
tokens was still outside the fix, and that the README rewrite had removed true statements
and introduced a false one. Nothing of either pass was released in between.

## What came out of it

| # | Finding | Severity | Pass | Issue |
|---|---|---|---|---|
| 1 | A one-time login secret or a Quick Connect approval fetched before a revocation still worked after it | high | first | [#243](https://github.com/be-nj/jellyfin-sso/issues/243) |
| 2 | Syncing a profile picture wrote an older copy of the whole account back | high | first | [#244](https://github.com/be-nj/jellyfin-sso/issues/244) |
| 3 | A first login that failed halfway left a local account with no password | high | first | [#245](https://github.com/be-nj/jellyfin-sso/issues/245) |
| 4 | A first login and a self-service link could give one identity two accounts | medium | first | [#246](https://github.com/be-nj/jellyfin-sso/issues/246) |
| 5 | The signing-key address was fetched before its host was checked | medium | first | [#247](https://github.com/be-nj/jellyfin-sso/issues/247) |
| 6 | The weekly upstream run could open its pull request but not start its tests | medium | first | [#248](https://github.com/be-nj/jellyfin-sso/issues/248) |
| 7 | Switching self-service linking off did not stop a link already under way | medium | first | [#249](https://github.com/be-nj/jellyfin-sso/issues/249) |
| 8 | The fix for finding 1 put every Jellyfin login on the server behind one lock, which a flood of password attempts could use to hold a back-channel logout until the provider gave up | high | second | [#250](https://github.com/be-nj/jellyfin-sso/issues/250) |
| 9 | Jellyfin's own Quick Connect approval minted tokens outside the fix for finding 1 | medium | second | [#251](https://github.com/be-nj/jellyfin-sso/issues/251) |
| 10 | Links could be removed without revoking their sessions through Jellyfin's generic plugin-configuration endpoint | medium | second | [#252](https://github.com/be-nj/jellyfin-sso/issues/252) |
| 11 | An account disabled after its SSO login could still let a Quick Connect device in | low-medium | second | [#253](https://github.com/be-nj/jellyfin-sso/issues/253) |
| 12 | The stricter discovery check from finding 5 refused a whole provider over endpoints the plugin never calls, or over capitals in its host | low | second | [#254](https://github.com/be-nj/jellyfin-sso/issues/254) |
| 13 | The guard for finding 1 finds Jellyfin's login actions by name, with nothing saying when a name stops existing | low | second | [#255](https://github.com/be-nj/jellyfin-sso/issues/255) |
| 14 | Two logins colliding were reported to the person as a configuration error; the corrected password-reset provider name was overwritten on every login | low | second | [#256](https://github.com/be-nj/jellyfin-sso/issues/256) |
| 15 | New tests that passed without checking their claim, and a README that lost true statements and gained a false one | low | second | [#257](https://github.com/be-nj/jellyfin-sso/issues/257) |

Severity is this project's own scale, not CVSS. "High" here means that somebody the
plugin had just decided to shut out could get or keep a session, or that an account ended
up in a state nobody chose.

## The first pass

### 1. Revocation did not reach credentials that were not yet sessions

Unlinking an account and a back-channel logout both end the account's Jellyfin tokens.
Neither reached what a login hands out before it becomes a token: the one-time secret the
browser spends on Jellyfin's password route (90 seconds), and a Quick Connect approval
waiting for its click (two minutes). A person could finish a login, hold on to what it
returned, wait for the logout, and then spend it. After an unlink the session that
produced was also out of the plugin's reach, because back-channel logout finds an account
through its link. The second token sweep in `Unlinking` did not help: the secret could be
spent after both sweeps.

Reproduced at the unit level and over HTTP against Jellyfin 12.0: a secret fetched before
an unlink or a back-channel logout signed in afterwards.

### 2. The profile-picture sync wrote back a stale account

`ProfilePictureSync` read the whole `User` before downloading the picture and saved it
with `UpdateUserAsync` afterwards. In Jellyfin 12 that call writes every permission and
preference on the object it is given, so an administrator disabling the account during
the download was undone when the download finished. A picture server the provider points
at decides how long that window is. Reproduced against a real Jellyfin: the account an
administrator had just disabled was enabled again.

### 3. A failed first login left a passwordless local account

The account was created through Jellyfin's `CreateUserAsync`, which commits an ordinary
local account, and restricted in a second call. If the second call failed, the first
stayed: no link, no SSO provider, no password — and Jellyfin's default provider accepts
an empty password for an account that has none. The next SSO attempt then failed on the
username. Needs a database failure at the right moment; nobody showed a way to cause one
from outside.

### 4. First login and linking took different locks

The first-login path held `AccountBookkeeping` while it waited for the account to be
created; self-service and administrator links only took `ConfigurationLock`. A link
written in that wait was followed by the provisioner's own, without a second look, and
the identity had two accounts. Shown with a test that pauses account creation.

### 5. The signing keys were fetched before their host was checked

Duende fetches `jwks_uri` as part of discovery. The plugin had switched off Duende's
endpoint validation for Authentik's sake and checked the host afterwards, so a discovery
document naming keys on another host had that host contacted before it was refused. Not
an open SSRF — it needs control of the configured provider's discovery document — but the
documented host restriction did not hold.

### 6. The weekly run could not start its tests

The `bump` job in `upstream.yml` dispatches the test workflow with the job's own token but had
no `actions: write`. Found by reading; no run was triggered to see the 403.

### 7. A link flow outlived the switch that allowed it

The start endpoint checked *Allow self-service linking*; the callback did not, so a flow
started before an administrator switched it off still wrote its link. Reproduced over
HTTP.

### What the first pass also changed

The README fact-check corrected several statements: the unit tests use substitutes for
Jellyfin's services, so "not mocks" was wrong; Markdown-only changes skip the heavy jobs,
so not every push runs everything; K0lin's and 9p4's plugins have self-service linking,
which the comparison said they did not. It then rewrote far more than it had checked —
see #15.

It also fixed the conformance driver, which had been reporting two passing discovery
modules as failed and one unfinished module as passed — see
[the conformance rerun](#oidc-conformance-rerun).

## The second pass

### 8. The fix for #1 made revocation wait for strangers

To stop a token being minted between a revocation and its sweep, the first pass put one
process-wide `SemaphoreSlim` around everything: every SSO callback, every Quick Connect
approval, every revocation — and, through an MVC filter, every call to Jellyfin's own
`/Users/AuthenticateByName`, for every user and every authentication provider. Jellyfin
hashes the password (or asks LDAP) inside that action, and the endpoint has no rate limit.

A flood of password attempts against any name from `/Users/Public` therefore built a queue
that a back-channel logout had to wait in. When the provider stopped waiting, the request
was cancelled before anything was recorded, nothing was revoked, and the logout endpoint
answered nothing that invites a retry. The same queue let the 90-second login secrets
expire, so every SSO login on the server failed while it lasted. Before the fix, a
revocation waited for nothing.

### 9. Jellyfin's own Quick Connect approval was outside the fix

`POST /QuickConnect/Authorize` mints a token for the caller's account inside Jellyfin.
The filter did not watch it, and the caller's token is checked before any filter runs.
Somebody holding a stolen token could keep approving codes and have one of them land
after the sweep of a back-channel logout, with a fresh token the sweep had missed. The
documentation said Quick Connect authorisation finished "under the revocation gate"; only
the plugin's own approval did.

### 10. Links could leave without their sessions

The plugin's admin endpoint never takes links from a submitted configuration, because
removing one has to revoke its sessions first. Jellyfin's generic
`POST /Plugins/{id}/Configuration` replaced the whole configuration, links included, with
no revocation. Administrator-only, but it broke the rule every other path keeps.

### 11. A disabled account could still let a device in

Jellyfin's `AuthorizeRequest` checks nothing about the account. An administrator who
disabled an account between its SSO login and the click on the Quick Connect page did not
stop the device from being let in.

### 12. The discovery check from #5 refused more than it should

Duende's validation covers every key ending in `endpoint`, plus `check_session_iframe`,
and compares host and prefix exactly. A provider publishing device authorisation or
introspection on another host or port, or an authority typed with capitals, had its whole
discovery document refused — every login and every back-channel logout. None of the
documented providers does either; the plugin's own host check after the fetch already
covered the endpoints it calls.

### 13. A guard by name, with no word when the name goes

Jellyfin.Api is not a package a plugin can reference, so the filter matches controller
and action names as strings. A rename in a Jellyfin minor would have dropped the guard
without a failing request, and the unit test built its own descriptors, so nothing would
have noticed.

### 14. Small things in the same code

A collision between two first logins threw `InvalidOperationException`, which the
callback reports as "this provider is not configured correctly", sending the operator
looking for a configuration problem. And the first pass corrected the password-reset
provider's name for new accounts, while `PermissionSync` wrote the old name back on every
login. Jellyfin falls back to its default provider for an unknown name, so neither did
harm; it was two answers to one question.

### 15. Tests and documents

No test was weakened — no assertion loosened, no case removed or skipped. But several new
ones passed without checking their claim: the self-service test ignored the callback's
answer and only looked for an absent link; two tests accepted any failing status; a test
hook was not reset between tests; two assertions were true whatever the code did.

The README rewrite removed the comparison table, the "Where they come from" figures and
the section on this project's own weaknesses, which the maintainer had asked for. It
dropped true statements — the mutation score, CodeQL's state, the short manifest address,
the release attestations — and added a false one: that Jellyfin for Kodi has no Quick
Connect. It does since 2.2.0 (`login_quick_connect` in `jellyfin_kodi/connect.py`);
Jellyfin's official table predates that. The old comparison table had its own errors,
which the rewrite had removed rather than fixed: self-service linking, nested claim paths
and profile pictures were marked wrong for K0lin and 9p4. The two reports were in German
where every other review is in English, and pointed readers at paths under `/tmp`.

Also found while checking #1: a test the second pass wrote for a suspected gap — a device collecting its
Quick Connect token after a logout — failed at first, and the gap was not real. Jellyfin
mints that token at approval and the sweep deletes it; the device collects a dead token.
The assertion had checked the wrong thing. It now checks that the token is refused, and
stays as a regression test.

## What the fixes do

**Revocation never waits for a login** (#1, #8, #9). `LoginLifetime` keeps a version per
account and per identity. A revocation records it — synchronously, before it awaits
anything — for the account and every identity linked to it, and only then sweeps the
session store. Every login state carries the version from when it started; the secret and
the Quick Connect approval it hands out at the end carry that same version and the
identity, so a revocation anywhere in between reaches them. The places that mint a token
check again *after* the token exists: Jellyfin's password and SSO-secret routes, the
plugin's Quick Connect approval and Jellyfin's own. If a revocation landed in between,
they take the token back; if it landed later, its sweep finds a token that is already in
the store. Jellyfin's own Quick Connect approval cannot be judged by version, because its
caller's token was checked before the filter ran, so it waits for any sweep of the
caller's account in progress and then checks that the caller's token still exists. Only
revocations hold that per-account lock. Every credential store refuses a lifetime longer
than the fifteen minutes a revocation is remembered.

**Accounts** (#2, #3, #4). New accounts are written directly, disabled and SSO-only, in
the same commit that creates them, and enabled once the policy is set; a failure removes
the account and its link. The picture sync changes only the image row, through a tracked
context. The first-login path decides uniqueness under the same lock as every other link
writer, immediately before it writes, and gives way to a link that won the race. Both
bypass `IUserManager`, because it offers neither an atomic safe creation nor an image-only
write; that couples them to Jellyfin 12's database schema, which the server matrix tests.

**The rest.** Duende validates endpoints before it fetches keys, with the configured
origin allowed for Authentik's shared paths, both as typed and as normalised, and with the
endpoints the plugin never calls left out (#5, #12). The `bump` job has `actions: write`
(#6). The callback re-checks the self-service switch and the revocation under the link
lock (#7). Submitted configurations never carry links, whichever endpoint they arrive
through (#10). The Quick Connect approval refuses a disabled account (#11). A startup line
says whether every guarded Jellyfin action exists (#13). Collisions answer 409 and ask the
person to sign in again; one constant names the password-reset provider (#14).

## Validation

At the reviewed commit, before any change: the unit suite passed (594), and six
reproductions written for the first pass failed as expected, four of them over HTTP
against Jellyfin 12.0.

After the fixes, on the tree that is committed:

| Check | Result |
|---|---|
| Unit tests | **630 passed**, none failed or skipped. 607 after the first pass; the second added 23, among them the flood regression `A_back_channel_logout_does_not_wait_for_a_login_in_progress` |
| Regular end-to-end tier, Jellyfin 12.0 | **149 passed** |
| Regular end-to-end tier, Jellyfin 12.3 | 148 passed, 1 failed: `The_session_survives_a_reload`, the web client not adopting a session the plugin had already handed it — the flake in [#224](https://github.com/be-nj/jellyfin-sso/issues/224), same test, same symptom. `BrowserLoginTests` then passed three runs of three |
| Regular end-to-end tier, Jellyfin 12.4 | **149 passed** |
| Real providers, Jellyfin 12.4 | **16 passed**: Authentik 2026.8.4 (4), Keycloak 26.5 (8), Authelia 4.39 (4), through their own login forms |
| Build | no warnings, no errors |

The end-to-end filter is the one CI uses,
`Category!=RealIdp&Category!=Screenshots&Category!=Demo&Category!=Manual`. Among the 149
are the second pass's HTTP checks against a real server: a secret fetched before a
revocation is refused with 401; a logout during the picture download refuses the login
with "This login was revoked"; a device that collects its Quick Connect token after a
logout collects a dead one; a disabled account cannot approve a device; Jellyfin's
generic configuration endpoint does not remove a link; and the startup line says every
guarded action exists, on each of the three servers.

CodeQL runs on the pull request; mutation testing runs weekly and was not run for this
change. Neither is reported here as a result.

## OIDC conformance rerun

Run twice: on 11 November against the first pass's fixes, and on 12 November against the
tree that is committed, because the second pass changed the discovery policy and the
login path. Both times against Jellyfin 12.0 with the plugin package built from the tree,
through the harness in [`tests/conformance/`](../../tests/conformance/README.md), with the
OpenID Foundation suite at image digest `sha256:7740c118…021e` and its nginx at
`sha256:3bf3f488…b984`. The profile is the plugin's default: authorization code, PKCE,
public client, static registration. The two runs came out identical.

| Plan | Suite verdicts |
|---|---|
| 28 login modules | 20 `PASSED`, 4 `FAILED`, 1 `SKIPPED`, 3 with no verdict |
| 3 discovery modules, each with a cold cache | 3 `PASSED` |
| 8 back-channel logout modules | 8 `PASSED` |

That is not "the login plan passes", and what is not `PASSED` is:

| Module (`oidcc-client-test-…`) | Suite | What it is |
|---|---|---|
| `client-secret-basic` | `FAILED` | Needs a confidential client; the profile run is a public one. Not tested |
| `userinfo-bearer-body` | `FAILED` | The plugin sends the access token in the header and refuses this flow with 400, by design |
| `discovery-jwks-uri-keys`, `discovery-issuer-mismatch` in the shared plan | `FAILED` | The plan shares one issuer, and the plugin caches discovery for half an hour. Run separately with a fresh provider id, both pass — the discovery row above |
| `idtoken-sig-none` | `SKIPPED` | The plugin refuses the unsigned token with 400; the suite does not require support and stops there |
| `distributed-claims`, `discovery-webfinger-acct`, `discovery-webfinger-url` | none, then interrupted | Features this plugin does not implement |

The driver's own reading of the login plan — who stopped each flow, and whether that was
the outcome the module asks for — is 22 `OK`, 2 `LOOK` (the two cache casualties) and 4
`n/a`. It does not replace the suite's verdict. Every refusal the plugin made was a 400;
the server log has no 500 and no error, and on the issuer-mismatch module it names the
issuer that did not match. Both key-rotation modules pass, the regular one after the two
logins it needs. The logout plan refuses all seven broken logout tokens — `alg=none`, the
wrong algorithm, audience or issuer, a missing or wrong event, a nonce — and accepts the
valid one, with the RS256 pin the harness sets for that plan.

### Corrections to the harness

Made by the first pass, checked by the second, which found none of them turning a failure
into a pass:

- The discovery driver followed the start endpoint's redirect into the authorization
  request after the positive discovery modules had already finished, which the suite
  records as `Illegal test state change: FINISHED -> RUNNING` and a late `FAILED`. It now
  stops at the redirect and attributes the end to itself, not to the plugin; it uses a
  fresh provider id and issuer alias per run, and its `OK` needs the suite's `PASSED`, a
  module that completed without an earlier failure, and the expected HTTP status.
- The regular key-rotation module rotates on the *second* authorization request; the
  driver now performs two logins and records both.
- `distributed-claims` was counted `OK` because the login completed, while the module
  wants a separate claims endpoint fetched, which the plugin does not do. It is now out
  of scope with WebFinger.
- The wait loops polled `/api/runner/{id}`, which has no `status`; they read
  `/api/info/{id}` now, and stop a timed-out module with `DELETE` instead of the `POST`
  that resumes it.

Still weak, and recorded rather than fixed: the issuer-mismatch verdict accepts any 400,
so the server log is what says it was the issuer; `n/a` modules would not flag a 5xx; and
`run_logout.py` has no who-stopped attribution, which matters less there because the
suite observes the plugin's response directly.

## What this does not show

Both passes were AI agents, the second checking the first and its sub-agents checking
the same tree; no person outside has read this code. They found things in each other's
work that neither found in its own, which argues for a reader of a different kind rather
than for more of the same. The conformance runs are not a certification, and the profile
tested is a public client: the confidential-client modules were not run, and distributed
claims and WebFinger are outside what this plugin implements. API keys created from a
session are not revoked with it; that is Jellyfin's model of API keys and is recorded as
an open question rather than fixed here, with the rest of what was deliberately left, in
[#258](https://github.com/be-nj/jellyfin-sso/issues/258).
