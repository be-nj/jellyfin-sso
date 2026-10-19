# Review, 19 October 2026

Five passes over the whole plugin — authentication, authorization, input handling,
resources, maintainability — each looking for a different kind of defect, logged
separately and consolidated here. Everything was found by reading this repository's own
code, so the list is as much about where the tests were not looking as about the code.

Reviewed at `f66801c`; fixes landed in `0decb9e`, `1c6c6f5`, `279d2a3` and `f44d662`.

## What came out of it

| | Finding | Severity | Issue |
|---|---|---|---|---|
| 1 | Quick Connect: a login approved the code without asking | medium-high | [#1](https://github.com/be-nj/jellyfin-sso/issues/1) |
| 2 | Every login reset policy settings the plugin does not manage | medium | [#3](https://github.com/be-nj/jellyfin-sso/issues/3) |
| 3 | The login button was never written to Jellyfin's branding | medium | [#2](https://github.com/be-nj/jellyfin-sso/issues/2) |
| 4 | A server under a sub-path could not complete a login | medium | [#5](https://github.com/be-nj/jellyfin-sso/issues/5) |
| 5 | A provider's key rotation broke logins until a restart | medium | [#6](https://github.com/be-nj/jellyfin-sso/issues/6) |
| 6 | Two admin link endpoints; the hardening went to the unused one | medium | [#7](https://github.com/be-nj/jellyfin-sso/issues/7) |
| 7 | The configuration page's stylesheet never reached the browser | low | [#4](https://github.com/be-nj/jellyfin-sso/issues/4) |
| 8 | No HTTP timeout, unsynchronised configuration writes, an uncapped group list, a 500 on a missing ID token, caller input echoed in errors | low | fixed in the commits above |
| 9 | Pending-state cap, an apostrophe in `escAttr`, an 800-line controller, revocation latency | low | [#8](https://github.com/be-nj/jellyfin-sso/issues/8), closed |

## Every finding, and where it went

Eighteen findings came out of the five passes and the work that followed. None is only in
a chat log: each is either fixed with a test, or filed.

| # | Finding | Pass | Where it went | Test that holds it |
|---|---|---|---|---|
| 1 | Quick Connect approved a code without asking | 1 | fixed, `279d2a3` · [#1](https://github.com/be-nj/jellyfin-sso/issues/1) | `A_login_approves_the_code_only_after_the_person_says_so` |
| 2 | A provider returning no ID token crashed the callback | 1 | fixed, `279d2a3` | — |
| 3 | The provider id was echoed into error responses | 1 | fixed, `279d2a3` | — |
| 4 | Pending logins bounded in time, not in count | 1 | fixed, `cbe616e` (cap at 10,000) | `The_store_stops_growing_at_its_cap` |
| 5 | Two admin endpoints for links; hardening on the unused one | 2 | fixed, `1c6c6f5` · [#7](https://github.com/be-nj/jellyfin-sso/issues/7) | `A_link_to_a_user_Jellyfin_does_not_have_is_refused` |
| 6 | Unlinking removes every link of a user | 2 | documented, no change | — |
| 7 | `/sso/link` names providers to anonymous callers | 2 | accepted, documented | — |
| 8 | `escAttr` did not escape the apostrophe | 3 | fixed, `cbe616e` | — |
| 9 | Discovery document and signing keys never expired | 4 | fixed, `1c6c6f5` · [#6](https://github.com/be-nj/jellyfin-sso/issues/6) | — |
| 10 | No HTTP timeout against the provider | 4 | fixed, `1c6c6f5` | — |
| 11 | Configuration writes were not serialised | 4 | fixed, `1c6c6f5` | — |
| 12 | `SeenGroups` grew without a bound | 4 | fixed, `1c6c6f5` | `The_remembered_group_names_stop_at_the_cap` |
| 13 | Dead wiring: the login button was never written | 5 | fixed, `0decb9e` · [#2](https://github.com/be-nj/jellyfin-sso/issues/2) | `Saving_the_configuration_puts_a_button_per_provider_into_the_branding` |
| 14 | The configuration page's stylesheet never loaded | 5 | fixed, `f66801c` · [#4](https://github.com/be-nj/jellyfin-sso/issues/4) | `The_pages_own_stylesheet_reaches_the_dashboard` |
| 15 | The test suite was weaker than its count | 5 | measured, 44 → 72 % · [#17](https://github.com/be-nj/jellyfin-sso/issues/17) | — |
| 16 | Stale comments | 5 | fixed, `1c6c6f5` | — |
| 17 | `SsoController` is 800 lines | 5 | fixed, `0ce0a13` · [#8](https://github.com/be-nj/jellyfin-sso/issues/8) | `SsoPagesTests` |
| 18 | Revocation waits for the next login | 5 | fixed, `0ce0a13` · [#8](https://github.com/be-nj/jellyfin-sso/issues/8) | `A_logout_the_provider_announces_ends_the_session_it_names` |

Found afterwards, while checking the claims this review had made:

| Finding | Where it went | Test that holds it |
|---|---|---|
| Every login reset policy settings the plugin does not manage | fixed, `1c6c6f5` · [#3](https://github.com/be-nj/jellyfin-sso/issues/3) | `Settings_this_plugin_does_not_own_survive_a_sync` |
| A server under a sub-path could not complete a login | fixed, `0decb9e` · [#5](https://github.com/be-nj/jellyfin-sso/issues/5) | `The_sub_path_is_normalised` |
| Forwarded headers were trusted — open redirect on logout | fixed, `8721d63` · [#12](https://github.com/be-nj/jellyfin-sso/issues/12) | `A_forwarded_prefix_that_is_not_a_path_is_ignored` |
| The generic provider mark rendered as a black dot | fixed, `34cff74` · [#10](https://github.com/be-nj/jellyfin-sso/issues/10) | `Every_slug_answers_with_a_drawable_svg` |
| The login button needed a save after an update | fixed, `1473afb` · [#9](https://github.com/be-nj/jellyfin-sso/issues/9) | `Starting_the_server_writes_the_buttons` |
| The button could not sit inside the login form | done, `34cff74` · [#11](https://github.com/be-nj/jellyfin-sso/issues/11) | `The_button_appears_in_the_login_form` |
| Releases were not reproducible on another machine | fixed, `f402f9a` · [#14](https://github.com/be-nj/jellyfin-sso/issues/14) | `scripts/package.sh`, recipe in CONTRIBUTING |
| The release workflow could not publish its manifest | fixed, `cbe616e` | — |
| `make` used an SDK the pin forbids | fixed, `7c7d355` | — |
| Scorecard counts one unanalysed commit | filed · [#19](https://github.com/be-nj/jellyfin-sso/issues/19) | — |

A dash means the fix has no test of its own: an error message, a build script, a
configuration file. Where a test exists, `scripts/check-review-audit.sh` resolves its name
against the test sources, so a renamed or deleted test fails the build rather than quietly
leaving a claim behind.

Two CodeQL notes are dismissed rather than fixed, with the reason recorded at the alert
and in [#8](https://github.com/be-nj/jellyfin-sso/issues/8): both catch every exception on
purpose, where the alternative is a server that does not start or a login that cannot
finish.

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
