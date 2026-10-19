# Review, 19 October 2026

Five passes over the whole plugin — authentication, authorization, input handling,
resources, maintainability — each looking for a different kind of defect, logged
separately and consolidated here. Everything was found by reading this repository's own
code, so the list is as much about where the tests were not looking as about the code.

Reviewed at `f66801c`; fixes landed in `0decb9e`, `1c6c6f5`, `279d2a3` and `f44d662`.

## What came out of it

| | Finding | Severity | Issue |
|---|---|---|---|
| 1 | Quick Connect: a login approved the code without asking | medium-high | [#1](https://github.com/be-nj/jellyfin-sso/issues/1) |
| 2 | Every login reset policy settings the plugin does not manage | medium | [#3](https://github.com/be-nj/jellyfin-sso/issues/3) |
| 3 | The login button was never written to Jellyfin's branding | medium | [#2](https://github.com/be-nj/jellyfin-sso/issues/2) |
| 4 | A server under a sub-path could not complete a login | medium | [#5](https://github.com/be-nj/jellyfin-sso/issues/5) |
| 5 | A provider's key rotation broke logins until a restart | medium | [#6](https://github.com/be-nj/jellyfin-sso/issues/6) |
| 6 | Two admin link endpoints; the hardening went to the unused one | medium | [#7](https://github.com/be-nj/jellyfin-sso/issues/7) |
| 7 | The configuration page's stylesheet never reached the browser | low | [#4](https://github.com/be-nj/jellyfin-sso/issues/4) |
| 8 | No HTTP timeout, unsynchronised configuration writes, an uncapped group list, a 500 on a missing ID token, caller input echoed in errors | low | fixed in the commits above |
| 9 | Pending-state cap, an apostrophe in `escAttr`, an 800-line controller, revocation latency | low | [#8](https://github.com/be-nj/jellyfin-sso/issues/8), open |

## The pattern worth keeping

Three of the findings — the login button, the stylesheet, the duplicate endpoint — are the
same shape: code that was present, correct, and never executed, with tests covering the
part that worked. None of them could fail a build, and two had been broken for months.

What catches that shape is a test that looks from outside the process: ask the running
server for its branding, ask the browser for a computed style, call the endpoint the user
interface actually calls. Those tests exist now.

The second pattern is subtler. 124 tests passed before this review; mutation testing
killed 44 % of injected defects. Every expiry check, every playback permission, an
administrator's library list and the parental rating were written and never read back.
A count of tests says nothing about whether they would notice a defect — that number has
to be measured too, and now is, weekly and with a floor in CI.

## Method

Each pass had one lens, so that a finding missed in one had a second chance in another:

1. **Authentication** — token validation, nonce and state handling, secret lifecycles.
2. **Authorization** — who may call what, what a call may change.
3. **Input handling** — escaping, log forging, anything caller-controlled that reaches a
   page or the log.
4. **Resources** — caches, growth, concurrency, timeouts, failure paths.
5. **Maintainability** — dead code, duplication, stale comments, test gaps.

The per-pass logs are working notes; this page is what they add up to. Mutation testing
was run between passes 4 and 5 and produced findings of its own, which is why it is now
part of the routine rather than an experiment.
